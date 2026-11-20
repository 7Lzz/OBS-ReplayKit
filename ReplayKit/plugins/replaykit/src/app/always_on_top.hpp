// keeps the obs window and the windows of replaykit above other windows while the always on top setting is on
#pragma once

#include <string>

class QWidget;

namespace rk {

// carries the setting from the settings poll into the window, and a flip of View > Always On Top in obs back to the helper
void AlwaysOnTopApplySettings(const std::string &settingsBody);
// a window of replaykit made after the last poll takes the setting as it shows, so it never opens under an obs window that is on top
void AlwaysOnTopFollow(QWidget *window);

} // namespace rk
