// obs-browser cef handle, its blocking start-up and the hidden prewarm window
#include "prelude.hpp"
#include "core/cef_host.hpp"

#include <QByteArray>
#include <QLineEdit>

#include "core/async.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

std::mutex g_cefMutex;
bool g_cefInitTried = false;

// invisible throwaway browser to eat the first-ever cef surfaces one-time cost -- the real clips window paints only a corner and stays white the first time each obs session, and this burns that bad first attempt off-screen instead of chasing the exact cef-internal cause
bool g_cefPrewarmed = false;
bool g_cefPrewarmFinished = false;
bool g_cefClosed = false;
std::function<void()> g_afterCefPrewarm;
constexpr int kPrewarmFallbackMs = 800;
const char kPrewarmTitle[] = "rk-prewarm-painted";
const char kWarmPage[] = R"HTML(<!doctype html><meta charset="utf-8"><body style="margin:0;background:#272a33;color:#fff;font:12px sans-serif">Paint<script>
const paint = new PerformanceObserver(list => {
    if (!list.getEntries().some(entry => entry.name === "first-contentful-paint")) return;
    paint.disconnect();
    document.title = "rk-prewarm-painted";
});
paint.observe({type:"paint", buffered:true});
</script>)HTML";

// the page of a window that is warmed up says it is up by setting its title once two frames of it were drawn, or half a second after the load when no frame comes, since a page that is not drawing never gets to one
const char kReadyTitle[] = "rk-ready";
const char kReadyScript[] = "(function(){var done=false;function go(){if(done)return;done=true;document.title='rk-ready';}requestAnimationFrame(function(){requestAnimationFrame(go);});setTimeout(go,500);})();";

bool WatchBrowserTitle(QCefWidget *browser, QWidget *owner, const char *title, std::function<void()> ready)
{
	// obs-browser's title signal is only available through the runtime meta-object
	QLineEdit *probe = new QLineEdit(owner);
	probe->hide();
	if (!QObject::connect(browser, SIGNAL(titleChanged(QString)), probe, SLOT(setText(QString)))) {
		delete probe;
		return false;
	}
	QObject::connect(probe, &QLineEdit::textChanged, owner, [title, ready = std::move(ready)](const QString &text) {
		if (text == QLatin1String(title))
			ready();
	});
	return true;
}

void FinishCefPrewarm(const char *reason)
{
	if (g_cefPrewarmFinished || g_cefClosed)
		return;
	g_cefPrewarmFinished = true;
	blog(LOG_INFO, "[replaykit] CEF warm-up released the shell: %s", reason);
	auto ready = std::move(g_afterCefPrewarm);
	if (ready)
		ready();
}

} // namespace

QCef *g_cef = nullptr;
QPointer<QWidget> g_prewarmWindow;

bool CefOpen()
{
	return g_cef && !g_cefClosed;
}

void CefCloseForShutdown()
{
	g_cefClosed = true;
	g_afterCefPrewarm = {};
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
				warmWin->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::WindowDoesNotAcceptFocus);
				warmWin->setAttribute(Qt::WA_ShowWithoutActivating);
				warmWin->move(-10000, -10000);
				warmWin->resize(64, 64);

				QVBoxLayout *layout = new QVBoxLayout(warmWin);
				layout->setContentsMargins(0, 0, 0, 0);
				const std::string url = "data:text/html;base64," + QByteArray(kWarmPage).toBase64().toStdString();
				QCefWidget *browser = g_cef->create_widget(warmWin, url, nullptr);
				if (!browser) {
					delete warmWin;
					return;
				}
				layout->addWidget(browser);
				WatchBrowserTitle(browser, warmWin, kPrewarmTitle, []() { FinishCefPrewarm("content painted"); });
				g_prewarmWindow = warmWin;
				warmWin->show();
				QTimer::singleShot(3000, warmWin, &QObject::deleteLater);
			},
			Qt::QueuedConnection);
	});
}

bool CefPrewarmFinished()
{
	return g_cefPrewarmFinished && !g_cefClosed;
}

void AfterCefPrewarm(std::function<void()> ready)
{
	if (g_cefClosed)
		return;
	if (CefPrewarmFinished()) {
		ready();
		return;
	}
	g_afterCefPrewarm = std::move(ready);
	QTimer::singleShot(kPrewarmFallbackMs, g_callbacks, []() { FinishCefPrewarm("800 ms fallback"); });
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
	return WatchBrowserTitle(browser, owner, kReadyTitle, std::move(ready));
}

} // namespace rk
