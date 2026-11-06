// background worker pool for the blocking http calls
#include "prelude.hpp"
#include "core/async.hpp"

namespace rk {

namespace {

std::mutex g_workerMutex;
std::vector<std::future<void>> g_workers;
bool g_workersStopping = false;

} // namespace

void RunAsync(std::function<void()> work)
{
	std::lock_guard<std::mutex> lock(g_workerMutex);
	if (g_workersStopping)
		return;
	for (auto it = g_workers.begin(); it != g_workers.end();) {
		if (it->wait_for(std::chrono::seconds(0)) == std::future_status::ready) {
			try { it->get(); } catch (...) { blog(LOG_WARNING, "ReplayKit background task failed"); }
			it = g_workers.erase(it);
		} else {
			++it;
		}
	}
	g_workers.emplace_back(std::async(std::launch::async, std::move(work)));
}

void StopWorkers()
{
	std::vector<std::future<void>> workers;
	{
		std::lock_guard<std::mutex> lock(g_workerMutex);
		g_workersStopping = true;
		workers.swap(g_workers);
	}
	for (auto &worker : workers) {
		try { worker.get(); } catch (...) { blog(LOG_WARNING, "ReplayKit background task failed"); }
	}
}

// lets a freshly loaded module queue work again after an earlier unload stopped the pool
void StartWorkers()
{
	std::lock_guard<std::mutex> lock(g_workerMutex);
	g_workersStopping = false;
}

} // namespace rk
