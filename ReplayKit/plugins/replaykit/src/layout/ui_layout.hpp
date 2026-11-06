// classic and clean layout switching
#pragma once

#include <QDockWidget>
#include <QMainWindow>
#include <string>

namespace rk {

enum class UiLayout { Classic, Clean };

// the layout the helper settings ask for, re-read from the settings file at startup and cached in window_state.ini. it only takes effect on the next obs launch, see g_shellOn
extern UiLayout g_wantedLayout;
QDockWidget *FindSidebarDock(QMainWindow *mw);
void ApplyLayoutChrome(bool clean);
void ApplyLayoutDocks(UiLayout wanted);
void PrepareDocksForNextLaunch();
void LockCleanLayoutMenus();
void ApplyLayout();

// set when the previous clean shell start never got as far as clearing its marker, see InitLayoutAtStartup
extern bool g_shellBootSkipped;
void InitLayoutAtStartup();
void ClearShellBootMark();
void ApplyLayoutSetting(const std::string &settingsBody);
void RestartIntoLayout(const char *layout);

} // namespace rk
