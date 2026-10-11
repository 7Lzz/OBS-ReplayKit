// native frame of the frameless obs window
#pragma once

#include <QAbstractNativeEventFilter>
#include <QByteArray>
#include <QMainWindow>
#include <QMargins>
#include <QtGlobal>

#include "core/win32.hpp"

namespace rk {

void ShellSyncFrameMargins(HWND hwnd);
bool ShellIsMaximized();
void ShellToggleMaximize(QMainWindow *mw);
void ShellMakeMaximizeReal();
void ShellRestoreDrag(QMainWindow *mw, POINT cursor, bool handOff = true);
void ShellBindHwnd();
// lets the window out if its first show is still holding it back, for the module going away
void ShellUncloak();
QMargins ShellMaximizeOverhang(QMainWindow *mw);
extern ITaskbarList2 *g_shellTaskbar;
void ShellNativeFullscreen(bool on);
// true when a screen point (the lParam of a WM_NCHITTEST) lands on the caption of the window, which a dim laid over the whole window uses to let a drag start there
bool ShellPointIsCaption(LPARAM screenPoint);
// true when a point on the screen is on the caption or the sizing frame of the obs window and nothing covers it, where a press may begin a drag or a resize; the frame settles that press itself, a click is a click away from the menus of the title bar and a drag is not
bool ShellPointIsFrame(POINT screen);

// answers the non-client messages of the obs main window so it behaves like a normal top-level (drag by the title bar, edge resize, snap, maximize) while the whole surface stays ours. every message for any other window passes straight through untouched
class ShellFrameFilter : public QAbstractNativeEventFilter {
public:
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *result) override;
};

} // namespace rk
