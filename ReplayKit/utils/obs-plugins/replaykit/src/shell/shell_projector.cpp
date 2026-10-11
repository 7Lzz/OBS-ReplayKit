// the projector view: the preview, the strip above it and the sources panel under it
#include "prelude.hpp"
#include "shell/shell_projector.hpp"

#include <QEasingCurve>
#include <QStatusBar>
#include <QVariantAnimation>

#include "core/dev_log.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_curtain.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

bool ProjectorShowing()
{
	return g_shellOn && g_shellView == ShellView::Projector;
}

// a window in the tray or on the taskbar has had its preview switched off by obs itself
bool WindowOnScreen()
{
	QMainWindow *mw = ObsMainWindowQt();
	return mw && mw->isVisible() && !mw->isMinimized();
}

// the sources panel of obs is the way to add a camera, a text or a picture to the scene, so the projector view has it under the preview and no other view does. it is only shown and hidden, never closed, since closing a dock of obs asks a question first
void ShowSourcesPanel(bool show)
{
	QMainWindow *mw = ObsMainWindowQt();
	QDockWidget *dock = mw ? mw->findChild<QDockWidget *>(QStringLiteral("sourcesDock")) : nullptr;
	if (!dock)
		return;
	if (show) {
		dock->setFeatures(dock->features() & ~QDockWidget::DockWidgetClosable);
		if (dock->isFloating())
			dock->setFloating(false);
	}
	if (dock->isHidden() == show)
		dock->setVisible(show);
	// the stage is what is left of the window around the dock, and the views read it right after this
	if (QLayout *layout = mw->layout())
		layout->activate();
}

ShellCurtain *Cover()
{
	return static_cast<ShellCurtain *>(g_shellCurtain.data());
}

// obs puts a notice of its own with an enable button in the stage when the preview is off, and it would sit on the message of the shell in that same place. the shell says it in its own way, so that notice is kept from ever showing
class KeepHidden : public QObject {
public:
	explicit KeepHidden(QWidget *notice) : QObject(notice) {}

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (event->type() == QEvent::Show) {
			// hiding inside its own show event would upset the show that is running, so it happens right after and before the first paint
			QMetaObject::invokeMethod(static_cast<QWidget *>(watched), "hide", Qt::QueuedConnection);
		}
		return false;
	}
};

// what was chosen, kept for the times obs has the preview off out of the way: a window in the tray, a dialog of obs over the stage
bool g_previewWish = true;
// what the last apply saw, so the tick only does the work of an apply when the answer changed
int g_appliedState = -1;
// a change of the preview that waits for the stage to be covered, 1 on or 0 off, and -1 when none does; the generation lets a change that was overtaken by a newer one go by
int g_pendingPreview = -1;
int g_previewGen = 0;
// how long the cover waits over a preview that has just changed, so its first frames are not the ones that show
constexpr int kPreviewSettleMs = 60;

QAction *LockAction()
{
	static QPointer<QAction> action;
	if (!action) {
		QMainWindow *mw = ObsMainWindowQt();
		action = mw ? mw->findChild<QAction *>(QStringLiteral("actionLockPreview")) : nullptr;
	}
	return action;
}

int StateBits()
{
	return (ProjectorShowing() ? 1 : 0) | (!WindowOnScreen() || ShellPreviewIsOn() ? 2 : 0) | (ShellPreviewWanted() ? 4 : 0) | (ShellStudioModeOn() ? 8 : 0) | (ShellSceneLocked() ? 16 : 0);
}

// the stage goes to its own colour before a view swaps and comes back from it after, so a swap between the preview and the browser is a dip and never a cut. a fade in the browser is not an option for the preview, which is a native window, so the dip is a translucent window of its own above the stage, the way the storage card dims the window
constexpr int kFadeOutMs = 90;
constexpr int kFadeInMs = 240;

class StageFade : public QWidget {
public:
	explicit StageFade(QWidget *owner) : QWidget(owner)
	{
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint | Qt::WindowTransparentForInput);
		setAttribute(Qt::WA_TranslucentBackground, true);
		setAttribute(Qt::WA_ShowWithoutActivating, true);
		setAttribute(Qt::WA_TransparentForMouseEvents, true);
		connect(&m_anim, &QVariantAnimation::valueChanged, this, [this](const QVariant &value) {
			m_alpha = value.toReal();
			update();
		});
		connect(&m_anim, &QVariantAnimation::finished, this, [this]() {
			m_alpha = m_target;
			// clear means the stage is theirs again; opaque stays up until the next half of the dip takes it away
			if (m_alpha <= 0.0)
				hide();
			else
				update();
			std::function<void()> done;
			done.swap(m_done);
			if (done)
				done();
		});
	}

	// the window is up and painted before anything under it changes, so no frame of the swap gets out ahead of the dip. whole says the cover is the whole of the window under the title bar and beside the rail, which a swap of views needs since the strip of the projector view and the sources panel come and go around the stage; otherwise it is the stage alone
	void Cover(const QRect &globalRect, qreal alpha, bool whole)
	{
		m_anim.stop();
		m_done = nullptr;
		m_whole = whole;
		setGeometry(globalRect);
		UpdateShape();
		m_alpha = alpha;
		show();
		raise();
		repaint();
	}

	// the stage moved or changed size under a cover that is up (the sources panel came or went)
	void Follow(const QRect &globalRect)
	{
		if (isVisible() && geometry() != globalRect)
			setGeometry(globalRect);
		UpdateShape();
	}

	bool Whole() const { return m_whole; }

	// the animation starts from what is showing now, and done runs once it has arrived
	void FadeTo(qreal alpha, int ms, std::function<void()> done)
	{
		m_anim.stop();
		m_done = std::move(done);
		m_target = alpha;
		m_anim.setStartValue(m_alpha);
		m_anim.setEndValue(alpha);
		m_anim.setDuration(ms);
		m_anim.setEasingCurve(QEasingCurve::InOutSine);
		m_anim.start();
	}

	void Drop()
	{
		m_anim.stop();
		m_done = nullptr;
		m_alpha = 0.0;
		hide();
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QColor c = g_chromeBg;
		c.setAlphaF(qBound(0.0, m_alpha, 1.0));
		QPainter p(this);
		if (!m_shape.isEmpty())
			p.setClipRegion(m_shape);
		p.fillRect(rect(), c);
	}

private:
	// the stage has a rounded corner, drawn by a small window of its own under this one, and a cover that goes over it squares the corner off for as long as it is up and lets it come back as it fades. wherever that corner lies under the cover, the same sliver the corner window is cut to is left clear, so the curve is the same at every step of a dip
	void UpdateShape()
	{
		QRegion shape(rect());
		if (g_shellCorner && g_shellCorner->isVisible()) {
			const QPoint at = g_shellCorner->mapToGlobal(QPoint(0, 0)) - geometry().topLeft();
			if (QRect(at, g_shellCorner->size()).intersects(rect()))
				shape = shape.subtracted(QRegion(ShellCornerEar()).translated(at));
		}
		if (m_whole)
			shape = shape.subtracted(WindowCorners());
		if (shape != m_shape) {
			m_shape = shape;
			update();
		}
	}

	// the window has rounded corners at the bottom and a cover that reaches the bottom of the window is square, so what lies outside the curve is cut from it. a window that is maximized has square corners
	QRegion WindowCorners() const
	{
		QMainWindow *mw = ObsMainWindowQt();
		if (!mw || mw->isMaximized() || mw->isFullScreen())
			return QRegion();
		const QRect window(mw->mapToGlobal(QPoint(0, 0)), mw->size());
		const QRect mine = geometry();
		if (mine.bottom() < window.bottom() - 1)
			return QRegion();
		constexpr int r = 8;
		QRegion corners;
		if (mine.left() <= window.left() + 1)
			corners = corners.united(QRegion(0, height() - r, r, r).subtracted(QRegion(0, height() - 2 * r, 2 * r, 2 * r, QRegion::Ellipse)));
		if (mine.right() >= window.right() - 1)
			corners = corners.united(QRegion(width() - r, height() - r, r, r).subtracted(QRegion(width() - 2 * r, height() - 2 * r, 2 * r, 2 * r, QRegion::Ellipse)));
		return corners;
	}

	QVariantAnimation m_anim;
	QRegion m_shape;
	bool m_whole = false;
	qreal m_alpha = 0.0;
	qreal m_target = 0.0;
	std::function<void()> m_done;
};

QPointer<StageFade> g_fade;

// where the cover goes on screen right now, empty when the window is in the tray or on the taskbar and there is nothing to dip: the stage, or with whole the window under the title bar and beside the rail, minus the status bar where obs shows one
QRect CoverOnScreen(bool whole)
{
	QMainWindow *mw = ObsMainWindowQt();
	QWidget *central = mw ? mw->centralWidget() : nullptr;
	if (!central || !mw->isVisible() || mw->isMinimized())
		return QRect();
	if (!whole)
		return QRect(mw->mapToGlobal(central->geometry().topLeft()), central->size());
	const bool fullscreen = mw->isFullScreen();
	const QMargins inset = mw->isMaximized() && !fullscreen ? ShellMaximizeOverhang(mw) : QMargins();
	const int titleH = fullscreen ? 0 : kShellTitleH;
	const int railW = fullscreen ? 0 : kShellRailW;
	QRect area(inset.left() + railW, inset.top() + titleH, mw->width() - inset.left() - inset.right() - railW, mw->height() - inset.top() - inset.bottom() - titleH);
	if (QStatusBar *status = mw->findChild<QStatusBar *>(); status && status->isVisible())
		area.setBottom(area.bottom() - status->height());
	return QRect(mw->mapToGlobal(area.topLeft()), area.size());
}

StageFade *EnsureFade()
{
	if (!g_fade) {
		QMainWindow *mw = ObsMainWindowQt();
		if (!mw)
			return nullptr;
		g_fade = new StageFade(mw);
	}
	return g_fade;
}

} // namespace

bool ShellPreviewIsOn()
{
	static QPointer<QWidget> container;
	if (!container) {
		QMainWindow *mw = ObsMainWindowQt();
		container = mw ? mw->findChild<QWidget *>(QStringLiteral("previewContainer")) : nullptr;
	}
	return container && !container->isHidden();
}

bool ShellPreviewWanted()
{
	if (g_previewSuspended)
		g_previewWish = g_previewWasOn;
	else if (WindowOnScreen())
		g_previewWish = ShellPreviewIsOn();
	return g_previewWish;
}

bool ShellPreviewTarget()
{
	return g_pendingPreview >= 0 ? g_pendingPreview != 0 : ShellPreviewWanted();
}

bool ShellStudioModeOn()
{
	return obs_frontend_preview_program_mode_active();
}

bool ShellSceneLocked()
{
	QAction *action = LockAction();
	return action && action->isChecked();
}

void ShellProjectorSetup()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	// the menu of the preview has the projector windows, the transform items and a switch for the preview itself, and it is not what this view is for: the sources panel and the strip above the preview do the parts that matter
	if (QWidget *preview = mw->findChild<QWidget *>(QStringLiteral("preview")))
		preview->setContextMenuPolicy(Qt::NoContextMenu);
	if (QWidget *notice = mw->findChild<QWidget *>(QStringLiteral("previewDisabledWidget"))) {
		notice->installEventFilter(new KeepHidden(notice));
		notice->hide();
	}
}

void ShellProjectorPrepare()
{
	ShowSourcesPanel(ProjectorShowing());
}

void ShellProjectorApply()
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	g_appliedState = StateBits();
	const bool showing = ProjectorShowing();
	// a window that sits in the tray has had its preview switched off by obs itself, which says nothing about what was chosen, so that reads as on and the real state is read again once the window is back
	const bool on = !WindowOnScreen() || ShellPreviewIsOn();
	// while the preview is off its window is gone and the page the browser last showed would come through, so the stage is covered with a plain message in its place; the cover is taken away again once the preview is on or the view is another one. the preview windows sit above it
	if (g_shellCurtain) {
		if (showing && !on) {
			Cover()->SetMessage(QStringLiteral("Preview is off"), QStringLiteral("Turn it back on with the Preview switch above"));
			g_shellCurtain->show();
			g_shellCurtain->raise();
		} else if (Cover()->HasMessage()) {
			Cover()->SetMessage(QString(), QString());
			g_shellCurtain->hide();
		}
	}
	if (showing) {
		if (QWidget *central = mw->centralWidget())
			central->raise();
	}
	// the cover and the central widget have just been raised over the rounded corner of the stage and the resize grip, and both belong on top of them
	ShellRaiseGrip();
	if (g_shellBar)
		g_shellBar->SetState(ShellPreviewWanted(), ShellStudioModeOn(), ShellSceneLocked());
}

void ShellProjectorRefresh()
{
	if (StateBits() != g_appliedState)
		ShellProjectorApply();
}

void ShellProjectorSetPreview(bool on)
{
	if (!ObsMainWindowQt())
		return;
	DevLog("preview -> %s (studio mode %s)", on ? "on" : "off", ShellStudioModeOn() ? "on" : "off");
	const int gen = ++g_previewGen;
	// the preview is a native window and cannot fade, so while the projector view is up the change goes through the dip of a swap of views: the stage colour comes over the stage, the preview goes on or off under it, and the cover fades away again
	const bool dip = ProjectorShowing() && WindowOnScreen() && !ShellStudioModeOn();
	auto change = [gen, on, dip]() {
		if (gen != g_previewGen)
			return;
		g_pendingPreview = -1;
		QMainWindow *mw = ObsMainWindowQt();
		if (!mw)
			return;
		if (!ShellStudioModeOn()) {
			// the enable and disable slots keep the flag obs holds for the preview in step, which the display switch alone would not
			QMetaObject::invokeMethod(mw, on ? "EnablePreview" : "DisablePreview", Qt::DirectConnection);
			if (g_previewSuspended) {
				// a dialog of obs is over the stage and needs the display out of the way, so it goes off again and comes back when the dialog is gone, if it was chosen on
				g_previewWasOn = on;
				QMetaObject::invokeMethod(mw, "EnablePreviewDisplay", Qt::DirectConnection, Q_ARG(bool, false));
			}
			g_previewWish = on;
		}
		ShellProjectorApply();
		if (dip) {
			QTimer::singleShot(kPreviewSettleMs, g_callbacks, [gen]() {
				if (gen == g_previewGen)
					ShellProjectorReveal();
			});
		}
	};
	if (!dip) {
		change();
		return;
	}
	g_pendingPreview = on ? 1 : 0;
	ShellProjectorFadeOut(change, false);
}

void ShellProjectorSetLocked(bool locked)
{
	QAction *action = LockAction();
	DevLog("preview lock -> %s", locked ? "locked" : "unlocked");
	// the action is what obs itself goes through: it flips the lock of the preview, keeps its own checked state in step and saves the choice
	if (action && action->isChecked() != locked)
		action->trigger();
	ShellProjectorApply();
}

bool ShellProjectorCover(bool whole)
{
	const QRect area = CoverOnScreen(whole);
	StageFade *fade = area.isEmpty() ? nullptr : EnsureFade();
	if (!fade)
		return false;
	fade->Cover(area, 1.0, whole);
	return true;
}

void ShellProjectorFadeOut(std::function<void()> then, bool whole)
{
	const QRect area = CoverOnScreen(whole);
	StageFade *fade = area.isEmpty() ? nullptr : EnsureFade();
	if (!fade) {
		then();
		return;
	}
	fade->Cover(area, 0.0, whole);
	fade->FadeTo(1.0, kFadeOutMs, std::move(then));
}

void ShellProjectorFollow()
{
	if (!g_fade)
		return;
	const QRect area = CoverOnScreen(g_fade->Whole());
	if (!area.isEmpty())
		g_fade->Follow(area);
}

void ShellProjectorReveal()
{
	if (!g_fade || !g_fade->isVisible())
		return;
	ShellProjectorFollow();
	g_fade->FadeTo(0.0, kFadeInMs, nullptr);
}

void ShellProjectorDropCover()
{
	if (g_fade)
		g_fade->Drop();
}

} // namespace rk
