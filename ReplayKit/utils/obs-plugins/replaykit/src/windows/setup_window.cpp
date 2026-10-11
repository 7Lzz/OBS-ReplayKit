// opens the first-run setup window; the clean layout shows its setup view instead
#include "prelude.hpp"
#include "windows/setup_window.hpp"

#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/cef_host.hpp"
#include "core/state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "windows/cef_host_window.hpp"

namespace rk {

namespace {

bool g_setupCheckInFlight = false;

// twin of CreateSettingsWindow for the one-time first-run wizard. It gets the same native icon path so CEFs favicon cannot leave Setup visually different from Clips and Settings.
void CreateSetupWindow(bool quiet)
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit] obs-browser is unavailable; cannot open the setup wizard");
		return;
	}

	CefHostWindow *win = new CefHostWindow();
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle("ReplayKit Setup");
	win->resize(820, 640);
	win->setMinimumSize(640, 520);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/setup-view", nullptr);
	layout->addWidget(browser);
	win->browser = browser;

	ForceTaskbarButton(win);
	win->show();
	if (!quiet) {
		win->raise();
		win->activateWindow();
	}
	g_setupWindow = win;
	RefreshAppIcon();
	QTimer::singleShot(500, g_callbacks, []() { if (g_setupWindow) ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow"); });
	QTimer::singleShot(1600, g_callbacks, []() { if (g_setupWindow) ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow"); });
}

} // namespace

// quiet = the helper opened this by itself on a pending first run, so show it without pulling focus off obs as it starts up. a deliberate open (tray row, dock button) still raises and activates.
void ShowSetup(bool quiet)
{
	if (g_shellOn) {
		ShellOpenView(ShellView::Setup, quiet);
		return;
	}
	if (g_setupWindow) {
		g_setupWindow->show();
		if (!quiet) {
			g_setupWindow->raise();
			g_setupWindow->activateWindow();
		}
		return;
	}
	if (g_setupCheckInFlight)
		return;
	g_setupCheckInFlight = true;

	// nothing else ever opens this title, so no /focus-window pre-check like ShowSettings needs.
	RunAsync([quiet]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[cefReady, quiet]() {
				g_setupCheckInFlight = false;
				if (cefReady)
					CreateSetupWindow(quiet);
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
