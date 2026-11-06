// native crash and unclean shutdown records
#include "prelude.hpp"
#include "core/crash_reporter.hpp"

namespace rk {

namespace {

HMODULE g_replayKitModule = nullptr;
HANDLE g_nativeCrashLog = INVALID_HANDLE_VALUE;
PVOID g_nativeCrashHandler = nullptr;
volatile LONG g_nativeCrashWriting = 0;
volatile LONG g_nativeFaultCount = 0;
wchar_t g_nativeSessionPath[MAX_PATH] = {};
char g_nativeFaultBuffer[4096] = {};

bool IsReplayKitFault(DWORD code)
{
	return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ARRAY_BOUNDS_EXCEEDED ||
		code == EXCEPTION_ILLEGAL_INSTRUCTION || code == EXCEPTION_IN_PAGE_ERROR ||
		code == EXCEPTION_NONCONTINUABLE_EXCEPTION || code == EXCEPTION_STACK_OVERFLOW;
}

void FaultModuleName(void *base, char *name, size_t size)
{
	char path[MAX_PATH] = {};
	if (!base || !GetModuleFileNameA((HMODULE)base, path, MAX_PATH)) {
		strcpy_s(name, size, "unknown");
		return;
	}
	path[MAX_PATH - 1] = 0;
	const char *file = strrchr(path, '\\');
	strncpy_s(name, size, file ? file + 1 : path, _TRUNCATE);
	for (char *c = name; *c; ++c)
		if ((unsigned char)*c < 32 || (unsigned char)*c > 126 || *c == '"' || *c == '\\') *c = '?';
}

LONG CALLBACK RecordReplayKitException(EXCEPTION_POINTERS *exception)
{
	if (!exception || !exception->ExceptionRecord || !IsReplayKitFault(exception->ExceptionRecord->ExceptionCode))
		return EXCEPTION_CONTINUE_SEARCH;
	if (InterlockedIncrement(&g_nativeFaultCount) > 32 || InterlockedCompareExchange(&g_nativeCrashWriting, 1, 0) != 0)
		return EXCEPTION_CONTINUE_SEARCH;
	if (g_nativeCrashLog != INVALID_HANDLE_VALUE) {
		SYSTEMTIME time = {};
		GetSystemTime(&time);
		MEMORY_BASIC_INFORMATION memory = {};
		VirtualQuery(exception->ExceptionRecord->ExceptionAddress, &memory, sizeof(memory));
		char module[96] = {};
		FaultModuleName(memory.AllocationBase, module, sizeof(module));
		auto offset = (uintptr_t)exception->ExceptionRecord->ExceptionAddress - (uintptr_t)memory.AllocationBase;
		int length = snprintf(g_nativeFaultBuffer, sizeof(g_nativeFaultBuffer),
			"{\"kind\":\"first_chance_fault\",\"utc\":\"%04u-%02u-%02uT%02u:%02u:%02uZ\",\"pid\":%lu,"
			"\"code\":\"0x%08lX\",\"address\":\"%p\",\"moduleBase\":\"%p\",\"offset\":\"%llx\","
			"\"module\":\"%s\",\"inReplayKit\":%s,\"threadId\":%lu,\"build\":\"" __DATE__ " " __TIME__ "\",\"frames\":[",
			time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
			(unsigned long)GetCurrentProcessId(), (unsigned long)exception->ExceptionRecord->ExceptionCode,
			exception->ExceptionRecord->ExceptionAddress, memory.AllocationBase, (unsigned long long)offset,
			module, memory.AllocationBase == g_replayKitModule ? "true" : "false", (unsigned long)GetCurrentThreadId());
		void *frames[16] = {};
		USHORT count = exception->ExceptionRecord->ExceptionCode == EXCEPTION_STACK_OVERFLOW ? 0 : CaptureStackBackTrace(0, 16, frames, nullptr);
		for (USHORT i = 0; i < count && length > 0 && length < (int)sizeof(g_nativeFaultBuffer) - 384; ++i) {
			MEMORY_BASIC_INFORMATION frameMemory = {};
			VirtualQuery(frames[i], &frameMemory, sizeof(frameMemory));
			FaultModuleName(frameMemory.AllocationBase, module, sizeof(module));
			length += snprintf(g_nativeFaultBuffer + length, sizeof(g_nativeFaultBuffer) - length,
				"%s{\"module\":\"%s\",\"address\":\"%p\",\"moduleBase\":\"%p\",\"offset\":\"%llx\"}", i ? "," : "", module, frames[i],
				frameMemory.AllocationBase, (unsigned long long)((uintptr_t)frames[i] - (uintptr_t)frameMemory.AllocationBase));
		}
		if (length > 0 && length < (int)sizeof(g_nativeFaultBuffer) - 4) {
			memcpy(g_nativeFaultBuffer + length, "]}\r\n", 4);
			DWORD written = 0;
			WriteFile(g_nativeCrashLog, g_nativeFaultBuffer, (DWORD)length + 4, &written, nullptr);
			FlushFileBuffers(g_nativeCrashLog);
		}
	}
	InterlockedExchange(&g_nativeCrashWriting, 0);
	return EXCEPTION_CONTINUE_SEARCH;
}

// every unclean exit leaves a small record and nothing else ever removes them, so only the newest ones are kept
void PruneUncleanRecords(const std::wstring &directory)
{
	constexpr size_t kKeep = 30;
	std::vector<std::pair<ULONGLONG, std::wstring>> records;
	WIN32_FIND_DATAW entry = {};
	HANDLE files = FindFirstFileW((directory + L"\\replaykit-session-*.pending.unclean").c_str(), &entry);
	if (files == INVALID_HANDLE_VALUE)
		return;
	do {
		const ULONGLONG written = (ULONGLONG(entry.ftLastWriteTime.dwHighDateTime) << 32) | entry.ftLastWriteTime.dwLowDateTime;
		records.emplace_back(written, entry.cFileName);
	} while (FindNextFileW(files, &entry));
	FindClose(files);
	if (records.size() <= kKeep)
		return;
	std::sort(records.begin(), records.end(), [](const auto &a, const auto &b) { return a.first > b.first; });
	for (size_t i = kKeep; i < records.size(); i++)
		DeleteFileW((directory + L"\\" + records[i].second).c_str());
}

void StartSessionRecord(const std::wstring &directory)
{
	WIN32_FIND_DATAW entry = {};
	HANDLE files = FindFirstFileW((directory + L"\\replaykit-session-*.pending").c_str(), &entry);
	if (files != INVALID_HANDLE_VALUE) {
		do {
			unsigned long pid = 0;
			if (swscanf_s(entry.cFileName, L"replaykit-session-%lu.pending", &pid) != 1) continue;
			HANDLE process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
			bool ended = !process && GetLastError() == ERROR_INVALID_PARAMETER;
			if (process) {
				FILETIME created, exited, kernel, user;
				ended = WaitForSingleObject(process, 0) == WAIT_OBJECT_0 ||
					(GetProcessTimes(process, &created, &exited, &kernel, &user) && CompareFileTime(&created, &entry.ftLastWriteTime) > 0);
				CloseHandle(process);
			}
			if (ended) {
				std::wstring oldPath = directory + L"\\" + entry.cFileName;
				if (MoveFileExW(oldPath.c_str(), (oldPath + L".unclean").c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
					blog(LOG_WARNING, "[replaykit] previous OBS session %lu ended without orderly plugin shutdown; record: %ls. This may be a crash, forced exit, or power loss.", pid, oldPath.c_str());
			}
		} while (FindNextFileW(files, &entry));
		FindClose(files);
	}
	PruneUncleanRecords(directory);
	swprintf_s(g_nativeSessionPath, L"%ls\\replaykit-session-%lu.pending", directory.c_str(), (unsigned long)GetCurrentProcessId());
	HANDLE marker = CreateFileW(g_nativeSessionPath, GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_FLAG_WRITE_THROUGH, nullptr);
	if (marker != INVALID_HANDLE_VALUE) {
		const char record[] = "OBS ReplayKit session started; this marker is removed only on orderly plugin shutdown.\r\nBuild: " __DATE__ " " __TIME__ "\r\n";
		DWORD written;
		WriteFile(marker, record, sizeof(record) - 1, &written, nullptr);
		FlushFileBuffers(marker);
		CloseHandle(marker);
	}
}

} // namespace

void StartReplayKitCrashReporter()
{
	wchar_t appData[MAX_PATH] = {};
	DWORD appDataLength = GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH);
	if (!appDataLength || appDataLength >= MAX_PATH)
		return;
	std::wstring obsDirectory = std::wstring(appData) + L"\\obs-studio";
	std::wstring crashesDirectory = obsDirectory + L"\\crashes";
	std::wstring directory = crashesDirectory + L"\\replaykit";
	CreateDirectoryW(obsDirectory.c_str(), nullptr);
	CreateDirectoryW(crashesDirectory.c_str(), nullptr);
	if (!CreateDirectoryW(directory.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
		return;
	std::wstring path = directory + L"\\replaykit-native.jsonl";
	g_nativeCrashLog = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
		nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)&StartReplayKitCrashReporter, &g_replayKitModule);
	InterlockedExchange(&g_nativeFaultCount, 0);
	StartSessionRecord(directory);
	g_nativeCrashHandler = AddVectoredExceptionHandler(1, RecordReplayKitException);
	if (g_nativeCrashLog == INVALID_HANDLE_VALUE || !g_nativeCrashHandler)
		blog(LOG_WARNING, "[replaykit] crash reporter could not start");
	else
		blog(LOG_INFO, "[replaykit] crash reporter active: %ls", path.c_str());
}

void StopReplayKitCrashReporter()
{
	if (g_nativeCrashHandler) {
		RemoveVectoredExceptionHandler(g_nativeCrashHandler);
		g_nativeCrashHandler = nullptr;
	}
	if (g_nativeCrashLog != INVALID_HANDLE_VALUE) {
		CloseHandle(g_nativeCrashLog);
		g_nativeCrashLog = INVALID_HANDLE_VALUE;
	}
	// an orderly shutdown removes the marker that would otherwise report this session as unclean
	if (g_nativeSessionPath[0])
		DeleteFileW(g_nativeSessionPath);
}

} // namespace rk
