// reads the settings from the helper whenever it pushes a change, and applies them
#pragma once

namespace rk {

// reads the settings now and follows the obs settings dialog from then on
void SettingsPollStart();
// reads the settings now: the helper asks for it over the pipe whenever they change
void PollHelperSettings();
// reads the full settings now, which brings in what was changed on the obs side (hotkeys, clip length, mic, volumes)
void RequestFullSettingsSync();

} // namespace rk
