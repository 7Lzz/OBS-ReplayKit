// the program projector that share preview hands to discord
#pragma once

namespace rk {

// opens it see-through on the ui thread; false when obs opened it but it could not be made see-through before it showed
bool OpenShareProjectorHidden();

} // namespace rk
