// global hotkey that opens or closes the clips window
#pragma once

#include <QPointer>
#include <QTimer>
#include <string>

namespace rk {

bool RegisterOpenClipsHotkey(const std::string &settingsBody);
// true while the settings page captures a key, when every obs hotkey replaykit binds is held off
bool HotkeyCaptureActive();
bool SetHotkeyCapture(bool active);
void InstallOpenClipsHotkeyFilter();
void RemoveOpenClipsHotkey();

} // namespace rk
