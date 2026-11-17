// the window discord streams: borderless, never activated, created off screen and see-through until the helper parks it, the way the helper parks the obs projector it replaces
#pragma once

#include <windows.h>

#include <functional>
#include <string>

namespace rk::share {

class ShareWindow {
public:
	~ShareWindow();
	bool Create(const std::wstring &title);
	// layered at alpha zero, which has to come after the swap chain is made for the window
	bool MakeSeeThrough();
	void Show();
	// what keeps the window drawing while windows holds it in a move, a resize or its menu, where the loop of the app does not run
	void SetModalTick(std::function<void()> tick) { modalTick = std::move(tick); }
	// the title bar it has while the helper shows it for a look, in the colours of the theme
	bool SetTitleBarColors(COLORREF caption, COLORREF text, bool dark);

	HWND Handle() const { return hwnd; }
	bool CloseRequested() const { return closeRequested; }
	bool TakeResized()
	{
		bool value = resized;
		resized = false;
		return value;
	}

private:
	static LRESULT CALLBACK Proc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam);
	LRESULT OnMessage(UINT message, WPARAM wparam, LPARAM lparam);

	HWND hwnd = nullptr;
	bool closeRequested = false;
	bool resized = false;
	std::function<void()> modalTick;
};

} // namespace rk::share
