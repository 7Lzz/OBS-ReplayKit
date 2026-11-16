// the log of the share window, in the replaykit logs folder under %TEMP%, where the helper reads it for the sdk
#pragma once

namespace rk::share {

void LogOpen();
void Log(const char *format, ...);

} // namespace rk::share
