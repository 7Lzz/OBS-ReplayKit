// reacts to obs frontend events and wires the plugin together
#pragma once

#include <obs-frontend-api.h>

namespace rk {

void OnFrontendEvent(enum obs_frontend_event event, void *);
void ForgetProjectorTimer();
void RemoveNotifyTrackFilter();

} // namespace rk
