#include <windows.h>

#include <cstring>
#include <string>

#include "log.hpp"
#include "shared_state.hpp"
#include "share_window_values.hpp"

namespace rk::share {

namespace {

std::wstring LocalName(const wchar_t *name)
{
	return std::wstring(L"Local\\") + name;
}

} // namespace

SharedStateHost::~SharedStateHost()
{
	if (state) {
		state->producerPid.store(0);
		UnmapViewOfFile(state);
	}
	if (mapping)
		CloseHandle(mapping);
	if (frameEvent)
		CloseHandle(frameEvent);
	if (audioEvent)
		CloseHandle(audioEvent);
	if (producerProcess)
		CloseHandle(producerProcess);
}

bool SharedStateHost::Create(SECURITY_ATTRIBUTES *security, HWND window)
{
	const DWORD size = DWORD(sizeof(SharedState));
	mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, security, PAGE_READWRITE, 0, size, LocalName(kShareWindowStateMappingName).c_str());
	if (!mapping) {
		Log("could not create the shared state: %lu", GetLastError());
		return false;
	}
	// a mapping that already exists belongs to a plugin that still holds the one of a window before this one; it is started over, since only one window runs at a time
	bool existed = GetLastError() == ERROR_ALREADY_EXISTS;
	state = static_cast<SharedState *>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, size));
	if (!state) {
		Log("could not map the shared state: %lu", GetLastError());
		return false;
	}
	std::memset(static_cast<void *>(state), 0, size);
	state->protocol = kShareWindowProtocolVersion;
	state->size = size;
	state->windowPid = GetCurrentProcessId();
	state->windowHwnd = uint64_t(uintptr_t(window));
	// the magic goes last, so a plugin never takes a half written header for a ready one
	std::atomic_thread_fence(std::memory_order_release);
	state->magic = kMagic;

	frameEvent = CreateEventW(security, FALSE, FALSE, LocalName(kShareWindowFrameEventName).c_str());
	audioEvent = CreateEventW(security, FALSE, FALSE, LocalName(kShareWindowAudioEventName).c_str());
	if (!frameEvent || !audioEvent) {
		Log("could not create the share signals: %lu", GetLastError());
		return false;
	}
	Log("shared state ready (%lu bytes%s)", size, existed ? ", taken over from an earlier window" : "");
	return true;
}

void SharedStateHost::PublishTexture(uint32_t handle, uint32_t width, uint32_t height, uint32_t format, uint64_t adapterLuid)
{
	uint32_t seq = state->textureSeq.load();
	state->textureSeq.store(seq | 1);
	std::atomic_thread_fence(std::memory_order_release);
	state->textureHandle = handle;
	state->textureWidth = width;
	state->textureHeight = height;
	state->textureFormat = format;
	state->windowAdapterLuid = adapterLuid;
	std::atomic_thread_fence(std::memory_order_release);
	state->textureSeq.store((seq | 1) + 1);
}

uint32_t SharedStateHost::Generation() const
{
	return state->textureSeq.load() / 2;
}

uint32_t SharedStateHost::LiveProducer()
{
	uint32_t pid = state->producerPid.load();
	if (!pid) {
		if (producerProcess) {
			CloseHandle(producerProcess);
			producerProcess = nullptr;
			producerProcessPid = 0;
		}
		return 0;
	}
	if (pid != producerProcessPid) {
		if (producerProcess)
			CloseHandle(producerProcess);
		// limited query rights still open an admin obs from a normal window, which waiting rights do not
		producerProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
		producerProcessPid = pid;
		if (!producerProcess)
			return pid;
	}
	DWORD exitCode = STILL_ACTIVE;
	if (producerProcess && GetExitCodeProcess(producerProcess, &exitCode) && exitCode != STILL_ACTIVE) {
		Log("obs (pid %lu) exited without letting go of the share", pid);
		uint32_t expected = pid;
		state->producerPid.compare_exchange_strong(expected, 0);
		state->producerState.store(ProducerNone);
		CloseHandle(producerProcess);
		producerProcess = nullptr;
		producerProcessPid = 0;
		return 0;
	}
	return pid;
}

} // namespace rk::share
