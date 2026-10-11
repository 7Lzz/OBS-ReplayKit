// the obs main window and its handle for the helper
#pragma once

#include <QMainWindow>

namespace rk {

QMainWindow *ObsMainWindowQt();
// hides or shows the window through the toggle slot of obs, false when this obs build has none
bool ToggleMainWindowShowing();
void PublishMainWindow();

} // namespace rk
