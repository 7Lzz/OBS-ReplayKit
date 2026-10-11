// what the interface does, written to the obs log while development mode and interface logging are both on
#pragma once

namespace rk {

void DevLogSetEnabled(bool on);
bool DevLogEnabled();
// printf style; a call while the logging is off costs one flag check
void DevLog(const char *format, ...);

} // namespace rk
