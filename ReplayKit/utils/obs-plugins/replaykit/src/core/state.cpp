// cross-cutting plugin state shared by every module
#include "prelude.hpp"
#include "core/state.hpp"

namespace rk {

QObject *g_callbacks = nullptr;
QPointer<QWidget> g_clipsWindow;
QPointer<QCefWidget> g_clipsBrowser;
bool g_clipsFullscreenActive = false;
bool g_shellNativeFullscreen = false;
QPointer<QWidget> g_settingsWindow;
QPointer<QWidget> g_setupWindow;
bool g_closeToTray = true;
QPointer<QWidget> g_mainWindow;
QElapsedTimer g_menuShownTimer;
bool g_shellOn = false;

} // namespace rk
