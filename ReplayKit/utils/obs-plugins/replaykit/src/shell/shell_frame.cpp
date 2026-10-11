// native frame of the frameless obs window
#include "prelude.hpp"
#include "shell/shell_frame.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_title_bar.hpp"
#include "shell/shell_types.hpp"
#include "ui/anchored_panel.hpp"
#include "ui/confirm_dialog.hpp"

namespace rk {

namespace {

bool ShellWorkAreaOf(HWND hwnd, RECT *work)
{
	MONITORINFO info = {};
	info.cbSize = sizeof(info);
	if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &info))
		return false;
	*work = info.rcWork;
	return true;
}

// the floor of the window size in physical px: the minimum of the shell, and never more than most of the screen the window is on, so a small screen is not asked for more than it has
void ShellMinTrack(HWND hwnd, qreal dpr, LONG *w, LONG *h)
{
	*w = LONG(qRound(kShellMinW * dpr));
	*h = LONG(qRound(kShellMinH * dpr));
	RECT work = {};
	if (ShellWorkAreaOf(hwnd, &work)) {
		*w = qMin<LONG>(*w, (work.right - work.left) * 9 / 10);
		*h = qMin<LONG>(*h, (work.bottom - work.top) * 9 / 10);
	}
}

// a window that was left smaller than the floor, which it could be made before there was one, is brought up to it inside the work area
void ShellEnsureMinimumSize(HWND hwnd)
{
	QMainWindow *mw = ObsMainWindowQt();
	RECT now = {};
	RECT work = {};
	if (!mw || !hwnd || IsZoomed(hwnd) || IsIconic(hwnd) || g_shellNativeFullscreen || !GetWindowRect(hwnd, &now) || !ShellWorkAreaOf(hwnd, &work))
		return;
	LONG minW = 0;
	LONG minH = 0;
	ShellMinTrack(hwnd, mw->devicePixelRatioF(), &minW, &minH);
	const LONG w = qMax<LONG>(now.right - now.left, minW);
	const LONG h = qMax<LONG>(now.bottom - now.top, minH);
	if (w == now.right - now.left && h == now.bottom - now.top)
		return;
	const LONG left = qBound<LONG>(work.left, now.left, qMax<LONG>(work.left, work.right - w));
	const LONG top = qBound<LONG>(work.top, now.top, qMax<LONG>(work.top, work.bottom - h));
	blog(LOG_INFO, "[replaykit] the window was %dx%d, smaller than the shell allows; made %dx%d", int(now.right - now.left), int(now.bottom - now.top), int(w), int(h));
	SetWindowPos(hwnd, nullptr, left, top, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
}

// DWMWA_CLOAK: a cloaked window is still composed, so it paints and is seen by the system as visible, but it is not put on the screen
constexpr DWORD kDwmCloak = 13;
bool g_shellFirstShown = false;
bool g_shellCloaked = false;
QElapsedTimer g_shellFirstShowClock;

void ShellSetCloak(HWND hwnd, bool on)
{
	if (!hwnd)
		return;
	const BOOL value = on ? TRUE : FALSE;
	DwmSetWindowAttribute(hwnd, kDwmCloak, &value, sizeof(value));
	g_shellCloaked = on;
}

// the chrome has had its first paint and the compositor has taken it in, so the window can be let out
void ShellFinishFirstShow()
{
	if (!g_shellCloaked)
		return;
	QMainWindow *mw = ObsMainWindowQt();
	if (mw) {
		ShellEnsureMinimumSize(g_shellHwnd);
		mw->repaint();
		for (QWidget *w : {static_cast<QWidget *>(g_shellTitle.data()), static_cast<QWidget *>(g_shellRail.data()), g_shellCurtain.data()}) {
			if (w && w->isVisible())
				w->repaint();
		}
		DwmFlush();
	}
	ShellSetCloak(g_shellHwnd, false);
	blog(LOG_INFO, "[replaykit] first show: the window stayed out of sight %d ms until its chrome had painted", int(g_shellFirstShowClock.elapsed()));
}

// a window that was maximized before it ever had a normal size restores to a rectangle as big as the screen, which looks as if nothing happened, so a restore that lands on one is given a proper size, centred on the work area
void ShellFixRestoredSize(HWND hwnd)
{
	RECT work = {};
	RECT now = {};
	if (!ShellWorkAreaOf(hwnd, &work) || !GetWindowRect(hwnd, &now))
		return;
	const LONG workW = work.right - work.left;
	const LONG workH = work.bottom - work.top;
	if (now.right - now.left < workW - 24 || now.bottom - now.top < workH - 24)
		return;
	const LONG w = workW * 7 / 10;
	const LONG h = workH * 8 / 10;
	SetWindowPos(hwnd, nullptr, work.left + (workW - w) / 2, work.top + (workH - h) / 2, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
}

// the frameless top-level qt made has no sizing border, caption or snap support of its own. the system styles that give it those come back here, and the non-client area is then removed again in the frame filter so none of it is drawn
void ShellApplyFrameStyle(HWND hwnd)
{
	const LONG_PTR style = GetWindowLongPtr(hwnd, GWL_STYLE);
	const LONG_PTR want = style | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
	if (want != style) {
		SetWindowLongPtr(hwnd, GWL_STYLE, want);
		SetWindowPos(hwnd, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
	}
	// windows takes a maximized window that covers its monitor for a fullscreen one and pulls the taskbar down under it, even an auto-hide taskbar that should slide in over it
	if (!g_shellNativeFullscreen)
		SetPropW(hwnd, L"NonRudeHWND", reinterpret_cast<HANDLE>(1));
	// one pixel of extended frame is what keeps the drop shadow on a window with no non-client area; the client is opaque, so it never shows through
	ShellSyncFrameMargins(hwnd);
}

// p is in main window coordinates
LRESULT ShellHitTestAt(QMainWindow *mw, const QPoint &p)
{
	// the grip handles its own press, so it has to be client area
	if (g_shellGrip && g_shellGrip->isVisible() && g_shellGrip->geometry().contains(p))
		return HTCLIENT;
	const bool maximized = IsZoomed(g_shellHwnd) || mw->isFullScreen() || g_shellNativeFullscreen;
	if (!maximized) {
		const int w = mw->width();
		const int h = mw->height();
		const bool left = p.x() < kShellEdge;
		const bool right = p.x() >= w - kShellEdge;
		const bool top = p.y() < kShellEdge;
		const bool bottom = p.y() >= h - kShellEdge;
		const bool nearLeft = p.x() < kShellCorner;
		const bool nearRight = p.x() >= w - kShellCorner;
		const bool nearTop = p.y() < kShellCorner;
		const bool nearBottom = p.y() >= h - kShellCorner;
		if ((top && nearLeft) || (left && nearTop))
			return HTTOPLEFT;
		if ((top && nearRight) || (right && nearTop))
			return HTTOPRIGHT;
		if ((bottom && nearLeft) || (left && nearBottom))
			return HTBOTTOMLEFT;
		if ((bottom && nearRight) || (right && nearBottom))
			return HTBOTTOMRIGHT;
		if (left)
			return HTLEFT;
		if (right)
			return HTRIGHT;
		if (top)
			return HTTOP;
		if (bottom)
			return HTBOTTOM;
	}
	ShellTitleBar *title = g_shellTitle.data();
	// a maximized window is left to the title bar widget, which restores it on a drag; windows itself would not
	if (title && title->isVisible() && title->geometry().contains(p) && title->IsCaptionAt(title->mapFrom(mw, p)))
		return maximized ? HTCLIENT : HTCAPTION;
	return HTCLIENT;
}

// the point of a WM_NCHITTEST arrives in screen pixels; the layout works in logical px from the top left of the window, and the client area is the whole window here
QPoint ShellPointFromHitTest(QMainWindow *mw, LPARAM lParam)
{
	POINT pt = {(int)(short)LOWORD(lParam), (int)(short)HIWORD(lParam)};
	ScreenToClient(g_shellHwnd, &pt);
	const qreal dpr = mw->devicePixelRatioF();
	return QPoint(qFloor(pt.x / dpr), qFloor(pt.y / dpr));
}

// what the window was before a clip took over its monitor, kept for the way back
WINDOWPLACEMENT g_shellFsPlacement = {};
RECT g_shellFsRect = {};
bool g_shellTaskbarTried = false;

// windows takes a window for a fullscreen one (taskbar out of the way) when it covers the monitor, but only guesses, and never for a window with a caption style, so the window is marked explicitly as well
ITaskbarList2 *ShellTaskbarList()
{
	if (!g_shellTaskbarTried) {
		g_shellTaskbarTried = true;
		if (FAILED(CoCreateInstance(__uuidof(TaskbarList), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&g_shellTaskbar)))) {
			g_shellTaskbar = nullptr;
		} else if (FAILED(g_shellTaskbar->HrInit())) {
			g_shellTaskbar->Release();
			g_shellTaskbar = nullptr;
		}
	}
	return g_shellTaskbar;
}

} // namespace

// the extended frame is what gives a frameless window its shadow, and it is also a hairline of frame colour along every edge: right for a window floating on the desktop, wrong once the window fills the screen or a whole monitor
void ShellSyncFrameMargins(HWND hwnd)
{
	if (!hwnd)
		return;
	const bool fills = IsZoomed(hwnd) || g_shellNativeFullscreen;
	const MARGINS margins = fills ? MARGINS{0, 0, 0, 0} : MARGINS{1, 1, 1, 1};
	DwmExtendFrameIntoClientArea(hwnd, &margins);
}

bool ShellIsMaximized()
{
	return g_shellHwnd && IsZoomed(g_shellHwnd);
}

void ShellUncloak()
{
	if (g_shellCloaked)
		ShellSetCloak(g_shellHwnd, false);
}

// the maximize and restore buttons and the double click on the title bar all go through windows itself, so the window really is maximized: no border, no rounded corners, and the shell knows what to do with it
void ShellToggleMaximize(QMainWindow *mw)
{
	HWND hwnd = g_shellHwnd;
	if (!hwnd) {
		if (mw->isMaximized())
			mw->showNormal();
		else
			mw->showMaximized();
		return;
	}
	if (IsZoomed(hwnd)) {
		ShowWindow(hwnd, SW_RESTORE);
		ShellFixRestoredSize(hwnd);
	} else if (mw->isMaximized()) {
		mw->showNormal();
		ShellFixRestoredSize(hwnd);
	} else {
		ShowWindow(hwnd, SW_MAXIMIZE);
	}
}

// qt maximizes a frameless window by resizing it to the available area, which windows knows nothing about: the window keeps its border and rounded corners, and a drag on the title bar only moves it. so what qt calls maximized is turned into the real thing, restored the way qt knows how and then maximized by windows, which remembers the rectangle it had as the one to restore to
void ShellMakeMaximizeReal()
{
	QMainWindow *mw = ObsMainWindowQt();
	HWND hwnd = g_shellHwnd;
	if (!mw || !hwnd || !mw->isVisible() || mw->isMinimized() || mw->isFullScreen() || g_shellNativeFullscreen || !mw->isMaximized() || IsZoomed(hwnd))
		return;
	mw->showNormal();
	ShowWindow(hwnd, SW_MAXIMIZE);
}

// dragging the title bar of a maximized window restores it and carries on as a normal drag, the cursor keeping its place along the bar the way windows does it. all in screen pixels, from the cursor position windows reports. the hand-off to the system move at the end is what keeps snapping and multi-monitor drags working as usual
void ShellRestoreDrag(QMainWindow *mw, POINT cursor, bool handOff)
{
	HWND hwnd = g_shellHwnd;
	RECT work = {};
	RECT maxed = {};
	WINDOWPLACEMENT placement = {};
	placement.length = sizeof(placement);
	if (!hwnd || !ShellWorkAreaOf(hwnd, &work) || !GetWindowRect(hwnd, &maxed) || !GetWindowPlacement(hwnd, &placement))
		return;
	const LONG workW = work.right - work.left;
	const LONG workH = work.bottom - work.top;
	LONG w = placement.rcNormalPosition.right - placement.rcNormalPosition.left;
	LONG h = placement.rcNormalPosition.bottom - placement.rcNormalPosition.top;
	// a remembered size as big as the screen would restore into what looks like the same window
	if (w >= workW - 24 && h >= workH - 24) {
		w = workW * 7 / 10;
		h = workH * 8 / 10;
	}
	// the maximized window hangs past the work area by its border, so the position along the bar is measured against the visible part
	const double along = workW > 0 ? qBound(0.0, double(cursor.x - work.left) / double(workW), 1.0) : 0.5;
	const LONG left = qBound<LONG>(work.left, cursor.x - LONG(along * w), qMax<LONG>(work.left, work.right - w));
	ShowWindow(hwnd, SW_RESTORE);
	SetWindowPos(hwnd, nullptr, left, qMax<LONG>(work.top, maxed.top), w, h, SWP_NOZORDER | SWP_NOACTIVATE);
	if (handOff) {
		if (QWindow *handle = mw->windowHandle())
			handle->startSystemMove();
	}
}

void ShellBindHwnd()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !mw->testAttribute(Qt::WA_WState_Created))
		return;
	HWND hwnd = (HWND)mw->winId();
	if (!hwnd)
		return;
	if (hwnd != g_shellHwnd) {
		g_shellHwnd = hwnd;
		blog(LOG_INFO, "[replaykit] clean shell frame bound to the obs window");
	}
	ShellApplyFrameStyle(hwnd);
}

// a maximized window with a sizing border is positioned so its frame hangs over the screen edge, and qt treats the whole window rectangle as its client area, so what is off screen has to be measured and kept out of the layout. read off the real window rectangle against the work area instead of assumed from system metrics, in logical px
QMargins ShellMaximizeOverhang(QMainWindow *mw)
{
	HWND hwnd = (HWND)mw->winId();
	RECT wr = {};
	MONITORINFO info = {};
	info.cbSize = sizeof(info);
	if (g_shellNativeFullscreen || !IsZoomed(hwnd) || !GetWindowRect(hwnd, &wr) || !GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &info))
		return QMargins();
	const qreal dpr = mw->devicePixelRatioF();
	auto logical = [dpr](LONG px) { return px > 0 ? int(qRound(px / dpr)) : 0; };
	return QMargins(logical(info.rcWork.left - wr.left), logical(info.rcWork.top - wr.top), logical(wr.right - info.rcWork.right),
			logical(wr.bottom - info.rcWork.bottom));
}
ITaskbarList2 *g_shellTaskbar = nullptr;

// stretches the obs window over the whole monitor it is on, or puts it back. this is the fullscreen of a playing clip, done on the window itself so nothing of the frame, the rail or the taskbar is left showing
void ShellNativeFullscreen(bool on)
{
	QMainWindow *mw = ObsMainWindowQt();
	HWND hwnd = g_shellHwnd;
	if (!mw || !hwnd || on == g_shellNativeFullscreen)
		return;
	if (on) {
		// the obs fullscreen interface already fills the monitor, and a hidden or minimized window has nothing to stretch
		if (mw->isFullScreen() || !mw->isVisible() || mw->isMinimized())
			return;
		MONITORINFO info = {};
		info.cbSize = sizeof(info);
		if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &info))
			return;
		g_shellFsPlacement = {};
		g_shellFsPlacement.length = sizeof(g_shellFsPlacement);
		GetWindowPlacement(hwnd, &g_shellFsPlacement);
		GetWindowRect(hwnd, &g_shellFsRect);
		g_shellNativeFullscreen = true;
		RemovePropW(hwnd, L"NonRudeHWND");
		// a maximized window stays on its maximized rectangle whatever it is asked for, so it is restored first and maximized again on the way back
		if (IsZoomed(hwnd))
			ShowWindow(hwnd, SW_RESTORE);
		ShellSyncFrameMargins(hwnd);
		const int monW = info.rcMonitor.right - info.rcMonitor.left;
		const int monH = info.rcMonitor.bottom - info.rcMonitor.top;
		SetWindowPos(hwnd, HWND_TOP, info.rcMonitor.left, info.rcMonitor.top, monW, monH, SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
		// a window that would not take the rectangle (a maximize that was still settling) is asked once more
		RECT now = {};
		if (GetWindowRect(hwnd, &now) && (now.left != info.rcMonitor.left || now.top != info.rcMonitor.top || now.right - now.left != monW || now.bottom - now.top != monH)) {
			blog(LOG_WARNING, "[replaykit] clip fullscreen: the window is %dx%d at %d,%d instead of the monitor, asking again", int(now.right - now.left), int(now.bottom - now.top), int(now.left), int(now.top));
			SetWindowPos(hwnd, HWND_TOP, info.rcMonitor.left, info.rcMonitor.top, monW, monH, SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
		}
		if (ITaskbarList2 *list = ShellTaskbarList())
			list->MarkFullscreenWindow(hwnd, TRUE);
		return;
	}
	g_shellNativeFullscreen = false;
	if (ITaskbarList2 *list = ShellTaskbarList())
		list->MarkFullscreenWindow(hwnd, FALSE);
	SetPropW(hwnd, L"NonRudeHWND", reinterpret_cast<HANDLE>(1));
	// the placement brings back maximized or normal, and the rectangle after it puts a maximized window on the work area again
	SetWindowPlacement(hwnd, &g_shellFsPlacement);
	// a maximized window is put back on its rectangle by windows; a normal one is placed again exactly as it was
	if (g_shellFsPlacement.showCmd != SW_SHOWMAXIMIZED)
		SetWindowPos(hwnd, nullptr, g_shellFsRect.left, g_shellFsRect.top, g_shellFsRect.right - g_shellFsRect.left,
			     g_shellFsRect.bottom - g_shellFsRect.top, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
	else if (!IsZoomed(hwnd))
		ShowWindow(hwnd, SW_MAXIMIZE);
	ShellSyncFrameMargins(hwnd);
}

// set by a press on the caption or the sizing frame and settled when the press is over. what tells a click from a drag is where the window is by then, not the messages of the move loop: a click may or may not start one, and a loop that starts reports movement even for a pointer that never left the pixel
static bool g_framePressed = false;
static RECT g_framePressRect = {};
// a click can wobble a few pixels, and that is still a click
constexpr int kClickSlop = 4;

static bool FrameMovedSincePress()
{
	RECT now = {};
	if (!GetWindowRect(g_shellHwnd, &now))
		return true;
	return qAbs(now.left - g_framePressRect.left) > kClickSlop || qAbs(now.top - g_framePressRect.top) > kClickSlop ||
	       qAbs((now.right - now.left) - (g_framePressRect.right - g_framePressRect.left)) > kClickSlop ||
	       qAbs((now.bottom - now.top) - (g_framePressRect.bottom - g_framePressRect.top)) > kClickSlop;
}

// the press is over: a click is a click away from the menus that hang off the title bar and they roll up, a drag leaves them open, they went along with the window
static void SettleFramePress()
{
	if (!g_framePressed)
		return;
	// still held down, so it is neither yet
	if (GetAsyncKeyState(VK_LBUTTON) & 0x8000) {
		QTimer::singleShot(60, g_callbacks, []() { SettleFramePress(); });
		return;
	}
	g_framePressed = false;
	if (!FrameMovedSincePress())
		AnchoredPanel::DismissForPress(true);
}

bool ShellPointIsCaption(LPARAM screenPoint)
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && g_shellHwnd && ShellHitTestAt(mw, ShellPointFromHitTest(mw, screenPoint)) == HTCAPTION;
}

bool ShellPointIsFrame(POINT screen)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellHwnd)
		return false;
	// only where the window itself is under the point: a window of another program over it, or a menu of ours, is not the frame
	const HWND under = WindowFromPoint(screen);
	if (!under || GetAncestor(under, GA_ROOT) != g_shellHwnd)
		return false;
	const QPoint local = ShellPointFromHitTest(mw, MAKELPARAM(screen.x, screen.y));
	const LRESULT hit = ShellHitTestAt(mw, local);
	if (hit == HTCAPTION || (hit >= HTLEFT && hit <= HTBOTTOMRIGHT))
		return true;
	// a maximized window hands its title bar to the widget, which tells a click from a drag itself
	ShellTitleBar *title = g_shellTitle.data();
	return title && title->isVisible() && title->geometry().contains(local) && title->IsCaptionAt(title->mapFrom(mw, local));
}

bool ShellFrameFilter::nativeEventFilter(const QByteArray &, void *message, qintptr *result)
{
	MSG *msg = static_cast<MSG *>(message);
	if (!msg || !g_shellHwnd)
		return false;
	if (msg->message == WM_NCHITTEST && msg->hwnd != g_shellHwnd && IsChild(g_shellHwnd, msg->hwnd)) {
		// the title bar, the rail and the preview are native child windows, and each would answer this hit test itself with plain client area, so the frame never got asked and the window could be neither dragged nor resized from them. wherever the frame should answer they step aside and the question falls through to the main window
		QMainWindow *mw = ObsMainWindowQt();
		if (mw && ShellHitTestAt(mw, ShellPointFromHitTest(mw, msg->lParam)) != HTCLIENT) {
			*result = HTTRANSPARENT;
			return true;
		}
		return false;
	}
	if (msg->hwnd != g_shellHwnd)
		return false;
	switch (msg->message) {
	case WM_SHOWWINDOW:
		// the first time the window comes up, the title bar, the rail and the cover of the stage paint for the first time and the browser is made, and until that has happened a native window shows black or white for a frame. the window stays cloaked meanwhile, and is let out once the chrome has painted
		if (msg->wParam && !g_shellFirstShown) {
			g_shellFirstShown = true;
			g_shellFirstShowClock.start();
			ShellSetCloak(g_shellHwnd, true);
			QTimer::singleShot(90, g_callbacks, []() { ShellFinishFirstShow(); });
			// a window must never stay hidden, whatever becomes of the first timer
			QTimer::singleShot(1500, g_callbacks, []() { ShellFinishFirstShow(); });
		}
		return false;
	case WM_GETMINMAXINFO: {
		// a frameless window that the system sizes has no floor but the height of a caption, so it could be made smaller than anything on it
		QMainWindow *mw = ObsMainWindowQt();
		MINMAXINFO *info = reinterpret_cast<MINMAXINFO *>(msg->lParam);
		if (!mw || !info)
			return false;
		LONG minW = 0;
		LONG minH = 0;
		ShellMinTrack(g_shellHwnd, mw->devicePixelRatioF(), &minW, &minH);
		info->ptMinTrackSize.x = minW;
		info->ptMinTrackSize.y = minH;
		*result = 0;
		return true;
	}
	case WM_NCCALCSIZE:
		// the whole window is client area: leaving the proposed rectangle as it came in and answering 0 is what removes the frame
		*result = 0;
		return true;
	case WM_NCHITTEST: {
		QMainWindow *mw = ObsMainWindowQt();
		if (!mw)
			return false;
		*result = ShellHitTestAt(mw, ShellPointFromHitTest(mw, msg->lParam));
		return true;
	}
	case WM_NCACTIVATE:
		// lParam -1 tells windows not to repaint the (empty) caption on activation, which is what flashes a white bar
		*result = DefWindowProcW(msg->hwnd, WM_NCACTIVATE, msg->wParam, -1);
		return true;
	case WM_WINDOWPOSCHANGED:
	case WM_MOVE:
	case WM_SIZE:
		// the menus that hang off the title bar, the card of the storage and the confirm sheet keep up with the window all through a drag or a resize, where no timer runs. each one is placed from the window rectangle itself, since qt has not taken the new one in yet
		AnchoredPanel::FollowAll();
		StorageModalFollow();
		ConfirmDialogFollow();
		return false;
	case WM_NCLBUTTONDOWN:
		// a press on the caption or on the sizing frame may begin a drag or a resize, and the menus that hang off the title bar keep up with those, so only a press that ends without the window moving is a click away from them
		if (msg->wParam == HTCAPTION || (msg->wParam >= HTLEFT && msg->wParam <= HTBOTTOMRIGHT)) {
			g_framePressed = true;
			GetWindowRect(g_shellHwnd, &g_framePressRect);
			// the loop of a drag reports its end below, a press that starts no loop is settled by this timer once the button is up
			QTimer::singleShot(60, g_callbacks, []() { SettleFramePress(); });
			return false;
		}
		AnchoredPanel::DismissForPress();
		return false;
	case WM_NCRBUTTONDOWN:
		AnchoredPanel::DismissForPress();
		return false;
	case WM_EXITSIZEMOVE:
		if (g_framePressed) {
			g_framePressed = false;
			if (!FrameMovedSincePress())
				AnchoredPanel::DismissForPress(true);
		}
		return false;
	default:
		return false;
	}
}

} // namespace rk
