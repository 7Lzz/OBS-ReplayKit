// the program projector that share preview hands to discord
#include "prelude.hpp"
#include "app/share_projector.hpp"

namespace rk {

namespace {

// only touched on the ui thread, where both the close event and the queued open from the helper run
bool g_obsClosing = false;

// obs shows a projector inside its constructor, and qt sends a widget its show event before it asks windows to show the window, so one made see-through here never reaches the screen; the helper then parks it the way it parks any projector it finds
class SeeThroughOnShow : public QObject {
public:
	bool caught = false;
	HWND hwnd = nullptr;

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (event->type() != QEvent::Show || !watched->isWidgetType())
			return false;
		auto *widget = static_cast<QWidget *>(watched);
		QWindow *window = widget->isWindow() ? widget->windowHandle() : nullptr;
		if (!window || !window->property("isOBSProjectorWindow").toBool())
			return false;
		hwnd = reinterpret_cast<HWND>(widget->winId());
		SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
		caught = SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA) != FALSE;
		return false;
	}
};

} // namespace

HWND OpenShareProjectorHidden()
{
	// the helper asks again as soon as obs deletes the projector while closing, and that request can run in the middle of the shutdown
	if (g_obsClosing) {
		blog(LOG_INFO, "[replaykit] the share projector was not opened because obs is closing");
		return nullptr;
	}
	SeeThroughOnShow filter;
	const HWND focused = GetForegroundWindow();
	// installed for this one call only, since the projector is built and shown before it returns
	QCoreApplication::instance()->installEventFilter(&filter);
	obs_frontend_open_projector("StudioProgram", -1, nullptr, nullptr);
	QCoreApplication::instance()->removeEventFilter(&filter);
	// obs activates every projector it opens, which takes the keyboard from the game or app in front; this one is invisible, so the window that had the keyboard gets it back
	if (filter.hwnd && GetForegroundWindow() == filter.hwnd && focused && focused != filter.hwnd && IsWindow(focused))
		SetForegroundWindow(focused);
	if (filter.hwnd && !filter.caught)
		blog(LOG_WARNING, "[replaykit] the share projector opened but could not be made see-through before it showed");
	return filter.hwnd;
}

void StopOpeningShareProjector()
{
	g_obsClosing = true;
}

} // namespace rk
