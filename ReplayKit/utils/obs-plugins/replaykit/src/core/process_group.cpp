#include "prelude.hpp"
#include "core/process_group.hpp"

#include <shobjidl.h>

namespace rk {

namespace {
HANDLE processGroup = nullptr;
PWSTR previousAppId = nullptr;
}

bool StartProcessGroup()
{
	if (processGroup)
		return true;
	HANDLE group = CreateJobObjectW(nullptr, nullptr);
	JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
	limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
	if (!group || !SetInformationJobObject(group, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
	    !AssignProcessToJobObject(group, GetCurrentProcess())) {
		const DWORD error = GetLastError();
		if (group)
			CloseHandle(group);
		blog(LOG_WARNING, "[replaykit] could not establish the OBS process group: %lu", error);
		return false;
	}
	GetCurrentProcessExplicitAppUserModelID(&previousAppId);
	const HRESULT identity = SetCurrentProcessExplicitAppUserModelID(L"OBS.ReplayKit.Clips");
	if (FAILED(identity)) {
		CoTaskMemFree(previousAppId);
		previousAppId = nullptr;
		CloseHandle(group);
		blog(LOG_WARNING, "[replaykit] could not set the OBS process identity: 0x%08lx", identity);
		return false;
	}
	processGroup = group;
	return true;
}

void StopProcessGroup()
{
	if (!processGroup)
		return;
	SetCurrentProcessExplicitAppUserModelID(previousAppId ? previousAppId : L"");
	CoTaskMemFree(previousAppId);
	previousAppId = nullptr;
	CloseHandle(processGroup);
	processGroup = nullptr;
}

}
