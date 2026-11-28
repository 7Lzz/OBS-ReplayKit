// starts obs again with administrator rights when Run OBS as administrator is on and this copy has none, or lacks the switches every launch by replaykit carries
#pragma once

namespace rk {

// runs while obs loads its modules, before the helper is up, so the setting is read from the settings file itself; a missing file or key reads as off, so a fresh or broken file never elevates on its own
void ElevateIfAsked();

} // namespace rk
