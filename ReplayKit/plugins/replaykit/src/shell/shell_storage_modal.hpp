// the card that frees up space, drawn over a dimmed obs window
#pragma once

class QJsonObject;

namespace rk {

// builds the card while the pointer is over the storage figure; its page starts with the warm-up of the shell, or with the first open
void StorageModalPrepare();
void StorageModalOpen();
// starts the page of the card out of sight, so the first open finds it drawn; true while that is still to come or on its way
bool StorageModalWarmNext();
// how the warm-up of the card stands, for the command channel
QJsonObject StorageModalWarmReport();
// the obs window moved or changed size: the dim and the card go with it, placed from the window rectangle windows reports
void StorageModalFollow();
// the card itself asks for this (its close button, escape, go to library), through the helper and the ipc pipe
void StorageModalClose();
bool StorageModalIsOpen();
// whether the card is open and shows nothing, and how many times it was built again for that
QJsonObject StorageModalProbe();
// deletes the windows; runs before cef shuts down
void StorageModalDestroy();

} // namespace rk
