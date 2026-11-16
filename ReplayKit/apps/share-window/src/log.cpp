#include <windows.h>

#include <share.h>

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <string>

#include "log.hpp"
#include "share_window_values.hpp"

namespace rk::share {

namespace {

std::mutex g_lock;
std::wstring g_path;
FILE *g_file = nullptr;
constexpr long kMaxBytes = 1024 * 1024;

std::wstring Widen(const char *text)
{
	int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
	std::wstring out(length > 0 ? length - 1 : 0, L'\0');
	if (length > 1)
		MultiByteToWideChar(CP_UTF8, 0, text, -1, out.data(), length);
	return out;
}

// one older copy is kept, so a long running window never grows the log past two megabytes
void RollIfLarge()
{
	if (!g_file || ftell(g_file) < kMaxBytes)
		return;
	fclose(g_file);
	g_file = nullptr;
	std::wstring old = g_path + L".1";
	MoveFileExW(g_path.c_str(), old.c_str(), MOVEFILE_REPLACE_EXISTING);
	g_file = _wfsopen(g_path.c_str(), L"ab", _SH_DENYNO);
}

} // namespace

void LogOpen()
{
	std::lock_guard<std::mutex> guard(g_lock);
	wchar_t temp[MAX_PATH + 1] = {};
	DWORD length = GetTempPathW(MAX_PATH + 1, temp);
	if (!length || length > MAX_PATH)
		return;
	std::wstring dir = std::wstring(temp) + Widen(kTempFolder);
	CreateDirectoryW(dir.c_str(), nullptr);
	dir += L"\\" + Widen(kTempLogsFolder);
	CreateDirectoryW(dir.c_str(), nullptr);
	g_path = dir + L"\\" + kShareWindowLogFile;
	// shared, so the helper can read it while the window writes
	g_file = _wfsopen(g_path.c_str(), L"ab", _SH_DENYNO);
	if (g_file)
		fseek(g_file, 0, SEEK_END);
}

void Log(const char *format, ...)
{
	char message[1024];
	va_list args;
	va_start(args, format);
	vsnprintf(message, sizeof(message), format, args);
	va_end(args);

	SYSTEMTIME now;
	GetLocalTime(&now);
	char line[1200];
	snprintf(line, sizeof(line), "%04u-%02u-%02u %02u:%02u:%02u.%03u [%lu] %s\n", now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond,
		 now.wMilliseconds, GetCurrentProcessId(), message);
	OutputDebugStringA(line);

	std::lock_guard<std::mutex> guard(g_lock);
	if (!g_file)
		return;
	fputs(line, g_file);
	fflush(g_file);
	RollIfLarge();
}

} // namespace rk::share
