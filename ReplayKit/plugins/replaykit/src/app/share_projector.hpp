// the program projector that share preview hands to discord
#pragma once

namespace rk {

// opens it see-through on the ui thread and returns its window, so the helper parks that exact one; null when obs showed no projector while it was being opened
HWND OpenShareProjectorHidden();

} // namespace rk
