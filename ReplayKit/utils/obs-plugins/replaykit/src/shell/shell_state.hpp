// state of the clean shell
#pragma once

#include <QAbstractNativeEventFilter>
#include <QDialog>
#include <QElapsedTimer>
#include <QObject>
#include <QPointer>
#include <QString>
#include <QTimer>
#include <QWidget>

#include "core/win32.hpp"
#include "shell/shell_grip.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_projector_bar.hpp"
#include "shell/shell_rail.hpp"
#include "shell/shell_title_bar.hpp"
#include "shell/shell_types.hpp"

namespace rk {

extern HWND g_shellHwnd;

extern QPointer<ShellTitleBar> g_shellTitle;
extern QPointer<ShellRail> g_shellRail;
extern QPointer<ShellPage> g_shellHost;
extern QPointer<ShellGrip> g_shellGrip;
extern QPointer<ShellCorner> g_shellCorner;
// the strip along the top of the projector view, with the preview switch and the lock
extern QPointer<ShellProjectorBar> g_shellBar;
// stage cover while the browser is still loading its first page, so the preview never shows through for a moment
extern QPointer<QWidget> g_shellCurtain;
// plain host for the obs settings dialog once it is reparented into the window
extern QPointer<QWidget> g_viewObsHost;
extern QPointer<QDialog> g_obsSettingsDialog;
// the column that stands in for the list of pages of that dialog
extern QPointer<QWidget> g_obsNav;
extern ShellView g_shellView;
// a quiet open (first-run wizard while obs sits in the tray) waits here for the window to come back
extern bool g_shellPendingSetup;
// the browser is created once cef and the helper are up; ready means it has reported its first page on screen
extern bool g_hostCreating;
extern bool g_hostReady;
// whether the browser is the top window of the stage right now, as opposed to sitting under the preview or the obs settings
extern bool g_hostOnTop;
// the page the browser was last told to show, and the one it last reported showing
extern ShellView g_hostRequested;
extern ShellView g_hostShown;
// bumped on every wait for the browser, so a backstop timer that fires late knows it belongs to a wish that is already over
extern int g_hostWaitGen;
// the page is pinged now and then to learn whether its renderer still runs (see ShellProbeHost): pings sent, the newest one answered, whether any was ever answered, and how many in a row went unanswered
extern int g_hostPingSent;
extern int g_hostPingAcked;
extern bool g_hostPingProven;
extern int g_hostPingMisses;
// when the page was last reloaded to bring it back
extern QElapsedTimer g_hostReloadTimer;
// a native window shows black for a frame until its first paint, so the obs settings dialog is only lifted above the stage after it has painted once
extern bool g_obsSettingsPainted;
// set when the browser first becomes visible: the wait for its first page is counted from there, since a browser under a hidden window cannot load
extern QElapsedTimer g_hostVisibleTimer;
extern bool g_previewSuspended;
extern bool g_previewWasOn;
// the settings tab to jump to once the page is showing (the clip badge sends people to the tab that holds the clipping switch)
extern QString g_shellSettingsPage;
extern QPointer<QTimer> g_shellTimer;
extern int g_shellTickCount;
// the app-wide filters the shell installs, kept so they can come off before this module is unloaded
extern QAbstractNativeEventFilter *g_shellFrameFilter;
extern QObject *g_obsEmbedFilter;

} // namespace rk
