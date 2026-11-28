// opens the replaykit settings window; the clean layout shows its settings view instead
#include "prelude.hpp"
#include "windows/settings_window.hpp"

#include "app/always_on_top.hpp"
#include "chrome/title_keybind_badge.hpp"
#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/cef_host.hpp"
#include "core/helper_client.hpp"
#include "core/state.hpp"
#include "notify/notify_button.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "windows/cef_host_window.hpp"

namespace rk {

namespace {

bool g_settingsCheckInFlight = false;

// same shape as CreateClipsWindow -- title matches settings.htmls <title> and the /close-window whitelist, size matches the controls_app.html popup so it looks the same either way its opened
void CreateSettingsWindow()
{
	if (!CefOpen()) {
		blog(LOG_WARNING, "[replaykit] obs-browser is unavailable or shutting down; cannot open settings");
		return;
	}

	CefHostWindow *win = new CefHostWindow();
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle(kSettingsWindowTitle);
	win->resize(980, 760);
	win->setMinimumSize(kSettingsWindowMinWidth, kSettingsWindowMinHeight);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, HelperUrl("/settings-view"), nullptr);
	layout->addWidget(browser);
	win->browser = browser;

	ForceTaskbarButton(win);
	win->show();
	AlwaysOnTopFollow(win);
	g_settingsWindow = win;
	RefreshAppIcon(); // give the new window the current app icon + taskbar retag
	// focus last, for the same reason as CreateClipsWindow
	win->raise();
	win->activateWindow();
	AttachNotifyButton(win);
	AttachTitleKeybindBadge(win);
	// re-apply a couple times: winId()/the cef child arent fully realised on the first pass, and a slow RKICON over the pipe can land after this. cheap -- the tag guard skips the taskbar rebuild on the repeats.
	QTimer::singleShot(500, g_callbacks, []() { if (g_settingsWindow) ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow"); });
	QTimer::singleShot(1600, g_callbacks, []() { if (g_settingsWindow) ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow"); });
}

} // namespace

void ShowSettings()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_shellOn) {
		ShellOpenView(ShellView::Settings, false);
		return;
	}
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
	RunAsync([]() {
		const QByteArray path = "/focus-window?title=" + QUrl::toPercentEncoding(QString::fromLatin1(kSettingsWindowTitle));
		bool focused = HttpFocusWindowSucceeded(path.constData(), 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[focused, cefReady]() {
				g_settingsCheckInFlight = false;
				if (!focused && cefReady)
					CreateSettingsWindow();
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
