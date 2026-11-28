// turns obs key names into the labels the keybind chips show
#include "prelude.hpp"
#include "core/keybind_label.hpp"

#include "core/json_lite.hpp"
#include "core/locale.hpp"

namespace rk {

namespace {

// mirrors keyLabel() in ui/_hotkey-keys.html exactly, so the tray and title badges read the same as the settings hotkey field for the same binding.
std::string KeyLabelFromObsKey(const std::string &obsKey)
{
	if (obsKey == "OBS_KEY_ESCAPE")
		return TrMessage("editor.esc").toStdString();
	if (obsKey == "OBS_KEY_DELETE")
		return TrMessage("keys.delete").toStdString();
	if (obsKey == "OBS_KEY_BACKSPACE")
		return TrMessage("editor.backspace").toStdString();
	static const std::unordered_map<std::string, std::string> named = {
		{"OBS_KEY_BACKSLASH", "\\"}, {"OBS_KEY_SLASH", "/"}, {"OBS_KEY_SPACE", "Space"},
		{"OBS_KEY_RETURN", "Enter"}, {"OBS_KEY_TAB", "Tab"},
		{"OBS_KEY_UP", "Up"}, {"OBS_KEY_DOWN", "Down"}, {"OBS_KEY_LEFT", "Left"}, {"OBS_KEY_RIGHT", "Right"},
	};
	auto found = named.find(obsKey);
	if (found != named.end())
		return found->second;
	const std::string prefix = "OBS_KEY_";
	if (obsKey.rfind(prefix, 0) == 0) {
		std::string rest = obsKey.substr(prefix.size());
		std::replace(rest.begin(), rest.end(), '_', ' ');
		return rest;
	}
	return obsKey;
}

} // namespace

// same modifier set/order/key-name mapping as comboToLabel() in ui/_hotkey-keys.html, but joined with a bare "+" (no spaces) so the chip stays as compact as possible and never stretches the menu wider than it needs to be.
std::string KeybindLabelFromSettingsJson(const std::string &settingsBody, const char *field)
{
	std::string obj = ExtractJsonObjectField(settingsBody, field);
	if (obj.empty())
		return std::string();
	std::string key = ExtractJsonStringField(obj, "key");
	if (key.empty())
		return std::string();
	std::vector<std::string> parts;
	if (JsonBoolField(obj, "control", false))
		parts.push_back("Ctrl");
	if (JsonBoolField(obj, "alt", false))
		parts.push_back("Alt");
	if (JsonBoolField(obj, "shift", false))
		parts.push_back("Shift");
	if (JsonBoolField(obj, "command", false))
		parts.push_back("Win");
	parts.push_back(KeyLabelFromObsKey(key));
	std::string label;
	for (size_t i = 0; i < parts.size(); i++) {
		if (i)
			label += "+";
		label += parts[i];
	}
	return label;
}

} // namespace rk
