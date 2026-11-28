// carries the clip, recording and screenshot keybinds of replaykit into the native hotkeys of obs
#include "prelude.hpp"
#include "core/native_hotkeys.hpp"

#include <QJsonObject>
#include <QRegularExpression>
#include <algorithm>
#include <set>
#include <vector>

#include "core/json_lite.hpp"
#include "core/open_clips_hotkey.hpp"

namespace rk {

namespace {

struct Combo {
	QString key;
	bool control = false, alt = false, shift = false, command = false;
};

struct Target {
	// obs 33 moved replay saving from the output to a frontend hotkey, so the clip binding goes to both names
	std::vector<std::string> names;
	const char *settingsKey;
	const char *label;
};

const Target kTargets[] = {
	{{"ReplayBuffer.Save", "OBSBasic.SaveReplayBuffer"}, "clipKeybind", "clip"},
	{{"OBSBasic.StartRecording"}, "recordingKeybind", "start recording"},
	{{"OBSBasic.StopRecording"}, "recordingStopKeybind", "stop recording"},
	// the screenshot output of obs; the plugin moves the picture it saves into the Screenshots folder
	{{"OBSBasic.Screenshot"}, "screenshotKeybind", "screenshot"},
};

QJsonObject g_latest;
bool g_haveLatest = false;
// what obs was last given, so a read that changed nothing loads nothing
std::string g_applied;
std::set<std::string> g_missingWarned;

Combo ReadCombo(const QJsonObject &settings, const QString &name)
{
	static const QRegularExpression valid(QStringLiteral("^OBS_KEY_[A-Z0-9_]+$"));
	Combo combo;
	const QJsonObject item = settings.value(name).toObject();
	const QString key = item.value(QStringLiteral("key")).toString();
	if (key.size() < 9 || key.size() > 56 || !valid.match(key).hasMatch())
		return combo;
	combo.key = key;
	combo.control = item.value(QStringLiteral("control")).toBool();
	combo.alt = item.value(QStringLiteral("alt")).toBool();
	combo.shift = item.value(QStringLiteral("shift")).toBool();
	combo.command = item.value(QStringLiteral("command")).toBool();
	return combo;
}

std::string Signature(const Combo &combo)
{
	if (combo.key.isEmpty())
		return {};
	std::string text;
	const std::pair<bool, const char *> modifiers[] = {{combo.control, "control"}, {combo.alt, "alt"}, {combo.shift, "shift"}, {combo.command, "command"}};
	for (const auto &[on, name] : modifiers)
		if (on)
			text += std::string(name) + "+";
	return text + combo.key.toStdString();
}

// a hotkey is its saved combo plus the one extra binding the settings page allows; a stop binding never set follows the start binding
std::vector<Combo> CombosFor(const QJsonObject &settings, const char *settingsKey)
{
	QString key = QString::fromLatin1(settingsKey);
	if (key == QLatin1String("recordingStopKeybind") && !settings.value(key).isObject())
		key = QStringLiteral("recordingKeybind");
	return {ReadCombo(settings, key), ReadCombo(settings, key + QStringLiteral("Extra"))};
}

void Load(const Target &target, const std::vector<Combo> &combos)
{
	struct Search {
		const std::vector<std::string> *names;
		std::vector<obs_hotkey_id> found;
	} search{&target.names, {}};
	obs_enum_hotkeys([](void *data, obs_hotkey_id id, obs_hotkey_t *hotkey) {
		auto *search = static_cast<Search *>(data);
		const char *name = obs_hotkey_get_name(hotkey);
		if (name && std::find(search->names->begin(), search->names->end(), name) != search->names->end())
			search->found.push_back(id);
		return true;
	}, &search);
	if (search.found.empty()) {
		if (g_missingWarned.insert(target.label).second)
			blog(LOG_WARNING, "[replaykit] the obs %s hotkey was not found, so its keybind was not loaded", target.label);
		return;
	}
	obs_data_array_t *bindings = obs_data_array_create();
	std::set<std::string> seen;
	for (const Combo &combo : combos) {
		const std::string signature = Signature(combo);
		if (signature.empty() || !seen.insert(signature).second)
			continue;
		obs_data_t *item = obs_data_create();
		if (combo.control)
			obs_data_set_bool(item, "control", true);
		if (combo.alt)
			obs_data_set_bool(item, "alt", true);
		if (combo.shift)
			obs_data_set_bool(item, "shift", true);
		if (combo.command)
			obs_data_set_bool(item, "command", true);
		obs_data_set_string(item, "key", combo.key.toUtf8().constData());
		obs_data_array_push_back(bindings, item);
		obs_data_release(item);
	}
	for (const obs_hotkey_id id : search.found)
		obs_hotkey_load(id, bindings);
	obs_data_array_release(bindings);
}

void Apply()
{
	// a key capture holds every binding off until it ends, and puts them back itself
	if (!g_haveLatest || HotkeyCaptureActive())
		return;
	std::vector<std::vector<Combo>> wanted;
	std::string signature;
	for (const Target &target : kTargets) {
		wanted.push_back(CombosFor(g_latest, target.settingsKey));
		signature += std::string(target.settingsKey) + "=" + Signature(wanted.back()[0]) + "," + Signature(wanted.back()[1]) + "|";
	}
	if (signature == g_applied)
		return;
	g_applied = signature;
	for (size_t i = 0; i < std::size(kTargets); ++i)
		Load(kTargets[i], wanted[i]);
	blog(LOG_INFO, "[replaykit] native hotkeys loaded: %s", signature.c_str());
}

} // namespace

void NativeHotkeysApplySettings(const std::string &settingsBody)
{
	const QJsonValue settings = JsonBodyObject(settingsBody).value(QStringLiteral("settings"));
	if (!settings.isObject())
		return;
	g_latest = settings.toObject();
	g_haveLatest = true;
	Apply();
}

void NativeHotkeysCaptureEnded()
{
	g_applied.clear();
	Apply();
}

} // namespace rk
