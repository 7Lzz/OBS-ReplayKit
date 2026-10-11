// obs actions the tray menu, the dock pages and the shell trigger
#pragma once

#include <string>

namespace rk {

void HandleConfirmRequest(const std::string &kind);
void PromptInstallSharePreview();
void ToggleSharePreview(bool checked);
void RestartObs();
void OpenCrashLogsFolder();
void ConfirmedExit();
void ToggleRecording();
void ToggleReplayBuffer();

} // namespace rk
