#include <windows.h>

#include <chrono>
#include <string>

#include "control_pipe.hpp"
#include "log.hpp"
#include "share_window_values.hpp"

namespace rk::share {

namespace {

constexpr DWORD kMaxLine = 4096;

} // namespace

ControlPipe::~ControlPipe()
{
	Stop();
	if (ready)
		CloseHandle(ready);
	if (stopping)
		CloseHandle(stopping);
}

bool ControlPipe::Start(SECURITY_ATTRIBUTES *attributes)
{
	security = attributes;
	ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	stopping = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (!ready || !stopping)
		return false;
	thread = std::thread([this] { Serve(); });
	return true;
}

void ControlPipe::Stop()
{
	if (!thread.joinable())
		return;
	SetEvent(stopping);
	{
		std::lock_guard<std::mutex> guard(lock);
		for (Command *command : queue) {
			command->reply = "{\"ok\":false,\"message\":\"closing\"}";
			command->done = true;
		}
		queue.clear();
	}
	answered.notify_all();
	thread.join();
}

std::string ControlPipe::Ask(const std::string &line)
{
	Command command;
	command.line = line;
	{
		std::lock_guard<std::mutex> guard(lock);
		queue.push_back(&command);
	}
	SetEvent(ready);
	std::unique_lock<std::mutex> guard(lock);
	if (!answered.wait_for(guard, std::chrono::seconds(5), [&] { return command.done; })) {
		// one the main loop never took is taken back before it goes out of scope; one it is running right now is waited for, since the loop still writes its reply
		for (auto it = queue.begin(); it != queue.end(); ++it) {
			if (*it == &command) {
				queue.erase(it);
				return "{\"ok\":false,\"message\":\"the share window did not answer\"}";
			}
		}
		answered.wait(guard, [&] { return command.done; });
	}
	return command.reply;
}

void ControlPipe::Serve()
{
	const std::wstring name = std::wstring(L"\\\\.\\pipe\\") + kShareWindowPipeName;
	OVERLAPPED overlapped{};
	overlapped.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	while (WaitForSingleObject(stopping, 0) != WAIT_OBJECT_0) {
		// the first instance flag makes the create fail when another process already owns the name, so no one can sit in front of the window
		DWORD openMode = PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | (firstInstance ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0);
		HANDLE pipe = CreateNamedPipeW(name.c_str(), openMode, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, kMaxLine,
					       kMaxLine, 0, security);
		if (pipe == INVALID_HANDLE_VALUE) {
			Log("could not create the command pipe: %lu", GetLastError());
			if (WaitForSingleObject(stopping, 1000) == WAIT_OBJECT_0)
				break;
			continue;
		}
		firstInstance = false;
		ResetEvent(overlapped.hEvent);
		BOOL connected = ConnectNamedPipe(pipe, &overlapped);
		DWORD error = connected ? ERROR_PIPE_CONNECTED : GetLastError();
		if (error == ERROR_IO_PENDING) {
			HANDLE waits[] = {overlapped.hEvent, stopping};
			if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0) {
				CancelIo(pipe);
				CloseHandle(pipe);
				break;
			}
			DWORD ignored = 0;
			error = GetOverlappedResult(pipe, &overlapped, &ignored, FALSE) ? ERROR_PIPE_CONNECTED : GetLastError();
		}
		if (error != ERROR_PIPE_CONNECTED) {
			CloseHandle(pipe);
			continue;
		}

		std::string line;
		char buffer[512];
		bool complete = false;
		while (line.size() < kMaxLine) {
			DWORD read = 0;
			ResetEvent(overlapped.hEvent);
			if (!ReadFile(pipe, buffer, sizeof(buffer), &read, &overlapped)) {
				if (GetLastError() != ERROR_IO_PENDING)
					break;
				if (WaitForSingleObject(overlapped.hEvent, 3000) != WAIT_OBJECT_0) {
					CancelIo(pipe);
					break;
				}
				if (!GetOverlappedResult(pipe, &overlapped, &read, FALSE))
					break;
			}
			if (!read)
				break;
			line.append(buffer, read);
			size_t end = line.find('\n');
			if (end != std::string::npos) {
				line.resize(end);
				complete = true;
				break;
			}
		}
		if (!line.empty() && line.back() == '\r')
			line.pop_back();
		std::string reply = complete ? Ask(line) : std::string("{\"ok\":false,\"message\":\"the command was not one line\"}");
		reply += "\n";
		DWORD written = 0;
		ResetEvent(overlapped.hEvent);
		if (!WriteFile(pipe, reply.data(), DWORD(reply.size()), &written, &overlapped) && GetLastError() == ERROR_IO_PENDING) {
			if (WaitForSingleObject(overlapped.hEvent, 3000) != WAIT_OBJECT_0)
				CancelIo(pipe);
		}
		FlushFileBuffers(pipe);
		DisconnectNamedPipe(pipe);
		CloseHandle(pipe);
	}
	CloseHandle(overlapped.hEvent);
}

} // namespace rk::share
