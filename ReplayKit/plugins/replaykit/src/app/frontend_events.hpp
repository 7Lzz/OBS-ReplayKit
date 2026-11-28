// reacts to obs frontend events and wires the plugin together
#pragma once

#include <obs-frontend-api.h>

class QWidget;

namespace rk {

void OnFrontendEvent(enum obs_frontend_event event, void *);
void RemoveNotifyTrackFilter();
// stops the ticks of what only the shown window draws while it is hidden or minimized, and catches them up once it shows again
void MainWindowShownChanged(QWidget *window);

} // namespace rk
