// view switching of the clean shell
#pragma once

#include <QString>
#include <string>

#include "shell/shell_types.hpp"

namespace rk {

void ShellEndClipFullscreen();
void ShellDropParkedObsSettings();
void ShellSurfaceView();
// the rounded corner of the stage and the resize grip go back on top of whatever was raised over them
void ShellRaiseGrip();
// false when the change did not happen: the shell is not up, the obs settings are not built, or their dialog would not close
bool ShellActivate(ShellView view);
void ShellHostShown(const std::string &name);
void ShellOpenSettingsPage(const QString &page);
void ShellEnsureHost();
// asks the shell page for a sign of life and reloads it when none comes for several tries in a row. suspicious starts the count part way, for a moment when the page should answer at once
void ShellProbeHost(bool suspicious);
// the sign of life of the page came back through the helper
void ShellHostPong(int n);
// brings the shell page back from the start, the way it is after a launch
void ShellReloadHost();
void ShellOpenView(ShellView view, bool quiet);
// puts the keyboard on the browser of the shell when the window of obs is the one in front, so a page gets its keys without a click on it first; true when the browser has the keyboard afterwards
bool ShellFocusBrowser();
void ShellToggleClips();
// the rail was pressed on the page that is already showing: the page is told, and it may have a way back of its own (the editor of the clips view closes)
void ShellReselect(ShellView view);
void ShellTrayClickWithObsSettings();
void ShellCloseView(ShellView view);
void ShellSetClipsFullscreen(bool active);

} // namespace rk
