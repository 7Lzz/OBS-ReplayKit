// a notification too long for the bell panel, read in full on a card over the dimmed obs window
#pragma once

#include <string>

namespace rk {

// id is the notification to show, already checked to be letters, digits, - and _
void NotifyReaderOpen(const std::string &id);
// the page of the card asks for this (its close button, escape), through the helper and the ipc pipe
void NotifyReaderClose();
bool NotifyReaderWarmNext();
void NotifyReaderFollow();
void NotifyReaderDestroy();

} // namespace rk
