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
QPointer<QWidget> window;
QPointer<QCefWidget> browser;
QSet<QString> ready;
bool asleep = false;
// counts the wakes, so a load that waited for the helper is dropped once the browser has gone back to sleep or woken again
int wakeGen = 0;
const void *active = nullptr;
QString activeUrl;
std::function<void()> suspend;
// the window is cloaked from each pick of a page until the page says that pick has been drawn: the page shares its renderer with the pages of the shell, which keep it busy at startup, and the window came up empty until the pick got its turn
int pick = 0;
bool held = false;
// how long a page that never answers can keep the window off the screen
constexpr int kHoldCapMs = 2500;
// DWMWA_CLOAK: a cloaked window is still composed and painted, but not put on the screen
constexpr DWORD kDwmCloak = 13;

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
	if (!browser)
		return;
	QByteArray values = QJsonDocument(args).toJson(QJsonDocument::Compact);
	values = values.mid(1, values.size() - 2);
	RunBrowserScript(browser, "window.RKPopup && window.RKPopup." + std::string(method) + "(" + values.toStdString() + ");");
}

void Hold(bool on)
{
	if (!window || held == on)
		return;
	held = on;
	const BOOL value = on ? TRUE : FALSE;
	DwmSetWindowAttribute((HWND)window->winId(), kDwmCloak, &value, sizeof(value));
}

// a popup page that never came up at all (its load failed while the helper was down) answers nothing for the rest of the session, so it is loaded again once the helper answers, at most every half minute
QElapsedTimer reloadedDead;
void ReloadIfDead()
{
	if (!ready.isEmpty() || asleep || !browser || (reloadedDead.isValid() && reloadedDead.elapsed() < 30000))
		return;
	reloadedDead.start();
	const int gen = ++wakeGen;
	WhenHelperAnswers(120000, [gen](bool, int) {
		if (gen != wakeGen || asleep || !CefOpen() || !browser || !ready.isEmpty())
			return;
		blog(LOG_WARNING, "[replaykit] the popup page never came up, loading it again");
		browser->setURL(HelperUrl(kPopupPagePath));
	});
}

// asks the page for a pick and keeps the window cloaked until the page says that pick is drawn, or the cap runs out
void Pick(const QString &url)
{
	const int seq = ++pick;
	Hold(true);
	Call("select", {url, seq});
	QTimer::singleShot(kHoldCapMs, window, [seq]() {
		if (seq != pick || !held)
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
	if (window)
		return window;
	if (!owner || !g_cef->initialized())
		return nullptr;
	QWidget *host = ObsMainWindowQt();
	window = new QWidget(host ? host : owner);
	window->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	window->setAutoFillBackground(true);
	window->resize(680, 640);
	QPalette palette = window->palette();
	palette.setColor(QPalette::Window, g_menuPanel);
	window->setPalette(palette);
	auto *layout = new QVBoxLayout(window);
	layout->setContentsMargins(0, 0, 0, 0);
	browser = g_cef->create_widget(window, HelperUrl(kPopupPagePath), nullptr);
	layout->addWidget(browser);
	auto *probe = new QLineEdit(window);
	probe->hide();
	if (!QObject::connect(browser, SIGNAL(titleChanged(QString)), probe, SLOT(setText(QString)))) {
		Destroy();
		return nullptr;
	}
	// the title is the pages that are up, then after a bar the last pick the page has drawn
	QObject::connect(probe, &QLineEdit::textChanged, window, [](const QString &title) {
		// a report still on its way from the page that was just put to sleep says nothing about the blank page
		if (asleep || !title.startsWith(QLatin1String("rk-popup-ready:")))
			return;
		const int bar = title.indexOf(QLatin1Char('|'));
		QSet<QString> up;
		for (const QString &path : title.mid(15, bar < 0 ? -1 : bar - 15).split(QLatin1Char(','), Qt::SkipEmptyParts))
			if (!Page(HelperUrl(path)).isEmpty())
				up.insert(path);
		const bool changed = up != ready;
		ready = up;
		if (bar >= 0 && title.mid(bar + 1).toInt() == pick)
			Hold(false);
		// a page that came back up while it is the one showing is picked again
		if (changed && active)
			Call("select", {activeUrl});
	});
	return window;
}

QCefWidget *PopupHost::Browser() { return browser; }
bool PopupHost::Ready(const QString &url) { return window && ready.contains(Page(url)); }
bool PopupHost::Owns(const void *owner) { return owner && active == owner; }
bool PopupHost::Occupied() { return active != nullptr; }

bool PopupHost::Acquire(const void *owner, QWidget *windowOwner, const QString &url, const QSize &size, std::function<void()> suspended)
{
	if (!owner || Page(url).isEmpty() || !Prepare(windowOwner))
		return false;
	// something that opens while the window is still hidden finds its page loading, which the pick hold covers
	Wake();
	if (active && active != owner) {
		auto previous = std::move(suspend);
		if (previous)
			previous();
	}
	active = owner;
	activeUrl = url;
	suspend = std::move(suspended);
	window->resize(size);
	window->createWinId();
	SetWindowRgn((HWND)window->winId(), nullptr, TRUE);
	const DWORD square = 1;
	const COLORREF noBorder = 0xfffffffe;
	DwmSetWindowAttribute((HWND)window->winId(), 33, &square, sizeof(square));
	DwmSetWindowAttribute((HWND)window->winId(), 34, &noBorder, sizeof(noBorder));
	SetOwner(owner, windowOwner);
	Pick(url);
	return true;
}

void PopupHost::SetOwner(const void *owner, QWidget *windowOwner)
{
	if (Owns(owner) && window && windowOwner)
		SetWindowLongPtrW((HWND)window->winId(), GWLP_HWNDPARENT, (LONG_PTR)windowOwner->winId());
}

void PopupHost::SetUrl(const QString &url)
{
	if (Page(url).isEmpty())
		return;
	if (Page(activeUrl) == Page(url))
		activeUrl = url;
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
	if (window) {
		window->hide();
		Hold(false);
		// the owner it was lent to can be deleted next (a card that is built again deletes its dim), and windows destroys the windows a window owns along with it, the browser inside included, so it goes back to the obs window
		if (QWidget *host = window->parentWidget())
			SetWindowLongPtrW((HWND)window->winId(), GWLP_HWNDPARENT, (LONG_PTR)host->winId());
	}
	active = nullptr;
	activeUrl.clear();
	suspend = nullptr;
}

void PopupHost::Reload(const QString &url)
{
	const QString path = Page(url);
	if (path.isEmpty())
		return;
	ready.remove(path);
	Call("reload", {url});
}

void PopupHost::Destroy()
{
	active = nullptr;
	activeUrl.clear();
	suspend = nullptr;
	ready.clear();
	held = false;
	asleep = false;
	if (window)
		delete window.data();
	browser.clear();
}

void PopupHost::Sleep()
{
	if (!CefOpen() || !browser || asleep || active)
		return;
	asleep = true;
	++wakeGen;
	ready.clear();
	browser->setURL("about:blank");
	blog(LOG_INFO, "[replaykit] popup browser asleep");
}

void PopupHost::Wake()
{
	if (!asleep || !CefOpen() || !browser)
		return;
	asleep = false;
	ready.clear();
	const int gen = ++wakeGen;
	WhenHelperAnswers(120000, [gen](bool, int) {
		if (gen == wakeGen && !asleep && CefOpen() && browser)
			browser->setURL(HelperUrl(kPopupPagePath));
	});
	blog(LOG_INFO, "[replaykit] popup browser loading again after sleep");
}

bool PopupHost::Asleep() { return asleep; }

bool PopupHost::Revealed() { return !held; }

QJsonObject PopupHost::Probe()
{
	return {{"built", !window.isNull()}, {"visible", window && window->isVisible()}, {"parked", false}, {"asleep", asleep}, {"page", Page(activeUrl)},
		{"readyPages", ready.size()}, {"held", held}, {"surface", window ? QString::number(quintptr(window->internalWinId()), 16) : QString()}};
}

} // namespace rk
