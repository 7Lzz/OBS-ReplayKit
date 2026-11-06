// blocking http calls to the replaykit helper
#pragma once

#include <QString>
#include <string>

// kHelperHost, kHelperPort and kStateFolder, written from shared/ by scripts/sync_shared.ps1
#include "core/shared_values.hpp"

namespace rk {

// the address of a helper route or page, path starting with a slash
std::string HelperUrl(const char *path);
QString HelperUrl(const QString &path);

bool HttpFocusWindowSucceeded(const char *path, int timeoutMs);
std::string HttpRequest(const char *method, const char *path, const char *jsonBody, int timeoutMs);

} // namespace rk
