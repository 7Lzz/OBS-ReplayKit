// the popup of a finished job (a gif exported, clips compressed, a driver to update), on a small card over the dimmed obs window, so it is seen from whichever view is up
#pragma once

#include <string>

namespace rk {

// id is the popup to show, already checked to be letters and digits
void DonePopupOpen(const std::string &id);
// asked for by the helper: shown now when the obs window is on screen, else kept (the newest one) and shown as soon as the window is back
void DonePopupRequest(const std::string &id);
// the page of the card asks for this (its buttons, escape), through the helper and the ipc pipe
void DonePopupClose();
bool DonePopupWarmNext();
void DonePopupFollow();
void DonePopupDestroy();

} // namespace rk
