// native tray plugin adding "view clips" to obss tray menu since scripting cant reach the tray icon -- shares obs-browsers cef panel (same cookies/session as the docked clips ui) and stays unparented from the main window so "minimize to tray" cant hide it too

#include <obs-module.h>
#include <obs-frontend-api.h>

#include <QCoreApplication>
#include <QMenu>
#include <QAction>
#include <QSystemTrayIcon>
#include <QList>
#include <QObject>
#include <QString>
#include <QWidget>
#include <QVBoxLayout>
#include <QPointer>
#include <QTimer>
#include <QElapsedTimer>
#include <QEvent>
#include <QMouseEvent>
#include <QMessageBox>
#include <QDesktopServices>
#include <QUrl>
#include <QDir>
#include <QGuiApplication>
#include <QScreen>
#include <QWindow>
#include <QSaveFile>
#include <QTextStream>

#include "browser-panel.hpp"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <string>
#include <cstring>
#include <thread>

OBS_DECLARE_MODULE()

namespace {

QCef *g_cef = nullptr;
bool g_cefInitTried = false;
QPointer<QWidget> g_clipsWindow;
QPointer<QWidget> g_settingsWindow;
QPointer<QWidget> g_prewarmWindow;
// ui-thread only flag guarding against a second click opening a redundant window while the first ones background check is still talking to the helper
bool g_clipsCheckInFlight = false;
bool g_settingsCheckInFlight = false;

// share previews checked state is re-read from the helper every time the menu opens, since obs only builds this menu once
QPointer<QAction> g_sharePreviewAction;


// hidden, not removed, each time the menu opens -- see OnFrontendEvent for why removeAction was the wrong tool here
QPointer<QAction> g_previewProjectorAction;
QPointer<QAction> g_programProjectorAction;

// obss own native record/replay-buffer actions, stayed visible and never removed -- renamed and re-triggered in place (see RefreshActionRowText/ToggleRecording/ToggleReplayBuffer) rather than hidden behind a custom widget, since native tray menus render these directly and a custom stand-in could never reliably match that alignment.
QPointer<QAction> g_nativeRecordAction;
QPointer<QAction> g_nativeReplayBufferAction;

// menu opens upward from the cursor so restart obs lands right where an accidental double-clicks second click hits -- every action here bails if triggered within kMenuClickDebounceMs of aboutToShow
constexpr qint64 kMenuClickDebounceMs = 150;
QElapsedTimer g_menuShownTimer;

// background-thread only, can block for real time on a cold start -- waits for cefs background thread so widgets never hit the "first open only" white window race from being created before cef is ready
bool EnsureCefReadyBlocking()
{
	if (!g_cefInitTried) {
		g_cefInitTried = true;
		g_cef = obs_browser_init_panel();
	}
	if (!g_cef)
		return false;
	g_cef->init_browser();
	return g_cef->wait_for_browser_init();
}

// blocking http get to the replaykit helper, worker-thread only since it can hold for up to timeoutMs -- plain winsock to avoid linking qt6network, and checks the json body since /focus-window always returns 200 even when it found nothing
bool HttpFocusWindowSucceeded(const char *path, int port, int timeoutMs)
{
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		return false;

	bool focused = false;
	SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock != INVALID_SOCKET) {
		DWORD timeout = (DWORD)timeoutMs;
		setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
		setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		if (connect(sock, (sockaddr *)&addr, sizeof(addr)) == 0) {
			std::string req = std::string("GET ") + path +
					   " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
			if (send(sock, req.c_str(), (int)req.size(), 0) > 0) {
				char buf[512];
				int n = recv(sock, buf, sizeof(buf) - 1, 0);
				if (n > 0) {
					buf[n] = 0;
					focused = strstr(buf, "\"focused\":true") != nullptr;
				}
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return focused;
}

// same raw-socket approach as above but generalised to any method/path/body -- callers substring-search the small, known-shape response (JsonBoolField) instead of pulling in a real json parser for two fields
std::string HttpRequest(const char *method, const char *path, int port, const char *jsonBody, int timeoutMs)
{
	std::string response;
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		return response;

	SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock != INVALID_SOCKET) {
		DWORD timeout = (DWORD)timeoutMs;
		setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
		setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		if (connect(sock, (sockaddr *)&addr, sizeof(addr)) == 0) {
			std::string body = jsonBody ? jsonBody : "";
			// the helper only accepts same-origin requests on trusted routes -- pretend to be the dock.
			std::string req = std::string(method) + " " + path + " HTTP/1.1\r\n" + "Host: 127.0.0.1\r\n" +
					   "Origin: http://127.0.0.1:" + std::to_string(port) + "\r\n";
			if (!body.empty()) {
				req += "Content-Type: application/json\r\n";
				req += "Content-Length: " + std::to_string(body.size()) + "\r\n";
			}
			req += "Connection: close\r\n\r\n" + body;
			if (send(sock, req.c_str(), (int)req.size(), 0) > 0) {
				char buf[2048];
				int n;
				while ((n = recv(sock, buf, sizeof(buf), 0)) > 0)
					response.append(buf, n);
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return response;
}

bool JsonBoolField(const std::string &body, const char *field, bool defaultValue)
{
	if (body.find(std::string("\"") + field + "\":true") != std::string::npos)
		return true;
	if (body.find(std::string("\"") + field + "\":false") != std::string::npos)
		return false;
	return defaultValue;
}

// ui-thread only -- builds the actual window once we know theres no existing clips window to reuse and cef is already confirmed started via EnsureCefReadyBlocking
void CreateClipsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open clips");
		return;
	}

	QWidget *win = new QWidget(nullptr);
	win->setAttribute(Qt::WA_DeleteOnClose);
	win->setWindowTitle("Clips");
	win->resize(1280, 800);
	win->setMinimumSize(850, 620);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	// nullptr cookie manager shares the same cef storage as every other obs-browser dock, keeping streamable sign-in and favorites consistent with the docked clips ui
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/clips-view", nullptr);
	layout->addWidget(browser);

	win->show();
	win->raise();
	win->activateWindow();
	g_clipsWindow = win;
}

// same shape as CreateClipsWindow -- title matches settings.htmls <title> and the /close-window whitelist, size matches the controls_app.html popup so it looks the same either way its opened
void CreateSettingsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open settings");
		return;
	}

	QWidget *win = new QWidget(nullptr);
	win->setAttribute(Qt::WA_DeleteOnClose);
	win->setWindowTitle("ReplayKit Settings");
	win->resize(980, 760);
	// derived from settings.htmls own grid, not guessed: .shell is a fixed 190px sidebar + flexible main column inside 18px of shell padding, .main adds another 10px right padding, and the widest row inside it (.form-row) needs 180px label + 8px gap + a 260px-minimum field (grid-template-columns:180px minmax(260px,1fr)) to avoid wrapping. 190+18+10+180+8+260 = 666px is the point a form row would start fighting for space; 700 gives a small margin. height has no equivalent hard floor (.main scrolls vertically), 500 is just enough to show a few rows without feeling cramped.
	win->setMinimumSize(700, 500);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/settings-view", nullptr);
	layout->addWidget(browser);

	win->show();
	win->raise();
	win->activateWindow();
	g_settingsWindow = win;
}

void ShowSettings()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_settingsWindow) {
		g_settingsWindow->show();
		g_settingsWindow->raise();
		g_settingsWindow->activateWindow();
		return;
	}
	if (g_settingsCheckInFlight)
		return;
	g_settingsCheckInFlight = true;

	// same reasoning as ShowClips -- the docks own settings button opens a window.open() popup this cant see directly, so check for it by title before opening a second one
	std::thread([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=ReplayKit%20Settings", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[focused, cefReady]() {
				g_settingsCheckInFlight = false;
				if (!focused && cefReady)
					CreateSettingsWindow();
			},
			Qt::QueuedConnection);
	}).detach();
}

void ShowClips()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_clipsWindow) {
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	if (g_clipsCheckInFlight)
		return;
	g_clipsCheckInFlight = true;

	// checks /focus-window first since the docks own button or a leftover window may already have one open, then runs both blocking http calls off the ui thread and posts back thru queued invokeMethod since qwidget/qcefwidget arent thread-safe
	std::thread([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=Clips", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[focused, cefReady]() {
				g_clipsCheckInFlight = false;
				if (!focused && cefReady)
					CreateClipsWindow();
			},
			Qt::QueuedConnection);
	}).detach();
}

// posts to the same /share-preview route the dock uses so the projector/audio-monitoring logic stays in one place -- fire and forget, since aboutToShow re-reads the real state next open so a failed toggle just looks unchanged
void ToggleSharePreview(bool checked)
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	std::thread([checked]() {
		std::string json = checked ? "{\"enabled\":true}" : "{\"enabled\":false}";
		std::string body = HttpRequest("POST", "/share-preview", 8767, json.c_str(), 8000);
		bool enabled = JsonBoolField(body, "enabled", !checked);
		QMetaObject::invokeMethod(
			qApp,
			[enabled]() {
				if (g_sharePreviewAction)
					g_sharePreviewAction->setChecked(enabled);
			},
			Qt::QueuedConnection);
	}).detach();
}

// posts to the helpers /restart-obs, a plain close-and-reopen (not the settings pages signed-out "clean" restart) -- confirms first since it force-kills obs rather than stopping gracefully, then fires and forgets since obs is about to die anyway
void RestartObs()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	QMessageBox::StandardButton reply = QMessageBox::question(
		nullptr, QObject::tr("Restart OBS"),
		QObject::tr("This will close and reopen OBS. Any active recording or stream will be stopped. Continue?"),
		QMessageBox::Yes | QMessageBox::No, QMessageBox::No);
	if (reply != QMessageBox::Yes)
		return;
	std::thread([]() { HttpRequest("POST", "/restart-obs", 8767, nullptr, 5000); }).detach();
}

// opens obss fixed crash-report folder directly in explorer rather than guessing "the latest one", mkpath first since a machine that never crashed wont have the folder yet
void OpenCrashLogsFolder()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	QString path = qEnvironmentVariable("APPDATA") + "/obs-studio/crashes";
	QDir().mkpath(path);
	QDesktopServices::openUrl(QUrl::fromLocalFile(path));
}

// obss own exit never confirmed and sits right where an upward-opening menu leaves the cursor, an easy accidental click the debounce cant catch since its aimed right, not just early -- same confirm-first shape as RestartObs. posts to the helpers /exit-obs instead of calling mainWindow->close() directly so this gets the same graceful-close-then-force-kill-if-needed handling restart already gets (see Stop-ReplayKitObsForRestart), which is what actually lets obs save window position/state on the way out.
void ConfirmedExit()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	QMessageBox::StandardButton reply = QMessageBox::question(
		nullptr, QObject::tr("Exit OBS"),
		QObject::tr("This will close OBS. Any active recording or stream will be stopped. Continue?"),
		QMessageBox::Yes | QMessageBox::No, QMessageBox::No);
	if (reply != QMessageBox::Yes)
		return;
	std::thread([]() { HttpRequest("POST", "/exit-obs", 8767, nullptr, 5000); }).detach();
}

// invisible throwaway browser to eat the first-ever cef surfaces one-time cost -- the real clips window paints only a corner and stays white the first time each obs session, and this burns that bad first attempt off-screen instead of chasing the exact cef-internal cause
bool g_cefPrewarmed = false;

void PrewarmCefBrowser()
{
	if (g_cefPrewarmed)
		return;
	g_cefPrewarmed = true;

	std::thread([]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[cefReady]() {
				if (!cefReady || !g_cef)
					return;

				QWidget *warmWin = new QWidget(nullptr);
				warmWin->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint);
				warmWin->move(-10000, -10000);
				warmWin->resize(64, 64);

				QVBoxLayout *layout = new QVBoxLayout(warmWin);
				layout->setContentsMargins(0, 0, 0, 0);
				QCefWidget *browser = g_cef->create_widget(warmWin, "about:blank", nullptr);
				layout->addWidget(browser);

				warmWin->show();
				g_prewarmWindow = warmWin;
				QTimer::singleShot(3000, warmWin, &QObject::deleteLater);
			},
			Qt::QueuedConnection);
	}).detach();
}

// synchronously deletes our cef widgets before obs_module_unload, mirroring how OBSBasic.cpp deletes extraBrowsers early, to dodge upstream race obsproject/obs-browser#353 where cef can still be mid-teardown when the browser-manager thread is joined -- direct delete not deleteLater becuase a deferred one wouldnt run before applicationShutdown anyway
void CloseCefWidgetsBeforeShutdown()
{
	if (g_clipsWindow)
		delete g_clipsWindow.data();
	if (g_settingsWindow)
		delete g_settingsWindow.data();
	if (g_prewarmWindow)
		delete g_prewarmWindow.data();
}

// blocks right-click activate (obss tray menu allows it like a native context menu) and fast-double-click debounce bypass for every item, including obss own unguarded exit -- scoped to the menu itself, not qApp, since an app-wide filter caused a confirmed 2026-08-10 crash by also catching cef widget events mid-teardown, and confirmed 2026-08-12 that a fast double-click delivers its second click as QEvent::MouseButtonDblClick, not a second press/release, which an earlier version never checked for
class TrayMenuGuard : public QObject {
public:
	explicit TrayMenuGuard(QMenu *menu) : QObject(menu), m_menu(menu) { menu->installEventFilter(this); }

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_menu)
			return false;
		const bool isClick = event->type() == QEvent::MouseButtonPress || event->type() == QEvent::MouseButtonRelease ||
				      event->type() == QEvent::MouseButtonDblClick;
		if (!isClick)
			return false;
		if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
			return true;
		return static_cast<QMouseEvent *>(event)->button() == Qt::RightButton;
	}

private:
	QMenu *m_menu;
};

// obss own isEnabled() on sysTrayRecord/sysTrayReplayBuffer cannot be trusted (confirmed via logging: both reported enabled=0 at click time despite obs_frontend_..._active() proving the underlying feature was genuinely toggleable -- obs only calls setEnabled on sysTrayReplayBuffer from inside ResetOutputs, gated on whether the output handler happened to already have a replay buffer object at that exact moment, never re-enabled later, and sysTrayRecord has no setEnabled call anywhere in obs at all), so these drive the toggle directly through the same stable public entry points obs itself and every obs-websocket-style integration use, instead of routing through the actions own (occasionally-lying) enabled/triggered state.
void ToggleRecording()
{
	bool active = obs_frontend_recording_active();
	blog(LOG_INFO, "[replaykit-tray] ToggleRecording: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_recording_stop() : obs_frontend_recording_start();
}

void ToggleReplayBuffer()
{
	bool active = obs_frontend_replay_buffer_active();
	blog(LOG_INFO, "[replaykit-tray] ToggleReplayBuffer: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_replay_buffer_stop() : obs_frontend_replay_buffer_start();
}

// a QWidgetAction-based custom row (colored keybind badge) was tried here and reverted -- confirmed via qt forum threads that native platform tray menus (which is what these actions render through) do not properly support QWidgetAction at all, which tracks with alignment never converging no matter the margin. plain native QAction text is what actually renders through the same layout pass as every sibling item, guaranteeing correct alignment; the bracketed keybind after the tab is qts own shortcut-hint convention (same mechanism every menus "Ctrl+S" style hint uses), not a real functional shortcut binding.
void RefreshActionRowText()
{
	if (g_nativeRecordAction)
		g_nativeRecordAction->setText(obs_frontend_recording_active() ? "Stop Recording" : "Start Recording");
	if (g_nativeReplayBufferAction)
		g_nativeReplayBufferAction->setText(obs_frontend_replay_buffer_active() ? "Stop Clipping" : "Start Clipping");
}

// obs builds the tray menu once and never rebuilds it, so this refreshes the share-preview checkbox and the record/clipping rename right before each show instead of polling on a timer nobody is watching. keybind text next to the rename (fetched from the helpers /settings) was tried and pulled back out -- it pushed the menu wide enough to run off the right edge of the screen, and the only way to show it any smaller/greyer needs a custom widget, which is the same QWidgetAction approach that turned out not to align with native items at all (see the comment on RefreshActionRowText). revisit that non-native-rendering path later; for now this stays plain rename only.
void RefreshDynamicMenuState()
{
	RefreshActionRowText();
	if (!g_sharePreviewAction)
		return;
	std::thread([]() {
		std::string getBody = HttpRequest("GET", "/share-preview", 8767, nullptr, 500);
		bool available = JsonBoolField(getBody, "available", false);
		bool enabled = JsonBoolField(getBody, "enabled", false);
		QMetaObject::invokeMethod(
			qApp,
			[available, enabled]() {
				if (!g_sharePreviewAction)
					return;
				g_sharePreviewAction->setEnabled(available);
				g_sharePreviewAction->setChecked(enabled);
			},
			Qt::QueuedConnection);
	}).detach();
}

// pins the menus bottom edge to the taskbar top since windows qt lets popups cover the taskbar instead of avoiding it like gnome/macos do (y), and separately pulls the right edge back onto the screen if the row width -- wider now that record/clipping show a keybind hint -- would otherwise push it off the right side (x); x is otherwise left wherever windows anchored it, never pushed further right than that. skips the move() entirely when already at the target position -- re-positioning an already-visible native popup is a plausible cause of a reported "clicking outside the menu doesnt close it" bug (moving a shown popups hwnd can desync qts click-outside-to-dismiss tracking on windows), so this only touches geometry when it actually needs correcting.
void PinMenuAboveTaskbar(QMenu *menu)
{
	QScreen *screen = QGuiApplication::screenAt(menu->pos());
	if (!screen)
		return;
	QRect avail = screen->availableGeometry();
	int taskbarTop = avail.bottom() + 1;
	int menuHeight = menu->sizeHint().height();
	int targetY = qMax(0, taskbarTop - menuHeight);
	int menuWidth = menu->sizeHint().width();
	int targetX = qMin(menu->pos().x(), qMax(avail.left(), avail.right() + 1 - menuWidth));
	if (menu->pos().x() == targetX && menu->pos().y() == targetY)
		return;
	menu->move(targetX, targetY);
}

// obs marks every real projector window with windowHandle()->setProperty("isOBSProjectorWindow", true) -- see OBSProjector.cpp, which does this specifically so obss own code (SetDisplayAffinity) can recognize one reliably. thats a qt object property, invisible to plain win32 window enumeration, so the replaykit helper (a separate powershell process with no qt/obs-object access, only raw EnumWindows/GetClassName-style calls) has no way to read it directly -- this plugin does, since it runs inside obss own qt process. publishing the current set of hwnds carrying that property to a file lets the helper check the SAME authoritative signal obs uses internally instead of only inferring "looks like a projector" from window class + ownership heuristics, which turned out to have real false-positive risk (many of obss own dialogs -- NameDialog, the Scripts window, the auto-config wizard, etc. -- are independently-owned top-level windows too, just not projectors).
QString ProjectorHandoffDir()
{
	return qEnvironmentVariable("TEMP") + "/ReplayKit/scratch";
}

QString ProjectorHandoffPath()
{
	return ProjectorHandoffDir() + "/obsreplaykit_projector_windows.txt";
}

QString MainWindowHandoffPath()
{
	return ProjectorHandoffDir() + "/obsreplaykit_main_window.txt";
}

// obs_frontend_get_main_window() is the only language-neutral way to identify obss real main window -- its title is a localized template ("<version> - profile: ... - scenes: ...") with no fixed substring a non-english build still renders, which is what broke the helpers old title-matching close. published once, not on the projector timer, since the handle never changes for the life of the process.
void PublishMainWindow()
{
	QWidget *mainWindow = (QWidget *)obs_frontend_get_main_window();
	if (!mainWindow)
		return;

	QDir().mkpath(ProjectorHandoffDir());
	QSaveFile file(MainWindowHandoffPath());
	if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
		return;
	QTextStream out(&file);
	out << QString::number((quintptr)mainWindow->winId());
	file.commit();
}

QTimer *g_projectorPublishTimer = nullptr;

// QSaveFile writes to a temp file and atomically renames it into place on commit(), so a reader on the other process never sees a half-written file -- plain QFile writing in place could get caught mid-write by the helpers poll.
void PublishProjectorWindows()
{
	QStringList hwnds;
	for (QWindow *w : QGuiApplication::topLevelWindows()) {
		if (w && w->property("isOBSProjectorWindow").toBool())
			hwnds << QString::number((quintptr)w->winId());
	}

	QDir().mkpath(ProjectorHandoffDir());
	QSaveFile file(ProjectorHandoffPath());
	if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
		return;
	QTextStream out(&file);
	for (const QString &h : hwnds)
		out << h << '\n';
	file.commit();
}

void OnFrontendEvent(enum obs_frontend_event event, void *)
{
	if (event == OBS_FRONTEND_EVENT_EXIT) {
		if (g_projectorPublishTimer)
			g_projectorPublishTimer->stop();
		CloseCefWidgetsBeforeShutdown();
		return;
	}

	if (event != OBS_FRONTEND_EVENT_FINISHED_LOADING)
		return;

	PrewarmCefBrowser();
	PublishMainWindow();

	// 250ms matches the replaykit helpers own poll cadence when its waiting for a projector to appear -- no benefit publishing faster than the one consumer of this file actually checks it.
	g_projectorPublishTimer = new QTimer(qApp);
	QObject::connect(g_projectorPublishTimer, &QTimer::timeout, qApp, []() { PublishProjectorWindows(); });
	g_projectorPublishTimer->start(250);
	PublishProjectorWindows();

	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	if (!tray)
		return;
	QMenu *trayMenu = tray->contextMenu();
	if (!trayMenu)
		return;

	new TrayMenuGuard(trayMenu);

	// obs builds this menu once in SystemTrayInit() and never rebuilds the top level (only the projector submenus contents refresh per click), so anything hidden below stays hidden without needing to be redone on every open
	QAction *viewClips = new QAction(QObject::tr("View Clips"), trayMenu);
	QObject::connect(viewClips, &QAction::triggered, trayMenu, []() { ShowClips(); });

	QAction *sharePreview = new QAction(QObject::tr("Share Preview"), trayMenu);
	sharePreview->setCheckable(true);
	QObject::connect(sharePreview, &QAction::triggered, trayMenu, [](bool checked) { ToggleSharePreview(checked); });
	g_sharePreviewAction = sharePreview;

	QAction *customSettings = new QAction(QObject::tr("Custom Settings"), trayMenu);
	QObject::connect(customSettings, &QAction::triggered, trayMenu, []() { ShowSettings(); });

	QObject::connect(trayMenu, &QMenu::aboutToShow, trayMenu, [trayMenu]() {
		g_menuShownTimer.start();
		RefreshDynamicMenuState();
		// belt-and-suspenders re-hide on top of the one-time setVisible(false) below -- free today since obss updateSysTrayProjectorMenu() doesnt touch these actions visibility, but it runs every click so worth guarding anyway
		if (g_previewProjectorAction) g_previewProjectorAction->setVisible(false);
		if (g_programProjectorAction) g_programProjectorAction->setVisible(false);

		// confirmed 2026-08-16 this alone didnt stick since qts native platform code can finish positioning the menu after aboutToShow returns and silently override an early move() -- kept it anyway (free if it does nothing) and queued a second attempt after native show finishes as the standard workaround. bumped 10ms to 50ms to give qts own positioning more headroom to finish first -- see the comment on PinMenuAboveTaskbar for why an unnecessary post-show move is suspected in a "clicking outside doesnt close the menu" report.
		PinMenuAboveTaskbar(trayMenu);
		QTimer::singleShot(50, trayMenu, [trayMenu]() { PinMenuAboveTaskbar(trayMenu); });
	});

	// lands right after "hide", before obss first separator, so it reads as part of the window-visibility group instead of mixed into streaming/recording actions
	QList<QAction *> actions = trayMenu->actions();
	QAction *before = actions.size() > 1 ? actions.at(1) : nullptr;
	trayMenu->insertAction(before, viewClips);
	trayMenu->insertAction(before, sharePreview);
	trayMenu->insertAction(before, customSettings);

	// obs localizes every one of these labels, so matching them by text (the original approach) silently found none of the three on non-english obs -- exit stays last regardless of language since qt tr() only changes the rendered string, not the action-insertion order SystemTrayInit runs in, and the projector entries are the only submenu-bearing actions in this menu, always preview before program for the same reason. setVisible(false) not removeAction(): a first attempt used removeAction() and shipped a suspected obs freeze on the first right-click; setVisible(false) on menuAction() is the qt-documented-correct way to hide a submenu entry without detaching it from trayMenu or disturbing obss own updateSysTrayProjectorMenu() rebuilds.
	QList<QAction *> trayActions = trayMenu->actions();
	QAction *exitAction = nullptr;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (!(*it)->isSeparator()) {
			exitAction = *it;
			break;
		}
	}
	QList<QAction *> projectorSubmenuActions;
	for (QAction *action : trayActions) {
		if (action != exitAction && action->menu())
			projectorSubmenuActions << action;
	}
	if (projectorSubmenuActions.size() >= 1) {
		g_previewProjectorAction = projectorSubmenuActions.at(0);
		g_previewProjectorAction->setVisible(false);
	}
	if (projectorSubmenuActions.size() >= 2) {
		g_programProjectorAction = projectorSubmenuActions.at(1);
		g_programProjectorAction->setVisible(false);
	}

	// obs always builds {Stream, Record, ReplayBuffer, VirtualCam} as one separator-delimited group immediately before Exit (OBSBasic::SystemTrayInit adds them in this fixed order every time), so walking backward from Exit to the next separator finds Record/ReplayBuffer by position -- locale-independent, unlike the text-matching that silently found nothing on non-english obs (see the projector-matching note above). size()==4 is a sanity check, not a guess: if some future obs version changes this layout, skip the customization entirely rather than misattribute the wrong action to record/clipping.
	QList<QAction *> streamGroup;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (*it == exitAction)
			continue;
		if ((*it)->isSeparator()) {
			if (!streamGroup.isEmpty())
				break;
			continue;
		}
		streamGroup.prepend(*it);
	}
	blog(LOG_INFO, "[replaykit-tray] stream group structural lookup found %d actions (expected 4: Stream, Record, ReplayBuffer, VirtualCam)",
	     (int)streamGroup.size());
	if (streamGroup.size() == 4) {
		g_nativeRecordAction = streamGroup.at(1);
		g_nativeReplayBufferAction = streamGroup.at(2);
		blog(LOG_INFO, "[replaykit-tray] record action: text=\"%s\" enabled=%d", g_nativeRecordAction->text().toUtf8().constData(),
		     g_nativeRecordAction->isEnabled());
		blog(LOG_INFO, "[replaykit-tray] replay-buffer action: text=\"%s\" enabled=%d",
		     g_nativeReplayBufferAction->text().toUtf8().constData(), g_nativeReplayBufferAction->isEnabled());

		// force-enabled since isEnabled() cant be trusted (see ToggleRecording/ToggleReplayBuffer above); disconnect(receiver=nullptr) drops obss own RecordActionTriggered/ReplayBufferActionTriggered listener (same qt-documented technique already used for exitAction below) so ours -- which does not depend on that same untrustworthy enabled state -- is the only one left, rather than risking both firing.
		g_nativeRecordAction->setEnabled(true);
		QObject::disconnect(g_nativeRecordAction, &QAction::triggered, nullptr, nullptr);
		QObject::connect(g_nativeRecordAction, &QAction::triggered, trayMenu, []() { ToggleRecording(); });

		g_nativeReplayBufferAction->setEnabled(true);
		QObject::disconnect(g_nativeReplayBufferAction, &QAction::triggered, nullptr, nullptr);
		QObject::connect(g_nativeReplayBufferAction, &QAction::triggered, trayMenu, []() { ToggleReplayBuffer(); });

		RefreshActionRowText();
	}

	// obs wires exit straight to close() with no confirmation, an easy accidental target at the cursors landing spot -- disconnect(receiver=nullptr) drops obss own listener (qts documented way to do so) so we can reconnect our confirm-first version instead of leaving two competing handlers
	if (exitAction) {
		QObject::disconnect(exitAction, &QAction::triggered, nullptr, nullptr);
		QObject::connect(exitAction, &QAction::triggered, trayMenu, []() { ConfirmedExit(); });
	}
	QAction *restartObs = new QAction(QObject::tr("Restart OBS"), trayMenu);
	QObject::connect(restartObs, &QAction::triggered, trayMenu, []() { RestartObs(); });
	QAction *viewCrashLogs = new QAction(QObject::tr("View Crash Logs"), trayMenu);
	QObject::connect(viewCrashLogs, &QAction::triggered, trayMenu, []() { OpenCrashLogsFolder(); });
	trayMenu->insertSeparator(exitAction);
	trayMenu->insertAction(exitAction, restartObs);
	trayMenu->insertAction(exitAction, viewCrashLogs);
}

} // namespace

bool obs_module_load(void)
{
	obs_frontend_add_event_callback(OnFrontendEvent, nullptr);
	return true;
}

void obs_module_unload(void) {}
