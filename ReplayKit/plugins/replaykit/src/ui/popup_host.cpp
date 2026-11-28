// the shared popup window: panels and cards take turns owning it, and a new owner suspends the one before it
#include "prelude.hpp"
#include "ui/popup_host.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLineEdit>
#include <QSet>

#include "core/cef_host.hpp"
#include "core/helper_client.hpp"
#include "core/main_window.hpp"
#include "core/theme.hpp"

namespace rk {
namespace {

constexpr char kPopupPagePath[] = "/popup-view";
constexpr QLatin1String kReadyPrefix("rk-popup-ready:");
QPointer<QWidget> g_window;
QPointer<QCefWidget> g_browser;
QSet<QString> g_ready;
bool g_asleep = false;
// counts the wakes, so a load that waited for the helper is dropped once the browser has gone back to sleep or woken again
int g_wakeGen = 0;
const void *g_active = nullptr;
QString g_activeUrl;
std::function<void()> g_suspend;
// the window is cloaked from each pick of a page until the page says that pick has been drawn: the page shares its renderer with the pages of the shell, which keep it busy at startup, and the window came up empty until the pick got its turn
int g_pick = 0;
bool g_held = false;
// how long a page that never answers can keep the window off the screen
constexpr int kHoldCapMs = 2500;

QString Page(const QString &url)
{
	const QUrl parsed(url);
	if (parsed.scheme() != QLatin1String("http") || parsed.host() != QLatin1String(kHelperHost) || parsed.port() != kHelperPort || !parsed.userInfo().isEmpty())
		return {};
	const QString path = parsed.path();
	if (path == QLatin1String("/hotkey-menu") || path == QLatin1String("/notifications-view") || path == QLatin1String("/account-menu") || path == QLatin1String("/quick-actions-view") ||
	    path == QLatin1String("/storage-view") || path == QLatin1String("/notification-reader-view") || path == QLatin1String("/done-popup-view"))
		return path;
	return {};
}

void Call(const char *method, const QJsonArray &args)
{
	if (!g_browser)
		return;
	QByteArray values = QJsonDocument(args).toJson(QJsonDocument::Compact);
	values = values.mid(1, values.size() - 2);
	RunBrowserScript(g_browser, "window.RKPopup && window.RKPopup." + std::string(method) + "(" + values.toStdString() + ");");
}

void Hold(bool on)
{
	if (!g_window || g_held == on)
		return;
	g_held = on;
	// a cloaked window is still composed and painted, but not put on the screen
	const BOOL value = on ? TRUE : FALSE;
	DwmSetWindowAttribute((HWND)g_window->winId(), DWMWA_CLOAK, &value, sizeof(value));
}

// a popup page that never came up at all (its load failed while the helper was down) answers nothing for the rest of the session, so it is loaded again once the helper answers, at most every half minute
QElapsedTimer g_reloadedDead;
void ReloadIfDead()
{
	if (!g_ready.isEmpty() || g_asleep || !g_browser || (g_reloadedDead.isValid() && g_reloadedDead.elapsed() < 30000))
		return;
	g_reloadedDead.start();
	const int gen = ++g_wakeGen;
	WhenHelperAnswers(120000, [gen](bool, int) {
		if (gen != g_wakeGen || g_asleep || !CefOpen() || !g_browser || !g_ready.isEmpty())
			return;
		blog(LOG_WARNING, "[replaykit] the popup page never came up, loading it again");
		g_browser->setURL(HelperUrl(kPopupPagePath));
	});
}

// asks the page for a pick and keeps the window cloaked until the page says that pick is drawn, or the cap runs out
void Pick(const QString &url)
{
	const int seq = ++g_pick;
	Hold(true);
	Call("select", {url, seq});
	QTimer::singleShot(kHoldCapMs, g_window, [seq]() {
		if (seq != g_pick || !g_held)
			return;
		blog(LOG_WARNING, "[replaykit] the popup page did not draw its pick in time, so it is shown as it is");
		Hold(false);
		ReloadIfDead();
	});
}

} // namespace

QWidget *PopupHost::Prepare(QWidget *owner)
{
	if (!CefOpen())
		return nullptr;
	if (g_window)
		return g_window;
	if (!owner || !g_cef->initialized())
		return nullptr;
	QWidget *host = ObsMainWindowQt();
	g_window = new QWidget(host ? host : owner);
	g_window->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	g_window->setAutoFillBackground(true);
	g_window->resize(kDefaultSize);
	QPalette palette = g_window->palette();
	palette.setColor(QPalette::Window, g_menuPanel);
	g_window->setPalette(palette);
	auto *layout = new QVBoxLayout(g_window);
	layout->setContentsMargins(0, 0, 0, 0);
	g_browser = g_cef->create_widget(g_window, HelperUrl(kPopupPagePath), nullptr);
	layout->addWidget(g_browser);
	auto *probe = new QLineEdit(g_window);
	probe->hide();
	if (!QObject::connect(g_browser, SIGNAL(titleChanged(QString)), probe, SLOT(setText(QString)))) {
		Destroy();
		return nullptr;
	}
	// the title is the pages that are up, then after a bar the last pick the page has drawn
	QObject::connect(probe, &QLineEdit::textChanged, g_window, [](const QString &title) {
		// a report still on its way from the page that was just put to sleep says nothing about the blank page
		if (g_asleep || !title.startsWith(kReadyPrefix))
			return;
		const int bar = title.indexOf(QLatin1Char('|'));
		QSet<QString> up;
		for (const QString &path : title.mid(kReadyPrefix.size(), bar < 0 ? -1 : bar - kReadyPrefix.size()).split(QLatin1Char(','), Qt::SkipEmptyParts))
			if (!Page(HelperUrl(path)).isEmpty())
				up.insert(path);
		const bool changed = up != g_ready;
		g_ready = up;
		if (bar >= 0 && title.mid(bar + 1).toInt() == g_pick)
			Hold(false);
		// a page that came back up while it is the one showing is picked again, carrying the pending pick so a wake uncloaks once it draws instead of waiting for the timeout
		if (changed && g_active) {
			if (g_held && g_ready.contains(Page(g_activeUrl)))
				Call("select", {g_activeUrl, g_pick});
			else
				Call("select", {g_activeUrl});
		}
	});
	return g_window;
}

QCefWidget *PopupHost::Browser() { return g_browser; }
bool PopupHost::Ready(const QString &url) { return g_window && g_ready.contains(Page(url)); }
bool PopupHost::Owns(const void *owner) { return owner && g_active == owner; }
bool PopupHost::Occupied() { return g_active != nullptr; }

bool PopupHost::Acquire(const void *owner, QWidget *windowOwner, const QString &url, const QSize &size, std::function<void()> suspended)
{
	if (!owner || Page(url).isEmpty() || !Prepare(windowOwner))
		return false;
	// something that opens while the window is still hidden finds its page loading, which the pick hold covers
	Wake();
	if (g_active && g_active != owner) {
		auto previous = std::move(g_suspend);
		if (previous)
			previous();
	}
	g_active = owner;
	g_activeUrl = url;
	g_suspend = std::move(suspended);
	g_window->resize(size);
	g_window->createWinId();
	SetWindowRgn((HWND)g_window->winId(), nullptr, TRUE);
	const DWM_WINDOW_CORNER_PREFERENCE square = DWMWCP_DONOTROUND;
	const COLORREF noBorder = DWMWA_COLOR_NONE;
	DwmSetWindowAttribute((HWND)g_window->winId(), DWMWA_WINDOW_CORNER_PREFERENCE, &square, sizeof(square));
	DwmSetWindowAttribute((HWND)g_window->winId(), DWMWA_BORDER_COLOR, &noBorder, sizeof(noBorder));
	SetOwner(owner, windowOwner);
	Pick(url);
	return true;
}

void PopupHost::SetOwner(const void *owner, QWidget *windowOwner)
{
	if (Owns(owner) && g_window && windowOwner)
		SetWindowLongPtrW((HWND)g_window->winId(), GWLP_HWNDPARENT, (LONG_PTR)windowOwner->winId());
}

void PopupHost::SetUrl(const QString &url)
{
	if (Page(url).isEmpty())
		return;
	if (Page(g_activeUrl) == Page(url))
		g_activeUrl = url;
	Call("url", {url});
}

void PopupHost::Run(const QString &url, const QString &script)
{
	if (!Page(url).isEmpty() && !script.isEmpty())
		Call("run", {url, script});
}

void PopupHost::Release(const void *owner)
{
	if (!Owns(owner))
		return;
	Call("park", {});
	if (g_window) {
		g_window->hide();
		Hold(false);
		// the owner it was lent to can be deleted next (a card that is built again deletes its dim), and windows destroys the windows a window owns along with it, the browser inside included, so it goes back to the obs window
		if (QWidget *host = g_window->parentWidget())
			SetWindowLongPtrW((HWND)g_window->winId(), GWLP_HWNDPARENT, (LONG_PTR)host->winId());
	}
	g_active = nullptr;
	g_activeUrl.clear();
	g_suspend = nullptr;
}

void PopupHost::Reload(const QString &url)
{
	const QString path = Page(url);
	if (path.isEmpty())
		return;
	g_ready.remove(path);
	Call("reload", {url});
}

void PopupHost::Destroy()
{
	g_active = nullptr;
	g_activeUrl.clear();
	g_suspend = nullptr;
	g_ready.clear();
	g_held = false;
	g_asleep = false;
	if (g_window)
		delete g_window.data();
	g_browser.clear();
}

void PopupHost::Sleep()
{
	if (!CefOpen() || !g_browser || g_asleep || g_active)
		return;
	g_asleep = true;
	++g_wakeGen;
	g_ready.clear();
	g_browser->setURL("about:blank");
	blog(LOG_INFO, "[replaykit] popup browser asleep");
}

void PopupHost::Wake()
{
	if (!g_asleep || !CefOpen() || !g_browser)
		return;
	g_asleep = false;
	g_ready.clear();
	const int gen = ++g_wakeGen;
	WhenHelperAnswers(120000, [gen](bool, int) {
		if (gen == g_wakeGen && !g_asleep && CefOpen() && g_browser)
			g_browser->setURL(HelperUrl(kPopupPagePath));
	});
	blog(LOG_INFO, "[replaykit] popup browser loading again after sleep");
}

bool PopupHost::Asleep() { return g_asleep; }

bool PopupHost::Revealed() { return !g_held; }

QJsonObject PopupHost::Probe()
{
	return {{"built", !g_window.isNull()}, {"visible", g_window && g_window->isVisible()}, {"parked", false}, {"asleep", g_asleep}, {"page", Page(g_activeUrl)},
		{"readyPages", g_ready.size()}, {"held", g_held}, {"surface", g_window ? QString::number(quintptr(g_window->internalWinId()), 16) : QString()}};
}

} // namespace rk
