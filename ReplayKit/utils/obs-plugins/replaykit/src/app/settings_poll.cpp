// applies the one second settings poll from the helper
#include "prelude.hpp"
#include "app/settings_poll.hpp"

#include "app/sdk_commands.hpp"
#include "chrome/title_keybind_badge.hpp"
#include "core/async.hpp"
#include "core/dev_log.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/open_clips_hotkey.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "core/window_state.hpp"
#include "layout/ui_layout.hpp"
#include "notify/notify_button.hpp"
#include "notify/notify_panel.hpp"
#include "ui/anchored_panel.hpp"
#include "shell/shell.hpp"
#include "shell/shell_layout.hpp"

namespace rk {

namespace {

bool g_openClipsHotkeyRequestInFlight = false;
bool g_startMinSeen = false;
// what the switch of obs held when it was last looked at, so a flip made in the settings of obs is told apart from a change made in replaykit
bool g_startMinLastObs = false;
// set from the moment the switch was flipped in obs until the helper shows the same value
bool g_startMinPushing = false;

// the bells follow the appearance theme: --grey5 / --grey3 come back on the same /settings body the hotkey poll already fetches, so this costs no extra request and lands within a second of a theme change.
void ApplyNotifyThemeColors(const std::string &settingsBody)
{
	std::string menuColors = ExtractJsonObjectField(settingsBody, "menuColors");
	if (menuColors.empty())
		return;
	QColor surface(QString::fromStdString(ExtractJsonStringField(menuColors, "surface")));
	QColor border(QString::fromStdString(ExtractJsonStringField(menuColors, "border")));
	QColor tip(QString::fromStdString(ExtractJsonStringField(menuColors, "tip")));
	QColor tipInk(QString::fromStdString(ExtractJsonStringField(menuColors, "text")));
	QColor accent(QString::fromStdString(ExtractJsonStringField(menuColors, "accent")));
	QColor accentLight(QString::fromStdString(ExtractJsonStringField(menuColors, "accentLight")));
	QColor onAccent(QString::fromStdString(ExtractJsonStringField(menuColors, "onAccent")));
	QColor mPanel(QString::fromStdString(ExtractJsonStringField(menuColors, "panel")));
	QColor mField(QString::fromStdString(ExtractJsonStringField(menuColors, "field")));
	QColor mFieldHover(QString::fromStdString(ExtractJsonStringField(menuColors, "fieldHover")));
	QColor mBorderStrong(QString::fromStdString(ExtractJsonStringField(menuColors, "borderStrong")));
	QColor mDanger(QString::fromStdString(ExtractJsonStringField(menuColors, "danger")));
	QColor mMuted(QString::fromStdString(ExtractJsonStringField(menuColors, "muted")));
	if (accent.isValid()) g_menuAccent = accent;
	if (accentLight.isValid()) g_menuAccentLight = accentLight;
	if (onAccent.isValid()) g_menuOnAccent = onAccent;
	if (mPanel.isValid()) g_menuPanel = mPanel;
	if (mField.isValid()) g_menuField = mField;
	if (mFieldHover.isValid()) g_menuFieldHover = mFieldHover;
	if (mBorderStrong.isValid()) g_menuBorderStrong = mBorderStrong;
	if (mDanger.isValid()) g_menuDanger = mDanger;
	if (mMuted.isValid()) g_menuMuted = mMuted;
	bool changed = false;
	QColor chromeBg(QString::fromStdString(ExtractJsonStringField(menuColors, "bg")));
	QColor chromeBar(QString::fromStdString(ExtractJsonStringField(menuColors, "bar")));
	QColor chromeSuccess(QString::fromStdString(ExtractJsonStringField(menuColors, "success")));
	QColor chromeWarning(QString::fromStdString(ExtractJsonStringField(menuColors, "warning")));
	if (chromeWarning.isValid()) g_chromeWarning = chromeWarning;
	if (chromeBg.isValid() && chromeBg != g_chromeBg) {
		g_chromeBg = chromeBg;
		changed = true;
	}
	if (chromeBar.isValid() && chromeBar != g_chromeBar) {
		g_chromeBar = chromeBar;
		changed = true;
	}
	if (chromeSuccess.isValid() && chromeSuccess != g_chromeSuccess) {
		g_chromeSuccess = chromeSuccess;
		changed = true;
	}
	if (tip.isValid() && tip != g_notifyTipBg) {
		g_notifyTipBg = tip;
		changed = true;
	}
	if (tipInk.isValid() && tipInk != g_notifyTipFg) {
		g_notifyTipFg = tipInk;
		changed = true;
	}
	if (surface.isValid() && surface != g_notifySurface) {
		g_notifySurface = surface;
		changed = true;
	}
	if (border.isValid() && border != g_notifyBorder) {
		g_notifyBorder = border;
		changed = true;
	}
	if (!changed)
		return;
	AnchoredPanel::SetGround(g_notifySurface);
	RefreshNotifyBadges();
	ShellRepaintChrome();
	// the shell paints its very first frame before the helper has answered, so the colours it cannot do without are kept from the last run
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	ini.setValue("chromeBg", g_chromeBg.name());
	ini.setValue("chromeBar", g_chromeBar.name());
	ini.setValue("chromeInk", g_notifyTipFg.name());
}

void WriteObsStartMinimized(config_t *config, bool on)
{
	config_set_bool(config, "BasicWindow", "SysTrayWhenStarted", on);
	config_save_safe(config, "tmp", nullptr);
	g_startMinLastObs = on;
	DevLog("start minimized: obs set to %s", on ? "on" : "off");
}

// obs decides on its own whether it starts in the tray, from a switch in its system tray settings; replaykit shows the same choice, so a change on either side is carried to the other, and the saved choice of replaykit wins on the first look
void SyncStartMinimized(const std::string &settingsBody)
{
	// an answer without the field means the helper did not answer, and then nothing is changed
	const bool setting = JsonBoolField(settingsBody, "startMinimized", true);
	if (setting != JsonBoolField(settingsBody, "startMinimized", false))
		return;
	config_t *config = obs_frontend_get_user_config();
	if (!config)
		return;
	const bool inObs = config_get_bool(config, "BasicWindow", "SysTrayWhenStarted");
	if (!g_startMinSeen) {
		g_startMinSeen = true;
		g_startMinLastObs = inObs;
		if (inObs != setting)
			WriteObsStartMinimized(config, setting);
		return;
	}
	if (inObs != g_startMinLastObs) {
		g_startMinLastObs = inObs;
		g_startMinPushing = true;
	}
	if (g_startMinPushing) {
		if (setting == inObs) {
			g_startMinPushing = false;
			return;
		}
		const std::string body = std::string("{\"startMinimized\":") + (inObs ? "true" : "false") + "}";
		RunAsync([body]() { HttpRequest("POST", "/settings", 8767, body.c_str(), 3000); });
		return;
	}
	if (setting != inObs)
		WriteObsStartMinimized(config, setting);
}

} // namespace

void PollHelperSettings()
{
	if (g_openClipsHotkeyRequestInFlight)
		return;
	g_openClipsHotkeyRequestInFlight = true;
	RunAsync([]() {
		std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 3000);
		QMetaObject::invokeMethod(g_callbacks, [settingsBody]() {
			g_openClipsHotkeyRequestInFlight = false;
			g_closeToTray = JsonBoolField(settingsBody, "closeToTray", true);
			// development mode is what lets the two development settings do anything
			const bool development = JsonBoolField(settingsBody, "developmentMode", false);
			DevLogSetEnabled(development && JsonBoolField(settingsBody, "logInterfaceEvents", false));
			SdkSetEnabled(development && JsonBoolField(settingsBody, "sdkMode", false));
			SyncStartMinimized(settingsBody);
			RegisterOpenClipsHotkey(settingsBody);
			ApplyNotifyThemeColors(settingsBody);
			RefreshTitleKeybindBadges(settingsBody);
			ShellApplySettings(settingsBody);
			ApplyLayoutSetting(settingsBody);
		}, Qt::QueuedConnection);
	});
}

} // namespace rk
