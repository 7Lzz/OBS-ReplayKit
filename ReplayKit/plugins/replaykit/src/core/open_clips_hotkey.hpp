// global hotkey that opens or closes the clips window
#pragma once

#include <QPointer>
#include <QTimer>
#include <string>

namespace rk {

extern QPointer<QTimer> g_openClipsHotkeyTimer;
bool RegisterOpenClipsHotkey(const std::string &settingsBody);
bool SetHotkeyCapture(bool active);
void InstallOpenClipsHotkeyFilter();
void RemoveOpenClipsHotkey();

} // namespace rk
