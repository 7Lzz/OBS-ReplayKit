// the plugin side of the share window: finds the window the helper started, claims its shared state, and copies every program frame into the texture the window owns, so discord keeps streaming that window while obs restarts
#pragma once

#include "share_protocol.hpp"

namespace rk {

// starts the once a second look for the window, from obs_module_load
void ShareLinkStart();
void ReleaseShareGraphics();
// obs is closing: the window is told to show its card and let go, before obs tears its scenes down
void ShareLinkStop();
QJsonObject ShareLinkProbe();

struct ShareLinkView {
	share::SharedState *state = nullptr;
	HANDLE frameEvent = nullptr;
	HANDLE audioEvent = nullptr;
	// bumps on every attach, so a user of the state can tell a new window from the one it set up
	uint32_t serial = 0;
};

// holds the link for as long as it lives, so the window cannot be let go of under the caller; state is null while no window is attached
class ShareLinkLock {
public:
	ShareLinkLock();
	~ShareLinkLock();
	ShareLinkLock(const ShareLinkLock &) = delete;
	ShareLinkLock &operator=(const ShareLinkLock &) = delete;
	const ShareLinkView &View() const;
};

} // namespace rk
