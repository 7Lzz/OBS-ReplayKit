// the popup of a finished job, built like the notification reader: a window of its own over the dimmed obs window, since the preview of the projector view is a native window that covers anything the page of the shell draws
#include "prelude.hpp"
#include "shell/shell_done_popup.hpp"

#include "core/cef_host.hpp"
#include "core/helper_client.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_modal_card.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

ModalCard &Card()
{
	static ModalCard card(ModalCardSpec{
		"done popup",
		QStringLiteral("ReplayKit"),
		HelperUrl(QStringLiteral("/done-popup-view")),
		440,
		430,
		std::string(),
		"window.__replaykitHideDone && window.__replaykitHideDone();",
		nullptr,
	});
	return card;
}

std::mutex g_doneAckLock;
std::vector<std::string> g_doneAcks;

bool WindowOnScreen()
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && mw->isVisible() && !mw->isMinimized();
}

} // namespace

void DonePopupRequest(const std::string &id)
{
	const bool accepted = g_shellOn && CefOpen() && WindowOnScreen() && !ShellSetupLocked() && !Card().IsOpen();
	if (accepted)
		DonePopupOpen(id);
	std::lock_guard<std::mutex> guard(g_doneAckLock);
	if (g_doneAcks.size() < 64)
		g_doneAcks.push_back("DONEPOPUP_ACK " + id + (accepted ? " 1" : " 0"));
}

std::vector<std::string> TakeDonePopupAcks()
{
	std::lock_guard<std::mutex> guard(g_doneAckLock);
	std::vector<std::string> messages;
	messages.swap(g_doneAcks);
	return messages;
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
