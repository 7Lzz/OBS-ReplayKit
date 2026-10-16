// the card that frees up space, drawn over a dimmed obs window
#include "prelude.hpp"
#include "shell/shell_storage_modal.hpp"

#include <QJsonObject>

#include "shell/shell_modal_card.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage.hpp"

namespace rk {

namespace {

ModalCard &Card()
{
	static ModalCard card(ModalCardSpec{
		"storage card",
		QStringLiteral("ReplayKit Storage"),
		QStringLiteral("http://127.0.0.1:8767/storage-view"),
		600,
		620,
		"window.__replaykitShowStorage && window.__replaykitShowStorage();",
		"window.__replaykitHideStorage && window.__replaykitHideStorage();",
		// the figure in the rail follows what was done in the card
		[]() { ShellStoragePoll(); },
	});
	return card;
}

} // namespace

void StorageModalPrepare()
{
	Card().Prepare();
}

bool StorageModalWarmNext()
{
	return Card().WarmNext();
}

QJsonObject StorageModalWarmReport()
{
	return Card().WarmReport();
}

bool StorageModalIsOpen()
{
	return Card().IsOpen();
}

void StorageModalFollow()
{
	Card().Follow();
}

void StorageModalOpen()
{
	// one card at a time over the window
	NotifyReaderClose();
	Card().Open();
}

void StorageModalClose()
{
	Card().Close();
}

QJsonObject StorageModalProbe()
{
	return Card().Probe();
}

void StorageModalDestroy()
{
	Card().Destroy();
}

} // namespace rk
