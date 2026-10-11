// the cef notification panel under the bell
#include "prelude.hpp"
#include "notify/notify_panel.hpp"

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "notify/notify_button.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

bool g_notifyPollInFlight = false;

} // namespace

int g_notifyUnread = 0;

// the helper owns the list, so the count is polled rather than pushed -- one small request every few seconds, skipped while one is already in flight so a slow helper cannot stack them up.
void PollNotificationCount()
{
	if (g_notifyPollInFlight)
		return;
	g_notifyPollInFlight = true;
	RunAsync([]() {
		std::string body = HttpRequest("GET", "/notifications", 8767, nullptr, 2500);
		int unread = JsonIntField(body, "unread", -1);
		QMetaObject::invokeMethod(
			g_callbacks,
			[unread]() {
				g_notifyPollInFlight = false;
				if (unread < 0)
					return; // helper down or answering something else: keep the last known count
				if (unread == g_notifyUnread)
					return;
				g_notifyUnread = unread;
				RefreshNotifyBadges();
			},
			Qt::QueuedConnection);
	});
}

void ShowNotificationPanel(NotifyButton *bell)
{
	if (!bell)
		return;
	AnchoredPanel *panel = bell->EnsurePanel();
	const QPointer<NotifyButton> button(bell);

	AnchoredPanel::Anchor anchor;
	anchor.key = bell;
	// right edge flush with the pill, not the divider zone, and a pixel of overlap so the triggers open bottom edge covers the panels top border and the seam disappears -- what .clip-sort-menu does with top:calc(100% - 1px)
	anchor.edge = AnchoredPanel::Edge::Right;
	anchor.seam = 1;
	anchor.tabRect = [button]() {
		if (!button || !button->isVisible())
			return QRect();
		return QRect(button->GlobalRect().topLeft(), QSize(NotifyButton::kPillWidth, button->height()));
	};
	// an embedded bell has no window of its own, so it is compared in qt coordinates instead
	anchor.owns = [button](const POINT &pt, const QPoint &qtPoint) {
		if (!button)
			return false;
		if (button->IsEmbedded())
			return button->GlobalRect().contains(qtPoint);
		RECT bellRect;
		return GetWindowRect((HWND)button->winId(), &bellRect) && PtInRect(&bellRect, pt);
	};
	anchor.setOpen = [button](bool) {
		if (button)
			button->update();
	};
	anchor.onHidden = []() { PollNotificationCount(); };
	anchor.raiseControl = [button]() {
		if (button)
			button->raise();
	};
	panel->Toggle(anchor, QStringLiteral("window.__replaykitShowNotifications && window.__replaykitShowNotifications();"));
}

} // namespace rk
