// shows the capture that fits what is in front, in every scene: game capture for a fullscreen game, window capture for a listed game or one game capture cannot hook, the game out of focus card while a listed game waits behind other windows, display capture otherwise; and keeps display capture on its chosen display
#pragma once

#include <string>

namespace rk {

// once the scene collection is loaded; nothing switches before the first settings read
void CaptureSwitchStart();
// takes the capture mode, the listed games and the switch delay out of a settings read; a body without settings leaves the policy as it was
void CaptureSwitchApplySettings(const std::string &settingsBody);
void CaptureSwitchStop();

} // namespace rk
