// global hotkey that opens or closes the clips window
#pragma once

#include <QPointer>
#include <QTimer>
#include <string>

namespace rk {

extern QPointer<QTimer> g_openClipsHotkeyTimer;
void RegisterOpenClipsHotkey(const std::string &settingsBody);
void InstallOpenClipsHotkeyFilter();
void RemoveOpenClipsHotkey();

} // namespace rk
