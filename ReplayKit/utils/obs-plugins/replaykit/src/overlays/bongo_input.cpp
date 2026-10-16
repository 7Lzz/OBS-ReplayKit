#include "prelude.hpp"
#include "overlays/bongo_input.hpp"

#include <condition_variable>
#include <vector>

namespace rk {

namespace {

// raw input allows one target window per process, so every bongo source shares this one; nothing else in obs takes the mouse (cef only does for pointer lock, which no replaykit page asks for)
constexpr UINT RefreshMessage = WM_APP + 1;
constexpr wchar_t WindowClass[] = L"ReplayKitBongoRawMouse";

std::mutex g_lock;
std::condition_variable g_started;
std::vector<BongoInput *> g_sources;
std::thread g_thread;
DWORD g_threadId = 0;
// MOUSE_MOVE_ABSOLUTE devices (tablets, remote desktop) report positions, so motion is the step between two of them; input thread only
bool g_absolute = false;
int g_lastX = 0, g_lastY = 0;

HINSTANCE ModuleInstance()
{
	HMODULE module = nullptr;
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCWSTR>(&ModuleInstance), &module);
	return module;
}

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
{
	if (message == WM_INPUT) {
		RAWINPUT raw{};
		UINT size = sizeof(raw);
		if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, &raw, &size, sizeof(RAWINPUTHEADER)) != UINT(-1) && raw.header.dwType == RIM_TYPEMOUSE) {
			int64_t dx = 0, dy = 0;
			if (!(raw.data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE)) {
				dx = raw.data.mouse.lLastX;
				dy = raw.data.mouse.lLastY;
				g_absolute = false;
			} else {
				const bool desktop = (raw.data.mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) != 0;
				const int x = int(raw.data.mouse.lLastX / 65535.0 * GetSystemMetrics(desktop ? SM_CXVIRTUALSCREEN : SM_CXSCREEN));
				const int y = int(raw.data.mouse.lLastY / 65535.0 * GetSystemMetrics(desktop ? SM_CYVIRTUALSCREEN : SM_CYSCREEN));
				if (g_absolute) {
					dx = x - g_lastX;
					dy = y - g_lastY;
				}
				g_lastX = x;
				g_lastY = y;
				g_absolute = true;
			}
			if (dx || dy) {
				std::lock_guard<std::mutex> guard(g_lock);
				for (BongoInput *source : g_sources) {
					if (source->Active())
						source->Add(dx, dy);
				}
			}
		}
	}
	return DefWindowProcW(window, message, wParam, lParam);
}

void Run()
{
	const HINSTANCE instance = ModuleInstance();
	WNDCLASSW type{};
	type.lpfnWndProc = WindowProc;
	type.hInstance = instance;
	type.lpszClassName = WindowClass;
	RegisterClassW(&type);
	HWND window = CreateWindowExW(0, WindowClass, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, instance, nullptr);
	if (!window)
		blog(LOG_WARNING, "[replaykit] bongo cat: raw mouse window failed, error %lu", GetLastError());
	MSG message;
	// makes the message queue exist before anyone posts to it
	PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
	{
		std::lock_guard<std::mutex> guard(g_lock);
		g_threadId = GetCurrentThreadId();
	}
	g_started.notify_all();

	bool registered = false;
	while (GetMessageW(&message, nullptr, 0, 0) > 0) {
		if (message.message != RefreshMessage) {
			DispatchMessageW(&message);
			continue;
		}
		bool wanted = false;
		{
			std::lock_guard<std::mutex> guard(g_lock);
			for (BongoInput *source : g_sources)
				wanted = wanted || source->Active();
		}
		if (!window || wanted == registered)
			continue;
		RAWINPUTDEVICE device{};
		device.usUsagePage = 1;
		device.usUsage = 2;
		device.dwFlags = wanted ? RIDEV_INPUTSINK : RIDEV_REMOVE;
		device.hwndTarget = wanted ? window : nullptr;
		if (RegisterRawInputDevices(&device, 1, sizeof(device)))
			registered = wanted;
		else
			blog(LOG_WARNING, "[replaykit] bongo cat: raw mouse %s failed, error %lu", wanted ? "registration" : "removal", GetLastError());
		g_absolute = false;
	}
	if (registered) {
		RAWINPUTDEVICE device{};
		device.usUsagePage = 1;
		device.usUsage = 2;
		device.dwFlags = RIDEV_REMOVE;
		RegisterRawInputDevices(&device, 1, sizeof(device));
	}
	if (window)
		DestroyWindow(window);
	UnregisterClassW(WindowClass, instance);
}

void Refresh()
{
	DWORD thread;
	{
		std::lock_guard<std::mutex> guard(g_lock);
		thread = g_threadId;
	}
	if (thread)
		PostThreadMessageW(thread, RefreshMessage, 0, 0);
}

} // namespace

// sources are created and destroyed on the obs interface thread, so the input thread starts with the first and ends with the last
BongoInput::BongoInput()
{
	std::unique_lock<std::mutex> guard(g_lock);
	g_sources.push_back(this);
	if (g_sources.size() == 1) {
		g_threadId = 0;
		g_thread = std::thread(Run);
		g_started.wait(guard, [] { return g_threadId != 0; });
	}
}

BongoInput::~BongoInput()
{
	std::thread finished;
	{
		std::lock_guard<std::mutex> guard(g_lock);
		g_sources.erase(std::remove(g_sources.begin(), g_sources.end(), this), g_sources.end());
		if (g_sources.empty()) {
			PostThreadMessageW(g_threadId, WM_QUIT, 0, 0);
			g_threadId = 0;
			finished = std::move(g_thread);
		} else {
			PostThreadMessageW(g_threadId, RefreshMessage, 0, 0);
		}
	}
	if (finished.joinable())
		finished.join();
}

void BongoInput::SetActive(bool value)
{
	if (active.exchange(value) != value)
		Refresh();
}

void BongoInput::Take(int64_t &dx, int64_t &dy)
{
	dx = pendingX.exchange(0);
	dy = pendingY.exchange(0);
}

void BongoInput::Add(int64_t dx, int64_t dy)
{
	pendingX += dx;
	pendingY += dy;
}

} // namespace rk
