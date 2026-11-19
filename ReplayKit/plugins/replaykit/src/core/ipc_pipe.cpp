// named pipe server the helper connects to
#include "prelude.hpp"
#include "core/ipc_pipe.hpp"

#include "app/ipc_commands.hpp"
#include "app/capture_context.hpp"
#include "shell/shell_done_popup.hpp"

namespace rk {

namespace {

// ipc pipe: this plugin is the server, the helper connects as a client and reconnects on its own -- replaces the old scratch-file handoff. the gui thread fills g_mainWinValue; the pipe thread forwards it to the helper and applies inbound OPENCLIPS / ALLOWCLOSE / CONFIRM <kind> (see PipeDispatchLine for the full list).
constexpr const wchar_t *kIpcPipeName = L"\\\\.\\pipe\\OBSReplayKitIpc";
std::thread g_pipeThread;
std::atomic<bool> g_pipeStop{false};
std::mutex configRepliesLock;
std::vector<std::string> configReplies;

// writes one newline-terminated line to the connected helper. false on any write failure so the caller drops the connection and waits for a reconnect.
bool PipeWriteLine(HANDLE pipe, const std::string &line)
{
	std::string framed = line;
	framed.push_back('\n');
	const char *p = framed.data();
	size_t left = framed.size();
	while (left > 0) {
		DWORD written = 0;
		if (!WriteFile(pipe, p, (DWORD)left, &written, nullptr) || written == 0)
			return false;
		p += written;
		left -= written;
	}
	return true;
}

// serves one connected helper until it disconnects or the plugin shuts down. non-blocking: PeekNamedPipe drains inbound, small WriteFiles push outbound, a 40ms sleep paces the loop. LOCALE and MAINWIN go once per connection.
void PipeServeClient(HANDLE pipe)
{
	bool mainWinSent = false;
	std::string inbound;
	const char *locale = obs_get_locale();
	if (!PipeWriteLine(pipe, std::string("LOCALE ") + (locale ? locale : "")))
		return;

	while (!g_pipeStop.load()) {
		for (const std::string &message : CaptureContextMessages())
			if (!PipeWriteLine(pipe, message))
				return;
		for (const std::string &message : TakeDonePopupAcks())
			if (!PipeWriteLine(pipe, message))
				return;
		quintptr mw = g_mainWinValue.load();
		if (!mainWinSent && mw != 0) {
			if (!PipeWriteLine(pipe, "MAINWIN " + std::to_string((unsigned long long)mw)))
				return;
			mainWinSent = true;
		}
		if (g_pipeSendAllowCloseAck.exchange(false)) {
			if (!PipeWriteLine(pipe, "ALLOWCLOSE_ACK"))
				return;
		}
		if (g_pipeSendOutputsReleasedAck.exchange(false)) {
			if (!PipeWriteLine(pipe, "RELEASEOUTPUTS_ACK"))
				return;
		}
		std::vector<std::string> replies;
		{
			std::lock_guard<std::mutex> lock(configRepliesLock);
			replies.swap(configReplies);
		}
		for (const auto &reply : replies) {
			if (!PipeWriteLine(pipe, reply))
				return;
		}
		const int shareCard = g_pipeShareCardAck.exchange(-1);
		if (shareCard >= 0) {
			if (!PipeWriteLine(pipe, "SHARECARD_ACK " + std::to_string(shareCard)))
				return;
		}

		DWORD avail = 0;
		if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &avail, nullptr))
			return; // broken pipe
		while (avail > 0) {
			char buf[1024];
			DWORD want = avail < sizeof(buf) ? avail : (DWORD)sizeof(buf);
			DWORD got = 0;
			if (!ReadFile(pipe, buf, want, &got, nullptr) || got == 0)
				return;
			inbound.append(buf, got);
			if (inbound.size() > 64 * 1024)
				return;
			avail -= got;
		}
		size_t nl;
		while ((nl = inbound.find('\n')) != std::string::npos) {
			std::string one = inbound.substr(0, nl);
			if (!one.empty() && one.back() == '\r')
				one.pop_back();
			inbound.erase(0, nl + 1);
			PipeDispatchLine(one);
		}

		std::this_thread::sleep_for(std::chrono::milliseconds(40));
	}
}

// the pipe server: one instance, recreated after every client disconnect so a helper hot-swap just reconnects.
void PipeServerThread()
{
	while (!g_pipeStop.load()) {
		HANDLE pipe = CreateNamedPipeW(kIpcPipeName, PIPE_ACCESS_DUPLEX,
					       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 64 * 1024, 64 * 1024,
					       0, nullptr);
		if (pipe == INVALID_HANDLE_VALUE) {
			std::this_thread::sleep_for(std::chrono::milliseconds(500));
			continue;
		}

		BOOL connected = ConnectNamedPipe(pipe, nullptr);
		if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
			connected = TRUE;
		// StopPipeServer cancels this wait with CancelSynchronousIo, which lands here as a failed connect with the stop flag already set.
		if (g_pipeStop.load()) {
			DisconnectNamedPipe(pipe);
			CloseHandle(pipe);
			break;
		}
		if (!connected) {
			CloseHandle(pipe);
			std::this_thread::sleep_for(std::chrono::milliseconds(200));
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

void QueueConfigReply(const std::string &id, bool ok)
{
	std::lock_guard<std::mutex> lock(configRepliesLock);
	if (configReplies.size() < 64)
		configReplies.push_back("CONFIG_ACK " + id + (ok ? " 1" : " 0"));
}

// starts the thread that serves the helper; the stop flag is cleared first so a reload does not begin stopped
void StartPipeServer()
{
	g_pipeStop.store(false);
	g_pipeThread = std::thread(PipeServerThread);
}

// stops the pipe thread and waits for it, before the code it runs is unloaded
void StopPipeServer()
{
	g_pipeStop.store(true);
	if (g_pipeThread.joinable()) {
		// cover a connect/write that starts just after the stop flag was set.
		while (WaitForSingleObject(g_pipeThread.native_handle(), 50) == WAIT_TIMEOUT)
			CancelSynchronousIo(g_pipeThread.native_handle());
		g_pipeThread.join();
	}
}

} // namespace rk
