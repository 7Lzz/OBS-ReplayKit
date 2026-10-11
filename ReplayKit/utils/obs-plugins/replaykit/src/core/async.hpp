// background worker pool for the blocking http calls
#pragma once

#include <functional>

namespace rk {

void RunAsync(std::function<void()> work);
void StopWorkers();
void StartWorkers();

} // namespace rk
