// the program projector that share preview hands to discord
#pragma once

namespace rk {

// opens it see-through on the ui thread and returns its window, so the helper parks that exact one; null when obs showed no projector while it was being opened
HWND OpenShareProjectorHidden();

// called when obs starts closing; from then on the share projector is not opened, since obs deletes its projectors while closing and its main window after, and the qt quit that follows closes every window still open, so one opened in between takes that close after the main window is gone and crashes obs in OBSProjector::EscapeTriggered
void StopOpeningShareProjector();

} // namespace rk
