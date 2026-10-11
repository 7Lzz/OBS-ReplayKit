// cross-cutting plugin state shared by every module
#pragma once

#include <QElapsedTimer>
#include <QObject>
#include <QPointer>
#include <QWidget>
#include <QtGlobal>

#include "browser-panel.hpp"

namespace rk {

extern QObject *g_callbacks;
extern QPointer<QWidget> g_clipsWindow;
extern QPointer<QCefWidget> g_clipsBrowser;
extern bool g_clipsFullscreenActive;
// set while the clean window is stretched over its monitor for a clip; the position savers skip that state so obs never remembers a monitor-sized window
extern bool g_shellNativeFullscreen;
extern QPointer<QWidget> g_settingsWindow;
extern QPointer<QWidget> g_setupWindow;

// close-to-tray: when true, the OBS windows X hides to tray instead of quitting. polled from /settings on the same 1s timer as the open-clips hotkey. real quits/restarts (tray Exit, restart routes) send ALLOWCLOSE over the ipc pipe just before posting WM_CLOSE so the filter lets those through untouched.
extern bool g_closeToTray;
extern QPointer<QWidget> g_mainWindow;

// menu opens upward from the cursor so restart obs lands right where an accidental double-clicks second click hits -- every action here bails if triggered within kMenuClickDebounceMs of aboutToShow
inline constexpr qint64 kMenuClickDebounceMs = 150;
extern QElapsedTimer g_menuShownTimer;
// what this process actually runs. decided once in obs_module_post_load: the clean shell needs a frameless top-level, and qt can only apply that cleanly before the native window exists, so changing layout means restarting obs
extern bool g_shellOn;

} // namespace rk
