// keeps the caption overlays glued to their window during a move loop
#include "prelude.hpp"
#include "chrome/caption_track_filter.hpp"

#include "chrome/title_keybind_badge.hpp"
#include "notify/notify_button.hpp"

namespace rk {

bool NotifyTrackFilter::nativeEventFilter(const QByteArray &, void *message, qintptr *)
{
	MSG *msg = static_cast<MSG *>(message);
	if (!msg)
		return false;
	if (msg->message != WM_WINDOWPOSCHANGED && msg->message != WM_MOVE && msg->message != WM_SIZE)
		return false;
	for (auto &btn : g_notifyButtons) {
		if (!btn || !btn->target())
			continue;
		if ((HWND)btn->target()->winId() != msg->hwnd)
			continue;
		PositionNotifyButton(btn.data());
		break;
	}
	for (auto &badge : g_titleKeybindBadges) {
		if (!badge || !badge->target())
			continue;
		if ((HWND)badge->target()->winId() != msg->hwnd)
			continue;
		PositionTitleKeybindBadge(badge.data());
		break;
	}
	return false;
}

} // namespace rk
