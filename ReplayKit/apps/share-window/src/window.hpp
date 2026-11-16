// the window discord streams: borderless, never activated, created off screen and see-through until the helper parks it, the way the helper parks the obs projector it replaces
#pragma once

#include <windows.h>

#include <string>

namespace rk::share {

class ShareWindow {
public:
	~ShareWindow();
	bool Create(const std::wstring &title);
	// layered at alpha zero, which has to come after the swap chain is made for the window
	bool MakeSeeThrough();
	void Show();

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
};

} // namespace rk::share
