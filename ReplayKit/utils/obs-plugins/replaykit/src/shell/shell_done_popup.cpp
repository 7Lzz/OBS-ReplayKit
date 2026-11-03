// the popup of a finished job, built like the notification reader: a window of its own over the dimmed obs window, since the preview of the record and projector views is a native window that covers anything the page of the shell draws
#include "prelude.hpp"
#include "shell/shell_done_popup.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_modal_card.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage_modal.hpp"

namespace rk {

namespace {

ModalCard &Card()
{
	static ModalCard card(ModalCardSpec{
		"done popup",
		QStringLiteral("ReplayKit"),
		QStringLiteral("http://127.0.0.1:8767/done-popup-view"),
		440,
		430,
		std::string(),
		"window.__replaykitHideDone && window.__replaykitHideDone();",
		nullptr,
	});
	return card;
}

// a popup that came while the window was minimized or hidden (a game in front, obs in the tray), waiting for the window to be back
std::string g_waiting;
QPointer<QTimer> g_waitTimer;

bool WindowOnScreen()
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && mw->isVisible() && !mw->isMinimized();
}

} // namespace

void DonePopupRequest(const std::string &id)
{
	// the card sits over the obs window of the clean layout; the classic layout has no place for it
	if (!g_shellOn)
		return;
	if (WindowOnScreen()) {
		g_waiting.clear();
		DonePopupOpen(id);
		return;
	}
	g_waiting = id;
	if (!g_waitTimer) {
		g_waitTimer = new QTimer(g_callbacks);
		g_waitTimer->setInterval(1000);
		QObject::connect(g_waitTimer, &QTimer::timeout, g_callbacks, []() {
			if (g_waiting.empty()) {
				g_waitTimer->stop();
				return;
			}
			if (!WindowOnScreen())
				return;
			const std::string shown = g_waiting;
			g_waiting.clear();
			g_waitTimer->stop();
			// a moment for the window to finish coming back before the card is laid over it
			QTimer::singleShot(400, g_callbacks, [shown]() { DonePopupOpen(shown); });
		});
	}
	g_waitTimer->start();
}

void DonePopupOpen(const std::string &id)
{
	// one card at a time over the window: a result that comes in takes the place of the storage card or the reader
	StorageModalClose();
	NotifyReaderClose();
	Card().Open("window.__replaykitShowDone && window.__replaykitShowDone('" + id + "');");
}

void DonePopupClose()
{
	Card().Close();
}

bool DonePopupWarmNext()
{
	return Card().WarmNext();
}

void DonePopupFollow()
{
	Card().Follow();
}

void DonePopupDestroy()
{
	Card().Destroy();
}

} // namespace rk
