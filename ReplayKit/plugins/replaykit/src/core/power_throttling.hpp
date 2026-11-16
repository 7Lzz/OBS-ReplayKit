#pragma once

namespace rk {

// keeps windows from running obs as a throttled background process while its window is hidden or covered, since the replay buffer and the share projector have to keep pace either way; called once from obs_module_load
void OptOutOfPowerThrottling();

}
