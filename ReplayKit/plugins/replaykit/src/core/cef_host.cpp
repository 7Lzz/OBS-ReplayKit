// obs-browser cef handle, its blocking start-up and the hidden prewarm window
#include "prelude.hpp"
#include "core/cef_host.hpp"

#include <QLineEdit>

#include "core/async.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

std::mutex g_cefMutex;
bool g_cefInitTried = false;

// invisible throwaway browser to eat the first-ever cef surfaces one-time cost -- the real clips window paints only a corner and stays white the first time each obs session, and this burns that bad first attempt off-screen instead of chasing the exact cef-internal cause
bool g_cefPrewarmed = false;

// the page of a window that is warmed up says it is up by setting its title once two frames of it were drawn, or half a second after the load when no frame comes, since a page that is not drawing never gets to one
const char kReadyTitle[] = "rk-ready";
const char kReadyScript[] = "(function(){var done=false;function go(){if(done)return;done=true;document.title='rk-ready';}requestAnimationFrame(function(){requestAnimationFrame(go);});setTimeout(go,500);})();";

} // namespace

QCef *g_cef = nullptr;
QPointer<QWidget> g_prewarmWindow;

namespace {
bool g_cefClosed = false;
} // namespace

bool CefOpen()
{
	return g_cef && !g_cefClosed;
}

void CefCloseForShutdown()
{
	g_cefClosed = true;
}

void RunBrowserScript(QCefWidget *browser, const std::string &script)
{
	// Closing a CEF widget runs a nested event loop while its main frame is disappearing.
	if (CefOpen() && browser)
		browser->executeJavaScript(script);
}

// background-thread only, can block for real time on a cold start -- waits for cefs background thread so widgets never hit the "first open only" white window race from being created before cef is ready
bool EnsureCefReadyBlocking()
{
	std::lock_guard<std::mutex> lock(g_cefMutex);
	if (!g_cefInitTried) {
		g_cefInitTried = true;
		g_cef = obs_browser_init_panel();
	}
	if (!g_cef)
		return false;
	g_cef->init_browser();
	return g_cef->wait_for_browser_init();
}

void PrewarmCefBrowser()
{
	if (g_cefPrewarmed)
		return;
	g_cefPrewarmed = true;

	RunAsync([]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[cefReady]() {
				if (!cefReady || !CefOpen())
					return;

				QWidget *warmWin = new QWidget(nullptr);
				warmWin->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint);
				warmWin->move(-10000, -10000);
				warmWin->resize(64, 64);

				QVBoxLayout *layout = new QVBoxLayout(warmWin);
				layout->setContentsMargins(0, 0, 0, 0);
				QCefWidget *browser = g_cef->create_widget(warmWin, "about:blank", nullptr);
				layout->addWidget(browser);

				warmWin->show();
				g_prewarmWindow = warmWin;
				QTimer::singleShot(3000, warmWin, &QObject::deleteLater);
			},
			Qt::QueuedConnection);
	});
}

QPoint CefOutOfSight(const QWidget *beside)
{
	int bottom = 0;
	for (const QScreen *screen : QGuiApplication::screens())
		bottom = qMax(bottom, screen->geometry().bottom());
	return QPoint(beside ? beside->frameGeometry().left() : 0, bottom + 400);
}

bool CefWatchPageReady(QCefWidget *browser, QWidget *owner, std::function<void()> ready)
{
	if (!browser || !owner)
		return false;
	browser->setStartupScript(kReadyScript);
	// the title of the page reaches this plugin as a signal that only exists at run time, since the meta object of the widget lives in obs-browser, so it is joined by name; a line edit that is never shown turns it into a signal that a function can be connected to here
	QLineEdit *probe = new QLineEdit(owner);
	probe->hide();
	if (!QObject::connect(browser, SIGNAL(titleChanged(QString)), probe, SLOT(setText(QString)))) {
		delete probe;
		return false;
	}
	QObject::connect(probe, &QLineEdit::textChanged, owner, [ready](const QString &text) {
		if (text == QLatin1String(kReadyTitle))
			ready();
	});
	return true;
}

} // namespace rk
