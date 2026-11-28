// answers the requests the helper makes of obs (sources, filters, scene items, outputs, video and profile settings) over the pipe, in the request and response shapes of obs-websocket v5, which the helper used before
#pragma once

#include <string>

namespace rk {

// one request line from the pipe: "<id> <requestType> <json>"; runs on the interface thread and answers the helper over http
void ObsRequestDispatch(const std::string &line);
// obs is tearing its scenes down, so every request from now on is answered as unavailable
void ObsRequestsStop();

} // namespace rk
