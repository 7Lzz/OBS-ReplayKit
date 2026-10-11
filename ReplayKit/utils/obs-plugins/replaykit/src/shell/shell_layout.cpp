// places the title bar, rail and pages in the obs window
#include "prelude.hpp"
#include "shell/shell_layout.hpp"

#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

// the preview display fills what the scene does not cover with a colour from the obs theme, which shows as a slab of its own inside the stage; it takes the stage colour so the projector view reads as one surface with the others
void ShellTintPreview()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	static QPointer<QWidget> preview;
	if (!preview)
		preview = mw->findChild<QWidget *>(QStringLiteral("preview"));
	if (preview)
		preview->setProperty("displayBackgroundColor", g_chromeBg);
}

void ShellRepaintChrome()
{
	if (g_shellTitle)
		g_shellTitle->update();
	if (g_shellRail)
		g_shellRail->update();
	if (g_shellGrip)
		g_shellGrip->update();
	if (g_shellCorner)
		g_shellCorner->update();
	if (g_shellBar) {
		g_shellBar->update();
		for (QWidget *child : g_shellBar->findChildren<QWidget *>())
			child->update();
	}
	for (QWidget *page : {(QWidget *)g_shellHost.data(), g_viewObsHost.data(), g_shellCurtain.data()}) {
		if (!page)
			continue;
		QPalette pal = page->palette();
		pal.setColor(QPalette::Window, g_chromeBg);
		page->setPalette(pal);
	}
	ShellTintPreview();
}

// the two views that show clips: the clips view and the recent clips on home. a clip can only be playing fullscreen in one of them
bool ShellShowsClips(ShellView view)
{
	return view == ShellView::Clips || view == ShellView::Home;
}

void ShellLayout()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || !g_shellTitle || !g_shellRail)
		return;
	// a fullscreen obs window (F11) has no frame to speak of, so the chrome steps out of the way with it
	const bool fullscreen = mw->isFullScreen();
	const int titleH = fullscreen ? 0 : kShellTitleH;
	const int railW = fullscreen ? 0 : kShellRailW;
	// the projector view has a strip of its own under the title bar, like the tab bar of the clips view, and the stage starts under it
	const int barH = !fullscreen && g_shellView == ShellView::Projector ? kShellBarH : 0;
	// maximized: the part of the window that hangs over the screen edge is skipped, so the controls stay reachable
	const QMargins inset = mw->isMaximized() && !fullscreen ? ShellMaximizeOverhang(mw) : QMargins();
	mw->setContentsMargins(inset.left() + railW, inset.top() + titleH + barH, inset.right(), inset.bottom());
	g_shellTitle->setGeometry(inset.left(), inset.top(), qMax(0, mw->width() - inset.left() - inset.right()), titleH);
	g_shellRail->setGeometry(inset.left(), inset.top() + titleH, railW, qMax(0, mw->height() - inset.top() - inset.bottom() - titleH));
	g_shellTitle->setVisible(!fullscreen);
	g_shellRail->setVisible(!fullscreen);
	if (g_shellBar) {
		g_shellBar->setGeometry(inset.left() + railW, inset.top() + titleH, qMax(0, mw->width() - inset.left() - inset.right() - railW), barH);
		g_shellBar->setVisible(barH > 0);
	}
	// the views read the stage right below, and the layout of the window only settles on its own a moment later
	if (QLayout *layout = mw->layout())
		layout->activate();
	ShellLayoutViews();
	// a resize moved the bell and the badges, and the menus under them follow at once
	AnchoredPanel::FollowAll();
}

// the area the preview normally fills: the central widget minus its own ring. a clip playing fullscreen covers the whole window instead
void ShellLayoutViews()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	QRect stage;
	if (QWidget *central = mw->centralWidget()) {
		stage = central->geometry();
		if (QLayout *layout = central->layout())
			stage = stage.marginsRemoved(layout->contentsMargins());
	}
	const bool coverAll = g_clipsFullscreenActive && ShellShowsClips(g_shellView);
	if (g_shellHost)
		g_shellHost->setGeometry(coverAll ? mw->rect() : stage);
	for (QWidget *cover : {g_viewObsHost.data(), g_shellCurtain.data()}) {
		if (cover)
			cover->setGeometry(stage);
	}
	if (g_shellGrip) {
		// only a window that can still be resized wears it: not maximized, not fullscreen, not under a clip that plays fullscreen
		const bool wanted = !mw->isFullScreen() && !mw->isMaximized() && !coverAll;
		if (wanted) {
			g_shellGrip->move(mw->width() - kShellGrip, mw->height() - kShellGrip);
			if (!g_shellGrip->isVisible()) {
				g_shellGrip->show();
				g_shellGrip->raise();
			}
		} else if (g_shellGrip->isVisible()) {
			g_shellGrip->hide();
		}
	}
	if (g_shellCorner) {
		// the curve belongs to the stage, so it goes with it: not under a fullscreen window and not under a clip that plays fullscreen
		const bool wanted = !mw->isFullScreen() && !g_shellNativeFullscreen && !coverAll && !stage.isEmpty();
		if (wanted) {
			// the curve belongs to the stage as a whole, the strip of the projector view included
			const int barH = g_shellBar && g_shellBar->isVisible() ? g_shellBar->height() : 0;
			g_shellCorner->move(stage.left(), stage.top() - barH);
			if (!g_shellCorner->isVisible()) {
				g_shellCorner->show();
				g_shellCorner->raise();
			}
		} else if (g_shellCorner->isVisible()) {
			g_shellCorner->hide();
		}
	}
}

bool ShellHostFilter::eventFilter(QObject *watched, QEvent *event)
{
	if (watched == m_mw) {
		switch (event->type()) {
		case QEvent::WinIdChange:
			ShellBindHwnd();
			break;
		case QEvent::Show:
			ShellBindHwnd();
			ShellLayout();
			ShellTintPreview();
			QTimer::singleShot(0, g_callbacks, []() { ShellMakeMaximizeReal(); });
			if (g_shellHost && !g_hostReady && !g_hostVisibleTimer.isValid())
				g_hostVisibleTimer.start();
			// a window that was hidden or minimized may have lost its page meanwhile, so it is asked right after the window is back
			QTimer::singleShot(1500, g_callbacks, []() { ShellProbeHost(true); });
			if (g_shellPendingSetup) {
				g_shellPendingSetup = false;
				QTimer::singleShot(0, g_callbacks, []() { ShellOpenView(ShellView::Setup, true); });
			}
			break;
		case QEvent::Resize:
		case QEvent::WindowStateChange:
			if (event->type() == QEvent::WindowStateChange) {
				ShellSyncFrameMargins(g_shellHwnd);
				// whoever asked for the maximize (obs restoring its saved state, a shortcut) went through qt, which only resizes
				QTimer::singleShot(0, g_callbacks, []() { ShellMakeMaximizeReal(); });
				// a window that was minimized may have lost its page meanwhile
				if (!m_mw->isMinimized())
					QTimer::singleShot(1500, g_callbacks, []() { ShellProbeHost(true); });
			}
			ShellLayout();
			break;
		case QEvent::WindowActivate:
			// the window is the one in front again, from another program, the tray or a menu of the title bar, and the page gets the keyboard back once qt has taken the activation in
			QTimer::singleShot(0, g_callbacks, []() { ShellFocusBrowser(); });
			[[fallthrough]];
		case QEvent::WindowDeactivate:
		case QEvent::WindowIconChange:
			if (g_shellTitle)
				g_shellTitle->update();
			break;
		default:
			break;
		}
	} else if (event->type() == QEvent::Resize || event->type() == QEvent::Move) {
		ShellLayoutViews();
	}
	return false;
}

} // namespace rk
