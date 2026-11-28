// the replaykit entries of the obs Tools menu and the hotkey that uploads the latest clip
#pragma once

namespace rk {

// registers the hotkey and the save callback that keeps its binding in the scene collection, before obs loads one
void ToolsActionsRegister();
// adds the Tools menu entries once the main window is up
void ToolsActionsAddMenu();
void ToolsActionsUnregister();

} // namespace rk
