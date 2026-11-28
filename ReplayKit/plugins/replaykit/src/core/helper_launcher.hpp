// starts the helper with obs and keeps it running: a helper that stops answering is asked to shut down and started again, and one that never ends is stopped after a grace
#pragma once

namespace rk {

// writes the config of the helper and starts it, then watches it from then on
void HelperLauncherStart();
// stops the watching and asks the helper to shut down, when obs exits
void HelperLauncherStop();

} // namespace rk
