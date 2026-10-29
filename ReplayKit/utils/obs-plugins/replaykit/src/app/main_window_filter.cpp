// close to tray, geometry saving and icon upkeep for the obs main window
#include "prelude.hpp"
#include "app/main_window_filter.hpp"

#include "core/app_icon.hpp"
#include "core/ipc_pipe.hpp"
#include "core/idle_memory.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "ui/anchored_panel.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

QPointer<QObject> g_mainWindowCloseFilter;

// turns the OBS main windows close (X) into hide-to-tray while g_closeToTray is on. narrowly scoped to the one QWidget (an app-wide filter caused a confirmed 2026-08-10 crash catching cef events mid-teardown). an ALLOWCLOSE over the ipc pipe from the helpers restart/exit routes, right before they post WM_CLOSE, opens a 60s window where the next close passes through so real quits + replaykit restarts still save geometry.
class MainWindowCloseFilter : public QObject, public QAbstractNativeEventFilter {
public:
	explicit MainWindowCloseFilter(QWidget *mw) : QObject(mw), m_mw(mw)
	{
		qApp->installNativeEventFilter(this);
	}
	~MainWindowCloseFilter() override
	{
		if (qApp)
			qApp->removeNativeEventFilter(this);
	}
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *) override
	{
		const MSG *msg = static_cast<const MSG *>(message);
		if (m_taskbarCreated && msg && msg->message == m_taskbarCreated && msg->hwnd == (HWND)m_mw->winId())
			ScheduleIconRefresh();
		return false;
	}

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_mw)
			return false;

		const QEvent::Type type = event->type();
		if (type == QEvent::Show || type == QEvent::Hide || type == QEvent::WindowStateChange)
			IdleMemory::WindowChanged(m_mw);
		if (type == QEvent::Move || type == QEvent::Resize) {
			ScheduleObsMainGeometrySave();
			return false; // observe only, never consume
		}
		if (type == QEvent::Show || type == QEvent::WindowStateChange) {
			// restoring from the tray rebuilds the taskbar button -- put our icon back on it
			ScheduleIconRefresh();
			return false;
		}
		// a clip playing fullscreen has the window stretched over its monitor, and obs would save that as the window position
		if (type == QEvent::Close && g_shellOn)
			ShellEndClipFullscreen();
		// an obs settings dialog folded into this window would be destroyed mid-exec by the close, exactly what its modal form prevents by blocking the window, so the dialog is asked to close first and the window stays put
		if (type == QEvent::Close && g_shellOn && g_obsSettingsDialog) {
			event->ignore();
			ShellActivate(ShellView::ObsSettings);
			if (g_obsSettingsDialog)
				g_obsSettingsDialog->close();
			return true;
		}
		if (type != QEvent::Close || !g_closeToTray)
			return false;

		if (GetTickCount64() < g_allowCloseUntilMs.load())
			return false; // a real restart/exit is in progress -- let obs close and save geometry

		QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
		if (!tray || !tray->isVisible())
			return false; // nothing to minimize into -- fall back to a normal close

		// EXIT wont fire on a hide-to-tray, so capture position now.
		SaveWindowGeometry("obsMainWindow", m_mw);
		event->ignore();
		if (m_mw) {
			// obs updates its tray Show/Hide row inside its own hide slot, which a plain hide() skips and leaves the row saying Hide for a window that is gone, and it has to be the toggle slot: SetShowing alone works from a list of dialogs that only the toggle refreshes, and that list held a deleted dialog after the folded in settings had been closed. an obs build without the slot gets the plain hide
			if (!ToggleMainWindowShowing())
				m_mw->hide();
			AnchoredPanel::HideOwnedBy(m_mw);
		}
		return true;
	}

private:
	void ScheduleIconRefresh()
	{
		if (m_iconRefreshQueued)
			return;
		m_iconRefreshQueued = true;
		QTimer::singleShot(0, this, [this]() { m_iconRefreshQueued = false; RefreshAppIcon(); });
	}
	const UINT m_taskbarCreated = RegisterWindowMessageW(L"TaskbarButtonCreated");
	bool m_iconRefreshQueued = false;
	QWidget *m_mw;
};

} // namespace

void InstallMainWindowCloseFilter()
{
	if (g_mainWindowCloseFilter)
		return;
	QWidget *mw = (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	// drop any ALLOWCLOSE window carried over from a previous sessions restart so it can never leak into this one.
	g_allowCloseUntilMs.store(0);
	g_mainWindow = mw;
	auto *filter = new MainWindowCloseFilter(mw);
	mw->installEventFilter(filter);
	g_mainWindowCloseFilter = filter;
	IdleMemory::WindowChanged(mw);

	// grab obss own icons now, while the main window is up and untouched, so a restore/aux-window path never finds them null.
	CaptureAppIconDefaults();

	// put the window back where it was last session. once here (obs has finished its own layout) and again shortly after, since qts platform code can still be repositioning the main window right after FINISHED_LOADING.
	RestoreWindowGeometry("obsMainWindow", mw);
	QTimer::singleShot(400, g_callbacks, []() {
		RestoreWindowGeometry("obsMainWindow", g_mainWindow);
		// capture a baseline even if the user never moves the window this session.
		ScheduleObsMainGeometrySave();
	});

	// slow safety-net save so a forced close cannot lose the last position -- see PollWindowGeometry.
	StartWindowGeometryPoll();
}

// the filter is parented to the main window, so it is already gone if the window was destroyed first; guard with the QPointer and only detach when both still exist.
void RemoveMainWindowCloseFilter()
{
	if (g_mainWindow && g_mainWindowCloseFilter)
		g_mainWindow->removeEventFilter(g_mainWindowCloseFilter);
	delete g_mainWindowCloseFilter.data();
}

} // namespace rk
