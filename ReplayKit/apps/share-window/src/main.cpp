// the share window: the window discord streams for share preview, in a process of its own so it outlives obs restarts, updates and crashes; the helper starts it while share preview is on and drives it over its command pipe
#include <windows.h>
#include <shellapi.h>

#include <cstdlib>
#include <string>
#include <vector>

#include "app.hpp"
#include "log.hpp"
#include "security.hpp"
#include "share_window_values.hpp"

namespace {

using RtlGetVersionFunction = LONG(WINAPI *)(OSVERSIONINFOW *);

bool Windows11OrNewer()
{
	auto getVersion = reinterpret_cast<RtlGetVersionFunction>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
	OSVERSIONINFOW info{};
	info.dwOSVersionInfoSize = sizeof(info);
	return getVersion && getVersion(&info) == 0 && info.dwMajorVersion == 10 && info.dwBuildNumber >= 22000;
}

// a window nobody looks at would otherwise run as a throttled background process, and discord captures it at the pace it presents
void OptOutOfPowerThrottling()
{
	PROCESS_POWER_THROTTLING_STATE state{};
	state.Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION;
	state.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
	if (Windows11OrNewer())
		state.ControlMask |= PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION;
	state.StateMask = 0;
	if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, &state, sizeof(state)))
		rk::share::Log("could not opt out of windows power throttling: %lu", GetLastError());
}

// --title <text> --keep <seconds>, both required, since the helper owns both values
bool ParseOptions(rk::share::Options &options)
{
	int count = 0;
	LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &count);
	if (!argv)
		return false;
	bool haveTitle = false, haveKeep = false;
	for (int i = 1; i + 1 < count; i += 2) {
		std::wstring name = argv[i];
		std::wstring value = argv[i + 1];
		if (name == L"--title" && !value.empty() && value.size() <= 256) {
			options.title = value;
			haveTitle = true;
		} else if (name == L"--keep") {
			wchar_t *end = nullptr;
			long long keep = wcstoll(value.c_str(), &end, 10);
			if (end && *end == L'\0' && keep >= -1 && keep <= 86400) {
				options.keepSeconds = keep;
				haveKeep = true;
			}
		}
	}
	LocalFree(argv);
	return haveTitle && haveKeep;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
	SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
	rk::share::LogOpen();
	rk::share::Options options;
	if (!ParseOptions(options)) {
		rk::share::Log("started without a valid --title and --keep, so not starting");
		return 2;
	}
	rk::share::UserOnlySecurity security;
	std::wstring mutexName = std::wstring(L"Local\\") + rk::kShareWindowInstanceMutexName;
	HANDLE instance = CreateMutexW(security.Attributes(), TRUE, mutexName.c_str());
	if (!instance || GetLastError() == ERROR_ALREADY_EXISTS) {
		rk::share::Log("another share window already runs");
		if (instance)
			CloseHandle(instance);
		return 0;
	}
	OptOutOfPowerThrottling();
	CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
	rk::share::Log("share window %s starting", REPLAYKIT_VERSION);
	int result = 0;
	{
		rk::share::ShareApp app;
		result = app.Run(options);
	}
	CoUninitialize();
	ReleaseMutex(instance);
	CloseHandle(instance);
	rk::share::Log("share window exited (%d)", result);
	return result;
}
