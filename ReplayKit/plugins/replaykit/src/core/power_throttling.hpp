// opts obs out of the windows power throttling of background processes
#pragma once

namespace rk {

// keeps windows from running obs as a throttled background process while its window is hidden or covered, since the clip buffer and the frame copy to the share window have to keep pace either way; called once from obs_module_load
void OptOutOfPowerThrottling();

}
