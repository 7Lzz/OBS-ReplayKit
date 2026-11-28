// keeps desktop audio monitored while Share Preview is on, since the share window relays what obs monitors to discord, and unmonitored while it is off, while discord streams something else with its sound, or while desktop audio records an output device
#include "prelude.hpp"
#include "share/share_monitoring.hpp"

#include <optional>

#include "core/json_lite.hpp"
#include "core/shared_values.hpp"
#include "share/share_audio.hpp"

namespace rk {

namespace {

// both unknown until the helper has answered once
std::optional<bool> g_enabled;
bool g_outputDevice = false;

void SetMonitoringOff(const char *name)
{
	obs_source_t *source = obs_get_source_by_name(name);
	if (!source)
		return;
	if (obs_source_get_monitoring_enabled(source)) {
		obs_source_set_monitoring_enabled(source, false);
		blog(LOG_INFO, "[replaykit] '%s' monitoring turned off", name);
	}
	obs_source_release(source);
}

void CoverDiscordPrograms(const char *name)
{
	obs_source_t *source = obs_get_source_by_name(name);
	if (!source)
		return;
	obs_data_t *settings = obs_source_get_settings(source);
	obs_data_array_t *list = obs_data_get_array(settings, "executable_list");
	if (!list)
		list = obs_data_array_create();
	QStringList have;
	for (size_t i = 0; i < obs_data_array_count(list); ++i) {
		obs_data_t *item = obs_data_array_item(list, i);
		have << QString::fromUtf8(obs_data_get_string(item, "value")).toLower();
		obs_data_release(item);
	}
	QStringList added;
	for (const char *program : kDiscordPrograms) {
		const QString lower = QString::fromUtf8(program).toLower();
		if (have.contains(lower))
			continue;
		have << lower;
		obs_data_t *item = obs_data_create();
		obs_data_set_string(item, "value", program);
		obs_data_array_push_back(list, item);
		obs_data_release(item);
		added << QString::fromUtf8(program);
	}
	if (!added.isEmpty()) {
		obs_data_set_array(settings, "executable_list", list);
		obs_source_update(source, settings);
		blog(LOG_INFO, "[replaykit] '%s' program list += %s", name, added.join(QStringLiteral(", ")).toUtf8().constData());
	}
	obs_data_array_release(list);
	obs_data_release(settings);
	obs_source_release(source);
}

void Ensure(bool refreshMute)
{
	if (!g_enabled)
		return;
	obs_source_t *source = obs_get_source_by_name(kDesktopAudioSourceName);
	if (!source)
		return;
	obs_data_t *settings = obs_source_get_settings(source);
	const bool deviceCapture = g_outputDevice || obs_data_get_bool(settings, "output_device");
	obs_data_release(settings);
	bool target = false;
	const char *why = "Share Preview off";
	if (deviceCapture)
		why = "desktop audio records an output device";
	else if (*g_enabled && ShareAudioHeld())
		why = "discord streams something else with its sound";
	else if (*g_enabled) {
		target = true;
		why = "Share Preview on";
	}
	// the on or off calls, since obs 33 logs a deprecation for every call of the typed ones
	const bool changed = obs_source_get_monitoring_enabled(source) != target;
	if (changed) {
		obs_source_set_monitoring_enabled(source, target);
		blog(LOG_INFO, "[replaykit] desktop audio monitoring turned %s (%s)", target ? "on" : "off", why);
	}
	// an output device capture keeps whatever mute it has; otherwise only a change of monitoring made here clears a mute, since switching it can leave the source muted, and a mute from the PC audio switch or the obs mixer stays
	if (!deviceCapture) {
		const bool muted = obs_source_muted(source);
		if (muted && changed) {
			obs_source_set_muted(source, false);
			blog(LOG_INFO, "[replaykit] desktop audio unmuted after its monitoring changed");
		} else if (!muted && (changed || refreshMute)) {
			// obs can leave the monitor icon of the mixer stale without a state signal of the source
			obs_source_set_muted(source, false);
		}
	}
	obs_source_release(source);
}

} // namespace

void ShareMonitoringStart()
{
	SetMonitoringOff(kMicSourceName);
	// mainly so the record-only discord source records friends, while desktop audio leaves them out
	CoverDiscordPrograms(kDesktopAudioSourceName);
	CoverDiscordPrograms(kDiscordAudioSourceName);
	Ensure(true);
}

void ShareMonitoringApplySettings(const std::string &settingsBody)
{
	const auto screenshare = JsonBoolIfPresent(settingsBody, "discord_screenshare_enabled");
	const auto projector = JsonBoolIfPresent(settingsBody, "discord_projector_enabled");
	if (!screenshare || !projector)
		return;
	const bool enabled = *screenshare && *projector;
	const bool outputDevice = ExtractJsonStringField(settingsBody, "recordingAudioMode") == "output_device";
	const bool changed = g_enabled != enabled || outputDevice != g_outputDevice;
	if (changed)
		blog(LOG_INFO, "[replaykit] Share Preview %s, desktop audio %s", enabled ? "on" : "off", outputDevice ? "records an output device" : "records the desktop");
	g_enabled = enabled;
	g_outputDevice = outputDevice;
	Ensure(changed);
}

void ShareMonitoringEnsure()
{
	Ensure(false);
}

} // namespace rk
