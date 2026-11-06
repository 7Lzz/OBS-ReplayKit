// the cef notification panel under the bell
#pragma once

#include "notify/notify_button.hpp"

namespace rk {

// the bell rides in the title bar of the three windows we own (obs main, Clips, ReplayKit Settings). windows gives no way to add a real caption button, so each one is a tiny frameless always-on-top widget parked over the caption strip just left of the system minimize button, kept in step with its target by an event filter on move/resize/show. the unread count comes from the helper; clicking opens the panel window

extern int g_notifyUnread;
void PollNotificationCount();
// opens the panel under the bell, or closes it when it is already open
void ShowNotificationPanel(NotifyButton *bell);

} // namespace rk
