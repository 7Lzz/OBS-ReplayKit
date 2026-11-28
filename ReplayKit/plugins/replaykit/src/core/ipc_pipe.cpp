// named pipe server the helper connects to
#include "prelude.hpp"
#include "core/ipc_pipe.hpp"

#include "app/ipc_commands.hpp"
#include "app/capture_context.hpp"
#include "core/shared_values.hpp"
#include "shell/shell_done_popup.hpp"

namespace rk {

namespace {

// ipc pipe: this plugin is the server (kIpcPipeName), the helper connects as a client and reconnects on its own. the gui thread fills g_mainWinValue; the pipe thread forwards it to the helper and hands each inbound line to PipeDispatchLine. the thread sleeps until a line comes in, something is queued for the helper (PipeWake) or the plugin stops, so a request is handled the moment it arrives and an idle pipe costs nothing
std::thread g_pipeThread;
std::atomic<bool> g_pipeStop{false};
HANDLE g_stopEvent = nullptr;
HANDLE g_wakeEvent = nullptr;
std::mutex configRepliesLock;
std::vector<std::string> configReplies;

// waits for an overlapped call to finish, or gives it up when the plugin stops; false when it failed or was given up
bool Finish(HANDLE pipe, OVERLAPPED &overlapped, DWORD &bytes)
{
	const HANDLE waits[] = {overlapped.hEvent, g_stopEvent};
	if (WaitForMultipleObjects(2, waits, FALSE, INFINITE) != WAIT_OBJECT_0) {
		CancelIoEx(pipe, &overlapped);
		GetOverlappedResult(pipe, &overlapped, &bytes, TRUE);
		return false;
	}
	return GetOverlappedResult(pipe, &overlapped, &bytes, FALSE) != FALSE;
}

// writes one newline-terminated line to the connected helper. false on any write failure so the caller drops the connection and waits for a reconnect.
bool PipeWriteLine(HANDLE pipe, const std::string &line)
{
	std::string framed = line;
	framed.push_back('\n');
	OVERLAPPED overlapped{};
	overlapped.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (!overlapped.hEvent)
		return false;
	const char *p = framed.data();
	size_t left = framed.size();
	bool ok = true;
	while (ok && left > 0) {
		DWORD written = 0;
		ResetEvent(overlapped.hEvent);
		if (!WriteFile(pipe, p, (DWORD)left, &written, &overlapped))
			ok = GetLastError() == ERROR_IO_PENDING && Finish(pipe, overlapped, written);
		ok = ok && written > 0;
		p += written;
		left -= written;
	}
	CloseHandle(overlapped.hEvent);
	return ok;
}

// everything other threads queued for the helper since the last pass
bool FlushOutbound(HANDLE pipe, bool &mainWinSent)
{
	for (const std::string &message : CaptureContextMessages())
		if (!PipeWriteLine(pipe, message))
			return false;
	for (const std::string &message : TakeDonePopupAcks())
		if (!PipeWriteLine(pipe, message))
			return false;
	quintptr mw = g_mainWinValue.load();
	if (!mainWinSent && mw != 0) {
		if (!PipeWriteLine(pipe, "MAINWIN " + std::to_string((unsigned long long)mw)))
			return false;
		mainWinSent = true;
	}
	if (g_pipeSendAllowCloseAck.exchange(false) && !PipeWriteLine(pipe, "ALLOWCLOSE_ACK"))
		return false;
	if (g_pipeSendOutputsReleasedAck.exchange(false) && !PipeWriteLine(pipe, "RELEASEOUTPUTS_ACK"))
		return false;
	std::vector<std::string> replies;
	{
		std::lock_guard<std::mutex> lock(configRepliesLock);
		replies.swap(configReplies);
	}
	for (const auto &reply : replies)
		if (!PipeWriteLine(pipe, reply))
			return false;
	const int shareCard = g_pipeShareCardAck.exchange(-1);
	if (shareCard >= 0 && !PipeWriteLine(pipe, "SHARECARD_ACK " + std::to_string(shareCard)))
		return false;
	return true;
}

// serves one connected helper until it disconnects or the plugin shuts down. LOCALE and MAINWIN go once per connection.
void PipeServeClient(HANDLE pipe)
{
	bool mainWinSent = false;
	std::string inbound;
	const char *locale = obs_get_locale();
	if (!PipeWriteLine(pipe, std::string("LOCALE ") + (locale ? locale : "")))
		return;

	OVERLAPPED read{};
	read.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (!read.hEvent)
		return;
	char buf[4096];
	bool reading = false;
	while (!g_pipeStop.load()) {
		if (!FlushOutbound(pipe, mainWinSent))
			break;
		if (!reading) {
			ResetEvent(read.hEvent);
			DWORD got = 0;
			if (!ReadFile(pipe, buf, sizeof(buf), &got, &read) && GetLastError() != ERROR_IO_PENDING)
				break;
			reading = true;
		}
		const HANDLE waits[] = {read.hEvent, g_wakeEvent, g_stopEvent};
		const DWORD woke = WaitForMultipleObjects(3, waits, FALSE, INFINITE);
		if (woke == WAIT_OBJECT_0 + 2)
			break;
		if (woke != WAIT_OBJECT_0)
			continue;
		DWORD got = 0;
		reading = false;
		if (!GetOverlappedResult(pipe, &read, &got, FALSE) || got == 0)
			break;
		inbound.append(buf, got);
		size_t nl;
		while ((nl = inbound.find('\n')) != std::string::npos) {
			std::string one = inbound.substr(0, nl);
			if (!one.empty() && one.back() == '\r')
				one.pop_back();
			inbound.erase(0, nl + 1);
			PipeDispatchLine(one);
		}
		// the limit is for one line, so lines that arrive in the same pass never add up to it; only the line that has no end yet counts, with room for its carriage return
		if (inbound.size() > size_t(kIpcMaxLineBytes) + 1)
			break;
	}
	if (reading) {
		DWORD got = 0;
		CancelIoEx(pipe, &read);
		GetOverlappedResult(pipe, &read, &got, TRUE);
	}
	CloseHandle(read.hEvent);
}

// the pipe server: one instance, recreated after every client disconnect so a helper hot-swap just reconnects.
void PipeServerThread()
{
	while (!g_pipeStop.load()) {
		HANDLE pipe = CreateNamedPipeW(kIpcPipeName, PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
					       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 64 * 1024, 64 * 1024,
					       0, nullptr);
		if (pipe == INVALID_HANDLE_VALUE) {
			if (WaitForSingleObject(g_stopEvent, 500) == WAIT_OBJECT_0)
				break;
			continue;
		}

		OVERLAPPED connect{};
		connect.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
		bool connected = false;
		if (connect.hEvent) {
			if (ConnectNamedPipe(pipe, &connect)) {
				connected = true;
			} else {
				const DWORD error = GetLastError();
				DWORD unused = 0;
				connected = error == ERROR_PIPE_CONNECTED || (error == ERROR_IO_PENDING && Finish(pipe, connect, unused));
			}
			CloseHandle(connect.hEvent);
		}
		if (g_pipeStop.load()) {
			DisconnectNamedPipe(pipe);
			CloseHandle(pipe);
			break;
		}
		if (!connected) {
			CloseHandle(pipe);
			if (WaitForSingleObject(g_stopEvent, 200) == WAIT_OBJECT_0)
				break;
			continue;
		}

		PipeServeClient(pipe);

		DisconnectNamedPipe(pipe);
		CloseHandle(pipe);
	}
}

} // namespace

std::atomic<quintptr> g_mainWinValue{0};
std::atomic<unsigned long long> g_allowCloseUntilMs{0};
std::atomic<bool> g_pipeSendAllowCloseAck{false};
std::atomic<bool> g_pipeSendOutputsReleasedAck{false};
std::atomic<int> g_pipeShareCardAck{-1};

void PipeWake()
{
	if (g_wakeEvent)
		SetEvent(g_wakeEvent);
}

void QueueConfigReply(const std::string &id, bool ok)
{
	{
		std::lock_guard<std::mutex> lock(configRepliesLock);
		if (configReplies.size() < 64)
			configReplies.push_back("CONFIG_ACK " + id + (ok ? " 1" : " 0"));
	}
	PipeWake();
}

// starts the thread that serves the helper; the stop flag is cleared first so a reload does not begin stopped
void StartPipeServer()
{
	g_pipeStop.store(false);
	g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	g_wakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
	g_pipeThread = std::thread(PipeServerThread);
}

// stops the pipe thread and waits for it, before the code it runs is unloaded
void StopPipeServer()
{
	g_pipeStop.store(true);
	if (g_stopEvent)
		SetEvent(g_stopEvent);
	if (g_pipeThread.joinable())
		g_pipeThread.join();
	for (HANDLE *event : {&g_stopEvent, &g_wakeEvent}) {
		if (*event)
			CloseHandle(*event);
		*event = nullptr;
	}
}

} // namespace rk
