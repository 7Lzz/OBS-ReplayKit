// turns obs key names into the labels the keybind chips show
#pragma once

#include <string>

namespace rk {

std::string KeybindLabelFromSettingsJson(const std::string &settingsBody, const char *field);

} // namespace rk
