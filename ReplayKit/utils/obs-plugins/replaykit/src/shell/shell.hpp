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

} // namespace rk
