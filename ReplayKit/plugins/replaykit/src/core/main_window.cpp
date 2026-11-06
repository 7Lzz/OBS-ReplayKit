// the obs main window and its handle for the helper
#include "prelude.hpp"
#include "core/main_window.hpp"

#include "core/ipc_pipe.hpp"
#include "core/state.hpp"

namespace rk {

QMainWindow *ObsMainWindowQt()
{
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	return qobject_cast<QMainWindow *>(mw);
}

// obs keeps the dialogs it hid in a member that only its own ToggleShowHide refreshes, so hiding or showing the window through that slot is the one way that never calls into a dialog deleted since; SetShowing straight from here did that once the folded in settings dialog had been closed. it also refuses while a modal dialog is up, the same rule the tray icon follows
bool ToggleMainWindowShowing()
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && QMetaObject::invokeMethod(mw, "ToggleShowHide", Qt::DirectConnection);
}

bool ShowMainWindow()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return false;
	if (!mw->isVisible()) {
		ToggleMainWindowShowing();
		if (!mw->isVisible())
			mw->show();
	}
	if (mw->isMinimized())
		ShowWindow((HWND)mw->winId(), SW_RESTORE);
	return mw->isVisible() && !mw->isMinimized();
}

// obs_frontend_get_main_window() is the only language-neutral way to identify obss real main window -- its title is a localized template ("<version> - profile: ... - scenes: ...") with no fixed substring a non-english build still renders, which is what broke the helpers old title-matching close. captured once, not on the projector timer, since the handle never changes for the life of the process; the pipe thread sends it to the helper on each (re)connect.
void PublishMainWindow()
{
	QWidget *mainWindow = (QWidget *)obs_frontend_get_main_window();
	if (!mainWindow)
		return;
	g_mainWinValue.store((quintptr)mainWindow->winId());
}

} // namespace rk
