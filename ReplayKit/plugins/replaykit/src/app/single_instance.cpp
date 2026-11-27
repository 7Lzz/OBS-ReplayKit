// keeps one obs with replaykit running: a second launch brings this window forward instead
#include "prelude.hpp"
#include "app/single_instance.hpp"

#include "core/state.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

HWINEVENTHOOK g_instanceHook = nullptr;
std::wstring g_ownImage;
std::wstring g_promptTitle;

std::wstring ProcessImage(DWORD pid)
{
	HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
	if (!process)
		return std::wstring();
	wchar_t path[MAX_PATH * 4] = {};
	DWORD size = DWORD(std::size(path));
	const bool ok = QueryFullProcessImageNameW(process, 0, path, &size);
	CloseHandle(process);
	return ok ? std::wstring(path, size) : std::wstring();
}

// obs checks for a running copy before it loads any plugin and asks with a message box of its own, so the second copy can only be stopped at that box: its Cancel is the button a close picks, and obs then exits before it has written anything
void CALLBACK OnWindowShown(HWINEVENTHOOK, DWORD, HWND hwnd, LONG idObject, LONG idChild, DWORD, DWORD)
{
	if (!g_instanceHook || idObject != OBJID_WINDOW || idChild != CHILDID_SELF || !hwnd || GetAncestor(hwnd, GA_ROOT) != hwnd)
		return;
	DWORD pid = 0;
	GetWindowThreadProcessId(hwnd, &pid);
	if (!pid || pid == GetCurrentProcessId())
		return;
	wchar_t text[256] = {};
	if (GetClassNameW(hwnd, text, int(std::size(text))) <= 0 || wcsncmp(text, L"Qt", 2) != 0)
		return;
	const int length = GetWindowTextW(hwnd, text, int(std::size(text)));
	if (length <= 0 || g_promptTitle != std::wstring(text, length))
		return;
	if (_wcsicmp(ProcessImage(pid).c_str(), g_ownImage.c_str()) != 0)
		return;
	blog(LOG_INFO, "[replaykit] a second obs (pid %lu) was started; it is closed at its prompt and this window brought forward", pid);
	// forward first, while the prompt still holds the foreground this window takes it from
	QTimer::singleShot(0, g_callbacks, [hwnd, pid]() {
		ShellBringMainWindowForward();
		DWORD owner = 0;
		if (GetWindowThreadProcessId(hwnd, &owner) && owner == pid)
			PostMessageW(hwnd, WM_CLOSE, 0, 0);
	});
}

} // namespace

void StartSingleInstanceWatch()
{
	if (g_instanceHook)
		return;
	const char *title = obs_frontend_get_locale_string("AlreadyRunning.Title");
	wchar_t image[MAX_PATH * 4] = {};
	const DWORD length = GetModuleFileNameW(nullptr, image, DWORD(std::size(image)));
	if (!title || !*title || length == 0 || length >= std::size(image)) {
		blog(LOG_WARNING, "[replaykit] a second obs cannot be caught: the prompt title or the path of obs is unknown");
		return;
	}
	g_promptTitle = QString::fromUtf8(title).toStdWString();
	g_ownImage.assign(image, length);
	g_instanceHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, nullptr, OnWindowShown, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
	if (!g_instanceHook)
		blog(LOG_WARNING, "[replaykit] a second obs cannot be caught: the window hook failed (%lu)", GetLastError());
}

// once obs is closing a second copy is left to its own prompt, since this window is about to go
void StopSingleInstanceWatch()
{
	if (!g_instanceHook)
		return;
	UnhookWinEvent(g_instanceHook);
	g_instanceHook = nullptr;
}

} // namespace rk
