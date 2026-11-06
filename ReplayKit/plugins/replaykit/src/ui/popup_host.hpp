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
	// the obs window stayed hidden: the browser goes to a blank page so its pages give their memory back, unless a panel or card is using it
	static void Sleep();
	// loads the pages again after Sleep; the panels wait for them as they do at startup
	static void Wake();
	static bool Asleep();
	// false while the window is kept off the screen until its page has drawn what it was asked to show
	static bool Revealed();
	static QJsonObject Probe();
};

} // namespace rk
