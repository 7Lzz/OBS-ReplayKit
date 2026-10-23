// one cef browser shared by the title bar menus and the shell cards -- each page is an iframe of popup.html, so they all live in a single renderer instead of one browser each
#pragma once

#include <QSize>
#include <QString>
#include <functional>

class QWidget;
class QCefWidget;
class QJsonObject;

namespace rk {

class PopupHost {
public:
	static QWidget *Prepare(QWidget *owner);
	static QCefWidget *Browser();
	static bool Ready(const QString &url);
	static bool Owns(const void *owner);
	static bool Occupied();
	static bool Acquire(const void *owner, QWidget *windowOwner, const QString &url, const QSize &size, std::function<void()> suspended);
	static void SetOwner(const void *owner, QWidget *windowOwner);
	static void SetUrl(const QString &url);
	static void Run(const QString &url, const QString &script);
	static void Release(const void *owner);
	static void Reload(const QString &url);
	static void Destroy();
	// the browser is closed while nothing uses it, the way obs closes the browser of a hidden dock; the widget stays, and builds a new browser at its pages the next time the warm-up shows it
	static void Park();
	static void Unpark();
	static QJsonObject Probe();
};

} // namespace rk
