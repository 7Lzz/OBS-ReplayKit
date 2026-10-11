// the installed replaykit version, asked from the helper and cached
#pragma once

#include <QString>
#include <functional>

namespace rk {

// "v1.2.6" once the helper has answered, empty before that
QString AppVersionText();
// asks the helper in the background; changed runs on the ui thread when the text is new
void RefreshAppVersion(std::function<void()> changed);

} // namespace rk
