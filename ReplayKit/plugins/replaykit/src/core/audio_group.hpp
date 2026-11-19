#pragma once

namespace rk {

// puts the obs audio session under the grouping id the role host also uses, so the windows mixer shows obs and its page host as one slider
void GroupAudioWithHost();
void StopAudioGroup();

} // namespace rk
