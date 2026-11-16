// draws the be right back card of the share window into a png, from a request of the helper: the helper owns what the card says and shows, and qt here has the svg, font and image drawing the window itself has none of
#pragma once

#include <string>

namespace rk {

// a SHARECARD request from the helper, one line of json; false when the request is refused or the card could not be written
bool RenderShareCard(const std::string &json);

} // namespace rk
