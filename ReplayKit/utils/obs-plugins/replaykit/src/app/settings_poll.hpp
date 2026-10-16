// applies the one second settings poll from the helper
#pragma once

namespace rk {

void PollHelperSettings();
// the next poll asks for the full settings, which brings in what was changed on the obs side (hotkeys, clip length, mic, volumes)
void RequestFullSettingsSync();

} // namespace rk
