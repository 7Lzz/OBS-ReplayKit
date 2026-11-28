// keeps desktop audio monitored while Share Preview is on, since the share window relays what obs monitors to discord, and unmonitored while it is off, while discord streams something else with its sound, or while desktop audio records an output device
#pragma once

#include <string>

namespace rk {

// once the scene collection is loaded: the mic is never monitored, and both audio sources list every discord build
void ShareMonitoringStart();
// takes Share Preview and the recording audio mode out of a settings read; a body without them leaves everything as it was
void ShareMonitoringApplySettings(const std::string &settingsBody);
// sets the monitoring of desktop audio to what it should be; nothing before the first settings read, so obs keeps what it saved until then
void ShareMonitoringEnsure();

} // namespace rk
