// keeps the profile on video encoders obs could load this session, and tells the helper what it found
#pragma once

namespace rk {

// runs in obs_module_post_load, after every module loaded and before obs builds its outputs from the profile
void EnsureUsableEncoders();
// runs at FINISHED_LOADING: sends the helper the encoders obs has and what was replaced, so its settings never name a missing one and it can tell the person why
void ReportEncoders();

} // namespace rk
