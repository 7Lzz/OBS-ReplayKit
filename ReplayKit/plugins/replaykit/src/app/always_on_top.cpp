// keeps the obs window and the windows of replaykit above other windows while the always on top setting is on
#include "prelude.hpp"
#include "app/always_on_top.hpp"

#include "core/async.hpp"
#include "core/dev_log.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

// what the windows are kept at, so a window made between two polls follows it
bool g_onTop = false;
// set from the moment View > Always On Top was flipped in obs until the helper shows the same value
bool g_pushing = false;
QPointer<QAction> g_watched;

QAction *ObsEntry(QMainWindow *mw)
{
	return mw->findChild<QAction *>(QStringLiteral("actionAlwaysOnTop"));
}

bool IsTopmost(QWidget *window)
{
	return (GetWindowLongPtrW((HWND)window->winId(), GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
}

// the same call obs makes for its own entry, checked against the window on every poll since qt puts a window back below the topmost ones whenever it changes its frame
void SetTopmost(QWidget *window, bool on)
{
	if (!window || !window->testAttribute(Qt::WA_WState_Created) || IsTopmost(window) == on)
		return;
	SetWindowPos((HWND)window->winId(), on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}

// windows ignores topmost on a hidden window, so a window coming back from the tray or a close takes it once it is on screen; the show event comes before the window is, hence the queued call
class ShowFilter : public QObject {
public:
	using QObject::QObject;

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (event->type() == QEvent::Show) {
			QPointer<QWidget> window = qobject_cast<QWidget *>(watched);
			QTimer::singleShot(0, g_callbacks, [window]() { SetTopmost(window, g_onTop); });
		}
		return false;
	}
};

void WatchShows(QWidget *window)
{
	if (!window || window->property("rkAlwaysOnTopWatched").toBool())
		return;
	window->setProperty("rkAlwaysOnTopWatched", true);
	window->installEventFilter(new ShowFilter(window));
}

// the clips, settings and setup windows are not owned by the obs window, so they would open under it unless they are on top as well
void ApplyAll(QMainWindow *mw, QAction *entry)
{
	SetTopmost(mw, g_onTop);
	// obs keeps the check of its entry as AlwaysOnTop in user.ini and starts with it; setChecked fires toggled only, and obs follows the entry through triggered
	if (entry && entry->isChecked() != g_onTop)
		entry->setChecked(g_onTop);
	SetTopmost(g_clipsWindow, g_onTop);
	SetTopmost(g_settingsWindow, g_onTop);
	SetTopmost(g_setupWindow, g_onTop);
}

// obs answers its entry with a queued toggle of the window, and this queues after it, so the window already shows what obs made of the flip
void WatchObsEntry(QAction *entry)
{
	if (!entry || g_watched == entry)
		return;
	g_watched = entry;
	QObject::connect(entry, &QAction::triggered, g_callbacks, []() {
		QMetaObject::invokeMethod(
			g_callbacks,
			[]() {
				QMainWindow *mw = ObsMainWindowQt();
				if (!mw || !mw->testAttribute(Qt::WA_WState_Created))
					return;
				g_onTop = IsTopmost(mw);
				g_pushing = true;
				ApplyAll(mw, ObsEntry(mw));
				DevLog("always on top: set to %s in obs", g_onTop ? "on" : "off");
			},
			Qt::QueuedConnection);
	});
}

} // namespace

void AlwaysOnTopApplySettings(const std::string &settingsBody)
{
	// an answer without the field means the helper did not answer, and then nothing is changed
	const auto present = JsonBoolIfPresent(settingsBody, "alwaysOnTop");
	if (!present)
		return;
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	QAction *entry = ObsEntry(mw);
	WatchObsEntry(entry);
	WatchShows(mw);
	if (g_pushing) {
		if (*present == g_onTop) {
			g_pushing = false;
		} else {
			const std::string body = std::string("{\"alwaysOnTop\":") + (g_onTop ? "true" : "false") + "}";
			RunAsync([body]() { HttpRequest("POST", "/settings", body.c_str(), 3000); });
		}
	} else {
		g_onTop = *present;
	}
	ApplyAll(mw, entry);
}

void AlwaysOnTopFollow(QWidget *window)
{
	WatchShows(window);
	SetTopmost(window, g_onTop);
}

} // namespace rk
