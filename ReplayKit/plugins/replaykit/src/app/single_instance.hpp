// keeps one obs with replaykit running: a second launch brings this window forward instead
#pragma once

namespace rk {

// watches for the "obs is already running" prompt of a second copy of this obs, brings this window forward and closes the prompt the way its Cancel does; runs from the end of loading until shutdown starts
void StartSingleInstanceWatch();
void StopSingleInstanceWatch();

} // namespace rk
