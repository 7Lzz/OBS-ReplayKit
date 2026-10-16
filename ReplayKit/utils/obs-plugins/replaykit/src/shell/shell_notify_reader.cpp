// a notification too long for the bell panel, read in full on a card over the dimmed obs window
#include "prelude.hpp"
#include "shell/shell_notify_reader.hpp"

#include "shell/shell_modal_card.hpp"
#include "shell/shell_storage_modal.hpp"

namespace rk {

namespace {

// a window of its own like the storage card, since the preview of the projector view is a native window that covers anything the page of the shell draws
ModalCard &Card()
{
	static ModalCard card(ModalCardSpec{
		"notification reader",
		QStringLiteral("ReplayKit Notification"),
		QStringLiteral("http://127.0.0.1:8767/notification-reader-view"),
		680,
		640,
		std::string(),
		"window.__replaykitHideReader && window.__replaykitHideReader();",
		nullptr,
	});
	return card;
}

} // namespace

void NotifyReaderOpen(const std::string &id)
{
	// one card at a time over the window
	StorageModalClose();
	Card().Open("window.__replaykitShowReader && window.__replaykitShowReader('" + id + "');");
}

void NotifyReaderClose()
{
	Card().Close();
}

bool NotifyReaderWarmNext()
{
	return Card().WarmNext();
}

void NotifyReaderFollow()
{
	Card().Follow();
}

void NotifyReaderDestroy()
{
	Card().Destroy();
}

} // namespace rk
