// opens, toggles and focuses the clips window; the clean layout shows its clips view instead
#include "prelude.hpp"
#include "windows/clips_window.hpp"

#include "chrome/title_keybind_badge.hpp"
#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/cef_host.hpp"
#include "core/helper_client.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "notify/notify_button.hpp"
#include "ui/anchored_panel.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

// ui-thread only flag guarding against a second click opening a redundant window while the first ones background check is still talking to the helper
bool g_clipsCheckInFlight = false;

// CEF treats a parent QWidgets close event as a browser-close request even when Qt leaves the parent allocated. Ignore that event after hiding the window so the browser stays usable when a hotkey reopens Clips immediately afterward.
class ClipsWindow : public QWidget {
protected:
	void closeEvent(QCloseEvent *event) override
	{
		SaveClipsWindowGeometry(this);
		hide();
		AnchoredPanel::HideOwnedBy(this);
		event->ignore();
	}
	// minimize is treated exactly like close -- hide the window instead of leaving a taskbar stub, and drop the WindowMinimized bit so the next ShowClips/ToggleClips brings it back at its normal (or maximized) size rather than still-minimized; deferred one tick since clearing the state mid WindowStateChange fights the window managers own minimize animation.
	void changeEvent(QEvent *event) override
	{
		QWidget::changeEvent(event);
		if (event->type() == QEvent::WindowStateChange && isMinimized()) {
			QTimer::singleShot(0, this, [this]() {
				hide();
				AnchoredPanel::HideOwnedBy(this);
				setWindowState(windowState() & ~Qt::WindowMinimized);
			});
		}
	}
	void moveEvent(QMoveEvent *event) override
	{
		QWidget::moveEvent(event);
		ScheduleClipsGeometrySave();
	}
	void resizeEvent(QResizeEvent *event) override
	{
		QWidget::resizeEvent(event);
		ScheduleClipsGeometrySave();
	}
};

// clear a leftover WindowMinimized bit before showing so a Clips window that was minimized then hidden comes back at its real size instead of a taskbar stub; keeps WindowMaximized intact.
void UnminimizeClips()
{
	if (g_clipsWindow)
		g_clipsWindow->setWindowState(g_clipsWindow->windowState() & ~Qt::WindowMinimized);
}

// ui-thread only -- builds the actual window once we know theres no existing clips window to reuse and cef is already confirmed started via EnsureCefReadyBlocking
void CreateClipsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit] obs-browser is unavailable; cannot open clips");
		return;
	}

	QWidget *win = new ClipsWindow();
	// closing Clips only hides it. Keeping its CEF host alive avoids a deferred-delete race when a hotkey reopens it immediately after close, which could otherwise create a blank window or call Qt through a stale widget pointer.
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle("Clips");
	win->resize(1280, 800);
	win->setMinimumSize(850, 620);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	// nullptr cookie manager shares the same cef storage as every other obs-browser dock, keeping streamable sign-in and favorites consistent with the docked clips ui
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/clips-view", nullptr);
	layout->addWidget(browser);
	g_clipsBrowser = browser;
	g_clipsWindow = win;

	// restore the remembered position/size before the first show so it doesnt flash at the default spot.
	RestoreWindowGeometry("clipsWindow", win);

	ForceTaskbarButton(win);
	win->show();
	// retag BEFORE taking focus -- RefreshAppIcon rebuilds taskbar buttons across our windows (ITaskbarList DeleteTab/AddTab) and that drops the foreground state, so activating first left clips open but unfocused and pushed settings behind
	RefreshAppIcon(); // give the fresh window the current app icon + taskbar retag
	win->raise();
	win->activateWindow();
	AttachNotifyButton(win);
	AttachTitleKeybindBadge(win);
}

// true when the foreground window is clips or a window it owns (the confirm dialog, the bell panel); cef keeps focus on a child hwnd, and windows still reports the top-level clips window as the foreground one
bool ClipsWindowIsForeground()
{
	if (!g_clipsWindow)
		return false;
	HWND foreground = GetForegroundWindow();
	return foreground && GetAncestor(foreground, GA_ROOTOWNER) == (HWND)g_clipsWindow->winId();
}

// brings the already open clips window in front of whatever has focus. the keybind counts as input for this process, so the qt calls normally win the foreground; attaching to the input queue of the current foreground thread covers the times windows still refuses
void BringClipsForward()
{
	if (!g_clipsWindow)
		return;
	UnminimizeClips();
	g_clipsWindow->raise();
	g_clipsWindow->activateWindow();
	HWND clips = (HWND)g_clipsWindow->winId();
	if (GetForegroundWindow() != clips) {
		HWND foreground = GetForegroundWindow();
		DWORD foregroundThread = foreground ? GetWindowThreadProcessId(foreground, nullptr) : 0;
		DWORD thisThread = GetCurrentThreadId();
		bool attached = foregroundThread && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, TRUE);
		BringWindowToTop(clips);
		SetForegroundWindow(clips);
		if (attached)
			AttachThreadInput(thisThread, foregroundThread, FALSE);
	}
	blog(LOG_INFO, "[replaykit] clips keybind: window was open without focus, brought forward (%s)",
	     GetForegroundWindow() == clips ? "focused" : "windows kept the foreground elsewhere");
}

} // namespace

void ShowClips()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_shellOn) {
		ShellOpenView(ShellView::Clips, false);
		return;
	}
	if (g_clipsWindow) {
		if (!g_clipsWindow->isVisible() && g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		UnminimizeClips();
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	if (g_clipsCheckInFlight)
		return;
	g_clipsCheckInFlight = true;

	// checks /focus-window first since the docks own button or a leftover window may already have one open, then runs both blocking http calls off the ui thread and posts back thru queued invokeMethod since qwidget/qcefwidget arent thread-safe
	RunAsync([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=Clips", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[focused, cefReady]() {
				g_clipsCheckInFlight = false;
				if (!focused && cefReady)
					CreateClipsWindow();
			},
			Qt::QueuedConnection);
	});
}

void ToggleClips()
{
	if (g_shellOn) {
		ShellToggleClips();
		return;
	}
	if (g_clipsWindow) {
		if (g_clipsWindow->isVisible()) {
			// the keybind only closes a clips window that has focus; one that is open but behind something else is brought forward instead
			if (!ClipsWindowIsForeground()) {
				BringClipsForward();
				return;
			}
			SaveClipsWindowGeometry();
			g_clipsWindow->hide();
			AnchoredPanel::HideOwnedBy(g_clipsWindow);
			return;
		}
		if (g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		UnminimizeClips();
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	ShowClips();
}

} // namespace rk
