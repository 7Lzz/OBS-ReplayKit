// view switching of the clean shell
#pragma once

#include <QString>
#include <functional>
#include <string>

#include "shell/shell_types.hpp"

namespace rk {

// the views the shell browser holds; projector and obs settings are native windows of their own
bool ShellIsPageView(ShellView view);
void ShellEndClipFullscreen();
void ShellDropParkedObsSettings();
// obs refuses to start any output while its settings dialog exists, so a parked one is dropped first and then runs once it is gone
void ShellStartOutputWhenFree(std::function<void()> start);
void ShellSurfaceView();
// the rounded corner of the stage and the resize grip go back on top of whatever was raised over them
void ShellRaiseGrip();
// false when the change did not happen: the shell is not up, the obs settings are not built, or their dialog would not close
bool ShellActivate(ShellView view);
bool ShellSetupLocked();
void ShellReclaimForeground();
void ShellHostShown(const std::string &name);
void ShellUpdateTitle();
void ShellSetEditor(ShellView view, bool active);
void ShellOpenSettingsPage(const QString &page);
void ShellEnsureHost();
// asks the shell page for a sign of life and reloads it when none comes for several tries in a row. suspicious starts the count part way, for a moment when the page should answer at once
void ShellProbeHost(bool suspicious);
// the sign of life of the page came back through the helper
void ShellHostPong(int n);
// brings the shell page back from the start, the way it is after a launch
void ShellReloadHost();
// the window went to the tray or came back: after a while hidden the shell and popup browsers are parked, and showing the window brings them back
void ShellHostWindowShown(bool shown);
bool ShellHostParked();
void ShellOpenView(ShellView view, bool quiet);
// puts the keyboard on the browser of the shell when the window of obs is the one in front, so a page gets its keys without a click on it first; true when the browser has the keyboard afterwards
bool ShellFocusBrowser();
void ShellToggleClips();
// the rail was pressed on the page that is already showing: the page is told, and it may have a way back of its own (the editor of the clips view closes)
void ShellReselect(ShellView view);
void ShellTrayClickWithObsSettings();
void ShellCloseView(ShellView view);
// the settings page answered the unsaved-changes prompt with save or leave, and the view it held back goes ahead
void ShellLeaveSettings(const std::string &target);
void ShellSetClipsFullscreen(bool active);
// tells the clips view a clip save started or ended, args being the json array of the state and the saved file name
void ShellClipSaving(const std::string &args);

} // namespace rk
