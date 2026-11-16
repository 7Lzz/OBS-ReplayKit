// the command pipe the helper drives the window with: one line in, one line of json out, one command per connection, for the signed in user only
#pragma once

#include <windows.h>

#include <condition_variable>
#include <deque>
#include <mutex>
#include <string>
#include <thread>

namespace rk::share {

class ControlPipe {
public:
	struct Command {
		std::string line;
		std::string reply;
		bool done = false;
	};

	~ControlPipe();
	bool Start(SECURITY_ATTRIBUTES *security);
	void Stop();

	// set whenever a command waits for the main loop
	HANDLE Ready() const { return ready; }
	// runs every waiting command on the calling thread and hands back each reply
	template <typename Handler> void Drain(Handler handler)
	{
		std::unique_lock<std::mutex> guard(lock);
		while (!queue.empty()) {
			Command *command = queue.front();
			queue.pop_front();
			guard.unlock();
			std::string reply = handler(command->line);
			guard.lock();
			command->reply = std::move(reply);
			command->done = true;
			answered.notify_all();
		}
	}

private:
	void Serve();
	std::string Ask(const std::string &line);

	SECURITY_ATTRIBUTES *security = nullptr;
	HANDLE ready = nullptr;
	HANDLE stopping = nullptr;
	std::thread thread;
	std::mutex lock;
	std::condition_variable answered;
	std::deque<Command *> queue;
	bool firstInstance = true;
};

} // namespace rk::share
