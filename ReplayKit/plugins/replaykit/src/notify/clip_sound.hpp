// plays the sound of a saved clip and of a recording that starts or stops, at the volumes of the settings
#pragma once

#include <functional>
#include <string>

namespace rk {

enum class ClipSound { ClipSaved, RecordingStarted, RecordingStopped };

// takes the two volumes out of a settings read; a body without them leaves the volumes as they were
void ClipSoundApplySettings(const std::string &settingsBody);
// plays sound off the ui thread and runs onStarted once playback starts (or immediately if muted or playback fails) so a toast appears in sync
void ClipSoundPlay(ClipSound sound, std::function<void()> onStarted = {});
// closes the player before the module is unloaded
void ClipSoundStop();

} // namespace rk
