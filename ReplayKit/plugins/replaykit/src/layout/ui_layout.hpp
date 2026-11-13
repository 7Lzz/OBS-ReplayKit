// classic and clean layout switching
#pragma once

#include <QDockWidget>
#include <QMainWindow>

namespace rk {

enum class UiLayout { Classic, Clean };

// the layout selected at startup, see g_shellOn for whether the shell actually loaded
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

} // namespace rk
