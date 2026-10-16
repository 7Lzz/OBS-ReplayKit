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
// puts the WASD Overlay and Mouse Overlay items of the current scene into a group named Group, the shape the helper scene file gives the input overlay; obs-websocket has no request that makes a group
void GroupInputOverlay();

} // namespace rk
