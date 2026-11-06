// puts the shell browser to sleep on a blank page while the obs window stays hidden, and loads it again when the window comes back
#include "prelude.hpp"
#include "shell/shell_sleep.hpp"

#include <QLineEdit>

#include "browser-panel.hpp"
#include "core/cef_host.hpp"
#include "core/json_lite.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_views.hpp"
#include "ui/popup_host.hpp"

namespace rk {

namespace {

// the page sets a title starting with this once none of its views holds work; a number follows so the same answer twice is still a change of title
constexpr char kReadyTitle[] = "rk-sleep-ready:";
// unknown until the helper has answered once
int g_sleepSeconds = -1;
bool g_asleep = false;
// a browser whose title cannot be followed never hears that its page is ready, so it is never put to sleep
bool g_followed = false;
QPointer<QTimer> g_sleepTimer;

bool WindowHidden()
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && (!mw->isVisible() || mw->isMinimized());
}

// the page answers through its title as soon as none of its views holds unsaved work or a running job, and keeps asking them until then
void AskForSleep()
{
	if (!CefOpen() || !g_followed || !g_shellOn || !g_shellHost || !g_shellHost->browser || g_asleep || !g_hostReady || g_sleepSeconds <= 0 || !WindowHidden())
		return;
	blog(LOG_INFO, "[replaykit] shell browser asked to sleep after %d s hidden", g_sleepSeconds);
	ShellRunJs(g_shellHost->browser, "window.RKShell && RKShell.sleep && RKShell.sleep();");
}

// obs-browser never reports the address of a panel, so the plugin itself navigates and keeps the state
void TitleChanged(const QString &title)
{
	if (!title.startsWith(QLatin1String(kReadyTitle)) || g_asleep || !CefOpen() || !g_shellOn || !g_shellHost || !g_shellHost->browser)
		return;
	// the window came back while the answer was on its way
	if (!WindowHidden())
		return;
	g_asleep = true;
	// nothing answers on the blank page, so the probe and the views wait for the page to come back
	g_hostReady = false;
	g_hostOnTop = false;
	ShellResetHostProbe();
	g_shellHost->browser->setURL("about:blank");
	blog(LOG_INFO, "[replaykit] shell browser asleep");
	// the menus and cards only show over the window, so their browser sleeps along unless one of them is using it
	PopupHost::Sleep();
}

} // namespace

bool ShellAsleep()
{
	return g_asleep;
}

void ShellSleepApplySettings(const std::string &settingsBody)
{
	const auto seconds = JsonIntIfPresent(settingsBody, "browserSleepSeconds");
	if (!seconds || *seconds < 0 || *seconds == g_sleepSeconds)
		return;
	g_sleepSeconds = *seconds;
	if (g_sleepTimer)
		g_sleepTimer->stop();
	ShellSleepWindowChanged();
}

void ShellSleepWindowChanged()
{
	if (!g_callbacks || !g_shellOn)
		return;
	if (!WindowHidden()) {
		if (g_sleepTimer)
			g_sleepTimer->stop();
		PopupHost::Wake();
		if (g_asleep) {
			g_asleep = false;
			ShellWakeHost();
		}
		return;
	}
	if (g_asleep || g_sleepSeconds <= 0)
		return;
	if (!g_sleepTimer) {
		g_sleepTimer = new QTimer(g_callbacks);
		g_sleepTimer->setSingleShot(true);
		QObject::connect(g_sleepTimer, &QTimer::timeout, g_callbacks, AskForSleep);
	}
	// a minimize that turns into a hide to the tray is still the same wait
	if (!g_sleepTimer->isActive())
		g_sleepTimer->start(g_sleepSeconds * 1000);
}

void ShellSleepWatch(QWidget *owner, QCefWidget *browser)
{
	g_asleep = false;
	// the title reaches this plugin as a signal that only exists at run time, like in CefWatchPageReady
	QLineEdit *title = new QLineEdit(owner);
	title->hide();
	g_followed = QObject::connect(browser, SIGNAL(titleChanged(QString)), title, SLOT(setText(QString)));
	if (!g_followed) {
		blog(LOG_WARNING, "[replaykit] shell browser title cannot be followed, so it never sleeps");
		delete title;
		return;
	}
	QObject::connect(title, &QLineEdit::textChanged, owner, TitleChanged);
}

} // namespace rk
