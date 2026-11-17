#include <windows.h>
#include <dwmapi.h>

#include "log.hpp"
#include "share_window_values.hpp"
#include "window.hpp"

namespace rk::share {

namespace {

// far off every desktop, so nothing of it shows before the helper parks it
constexpr int kStartX = -32000;
constexpr int kStartY = -32000;
constexpr int kStartWidth = 1280;
constexpr int kStartHeight = 720;
// the timer that keeps the window drawing while windows holds it in a move, a resize or its menu, at the pace of the presents of the app
constexpr UINT_PTR kModalTimer = 1;
constexpr UINT kModalTimerMs = 16;

} // namespace

ShareWindow::~ShareWindow()
{
	if (hwnd)
		DestroyWindow(hwnd);
}

bool ShareWindow::Create(const std::wstring &title)
{
	HINSTANCE instance = GetModuleHandleW(nullptr);
	WNDCLASSEXW windowClass{};
	windowClass.cbSize = sizeof(windowClass);
	windowClass.lpfnWndProc = Proc;
	windowClass.hInstance = instance;
	windowClass.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1));
	windowClass.hIconSm = windowClass.hIcon;
	windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
	windowClass.lpszClassName = kShareWindowWindowClass;
	if (!RegisterClassExW(&windowClass)) {
		Log("could not register the window class: %lu", GetLastError());
		return false;
	}
	// topmost app window and never a tool window, the styles discord lists a parked window with; never click-through, which takes it out of the discord picker
	hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_APPWINDOW, kShareWindowWindowClass, title.c_str(), WS_POPUP, kStartX, kStartY, kStartWidth, kStartHeight,
			       nullptr, nullptr, instance, this);
	if (!hwnd) {
		Log("could not create the share window: %lu", GetLastError());
		return false;
	}
	return true;
}

bool ShareWindow::MakeSeeThrough()
{
	SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
	if (!SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA)) {
		Log("could not make the share window see-through: %lu", GetLastError());
		return false;
	}
	return true;
}

bool ShareWindow::SetTitleBarColors(COLORREF caption, COLORREF text, bool dark)
{
	const BOOL darkMode = dark ? TRUE : FALSE;
	// dark mode is the one windows 10 has; the colours are windows 11 only and are refused on older windows, which keeps the dark or light bar
	HRESULT hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &darkMode, sizeof(darkMode));
	DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, &caption, sizeof(caption));
	DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, &caption, sizeof(caption));
	DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, &text, sizeof(text));
	return SUCCEEDED(hr);
}

void ShareWindow::Show()
{
	// the first show of a process takes the show state its starter asked for (the helper starts it hidden), so the second one is the one that counts
	ShowWindow(hwnd, SW_SHOWNOACTIVATE);
	ShowWindow(hwnd, SW_SHOWNOACTIVATE);
}

LRESULT CALLBACK ShareWindow::Proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
{
	if (message == WM_NCCREATE) {
		auto *self = static_cast<ShareWindow *>(reinterpret_cast<CREATESTRUCTW *>(lparam)->lpCreateParams);
		SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
		self->hwnd = hwnd;
	}
	auto *self = reinterpret_cast<ShareWindow *>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
	return self ? self->OnMessage(message, wparam, lparam) : DefWindowProcW(hwnd, message, wparam, lparam);
}

LRESULT ShareWindow::OnMessage(UINT message, WPARAM wparam, LPARAM lparam)
{
	switch (message) {
	case WM_MOUSEACTIVATE:
		// a click on the visible corner must never take the keyboard from a fullscreen game
		return MA_NOACTIVATE;
	case WM_ERASEBKGND:
		return 1;
	case WM_PAINT: {
		PAINTSTRUCT paint;
		BeginPaint(hwnd, &paint);
		EndPaint(hwnd, &paint);
		return 0;
	}
	case WM_SIZE:
		resized = true;
		return 0;
	case WM_DPICHANGED:
		// the helper sizes and places the window itself, so the size windows suggests for the new dpi is not applied
		return 0;
	case WM_ENTERSIZEMOVE:
	case WM_ENTERMENULOOP:
		SetTimer(hwnd, kModalTimer, kModalTimerMs, nullptr);
		return DefWindowProcW(hwnd, message, wparam, lparam);
	case WM_EXITSIZEMOVE:
	case WM_EXITMENULOOP:
		KillTimer(hwnd, kModalTimer);
		return DefWindowProcW(hwnd, message, wparam, lparam);
	case WM_TIMER:
		if (wparam == kModalTimer) {
			if (modalTick)
				modalTick();
			return 0;
		}
		return DefWindowProcW(hwnd, message, wparam, lparam);
	case WM_CLOSE:
		Log("the share window was asked to close");
		closeRequested = true;
		return 0;
	case WM_DESTROY:
		hwnd = nullptr;
		return 0;
	default:
		return DefWindowProcW(hwnd, message, wparam, lparam);
	}
}

} // namespace rk::share
