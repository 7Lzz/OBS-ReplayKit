#include "prelude.hpp"
#include "core/idle_memory.hpp"

#include <QPixmapCache>
#include <filesystem>
#include <iphlpapi.h>
#include <psapi.h>
#include <tlhelp32.h>

#include "core/async.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_views.hpp"
#include "ui/popup_host.hpp"

namespace rk::IdleMemory {

namespace {

QPointer<QTimer> timer;
bool hidden = false;
bool stateKnown = false;
constexpr DWORD kHiddenDelayMs = 30000;
constexpr DWORD kVisibleCheckMs = 30000;
#ifndef RK_VISIBLE_IDLE_MS
#define RK_VISIBLE_IDLE_MS 120000
#endif
constexpr DWORD kVisibleIdleMs = RK_VISIBLE_IDLE_MS;
constexpr DWORD kMaxIdleMs = 0x7fffffff;
std::atomic<unsigned> generation{0};
std::atomic<bool> running{false}, stopping{false};
std::atomic<DWORD> lastVisibleTrimInputTick{0};
std::atomic<bool> hasVisibleTrimAttempt{false};
std::mutex resultMutex;

struct Memory {
	bool available = false;
	SIZE_T privateBytes = 0, workingSet = 0;
};

struct Result {
	unsigned runs = 0, visibleRuns = 0;
	DWORD error = 0, trimError = 0;
	qint64 elapsedMs = 0;
	Memory before, after;
	bool visibleMainTrimAttempted = false, visibleMainTrimmed = false;
	DWORD visibleMainTrimError = 0;
	Memory visibleBefore, visibleAfter;
	unsigned childProcesses = 0, childTrimmed = 0, childFailures = 0;
	DWORD childError = 0;
	Memory childrenBefore, childrenAfter;
} result;

struct ChildResult {
	unsigned processes = 0, trimmed = 0, failures = 0;
	DWORD error = 0;
	bool cancelled = false;
	Memory before, after;
};

Memory ReadMemory()
{
	PROCESS_MEMORY_COUNTERS_EX counters{};
	counters.cb = sizeof(counters);
	Memory memory;
	memory.available = GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&counters), sizeof(counters));
	if (memory.available) {
		memory.privateBytes = counters.PrivateUsage;
		memory.workingSet = counters.WorkingSetSize;
	}
	return memory;
}

DWORD HelperProcessId(DWORD &error)
{
	ULONG size = 0;
	DWORD result = GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
	if (result != ERROR_INSUFFICIENT_BUFFER) {
		error = result;
		return 0;
	}
	std::vector<BYTE> buffer(size);
	result = GetExtendedTcpTable(buffer.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
	if (result != NO_ERROR) {
		error = result;
		return 0;
	}
	const auto *table = reinterpret_cast<const MIB_TCPTABLE_OWNER_PID *>(buffer.data());
	for (DWORD i = 0; i < table->dwNumEntries; ++i) {
		if (ntohs(static_cast<u_short>(table->table[i].dwLocalPort)) == 8767) {
			error = 0;
			return table->table[i].dwOwningPid;
		}
	}
	error = ERROR_NOT_FOUND;
	return 0;
}

void AddMemory(Memory &memory, const PROCESS_MEMORY_COUNTERS_EX &counters)
{
	memory.available = true;
	memory.privateBytes += counters.PrivateUsage;
	memory.workingSet += counters.WorkingSetSize;
}

bool ReadUserIdle(DWORD &lastInputTick, DWORD &idleMs)
{
	LASTINPUTINFO input{};
	input.cbSize = sizeof(input);
	if (!GetLastInputInfo(&input))
		return false;
	idleMs = GetTickCount() - input.dwTime;
	if (idleMs > kMaxIdleMs)
		return false;
	lastInputTick = input.dwTime;
	return true;
}

bool UserStillIdle(DWORD lastInputTick)
{
	DWORD currentInputTick = 0, idleMs = 0;
	return ReadUserIdle(currentInputTick, idleMs) && currentInputTick == lastInputTick && idleMs >= kVisibleIdleMs;
}

bool ChildExecutable(const std::wstring &ownPath, const wchar_t *childPath)
{
	if (_wcsicmp(ownPath.c_str(), childPath) == 0)
		return true;
	const auto root = std::filesystem::path(ownPath).parent_path().parent_path().parent_path();
	for (const auto *relative : {L"obs-plugins\\64bit\\obs-browser-page.exe", L"core\\obs-browser\\obs-browser-page.exe", L"bin\\64bit\\replaykit-host\\obs64.exe"}) {
		if (_wcsicmp((root / relative).c_str(), childPath) == 0)
			return true;
	}
	return false;
}

ChildResult TrimChildProcesses(unsigned current, bool requireUserIdle = false, DWORD lastInputTick = 0)
{
	ChildResult result;
	DWORD error = 0;
	const DWORD helper = HelperProcessId(error);
	if (!helper) {
		result.error = error;
		return result;
	}

	std::vector<wchar_t> ownPath(32768);
	DWORD ownPathSize = static_cast<DWORD>(ownPath.size());
	if (!QueryFullProcessImageNameW(GetCurrentProcess(), 0, ownPath.data(), &ownPathSize)) {
		result.error = GetLastError();
		return result;
	}
	const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
	if (snapshot == INVALID_HANDLE_VALUE) {
		result.error = GetLastError();
		return result;
	}
	PROCESSENTRY32W process{};
	process.dwSize = sizeof(process);
	if (!Process32FirstW(snapshot, &process)) {
		result.error = GetLastError();
		CloseHandle(snapshot);
		return result;
	}
	do {
		if (stopping.load() || generation.load() != current || (requireUserIdle && !UserStillIdle(lastInputTick))) {
			result.cancelled = true;
			break;
		}
		if (process.th32ParentProcessID == GetCurrentProcessId() && process.th32ProcessID != helper) {
			HANDLE child = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, FALSE, process.th32ProcessID);
			if (!child) {
				++result.failures;
				result.error = GetLastError();
			} else {
				std::vector<wchar_t> childPath(32768);
				DWORD childPathSize = static_cast<DWORD>(childPath.size());
				const bool eligible = QueryFullProcessImageNameW(child, 0, childPath.data(), &childPathSize) &&
						      ChildExecutable(ownPath.data(), childPath.data());
				if (eligible) {
					++result.processes;
					PROCESS_MEMORY_COUNTERS_EX counters{};
					counters.cb = sizeof(counters);
					if (!GetProcessMemoryInfo(child, reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&counters), sizeof(counters))) {
						++result.failures;
						result.error = GetLastError();
					} else {
						AddMemory(result.before, counters);
						if (EmptyWorkingSet(child))
							++result.trimmed;
						else {
							++result.failures;
							result.error = GetLastError();
						}
						counters = {};
						counters.cb = sizeof(counters);
						if (GetProcessMemoryInfo(child, reinterpret_cast<PROCESS_MEMORY_COUNTERS *>(&counters), sizeof(counters)))
							AddMemory(result.after, counters);
						else {
							++result.failures;
							result.error = GetLastError();
						}
					}
				}
				CloseHandle(child);
			}
		}
		process.dwSize = sizeof(process);
	} while (Process32NextW(snapshot, &process));
	CloseHandle(snapshot);
	return result;
}

bool Hidden(QWidget *window)
{
	return window && (!window->isVisible() || window->isMinimized());
}

bool VisibleTrimAllowed(QWidget *window)
{
	return window && !Hidden(window) && g_shellOn && g_hostReady && g_shellView != ShellView::Setup && g_shellView != ShellView::ObsSettings && !g_settingsDirty &&
	       !g_setupPending && !g_clipsFullscreenActive && !g_shellNativeFullscreen && !ShellEditorActive() && !PopupHost::Occupied() && !StorageModalIsOpen();
}

void Collect();

void CollectVisibleChildren()
{
	QWidget *window = ObsMainWindowQt();
	if (stopping.load() || !VisibleTrimAllowed(window) || running.load())
		return;
	DWORD lastInputTick = 0, idleMs = 0;
	if (!ReadUserIdle(lastInputTick, idleMs) ||
	    !ShouldTrimVisible(idleMs, lastInputTick, lastVisibleTrimInputTick.load(), hasVisibleTrimAttempt.load(), kVisibleIdleMs) || running.exchange(true))
		return;
	lastVisibleTrimInputTick.store(lastInputTick);
	hasVisibleTrimAttempt.store(true);
	const unsigned current = generation.load();
	const auto started = std::chrono::steady_clock::now();
	RunAsync([current, lastInputTick, started]() {
		if (stopping.load() || generation.load() != current || !UserStillIdle(lastInputTick)) {
			running.store(false);
			return;
		}
		const ChildResult children = TrimChildProcesses(current, true, lastInputTick);
		bool mainTrimAttempted = false;
		bool mainTrimmed = false;
		DWORD mainTrimError = 0;
		Memory mainBefore, mainAfter;
		if (!stopping.load() && generation.load() == current && UserStillIdle(lastInputTick)) {
			mainTrimAttempted = true;
			mainBefore = ReadMemory();
			mainTrimmed = EmptyWorkingSet(GetCurrentProcess());
			mainTrimError = mainTrimmed ? 0 : GetLastError();
			mainAfter = ReadMemory();
		}
		const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
		{
			std::lock_guard<std::mutex> lock(resultMutex);
			++result.visibleRuns;
			result.elapsedMs = elapsed;
			result.visibleMainTrimAttempted = mainTrimAttempted;
			result.visibleMainTrimmed = mainTrimmed;
			result.visibleMainTrimError = mainTrimError;
			result.visibleBefore = mainBefore;
			result.visibleAfter = mainAfter;
			result.childProcesses = children.processes;
			result.childTrimmed = children.trimmed;
			result.childFailures = children.failures;
			result.childError = children.error;
			result.childrenBefore = children.before;
			result.childrenAfter = children.after;
		}
		running.store(false);
		blog(children.error || children.failures ? LOG_WARNING : LOG_INFO,
		     "[replaykit] visible idle child memory: %u processes, working set %llu -> %llu, private %llu -> %llu bytes, %u trimmed, %u failed, %lld ms, error %lu",
		     children.processes, (unsigned long long)children.before.workingSet, (unsigned long long)children.after.workingSet,
		     (unsigned long long)children.before.privateBytes, (unsigned long long)children.after.privateBytes, children.trimmed, children.failures,
		     (long long)elapsed, (unsigned long)children.error);
		if (mainTrimAttempted)
			blog(mainTrimError ? LOG_WARNING : LOG_INFO,
			     "[replaykit] visible idle OBS memory: committed %llu -> %llu, resident %llu -> %llu bytes, trim error %lu",
			     (unsigned long long)mainBefore.privateBytes, (unsigned long long)mainAfter.privateBytes,
			     (unsigned long long)mainBefore.workingSet, (unsigned long long)mainAfter.workingSet, (unsigned long)mainTrimError);
	});
}

void TimerFired()
{
	if (stopping.load() || !g_shellOn)
		return;
	QWidget *window = ObsMainWindowQt();
	if (!window)
		return;
	if (Hidden(window)) {
		Collect();
		return;
	}
	CollectVisibleChildren();
	if (timer && !stopping.load() && g_shellOn && !Hidden(window))
		timer->start(kVisibleCheckMs);
}

void Collect()
{
	if (stopping.load() || !g_shellOn || !Hidden(ObsMainWindowQt()) || running.exchange(true))
		return;
	const unsigned current = generation.load();
	const auto started = std::chrono::steady_clock::now();
	const Memory before = ReadMemory();
	// live pixmaps retain their references; only the reusable painting cache is cleared
	QPixmapCache::clear();
	RunAsync([current, started, before]() {
		if (stopping.load() || generation.load() != current) {
			running.store(false);
			return;
		}
		HEAP_OPTIMIZE_RESOURCES_INFORMATION info{HEAP_OPTIMIZE_RESOURCES_CURRENT_VERSION, 0};
		// decommit unused allocator caches before releasing inactive resident pages
		const BOOL optimized = HeapSetInformation(nullptr, HeapOptimizeResources, &info, sizeof(info));
		const DWORD error = optimized ? 0 : GetLastError();
		if (stopping.load() || generation.load() != current) {
			running.store(false);
			return;
		}
		// once per hidden period; allocations and warm browser documents stay intact
		const BOOL trimmed = EmptyWorkingSet(GetCurrentProcess());
		const DWORD trimError = trimmed ? 0 : GetLastError();
		const ChildResult children = TrimChildProcesses(current);
		const Memory after = ReadMemory();
		const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
		{
			std::lock_guard<std::mutex> lock(resultMutex);
			++result.runs;
			result.error = error;
			result.trimError = trimError;
			result.elapsedMs = elapsed;
			result.before = before;
			result.after = after;
			result.childProcesses = children.processes;
			result.childTrimmed = children.trimmed;
			result.childFailures = children.failures;
			result.childError = children.error;
			result.childrenBefore = children.before;
			result.childrenAfter = children.after;
		}
		running.store(false);
		blog(error || trimError ? LOG_WARNING : LOG_INFO,
		     "[replaykit] idle native memory: committed %llu -> %llu, resident %llu -> %llu bytes, %lld ms, heap error %lu, trim error %lu",
		     (unsigned long long)before.privateBytes, (unsigned long long)after.privateBytes,
		     (unsigned long long)before.workingSet, (unsigned long long)after.workingSet, (long long)elapsed, (unsigned long)error, (unsigned long)trimError);
		if (children.processes || children.error)
			blog(children.error || children.failures ? LOG_WARNING : LOG_INFO,
			     "[replaykit] idle child memory: %u processes, working set %llu -> %llu, private %llu -> %llu bytes, %u trimmed, %u failed, error %lu",
			     children.processes, (unsigned long long)children.before.workingSet, (unsigned long long)children.after.workingSet,
			     (unsigned long long)children.before.privateBytes, (unsigned long long)children.after.privateBytes,
			     children.trimmed, children.failures, (unsigned long)children.error);
	});
}

QJsonValue Bytes(bool available, SIZE_T value)
{
	return available ? QJsonValue(static_cast<qint64>(value)) : QJsonValue(QJsonValue::Null);
}

}

bool ShouldTrimVisible(std::uint32_t idleMs, std::uint32_t lastInputTick, std::uint32_t lastTrimInputTick, bool hasTrimmedInput, std::uint32_t idleThresholdMs)
{
	return idleMs >= idleThresholdMs && (!hasTrimmedInput || lastInputTick != lastTrimInputTick);
}

void WindowChanged(QWidget *window)
{
	const bool nowHidden = g_shellOn && Hidden(window);
	const bool changed = !stateKnown || hidden != nowHidden;
	if (changed) {
		stateKnown = true;
		hidden = nowHidden;
		++generation;
		if (timer)
			timer->stop();
	}
	if (stopping.load() || !g_shellOn || !window || !g_callbacks) {
		if (timer)
			timer->stop();
		return;
	}
	if (!changed && (hidden || (timer && timer->isActive())))
		return;
	if (!timer) {
		timer = new QTimer(g_callbacks);
		timer->setSingleShot(true);
		timer->setTimerType(Qt::PreciseTimer);
		QObject::connect(timer, &QTimer::timeout, g_callbacks, TimerFired);
	}
	timer->start(hidden ? kHiddenDelayMs : kVisibleCheckMs);
}

void Stop()
{
	stopping.store(true);
	++generation;
	if (timer)
		timer->stop();
}

QJsonObject Probe()
{
	const Memory now = ReadMemory();
	std::lock_guard<std::mutex> lock(resultMutex);
	return {{"ok", true}, {"scheduled", timer && timer->isActive() && hidden}, {"running", running.load()}, {"runs", (int)result.runs},
		{"visibleScheduled", timer && timer->isActive() && !hidden}, {"visibleRuns", (int)result.visibleRuns}, {"visibleIdleMs", (int)kVisibleIdleMs},
		{"visibleMainTrimAttempted", result.visibleMainTrimAttempted}, {"visibleMainTrimmed", result.visibleMainTrimmed}, {"visibleMainTrimError", (int)result.visibleMainTrimError},
		{"visibleBeforePrivateBytes", Bytes(result.visibleBefore.available, result.visibleBefore.privateBytes)},
		{"visibleAfterPrivateBytes", Bytes(result.visibleAfter.available, result.visibleAfter.privateBytes)},
		{"visibleBeforeWorkingSetBytes", Bytes(result.visibleBefore.available, result.visibleBefore.workingSet)},
		{"visibleAfterWorkingSetBytes", Bytes(result.visibleAfter.available, result.visibleAfter.workingSet)},
		{"error", (int)result.error}, {"trimError", (int)result.trimError}, {"elapsedMs", result.elapsedMs}, {"pixmapCacheLimitKb", QPixmapCache::cacheLimit()},
		{"childProcesses", (int)result.childProcesses}, {"childTrimmed", (int)result.childTrimmed}, {"childFailures", (int)result.childFailures},
		{"childError", (int)result.childError},
		{"privateBytes", Bytes(now.available, now.privateBytes)}, {"workingSetBytes", Bytes(now.available, now.workingSet)},
		{"beforePrivateBytes", Bytes(result.before.available, result.before.privateBytes)}, {"afterPrivateBytes", Bytes(result.after.available, result.after.privateBytes)},
		{"beforeWorkingSetBytes", Bytes(result.before.available, result.before.workingSet)}, {"afterWorkingSetBytes", Bytes(result.after.available, result.after.workingSet)},
		{"beforeChildrenPrivateBytes", Bytes(result.childrenBefore.available, result.childrenBefore.privateBytes)},
		{"afterChildrenPrivateBytes", Bytes(result.childrenAfter.available, result.childrenAfter.privateBytes)},
		{"beforeChildrenWorkingSetBytes", Bytes(result.childrenBefore.available, result.childrenBefore.workingSet)},
		{"afterChildrenWorkingSetBytes", Bytes(result.childrenAfter.available, result.childrenAfter.workingSet)}};
}

}
