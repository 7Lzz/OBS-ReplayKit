// the installed replaykit version, asked from the helper and cached
#pragma once

#include <QString>
#include <functional>

namespace rk {

// the labelled versions once the helper has answered, empty before that
QString AppVersionText();
QString ObsVersionText();
// asks the helper in the background; changed runs on the ui thread when the text is new
void RefreshAppVersion(std::function<void()> changed);

} // namespace rk
