// opens, toggles and focuses the clips window; the clean layout shows its clips view instead
#pragma once

namespace rk {

void ShowClips();
void ToggleClips();
// the clips window of the classic layout hears about clip saves the way the clips view of the shell does
void ClipsWindowClipSaving(const std::string &args);

} // namespace rk
