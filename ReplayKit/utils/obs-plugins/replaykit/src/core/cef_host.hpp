// obs-browser cef handle, its blocking start-up and the hidden prewarm window
#pragma once

#include <QPoint>
#include <QPointer>
#include <QWidget>
#include <functional>

#include "browser-panel.hpp"

namespace rk {

extern QCef *g_cef;
extern QPointer<QWidget> g_prewarmWindow;
// whether a cef widget may be made now: cef is there and the widgets have not been taken down for shutdown. the ipc pipe keeps running until the module unloads, and a command it queued before obs exited would otherwise build a browser after CloseCefWidgetsBeforeShutdown, which is the teardown race that function is there to avoid
bool CefOpen();
void CefCloseForShutdown();
void RunBrowserScript(QCefWidget *browser, const std::string &script);
bool EnsureCefReadyBlocking();
void PrewarmCefBrowser();

// a window that is shown out of sight for its page to start gets this long to say it is up before the wait is given up on, is left up this long after it did so the page has been drawn once, and gets this long when its widget cannot report its page at all
constexpr int kCefWarmCapMs = 2500;
constexpr int kCefWarmSettleMs = 150;
constexpr int kCefWarmBlindMs = 1500;
// a spot below every screen, level with the window given, for a window that has to be shown without being seen
QPoint CefOutOfSight(const QWidget *beside);
// has the page of a cef widget tell the plugin when it is up: the widget gets a start-up script that sets the title of the page once two frames of it were drawn, and ready runs when that title arrives. false when the widget cannot report titles, and then ready is never called
bool CefWatchPageReady(QCefWidget *browser, QWidget *owner, std::function<void()> ready);

} // namespace rk
