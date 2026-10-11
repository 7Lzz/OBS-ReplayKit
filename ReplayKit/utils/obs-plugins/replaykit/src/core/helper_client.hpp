// blocking http calls to the replaykit helper
#pragma once

#include <string>

namespace rk {

bool HttpFocusWindowSucceeded(const char *path, int port, int timeoutMs);
std::string HttpRequest(const char *method, const char *path, int port, const char *jsonBody, int timeoutMs);

} // namespace rk
