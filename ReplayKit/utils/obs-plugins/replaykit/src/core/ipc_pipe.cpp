// named pipe server the helper connects to
#include "prelude.hpp"
#include "core/ipc_pipe.hpp"

#include "app/ipc_commands.hpp"

namespace rk {

namespace {

// ipc pipe: this plugin is the server, the helper connects as a client and reconnects on its own -- replaces the old scratch-file handoff. the gui thread fills g_mainWinValue / g_projectorCsv; the pipe thread forwards those to the helper and applies inbound OPENCLIPS / ALLOWCLOSE / CONFIRM <kind> (see PipeDispatchLine for the full list).
constexpr const wchar_t *kIpcPipeName = L"\\\\.\\pipe\\OBSReplayKitIpc";
std::thread g_pipeThread;
std::atomic<bool> g_pipeStop{false};
std::mutex g_projectorCsvMutex;
std::string g_projectorCsv;
bool g_projectorCsvReady = false;
// GetTickCount64() of the last PublishProjectorWindows run -- the pipe thread stops forwarding PROJECTORS if this goes stale (obs gui thread stalled), so the helper ages its snapshot out and falls back, same as the old file mtime.
unsigned long long g_projectorCsvAtMs = 0;

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

// serves one connected helper until it disconnects or the plugin shuts down. non-blocking: PeekNamedPipe drains inbound, small WriteFiles push outbound, a 40ms sleep paces the loop. MAINWIN goes once per connection, PROJECTORS about every 250ms (the cadence the old file write used).
void PipeServeClient(HANDLE pipe)
{
	bool mainWinSent = false;
	auto lastProjectors = std::chrono::steady_clock::now() - std::chrono::milliseconds(250);
	std::string inbound;

	while (!g_pipeStop.load()) {
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
		auto now = std::chrono::steady_clock::now();
		if (now - lastProjectors >= std::chrono::milliseconds(250)) {
			lastProjectors = now;
			std::string csv;
			bool fresh;
			{
				std::lock_guard<std::mutex> lock(g_projectorCsvMutex);
				csv = g_projectorCsv;
				fresh = g_projectorCsvReady && GetTickCount64() - g_projectorCsvAtMs <= 2000;
			}
			if (fresh && !PipeWriteLine(pipe, "PROJECTORS " + csv))
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

// obs marks every real projector window with windowHandle()->setProperty("isOBSProjectorWindow", true) -- see OBSProjector.cpp, which does this specifically so obss own code (SetDisplayAffinity) can recognize one reliably. thats a qt object property, invisible to plain win32 enumeration, so the helper (a separate process with no qt/obs-object access) cant read it directly -- this plugin can, since it runs inside obss own qt process. this refreshes the hwnd list the pipe thread streams to the helper so it can check the SAME authoritative signal obs uses internally instead of inferring "looks like a projector" from window class + ownership heuristics, which had real false-positive risk (NameDialog, the Scripts window, the auto-config wizard are independently-owned top-level windows too). must run on the gui thread for topLevelWindows()/winId().
void PublishProjectorWindows()
{
	QStringList hwnds;
	for (QWindow *w : QGuiApplication::topLevelWindows()) {
		if (w && w->property("isOBSProjectorWindow").toBool())
			hwnds << QString::number((quintptr)w->winId());
	}
	std::string csv = hwnds.join(',').toStdString();
	{
		std::lock_guard<std::mutex> lock(g_projectorCsvMutex);
		g_projectorCsv = csv;
		g_projectorCsvReady = true;
		g_projectorCsvAtMs = GetTickCount64();
	}
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
