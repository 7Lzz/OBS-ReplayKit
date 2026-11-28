// carries the clip, recording and screenshot keybinds of replaykit into the native hotkeys of obs
#pragma once

#include <string>

namespace rk {

// takes the keybinds out of a settings read and loads the ones that changed into obs; a body without settings leaves the hotkeys as they are
void NativeHotkeysApplySettings(const std::string &settingsBody);
// a key capture put back the bindings it saved, which can be older than the settings read meanwhile
void NativeHotkeysCaptureEnded();

} // namespace rk
