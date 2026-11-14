// the font the native chrome draws its text in, the uiFont setting of the helper
#pragma once

#include <QString>

#include <string>

namespace rk {

// the family the helper last sent, kept from the last run until it answers so the first frame already draws with it
QString UiFontFamily();
// takes uiFont and the file of a shipped font from a settings body of the helper; true when the family changed
bool UiFontApplySettings(const std::string &settingsBody);

} // namespace rk
