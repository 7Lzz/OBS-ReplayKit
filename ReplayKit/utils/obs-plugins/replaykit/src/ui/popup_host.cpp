// the shared popup window: panels and cards take turns owning it, and a new owner suspends the one before it
#include "prelude.hpp"
#include "ui/popup_host.hpp"

#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLineEdit>
#include <QSet>

#include "core/cef_host.hpp"
#include "core/main_window.hpp"
#include "core/theme.hpp"

namespace rk {
namespace {

QPointer<QWidget> window;
QPointer<QCefWidget> browser;
QSet<QString> ready;
const void *active = nullptr;
QString activeUrl;
std::function<void()> suspend;
bool parked = false;

QString Page(const QString &url)
{
	const QUrl parsed(url);
	if (parsed.scheme() != QLatin1String("http") || parsed.host() != QLatin1String("127.0.0.1") || parsed.port() != 8767 || !parsed.userInfo().isEmpty())
		return {};
	const QString path = parsed.path();
	if (path == QLatin1String("/hotkey-menu") || path == QLatin1String("/notifications-view") || path == QLatin1String("/account-menu") || path == QLatin1String("/quick-actions-view") ||
	    path == QLatin1String("/storage-view") || path == QLatin1String("/notification-reader-view"))
		return path;
	return {};
}

void Call(const char *method, const QJsonArray &args)
{
	if (!browser || parked)
		return;
	QByteArray values = QJsonDocument(args).toJson(QJsonDocument::Compact);
	values = values.mid(1, values.size() - 2);
	RunBrowserScript(browser, "window.RKPopup && window.RKPopup." + std::string(method) + "(" + values.toStdString() + ");");
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
	browser = g_cef->create_widget(window, "http://127.0.0.1:8767/popup-view", nullptr);
	layout->addWidget(browser);
	auto *probe = new QLineEdit(window);
	probe->hide();
	if (!QObject::connect(browser, SIGNAL(titleChanged(QString)), probe, SLOT(setText(QString)))) {
		Destroy();
		return nullptr;
	}
	QObject::connect(probe, &QLineEdit::textChanged, window, [](const QString &title) {
		if (!title.startsWith(QLatin1String("rk-popup-ready:")))
			return;
		ready.clear();
		for (const QString &path : title.mid(15).split(QLatin1Char(','), Qt::SkipEmptyParts))
			if (!Page(QStringLiteral("http://127.0.0.1:8767") + path).isEmpty())
				ready.insert(path);
		if (active)
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
	Call("select", {url});
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
	parked = false;
	if (window)
		delete window.data();
	browser.clear();
}

void PopupHost::Park()
{
	if (!browser || active || parked)
		return;
	parked = true;
	ready.clear();
	browser->closeBrowser();
}

void PopupHost::Unpark()
{
	parked = false;
}

QJsonObject PopupHost::Probe()
{
	return {{"built", !window.isNull()}, {"visible", window && window->isVisible()}, {"parked", parked}, {"page", Page(activeUrl)},
		{"readyPages", ready.size()}, {"surface", window ? QString::number(quintptr(window->internalWinId()), 16) : QString()}};
}

} // namespace rk
