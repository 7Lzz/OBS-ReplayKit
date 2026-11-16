#include "prelude.hpp"
#include "core/power_throttling.hpp"

#include <QOperatingSystemVersion>

namespace rk {

void OptOutOfPowerThrottling()
{
	PROCESS_POWER_THROTTLING_STATE state{};
	state.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
	state.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
	// from windows 11 on, windows can also stop honouring the timer resolution a process asks for once none of its windows can be seen, and obs paces its frames with that timer; the flag does not exist before windows 11
	if (QOperatingSystemVersion::current() >= QOperatingSystemVersion::Windows11)
		state.ControlMask |= PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
	state.StateMask = 0;
	if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, sizeof(state)))
		blog(LOG_INFO, "[replaykit] obs opted out of windows power throttling (control mask 0x%lx)", state.ControlMask);
	else
		blog(LOG_WARNING, "[replaykit] could not opt obs out of windows power throttling: %lu", GetLastError());
}

}
