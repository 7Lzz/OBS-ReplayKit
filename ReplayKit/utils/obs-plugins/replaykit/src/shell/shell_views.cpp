// view switching of the clean shell
#include "prelude.hpp"
#include "shell/shell_views.hpp"

#include <QLineEdit>

#include "core/async.hpp"
#include "core/cef_host.hpp"
#include "core/dev_log.hpp"
#include "core/helper_client.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "ui/anchored_panel.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_history.hpp"
#include "shell/shell_layout.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_rail.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_title_bar.hpp"
#include "windows/obs_settings.hpp"

namespace rk {

namespace {

// bumped on every change of view, so the second half of a dip that was started for a view the user has already left never runs
int g_activateGen = 0;
// the cover of the dip is up over the stage and comes away as soon as the browser is on top of it
bool g_revealPending = false;
// how long the page takes to fade out inside the browser before the preview goes up, and how long the cover waits over a preview that has just come up so its first frames are not the ones that show
constexpr int kLeavePageMs = 100;
constexpr int kSettleMs = 60;
// how long a cover that was only just shown is given to reach the screen before the layout changes under it
constexpr int kCoverSettleMs = 34;
// the first frame of a browser that was only just made may still be on its way to the screen when its page says it is up, so the cover stays a moment longer for the very first reveal
constexpr int kFirstRaiseSettleMs = 140;
// pings of the page that go unanswered in a row before it is taken for dead
constexpr int kHostMissLimit = 4;
bool g_setupForeground = false;
bool g_homeEditor = false;
bool g_clipsEditor = false;

bool ShellMainWindowIsForeground();

// obs settings + hidden main window helpers -- the shell has to be able to bring the window forward the same way ToggleClips brings its own window forward
void ShellForegroundMainWindow()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	if (!mw->isVisible()) {
		// hidden to the tray: the show path of obs also turns the preview display back on, which a plain show() leaves disabled. an obs build without the toggle slot gets the plain show
		if (!ToggleMainWindowShowing())
			mw->show();
	} else if (mw->isMinimized()) {
		// windows brings a window that was maximized before it was minimized back maximized, which a showNormal would not
		ShowWindow((HWND)mw->winId(), SW_RESTORE);
	}
	mw->raise();
	mw->activateWindow();
	HWND hwnd = (HWND)mw->winId();
	if (GetForegroundWindow() != hwnd) {
		HWND foreground = GetForegroundWindow();
		DWORD foregroundThread = foreground ? GetWindowThreadProcessId(foreground, nullptr) : 0;
		DWORD thisThread = GetCurrentThreadId();
		bool attached = foregroundThread && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, TRUE);
		BringWindowToTop(hwnd);
		SetForegroundWindow(hwnd);
		if (attached)
			AttachThreadInput(thisThread, foregroundThread, FALSE);
	}
	ShellFocusBrowser();
	// what came of it goes to the log: a window that is not in front cannot take the keyboard, and then the keys stay with whatever is in front until a click
	QTimer::singleShot(150, g_callbacks, []() {
		const bool front = ShellMainWindowIsForeground();
		const bool keys = ShellFocusBrowser();
		blog(LOG_INFO, "[replaykit] window brought forward: obs in front %d, browser has the keyboard %d", front ? 1 : 0, keys ? 1 : 0);
	});
}

bool ShellMainWindowIsForeground()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return false;
	HWND foreground = GetForegroundWindow();
	return foreground && GetAncestor(foreground, GA_ROOTOWNER) == (HWND)mw->winId();
}

void ShellSuspendPreview(bool suspend)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || suspend == g_previewSuspended)
		return;
	// the preview display is a native window and would sit on top of any plain widget placed over it, so the obs settings dialog (which is made of plain widgets) needs it switched off while it is up; the obs slot also stops the display from rendering, and a build that does not have it gets the container hidden, which takes the native display window with it
	QWidget *container = mw->findChild<QWidget *>("previewContainer");
	if (suspend) {
		g_previewWasOn = container && container->isVisibleTo(mw);
		if (g_previewWasOn && !QMetaObject::invokeMethod(mw, "EnablePreviewDisplay", Qt::DirectConnection, Q_ARG(bool, false)))
			container->setVisible(false);
	} else if (g_previewWasOn) {
		if (!QMetaObject::invokeMethod(mw, "EnablePreviewDisplay", Qt::DirectConnection, Q_ARG(bool, true)) && container)
			container->setVisible(true);
	}
	g_previewSuspended = suspend;
}

int ShellRailIdFor(ShellView view)
{
	switch (view) {
	case ShellView::Clips:
		return ShellRail::Clips;
	case ShellView::Projector:
		return ShellRail::Projector;
	case ShellView::Settings:
	case ShellView::ObsSettings:
		return ShellRail::Settings;
	default:
		return ShellRail::Home;
	}
}

// true when no obs settings dialog is left open. the close handler of the dialog asks about unsaved changes and may refuse, in which case the caller stays where it is
bool ShellCloseObsSettings()
{
	if (!g_obsSettingsDialog)
		return true;
	g_obsSettingsDialog->close();
	// hidden is the dialog itself closed; a dialog that is only parked with its host stays visible-to-itself, so it counts as refused
	return !g_obsSettingsDialog || g_obsSettingsDialog->isHidden();
}

const char *ShellViewName(ShellView view)
{
	switch (view) {
	case ShellView::Clips:
		return "clips";
	case ShellView::Settings:
		return "settings";
	case ShellView::Setup:
		return "setup";
	default:
		return "home";
	}
}

// the window goes to the tray when the tray icon is there and to the taskbar when it is not. the position is saved first because obs writes its own copy only on exit, which a hide to the tray never reaches, and the menus that hang off the window go with it
void ShellHideMainWindowToTray()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	SaveWindowGeometry("obsMainWindow", mw);
	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	if (!(tray && tray->isVisible() && ToggleMainWindowShowing()))
		mw->showMinimized();
	AnchoredPanel::HideOwnedBy(mw);
}

// puts the shell browser on top of the stage. the browser is a native window, so an alien parent raising is not enough and the browser widget is raised as well
void ShellRaiseHost()
{
	if (!g_shellHost)
		return;
	// the browser comes up over the cover first and the cover goes after, so there is no moment where the preview or a bare window shows between them
	g_shellHost->raise();
	if (g_shellHost->browser) {
		g_shellHost->browser->raise();
		g_shellHost->browser->setFocus();
	}
	if (g_shellCurtain)
		g_shellCurtain->hide();
	g_hostOnTop = true;
	ShellFocusBrowser();
	ShellRaiseGrip();
	// a page that came back from a native view was held out of sight until now, and it settles in as the cover of the dip fades off it
	ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.enter && RKShell.enter();");
	if (g_revealPending) {
		g_revealPending = false;
		ShellProjectorReveal();
	}
}

// a page view is what the user asked for: the browser is told to flip to it, and comes up over the preview only once the page says the flip is on screen, so what appears is never a stale page or an empty frame
void ShellSurfacePage()
{
	if (!g_shellHost || !g_hostReady) {
		// nothing to show yet, so the stage stays covered until the browser has said its first page is up
		if (g_shellCurtain) {
			g_shellCurtain->show();
			g_shellCurtain->raise();
		}
		g_hostOnTop = false;
		ShellEnsureHost();
		return;
	}
	const ShellView view = g_shellView;
	const bool alreadyShowing = g_hostShown == view && g_hostRequested == view;
	std::string js = std::string("RKShell.show('") + ShellViewName(view) + "'";
	if (view == ShellView::Settings && !g_shellSettingsPage.isEmpty())
		js += ",'" + g_shellSettingsPage.toStdString() + "'";
	js += ");";
	g_shellSettingsPage.clear();
	ShellRunJs(g_shellHost->browser, js);
	g_hostRequested = view;
	if (g_hostOnTop) {
		ShellFocusBrowser();
		return;
	}
	if (alreadyShowing) {
		ShellRaiseHost();
		return;
	}
	// the answer normally arrives within a few frames; the timer only exists so a lost answer cannot leave the user looking at the preview
	const int gen = ++g_hostWaitGen;
	QTimer::singleShot(1500, g_callbacks, [gen, view]() {
		if (gen != g_hostWaitGen || g_shellView != view || g_hostOnTop)
			return;
		blog(LOG_WARNING, "[replaykit] shell browser did not confirm '%s' in time; showing it anyway", ShellViewName(view));
		ShellRaiseHost();
		// a page that says nothing at all may have lost its renderer, which leaves the browser white
		ShellProbeHost(true);
	});
}

// a page that never reports (the helper died between the check and the load) must not leave the stage covered forever. the browser only loads while the window is on screen, so the twenty seconds are counted from when it first was
void ShellArmHostBackstop(int delayMs)
{
	QTimer::singleShot(delayMs, g_callbacks, []() {
		if (g_hostReady || !g_shellHost)
			return;
		if (!g_hostVisibleTimer.isValid() || g_hostVisibleTimer.elapsed() < 20000) {
			ShellArmHostBackstop(2000);
			return;
		}
		blog(LOG_WARNING, "[replaykit] shell browser never reported its first page; showing it as it is");
		g_hostReady = true;
		if (ShellIsPageView(g_shellView))
			ShellRaiseHost();
	});
}

// builds the browser under the preview, where it loads and paints unseen. it is created only once cef is up and the helper answers, and its page comes up when it reports that its first page is on screen
void ShellCreateHost(QMainWindow *mw)
{
	g_homeEditor = false;
	g_clipsEditor = false;
	ShellUpdateTitle();
	ShellPage *page = new ShellPage(mw);
	QVBoxLayout *layout = new QVBoxLayout(page);
	layout->setContentsMargins(0, 0, 0, 0);
	// nullptr cookie manager shares the same cef storage as every other obs-browser page, so the streamable session and favorites are the same ones the popup windows used
	QCefWidget *browser = g_cef->create_widget(page, "http://127.0.0.1:8767/shell-view", nullptr);
	layout->addWidget(browser);
	page->browser = browser;
	QLineEdit *status = new QLineEdit(page);
	status->hide();
	QObject::connect(browser, SIGNAL(titleChanged(QString)), status, SLOT(setText(QString)));
	QObject::connect(status, &QLineEdit::textChanged, page, [](const QString &title) {
		if (title == QLatin1String("rk-status:home:editor") || title == QLatin1String("rk-status:home:page"))
			ShellSetEditor(ShellView::Home, title.endsWith(QLatin1String(":editor")));
		else if (title == QLatin1String("rk-status:clips:editor") || title == QLatin1String("rk-status:clips:page"))
			ShellSetEditor(ShellView::Clips, title.endsWith(QLatin1String(":editor")));
	});
	g_shellHost = page;
	g_hostRequested = ShellView::Home;
	ShellLayoutViews();
	page->show();
	page->lower();
	browser->lower();
	ShellRaiseGrip();
	if (mw->isVisible() && !g_hostVisibleTimer.isValid())
		g_hostVisibleTimer.start();
	blog(LOG_INFO, "[replaykit] shell browser created");
	ShellArmHostBackstop(5000);
}

} // namespace

void ShellUpdateTitle()
{
	if (!g_shellOn || !g_shellTitle)
		return;
	QString page;
	switch (g_shellView) {
	case ShellView::Home: page = g_homeEditor ? QStringLiteral("Editor") : QStringLiteral("Home"); break;
	case ShellView::Clips: page = g_clipsEditor ? QStringLiteral("Editor") : QStringLiteral("Clips"); break;
	case ShellView::Projector: page = QStringLiteral("Projector"); break;
	case ShellView::Settings: page = QStringLiteral("Settings"); break;
	case ShellView::ObsSettings: page = QStringLiteral("OBS Settings"); break;
	case ShellView::Setup: page = QStringLiteral("Setup"); break;
	}
	g_shellTitle->SetPage(page);
}

void ShellSetEditor(ShellView view, bool active)
{
	if (view == ShellView::Home)
		g_homeEditor = active;
	else if (view == ShellView::Clips)
		g_clipsEditor = active;
	else
		return;
	if (g_shellView == view)
		ShellUpdateTitle();
}

// the views the shell browser holds; projector and obs settings are native windows of their own
bool ShellIsPageView(ShellView view)
{
	return view == ShellView::Home || view == ShellView::Clips || view == ShellView::Settings || view == ShellView::Setup;
}

// the keyboard goes to the host window cef puts around its page, which is what makes cef take the focus for the page the way a click does; focus placed on a window below it did not give the keys to the page until a click. the page that shows is told to take it as well, since the focus of a frame is not the focus of the browser. only while the window itself is in front, so a menu or a dialog that belongs to it keeps the keyboard it has
bool ShellFocusBrowser()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!g_shellOn || !mw || !g_shellHost || !g_shellHost->browser || !g_hostOnTop || GetForegroundWindow() != (HWND)mw->winId())
		return false;
	QCefWidget *browser = g_shellHost->browser;
	browser->setFocus(Qt::ActiveWindowFocusReason);
	HWND outer = (HWND)browser->winId();
	HWND host = nullptr;
	EnumChildWindows(outer, [](HWND child, LPARAM param) -> BOOL {
		wchar_t name[64] = {};
		GetClassNameW(child, name, 64);
		if (wcscmp(name, L"CefBrowserWindow") != 0)
			return TRUE;
		*reinterpret_cast<HWND *>(param) = child;
		return FALSE;
	}, reinterpret_cast<LPARAM>(&host));
	if (!host) {
		static bool warned = false;
		if (!warned)
			blog(LOG_WARNING, "[replaykit] the shell browser has no host window to give the keyboard to");
		warned = true;
		return false;
	}
	// a focus that already sits on the host window is not announced to it again, so it is moved up a level first
	if (GetFocus() == host)
		SetFocus(outer);
	SetFocus(host);
	ShellRunJs(browser, "window.RKShell && RKShell.focus && RKShell.focus();");
	const HWND focus = GetFocus();
	return focus && (focus == host || IsChild(host, focus));
}

void ShellRaiseGrip()
{
	if (g_shellCorner && g_shellCorner->isVisible())
		g_shellCorner->raise();
	if (g_shellGrip && g_shellGrip->isVisible())
		g_shellGrip->raise();
}

// the page starts over the way it does after a launch: the stage is covered until the page says its first view is on screen, and the view that was asked for is put up again from there. a browser is never torn down for this, only reloaded
void ShellReloadHost()
{
	if (!g_shellOn || !g_shellHost || !g_shellHost->browser)
		return;
	// a reload that did not bring the page back is not repeated every few seconds
	if (g_hostReloadTimer.isValid() && g_hostReloadTimer.elapsed() < 30000)
		return;
	g_hostReloadTimer.restart();
	g_homeEditor = false;
	g_clipsEditor = false;
	ShellUpdateTitle();
	blog(LOG_WARNING, "[replaykit] shell page stopped answering, reloading it");
	ShellEndClipFullscreen();
	g_hostReady = false;
	g_hostOnTop = false;
	g_hostShown = ShellView::Home;
	g_hostRequested = ShellView::Home;
	g_hostPingMisses = 0;
	++g_hostWaitGen;
	g_hostVisibleTimer.restart();
	g_shellHost->browser->reloadPage();
	if (ShellIsPageView(g_shellView))
		ShellSurfacePage();
	ShellArmHostBackstop(5000);
}

// a page whose renderer died stays blank until the browser is reloaded and nothing tells the plugin, so the page is asked for a sign of life: it answers through the helper (RKShell.ping, /shell/ping, SHELLPONG on the pipe). silence only counts against the page once an answer has come back at least once, since a helper that does not know the route never answers, and only while the helper itself is up
void ShellProbeHost(bool suspicious)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!g_shellOn || !g_shellHost || !g_hostReady || !mw || !mw->isVisible() || mw->isMinimized()) {
		g_hostPingMisses = 0;
		return;
	}
	if (suspicious)
		g_hostPingMisses = qMax(g_hostPingMisses, kHostMissLimit - 2);
	const int n = ++g_hostPingSent;
	ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.ping && RKShell.ping(" + std::to_string(n) + ");");
	QPointer<ShellPage> host = g_shellHost;
	QTimer::singleShot(2000, g_callbacks, [n, host]() {
		if (n <= g_hostPingAcked || !g_hostPingProven || !host || host != g_shellHost || !g_hostReady)
			return;
		if (++g_hostPingMisses < kHostMissLimit)
			return;
		g_hostPingMisses = 0;
		// a reload is no use while the helper is down, and a page that only waits on the helper is not the one to throw away
		RunAsync([n]() {
			const bool helperUp = !HttpRequest("GET", "/version", 8767, nullptr, 800).empty();
			QMetaObject::invokeMethod(
				g_callbacks,
				[n, helperUp]() {
					if (helperUp && n > g_hostPingAcked && g_hostReady)
						ShellReloadHost();
				},
				Qt::QueuedConnection);
		});
	});
}

void ShellHostPong(int n)
{
	// an answer to a number that was never sent says nothing about the page
	if (n < 1 || n > g_hostPingSent)
		return;
	if (!g_hostPingProven)
		blog(LOG_INFO, "[replaykit] shell page answers pings, the watchdog is armed");
	g_hostPingAcked = qMax(g_hostPingAcked, n);
	g_hostPingProven = true;
	g_hostPingMisses = 0;
}

// the native side of a clip fullscreen ends without the page having asked, so the page is told to leave its own as well. safe to call when nothing is fullscreen
void ShellEndClipFullscreen()
{
	const bool was = g_clipsFullscreenActive || g_shellNativeFullscreen;
	g_clipsFullscreenActive = false;
	ShellNativeFullscreen(false);
	if (was && g_shellHost)
		ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.leaveFullscreen();");
}

// a dialog that was built earlier and is only parked holds what obs held then, and writing a page of it back after replaykit changed the same values would undo that, so it is dropped when replaykit settings are saved. the cancel button is the way out, which is what reverts a theme the dialog was previewing and asks nothing. one that is on screen is left alone
void ShellDropParkedObsSettings()
{
	if (!g_obsSettingsDialog || g_shellView == ShellView::ObsSettings)
		return;
	if (QDialogButtonBox *buttons = g_obsSettingsDialog->findChild<QDialogButtonBox *>()) {
		if (QAbstractButton *cancel = buttons->button(QDialogButtonBox::Cancel))
			cancel->click();
	}
}

void ShellStartOutputWhenFree(std::function<void()> start)
{
	if (!g_shellOn || !g_obsSettingsDialog || g_shellView == ShellView::ObsSettings) {
		start();
		return;
	}
	ShellDropParkedObsSettings();
	// the dialog is torn down once its exec() returns, after this call has unwound, and that is when obs takes outputs again
	QTimer::singleShot(0, g_callbacks, [start]() { start(); });
}

// puts the native window of the current view on top of the stack. the preview, the obs settings and the shell browser are all native windows, so what shows is decided by their order, and nothing is hidden or shown again once it exists: a cef window that is shown fresh paints white first, which is the flash this replaces
void ShellSurfaceView()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	if (g_shellView == ShellView::Projector) {
		if (g_shellCurtain)
			g_shellCurtain->hide();
		if (g_viewObsHost)
			g_viewObsHost->hide();
		if (QWidget *central = mw->centralWidget())
			central->raise();
		g_hostOnTop = false;
		if (g_hostReady && g_shellHost)
			ShellRunJs(g_shellHost->browser, "RKShell.park();");
	} else if (g_shellView == ShellView::ObsSettings) {
		if (g_shellCurtain)
			g_shellCurtain->hide();
		if (g_viewObsHost) {
			g_viewObsHost->show();
			// until the dialog has painted it stays under the stage, so the page that was showing is what people see meanwhile
			if (g_obsSettingsPainted) {
				g_viewObsHost->raise();
				// the loader that covered the wait is done with
				if (g_shellHost)
					ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.busy(false);");
			} else {
				g_viewObsHost->lower();
			}
		}
		g_hostOnTop = false;
		if (g_hostReady && g_shellHost)
			ShellRunJs(g_shellHost->browser, "RKShell.park();");
	} else {
		if (g_viewObsHost)
			g_viewObsHost->hide();
		ShellSurfacePage();
	}
	ShellProjectorApply();
	ShellRaiseGrip();
}

// the second half of the change, once the cover of a dip is on screen: the layout and the native windows of the new view. dipIn says the cover went up for this change, and entering that the view is the projector
static void FinishActivate(ShellView view, bool coverUp, bool entering, bool dipIn)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn)
		return;
	// the sources panel first, so the layout that follows already sees the stage that is left around it. the whole layout and not only the views, since the projector view has a strip of its own that moves the stage
	ShellProjectorPrepare();
	ShellLayout();
	// a cover that is not part of this change has no business staying up over the stage
	if (!coverUp && !entering)
		ShellProjectorDropCover();
	if (coverUp) {
		ShellProjectorFollow();
		g_revealPending = true;
	}
	ShellSurfaceView();
	if (dipIn) {
		const int gen = g_activateGen;
		QTimer::singleShot(kSettleMs, g_callbacks, [gen]() {
			if (gen == g_activateGen)
				ShellProjectorReveal();
		});
	}
	if (g_shellRail) {
		g_shellRail->SetSelected(ShellRailIdFor(view));
		g_shellRail->SyncLock();
	}
	AnchoredPanel::HideOwnedBy(mw);
}

// the change itself: the state, the layout and the native windows of the new view. coverUp says the dip is already half done, the cover is opaque over the old stage and waits for the browser to come on top
static void ActivateNow(ShellView view, bool coverUp)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn)
		return;
	g_revealPending = false;
	const bool enteringProjector = view == ShellView::Projector && g_shellView != ShellView::Projector;
	DevLog("view %d -> %d (0 home, 1 projector, 2 clips, 3 settings, 4 setup, 5 obs settings)", int(g_shellView), int(view));
	g_shellView = view;
	ShellUpdateTitle();
	if (!ShellShowsClips(view))
		ShellEndClipFullscreen();
	ShellSuspendPreview(view == ShellView::ObsSettings);
	// the preview cannot fade, so the stage colour goes over it before it comes up and fades away once its first frames are past. the cover reaches over the strip and the sources panel as well, which are not part of the stage but come and go with this view, and it has to be on screen before any of that changes: a window that was only just shown is painted when the event loop next runs and not before, so the rest of the change waits a frame or two for it, and what changed meanwhile showed as black slabs where the strip and the panel were about to come up
	const bool dipIn = enteringProjector && ShellProjectorCover(true);
	if (dipIn) {
		const int gen = g_activateGen;
		QTimer::singleShot(kCoverSettleMs, g_callbacks, [view, coverUp, gen]() {
			if (gen != g_activateGen)
				return;
			DwmFlush();
			FinishActivate(view, coverUp, true, true);
		});
		return;
	}
	FinishActivate(view, coverUp, enteringProjector, false);
}

bool ShellSetupLocked()
{
	return g_setupPending && g_shellView == ShellView::Setup;
}

void ShellReclaimForeground()
{
	QMainWindow *mw = ObsMainWindowQt();
	HWND foreground = GetForegroundWindow();
	if (!mw || !mw->isVisible() || mw->isMinimized() || !foreground || foreground == (HWND)mw->winId())
		return;
	DWORD owner = 0;
	GetWindowThreadProcessId(foreground, &owner);
	if (owner == GetCurrentProcessId() && !IsWindowVisible(foreground))
		ShellForegroundMainWindow();
}

bool ShellActivate(ShellView view)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn)
		return false;
	if (view != ShellView::Setup && ShellSetupLocked())
		return false;
	// unsaved replaykit settings: the page asks to save or leave first and answers through ShellLeaveSettings; the obs settings open on top of that page, so they are not leaving it
	if (g_settingsDirty && g_shellView == ShellView::Settings && view != ShellView::Settings && view != ShellView::ObsSettings && g_shellHost && g_hostOnTop) {
		const char *target = view == ShellView::Projector ? "projector" : ShellViewName(view);
		ShellRunJs(g_shellHost->browser, std::string("window.RKShell && RKShell.confirmLeave && RKShell.confirmLeave('") + target + "');");
		return false;
	}
	// nothing to show until the dialog exists, and building it is the slow part, so it stays built while the two settings pages trade places
	if (view == ShellView::ObsSettings && !g_obsSettingsDialog)
		return false;
	if (view != ShellView::ObsSettings && view != ShellView::Settings && !ShellCloseObsSettings())
		return false;
	// the storage card and the notification reader are drawn over the view they were opened on, so they go with any change of view, keybind or click
	StorageModalClose();
	NotifyReaderClose();
	// the place is noted now and not when the change lands, since a dip may take a moment to come about
	ShellHistoryNote(view);
	const int gen = ++g_activateGen;
	// the rail answers the click at once, and the stage follows
	if (g_shellRail)
		g_shellRail->SetSelected(ShellRailIdFor(view));

	const bool dipPossible = g_hostReady && g_shellHost && mw->isVisible() && !mw->isMinimized() && !g_clipsFullscreenActive;
	if (dipPossible && g_shellView == ShellView::Projector && ShellIsPageView(view)) {
		// the preview goes into the stage colour and the page comes in over it, which is what the pages do among themselves
		ShellProjectorFadeOut(
			[gen, view]() {
				if (gen == g_activateGen)
					ActivateNow(view, true);
			},
			true);
		return true;
	}
	if (dipPossible && view == ShellView::Projector && ShellIsPageView(g_shellView) && g_hostOnTop) {
		// the page fades out inside the browser first, and the preview comes up under the cover once it is gone
		ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.leave && RKShell.leave();");
		QTimer::singleShot(kLeavePageMs, g_callbacks, [gen, view]() {
			if (gen == g_activateGen)
				ActivateNow(view, false);
		});
		return true;
	}
	ActivateNow(view, false);
	return true;
}

// the shell page reporting which view it now shows (the VIEWSHOWN message of the helper). only the current wish counts: an answer for a page the user has already left is just recorded
void ShellHostShown(const std::string &name)
{
	const ShellView view = name == "clips" ? ShellView::Clips : name == "settings" ? ShellView::Settings : name == "setup" ? ShellView::Setup : ShellView::Home;
	g_hostShown = view;
	const bool first = !g_hostReady;
	g_hostReady = true;
	blog(LOG_INFO, "[replaykit] shell page shown: %s%s", name.c_str(), first ? " (first)" : "");
	if (!g_shellOn)
		return;
	if (ShellIsPageView(g_shellView)) {
		if (g_shellView == view) {
			if (!g_hostOnTop && first) {
				QTimer::singleShot(kFirstRaiseSettleMs, g_callbacks, [view]() {
					if (g_shellView == view && !g_hostOnTop && g_hostReady)
						ShellRaiseHost();
				});
			} else if (!g_hostOnTop) {
				ShellRaiseHost();
			}
		} else if (first) {
			// the browser came up on home while the user had already asked for another page
			ShellSurfacePage();
		}
	} else if (first && g_shellHost) {
		// the preview or the obs settings are showing, so the page that just came up is told it is not
		ShellRunJs(g_shellHost->browser, "RKShell.park();");
	}
	if (name == "setup" && g_setupForeground) {
		g_setupForeground = false;
		QMainWindow *mw = ObsMainWindowQt();
		if (mw && mw->isVisible() && !mw->isMinimized() && !ShellMainWindowIsForeground())
			ShellForegroundMainWindow();
	}
}

void ShellOpenSettingsPage(const QString &page)
{
	if (!ShellSetupLocked())
		g_shellSettingsPage = page;
	ShellOpenView(ShellView::Settings, false);
}

// starts the browser when cef and the helper are both up. the helper is launched beside obs and can still be coming up, so the wait is a bounded poll on a worker thread
void ShellEnsureHost()
{
	if (!g_shellOn || g_shellHost || g_hostCreating)
		return;
	g_hostCreating = true;
	RunAsync([]() {
		const bool cefReady = EnsureCefReadyBlocking();
		bool helperUp = false;
		// a helper that is not listening yet refuses at once, so it is asked again soon; the pages come up the moment it answers
		for (int i = 0; cefReady && i < 400 && !helperUp; ++i) {
			helperUp = !HttpRequest("GET", "/version", 8767, nullptr, 800).empty();
			if (!helperUp)
				std::this_thread::sleep_for(std::chrono::milliseconds(150));
		}
		QMetaObject::invokeMethod(
			g_callbacks,
			[cefReady, helperUp]() {
				g_hostCreating = false;
				QMainWindow *mw = ObsMainWindowQt();
				if (!cefReady || !helperUp || !CefOpen() || !mw || !g_shellOn || g_shellHost) {
					blog(LOG_WARNING, "[replaykit] shell browser not created (cef ready: %d, helper up: %d)", cefReady, helperUp);
					return;
				}
				ShellCreateHost(mw);
			},
			Qt::QueuedConnection);
	});
}

void ShellOpenView(ShellView view, bool quiet)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellOn || !g_shellTitle)
		return;
	if (view != ShellView::Setup && ShellSetupLocked()) {
		if (!quiet)
			ShellForegroundMainWindow();
		return;
	}
	if (view == ShellView::Setup && !g_shellTimer) {
		g_shellPendingSetup = true;
		return;
	}
	if (view == ShellView::Setup && !quiet)
		g_setupForeground = true;
	if (view == ShellView::Home || view == ShellView::Projector) {
		ShellActivate(view);
		return;
	}

	const bool onScreen = mw->isVisible() && !mw->isMinimized();
	if (!onScreen && quiet) {
		// nothing to embed into yet: the first-run wizard waits for the window, anything else is simply dropped
		if (view == ShellView::Setup)
			g_shellPendingSetup = true;
		return;
	}
	if (!quiet)
		ShellForegroundMainWindow();

	if (view == ShellView::ObsSettings) {
		if (g_obsSettingsDialog) {
			ShellActivate(ShellView::ObsSettings);
			return;
		}
		// building the dialog blocks this thread for a while, and the browser is what stays alive meanwhile: it is put on the settings page with a loader over it, and the build starts a moment later so the loader has been drawn by then
		if (g_shellView != ShellView::Settings)
			ShellActivate(ShellView::Settings);
		if (g_shellHost)
			ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.busy('Loading OBS settings');");
		g_obsBuildTimer.start();
		// the action of obs opens and runs the dialog (it blocks in its own event loop until closed), so it is started from a fresh timer callback instead of from inside the click that asked for it. the app filter moves the dialog into the window as it is first polished
		QTimer::singleShot(120, g_callbacks, []() { OpenObsSettingsRaw(); });
		// a dialog that never comes up must not leave the loader over the page for good
		QTimer::singleShot(30000, g_callbacks, []() {
			if (!g_obsSettingsDialog && g_shellHost)
				ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.busy(false);");
		});
		return;
	}

	ShellActivate(view);
}

void ShellReselect(ShellView view)
{
	if (!g_shellOn || !g_shellHost || !g_hostOnTop || g_shellView != view || !ShellIsPageView(view))
		return;
	ShellRunJs(g_shellHost->browser, std::string("window.RKShell && RKShell.reselect && RKShell.reselect('") + ShellViewName(view) + "');");
}

// the keybind toggles clips like it toggled the window: pressed on the clips page with the obs window in front it sends the window to the tray, and a page that is open but behind another app is brought forward instead
void ShellToggleClips()
{
	QMainWindow *mw = ObsMainWindowQt();
	const bool onScreen = mw && mw->isVisible() && !mw->isMinimized();
	// the storage card sits over whatever view is under it, and the press is spent on taking it away when clips are already what shows
	const bool cardWasOpen = StorageModalIsOpen();
	StorageModalClose();
	NotifyReaderClose();
	if (onScreen && g_shellView == ShellView::Clips) {
		if (cardWasOpen || !ShellMainWindowIsForeground())
			ShellForegroundMainWindow();
		else
			ShellHideMainWindowToTray();
		return;
	}
	ShellOpenView(ShellView::Clips, false);
}

// the tray icon was clicked while the obs settings are folded in: they close and the window lands on the replaykit settings. obs refuses to hide the window meanwhile, so the next click is the one that hides it. the dialog can refuse to close (unsaved changes), and then the click is spent
void ShellTrayClickWithObsSettings()
{
	if (g_obsSettingsDialog && ShellCloseObsSettings())
		ShellActivate(ShellView::Settings);
}

// the helper asked for a page to go away (the settings page finishing its OK / Cancel, the wizard finishing)
void ShellCloseView(ShellView view)
{
	// OK and Cancel of the settings page have already saved or dropped the edits, and the clean flag from the page can arrive after this
	if (view == ShellView::Settings)
		g_settingsDirty = false;
	if (view == g_shellView && view != ShellView::Home)
		ShellActivate(ShellView::Home);
}

void ShellLeaveSettings(const std::string &target)
{
	g_settingsDirty = false;
	if (g_shellView != ShellView::Settings)
		return;
	const ShellView view = target == "projector" ? ShellView::Projector : target == "clips" ? ShellView::Clips : target == "setup" ? ShellView::Setup : ShellView::Home;
	ShellActivate(view);
}

// a clip started playing fullscreen (or stopped) in the clips view or in the recent clips of home: the browser covers the whole window while it plays
void ShellSetClipsFullscreen(bool active)
{
	// a page that just left, or one that never showed clips, must not pull the browser over another view
	if (active && !ShellShowsClips(g_shellView)) {
		blog(LOG_INFO, "[replaykit] clip fullscreen ignored, view %d shows no clips", int(g_shellView));
		return;
	}
	g_clipsFullscreenActive = active;
	if (g_shellOn) {
		// the window first, so the layout that follows already sees a monitor-sized window
		ShellNativeFullscreen(active);
		ShellLayoutViews();
		if (active)
			ShellRaiseHost();
		else
			ShellRaiseGrip();
		blog(LOG_INFO, "[replaykit] clip fullscreen %s, browser %dx%d", active ? "on" : "off", g_shellHost ? g_shellHost->width() : 0,
		     g_shellHost ? g_shellHost->height() : 0);
		// the window changed size and style under the browser, which can take the keyboard with it, so it is given back now and once more when the change has settled
		ShellFocusBrowser();
		QTimer::singleShot(150, g_callbacks, []() { ShellFocusBrowser(); });
	}
}

} // namespace rk
