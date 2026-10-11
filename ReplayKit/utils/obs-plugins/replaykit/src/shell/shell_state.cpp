// state of the clean shell
#include "prelude.hpp"
#include "shell/shell_state.hpp"

#include "shell/shell_grip.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_projector_bar.hpp"
#include "shell/shell_rail.hpp"
#include "shell/shell_title_bar.hpp"
#include "shell/shell_types.hpp"

namespace rk {

HWND g_shellHwnd = nullptr;
QPointer<ShellTitleBar> g_shellTitle;
QPointer<ShellRail> g_shellRail;
QPointer<ShellPage> g_shellHost;
QPointer<ShellGrip> g_shellGrip;
QPointer<ShellCorner> g_shellCorner;
QPointer<ShellProjectorBar> g_shellBar;
QPointer<QWidget> g_shellCurtain;
QPointer<QWidget> g_viewObsHost;
QPointer<QDialog> g_obsSettingsDialog;
QPointer<QWidget> g_obsNav;
ShellView g_shellView = ShellView::Home;
bool g_shellPendingSetup = false;
bool g_hostCreating = false;
bool g_hostReady = false;
bool g_hostOnTop = false;
ShellView g_hostRequested = ShellView::Home;
ShellView g_hostShown = ShellView::Home;
int g_hostWaitGen = 0;
int g_hostPingSent = 0;
int g_hostPingAcked = 0;
bool g_hostPingProven = false;
int g_hostPingMisses = 0;
QElapsedTimer g_hostReloadTimer;
bool g_obsSettingsPainted = false;
QElapsedTimer g_hostVisibleTimer;
bool g_previewSuspended = false;
bool g_previewWasOn = false;
QString g_shellSettingsPage;
QPointer<QTimer> g_shellTimer;
int g_shellTickCount = 0;
QAbstractNativeEventFilter *g_shellFrameFilter = nullptr;
QObject *g_obsEmbedFilter = nullptr;

} // namespace rk
