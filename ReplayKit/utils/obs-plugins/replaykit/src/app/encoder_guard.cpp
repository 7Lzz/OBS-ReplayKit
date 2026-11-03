// a gpu encoder whose driver is too old is left out by obs (nvenc logs "Test process failed: outdated_driver" and its module does not load), and a profile that still names it makes every recording and replay buffer fail with obs's "missing encoders" and "failed to start" dialogs. before obs builds its outputs, a missing encoder of the profile is swapped for the best one that did load (another gpu's hardware encoder first, then x264), so clipping keeps working, and the helper is told so it can say what happened and where to get the driver
#include "prelude.hpp"
#include "app/encoder_guard.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <util/config-file.h>

#include "core/async.hpp"
#include "core/helper_client.hpp"

namespace rk {

namespace {

std::string g_missing;
std::string g_fallback;

bool Registered(const std::string &id)
{
	return !id.empty() && obs_get_encoder_codec(id.c_str()) != nullptr;
}

bool IsHevc(const std::string &id)
{
	return id.find("hevc") != std::string::npos || id.find("h265") != std::string::npos;
}

// the hardware encoders replaykit sets up, best first: the codec of the missing one before the other, and x264 last
std::string PickFallback(const std::string &missing)
{
	static const char *hevc[] = {"obs_nvenc_hevc_tex", "h265_texture_amf", "obs_qsv11_hevc"};
	static const char *h264[] = {"obs_nvenc_h264_tex", "h264_texture_amf", "obs_qsv11_v2"};
	std::vector<const char *> order;
	if (IsHevc(missing))
		order.insert(order.end(), std::begin(hevc), std::end(hevc));
	order.insert(order.end(), std::begin(h264), std::end(h264));
	if (!IsHevc(missing))
		order.insert(order.end(), std::begin(hevc), std::end(hevc));
	for (const char *id : order)
		if (id != missing && Registered(id))
			return id;
	return Registered("obs_x264") ? "obs_x264" : std::string();
}

// constant-quality settings close to what the helper writes for each kind of encoder, so the first clips after the swap are not made at a default bitrate; the helper writes its own tuned ones the next time settings are applied
obs_data_t *FallbackSettings(const std::string &id)
{
	obs_data_t *data = obs_data_create();
	obs_data_set_int(data, "keyint_sec", 2);
	if (id == "obs_x264") {
		obs_data_set_string(data, "rate_control", "CRF");
		obs_data_set_int(data, "crf", 20);
		obs_data_set_string(data, "preset", "veryfast");
		obs_data_set_string(data, "profile", "high");
	} else if (id.find("qsv") != std::string::npos) {
		obs_data_set_string(data, "rate_control", "ICQ");
		obs_data_set_int(data, "icq_quality", 23);
		obs_data_set_string(data, "target_usage", "TU4");
	} else if (id.find("amf") != std::string::npos) {
		obs_data_set_string(data, "rate_control", "CQP");
		obs_data_set_int(data, "cqp", 22);
		obs_data_set_string(data, "preset", "quality");
	} else {
		obs_data_set_string(data, "rate_control", "CQP");
		obs_data_set_int(data, "cqp", 22);
		obs_data_set_string(data, "preset2", "p5");
	}
	return data;
}

std::string ConfigString(config_t *config, const char *section, const char *key)
{
	const char *value = config_get_string(config, section, key);
	return value ? value : "";
}

// one encoder key of the profile, with the json file of its settings; true when it was replaced
bool Replace(config_t *config, const char *key, const char *settingsFile)
{
	const std::string current = ConfigString(config, "AdvOut", key);
	if (current.empty() || current == "none" || Registered(current))
		return false;
	const std::string fallback = PickFallback(current);
	if (fallback.empty()) {
		blog(LOG_WARNING, "[replaykit] encoder %s is not loaded and no other video encoder is available", current.c_str());
		if (g_missing.empty())
			g_missing = current;
		return false;
	}
	config_set_string(config, "AdvOut", key, fallback.c_str());
	char *profile = obs_frontend_get_current_profile_path();
	if (profile && *profile) {
		const std::string path = std::string(profile) + "/" + settingsFile;
		obs_data_t *settings = FallbackSettings(fallback);
		obs_data_save_json_safe(settings, path.c_str(), "tmp", "bak");
		obs_data_release(settings);
	}
	bfree(profile);
	blog(LOG_WARNING, "[replaykit] encoder %s is not loaded (often an outdated graphics driver), switched AdvOut.%s to %s", current.c_str(), key, fallback.c_str());
	if (g_missing.empty()) {
		g_missing = current;
		g_fallback = fallback;
	}
	return true;
}

} // namespace

void EnsureUsableEncoders()
{
	config_t *config = obs_frontend_get_profile_config();
	if (!config)
		return;
	bool changed = Replace(config, "RecEncoder", "recordEncoder.json");
	changed = Replace(config, "Encoder", "streamEncoder.json") || changed;
	if (changed)
		config_save_safe(config, "tmp", nullptr);
}

void ReportEncoders()
{
	QJsonArray available;
	const char *id = nullptr;
	for (size_t i = 0; obs_enum_encoder_types(i, &id); i++)
		if (id && obs_get_encoder_type(id) == OBS_ENCODER_VIDEO)
			available.append(QString::fromUtf8(id));
	QJsonObject report{
		{"obsPid", (qint64)GetCurrentProcessId()},
		{"available", available},
		{"missing", QString::fromStdString(g_missing)},
		{"fallback", QString::fromStdString(g_fallback)},
	};
	const std::string body = QJsonDocument(report).toJson(QJsonDocument::Compact).toStdString();
	// the helper can still be starting, so the report is tried for a while
	RunAsync([body]() {
		for (int attempt = 0; attempt < 15; attempt++) {
			if (!HttpRequest("POST", "/encoders/report", 8767, body.c_str(), 3000).empty())
				return;
			std::this_thread::sleep_for(std::chrono::seconds(2));
		}
	});
}

} // namespace rk
