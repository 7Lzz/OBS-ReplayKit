// back and forward through the views of the clean shell
#include "prelude.hpp"
#include "shell/shell_history.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "shell/shell_history_model.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

ShellHistoryModel g_model;
// a step of the history is a change of view that is not a new place
bool g_stepping = false;
// the page, a qt event and the frame of the window can each see the same press, and a person cannot press twice in this little time
constexpr qint64 kRepeatGuardMs = 220;
qint64 g_lastRequestAt = 0;
int g_lastRequestDir = 0;

// a view the history can bring back: the wizard is not a place to return to, and the obs settings only while their dialog is still built
bool Openable(ShellView view)
{
	if (view == ShellView::Setup)
		return false;
	if (view == ShellView::ObsSettings)
		return g_obsSettingsDialog != nullptr;
	return true;
}

// the keys and the side buttons of the mouse over the qt parts of the window: the title bar, the rail, the preview and the obs settings. the pages of the shell are cef windows that qt never sees, and they say the same thing through the helper
class NavFilter : public QObject {
public:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		const QEvent::Type type = event->type();
		if (type != QEvent::MouseButtonPress && type != QEvent::MouseButtonRelease && type != QEvent::MouseButtonDblClick && type != QEvent::KeyPress)
			return false;
		if (!g_shellOn || !watched->isWidgetType())
			return false;
		QMainWindow *mw = ObsMainWindowQt();
		if (!mw || static_cast<QWidget *>(watched)->window() != mw)
			return false;
		if (type == QEvent::KeyPress) {
			auto *key = static_cast<QKeyEvent *>(event);
			int direction = 0;
			if (key->key() == Qt::Key_Back || (key->key() == Qt::Key_Left && key->modifiers() == Qt::AltModifier))
				direction = -1;
			else if (key->key() == Qt::Key_Forward || (key->key() == Qt::Key_Right && key->modifiers() == Qt::AltModifier))
				direction = 1;
			if (direction == 0)
				return false;
			// a key that is held down is one press, not a run through the whole history
			if (!key->isAutoRepeat())
				ShellHistoryRequest(direction);
			return true;
		}
		auto *mouse = static_cast<QMouseEvent *>(event);
		if (mouse->button() != Qt::BackButton && mouse->button() != Qt::ForwardButton)
			return false;
		// the step is taken when the button comes up, like a click; the press and the double click of these buttons mean nothing else in the window
		if (type == QEvent::MouseButtonRelease)
			ShellHistoryRequest(mouse->button() == Qt::BackButton ? -1 : 1);
		return true;
	}
};

NavFilter *g_filter = nullptr;

} // namespace

void ShellHistoryNote(ShellView view)
{
	if (g_stepping || view == ShellView::Setup)
		return;
	g_model.Note(view);
	if (g_shellTitle)
		g_shellTitle->SetNavigation(ShellCanGoBack(), ShellCanGoForward());
}

bool ShellCanGoBack()
{
	return g_model.Target(-1, Openable) >= 0;
}

bool ShellCanGoForward()
{
	return g_model.Target(1, Openable) >= 0;
}

bool ShellHistoryGo(int direction)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!g_shellOn || !mw || !mw->isVisible() || mw->isMinimized())
		return false;
	// a clip that plays fullscreen holds the whole window, and the first-run wizard is not a page to leave by a key
	if (g_clipsFullscreenActive || g_shellView == ShellView::Setup)
		return false;
	const int at = g_model.Target(direction < 0 ? -1 : 1, Openable);
	if (at < 0)
		return false;
	g_stepping = true;
	const bool went = ShellActivate(g_model.At(at));
	g_stepping = false;
	if (went)
		g_model.Land(at);
	if (g_shellTitle)
		g_shellTitle->SetNavigation(ShellCanGoBack(), ShellCanGoForward());
	return went;
}

void ShellHistoryRequest(int direction)
{
	if (direction == 0)
		return;
	const qint64 now = qint64(GetTickCount64());
	if (direction == g_lastRequestDir && now - g_lastRequestAt < kRepeatGuardMs)
		return;
	g_lastRequestDir = direction;
	g_lastRequestAt = now;
	ShellHistoryGo(direction);
}

void ShellHistoryStart()
{
	g_model.Reset(g_shellView);
	if (g_filter)
		return;
	g_filter = new NavFilter();
	qApp->installEventFilter(g_filter);
}

void ShellHistoryStop()
{
	if (!g_filter)
		return;
	qApp->removeEventFilter(g_filter);
	delete g_filter;
	g_filter = nullptr;
}

} // namespace rk
