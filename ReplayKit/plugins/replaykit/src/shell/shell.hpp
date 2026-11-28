// start and stop of the clean shell
#pragma once

#include <QMainWindow>
#include <string>

namespace rk {

void ShellApplySettings(const std::string &settingsBody);
bool ShellEnableFrame(QMainWindow *mw);
void ShellBuild();
void ShellStart();
void ShellStop();
// stops the tick of the title bar and rail while the window is hidden or minimized, unless obs records, and catches it up once the window shows
void ShellSetWindowShown(bool shown);

} // namespace rk
