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

#include <QCheckBox>
#include <QRegularExpression>
#include <QStandardPaths>

namespace rk {

namespace {

HBRUSH g_pageBrush = nullptr;
QRgb g_pageBrushColor = 0;

// windows fills a native window with the background of its class where it has grown and nothing is drawn yet, and the windows cef keeps under a page carry white, which flashed along the edges while the window was resized; they and the window itself take the window colour instead, again whenever the theme changes it. an earlier brush is never deleted, since a window of the same class elsewhere may still be using it
void ShellTintBrowserWindows(QMainWindow *mw, QWidget *browser)
{
	if (!browser)
		return;
	const QRgb want = g_chromeBg.rgb();
	if (!g_pageBrush || g_pageBrushColor != want) {
		g_pageBrush = CreateSolidBrush(RGB(g_chromeBg.red(), g_chromeBg.green(), g_chromeBg.blue()));
		g_pageBrushColor = want;
	}
	const LONG_PTR brush = (LONG_PTR)g_pageBrush;
	auto tint = [](HWND hwnd, LPARAM b) -> BOOL {
		if (GetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND) != b)
			SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND, b);
		return TRUE;
	};
	tint((HWND)mw->winId(), brush);
	const HWND root = (HWND)browser->winId();
	tint(root, brush);
	EnumChildWindows(root, tint, brush);
}

// the icon of a source is a picture on a label, so a selected row shows it in the ink of the accent and gets the picture obs gave it back once it is let go
QPixmap Inked(const QPixmap &pixmap, const QColor &ink)
{
	QImage image = pixmap.toImage().convertToFormat(QImage::Format_ARGB32_Premultiplied);
	QPainter painter(&image);
	painter.setCompositionMode(QPainter::CompositionMode_SourceIn);
	painter.fillRect(image.rect(), ink);
	painter.end();
	QPixmap out = QPixmap::fromImage(image);
	out.setDevicePixelRatio(pixmap.devicePixelRatio());
	return out;
}

void InkSourceIcon(QLabel *label, bool on)
{
	const QVariant original = label->property("rkIcon");
	if (on) {
		const QPixmap pixmap = original.isValid() ? original.value<QPixmap>() : label->pixmap();
		if (pixmap.isNull())
			return;
		if (!original.isValid())
			label->setProperty("rkIcon", pixmap);
		label->setPixmap(Inked(pixmap, g_menuOnAccent));
	} else if (original.isValid()) {
		label->setPixmap(original.value<QPixmap>());
		label->setProperty("rkIcon", QVariant());
	}
}

// the eye and the lock of a row are images of the obs theme, which no style can recolour, so copies in the ink of the accent are written to the scratch folder of replaykit and the selected rows point at those; made again only when the ink changes
QString InkedIndicatorRules(const QColor &ink)
{
	static QString madeFor;
	static QString rules;
	if (madeFor == ink.name())
		return rules;
	madeFor = ink.name();
	rules.clear();
	const QString dir = QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/scratch/source-icons");
	QDir().mkpath(dir);
	struct Indicator {
		const char *kind;
		const char *state;
		const char *source;
	};
	const Indicator indicators[] = {
		{"indicator-visibility", "checked", "theme:Dark/visible.svg"},
		{"indicator-visibility", "unchecked", ":res/images/invisible.svg"},
		{"indicator-lock", "checked", "theme:Dark/locked.svg"},
		{"indicator-lock", "unchecked", ":res/images/unlocked.svg"},
	};
	static const QRegularExpression paint(QStringLiteral("((?:fill|stroke)\\s*[:=]\\s*\"?)#[0-9a-fA-F]{3,8}"));
	for (const Indicator &indicator : indicators) {
		QFile in(QString::fromLatin1(indicator.source));
		if (!in.open(QIODevice::ReadOnly))
			continue;
		QString svg = QString::fromUtf8(in.readAll());
		svg.replace(paint, QStringLiteral("\\1") + ink.name());
		const QString path = dir + QStringLiteral("/%1-%2-%3.svg").arg(QLatin1String(indicator.kind), QLatin1String(indicator.state), ink.name().mid(1));
		QFile out(path);
		if (!out.open(QIODevice::WriteOnly | QIODevice::Truncate))
			continue;
		out.write(svg.toUtf8());
		out.close();
		// the dot classes of the obs theme are its own shorthand, so the class property is matched directly here
		rules += QStringLiteral("SourceTreeItem[rkSelected=\"true\"] QCheckBox[class~=\"%1\"]::indicator:%2, SourceTreeItem[rkSelected=\"true\"] QCheckBox[class~=\"%1\"]::indicator:%2:hover { image:url(%3); }")
				 .arg(QLatin1String(indicator.kind), QLatin1String(indicator.state), path);
	}
	return rules;
}

// a row of the sources list is a widget of its own that never hears about the selection, so each row is told with the rkSelected property, which the style of the dock turns into dark text and icons on the accent
void MarkSelectedSources(QAbstractItemView *tree)
{
	QItemSelectionModel *selection = tree->selectionModel();
	QAbstractItemModel *model = tree->model();
	if (!selection || !model)
		return;
	for (int row = 0; row < model->rowCount(); row++) {
		const QModelIndex index = model->index(row, 0);
		QWidget *item = tree->indexWidget(index);
		if (!item)
			continue;
		const bool on = selection->isSelected(index);
		const QVariant was = item->property("rkSelected");
		if (was.isValid() && was.toBool() == on)
			continue;
		item->setProperty("rkSelected", on);
		item->style()->unpolish(item);
		item->style()->polish(item);
		for (QLabel *label : item->findChildren<QLabel *>()) {
			if (label->property("class").toString() == QStringLiteral("source-icon"))
				InkSourceIcon(label, on);
			label->style()->unpolish(label);
			label->style()->polish(label);
		}
		for (QCheckBox *box : item->findChildren<QCheckBox *>()) {
			box->style()->unpolish(box);
			box->style()->polish(box);
		}
	}
}

// the rows are made again when the scene changes or a group opens, after the model says so, so the marks are put on once those widgets exist
void HookSourceSelection(QWidget *sourcesDock)
{
	static QPointer<QAbstractItemView> hooked;
	QAbstractItemView *tree = nullptr;
	for (QAbstractItemView *view : sourcesDock->findChildren<QAbstractItemView *>())
		if (view->inherits("SourceTree"))
			tree = view;
	if (!tree || !tree->selectionModel() || !tree->model())
		return;
	if (hooked == tree) {
		MarkSelectedSources(tree);
		return;
	}
	hooked = tree;
	QPointer<QAbstractItemView> guard(tree);
	auto later = [guard]() {
		QTimer::singleShot(0, guard, [guard]() {
			if (guard)
				MarkSelectedSources(guard);
		});
	};
	QObject::connect(tree->selectionModel(), &QItemSelectionModel::selectionChanged, tree, later);
	QObject::connect(tree->model(), &QAbstractItemModel::modelReset, tree, later);
	QObject::connect(tree->model(), &QAbstractItemModel::rowsInserted, tree, later);
	QObject::connect(tree->model(), &QAbstractItemModel::rowsMoved, tree, later);
	QObject::connect(tree->model(), &QAbstractItemModel::layoutChanged, tree, later);
	MarkSelectedSources(tree);
}

} // namespace

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
	// the canvas editor keeps a 1px ring around the preview that nothing paints, so it showed whatever obs settings left in the window backing store under it
	static QPointer<QWidget> canvasEditor;
	if (!canvasEditor)
		canvasEditor = mw->findChild<QWidget *>(QStringLiteral("canvasEditor"));
	if (canvasEditor) {
		const QString canvasStyle = QStringLiteral("#canvasEditor { background:%1; }").arg(g_chromeBg.name());
		if (canvasEditor->styleSheet() != canvasStyle) {
			canvasEditor->setAttribute(Qt::WA_StyledBackground, true);
			canvasEditor->setStyleSheet(canvasStyle);
		}
	}
	const QString sourcesStyle = QStringLiteral(
		"QDockWidget#sourcesDock, #dockWidgetContents_6, #sourcesFrame, SourceTree { background:%1; color:%3; }"
		"QDockWidget::title { background:%2; color:%3; border:none; }"
		"#sourcesToolbar { background:%2; color:%3; }"
		"SourceTree { alternate-background-color:%1; selection-background-color:%4; selection-color:%5; }"
		"SourceTreeItem { background:transparent; color:%3; }"
		// the last rows stop above the resize corner, which sits over the bottom right of the window
		"SourceTree { padding-bottom:%6px; }"
		"SourceTree::item:selected { background:%4; }"
		"SourceTreeItem[rkSelected=\"true\"], SourceTreeItem[rkSelected=\"true\"] QLabel { color:%5; }")
		.arg(g_chromeBg.name(), g_menuPanel.name(), g_notifyTipFg.name(), g_menuAccent.name(), g_menuOnAccent.name(), QString::number(kShellGrip))
		+ InkedIndicatorRules(g_menuOnAccent);
	if (QWidget *sources = mw->findChild<QWidget *>(QStringLiteral("sourcesDock"))) {
		if (sources->styleSheet() != sourcesStyle)
			sources->setStyleSheet(sourcesStyle);
		HookSourceSelection(sources);
	}
	// the theme gives the toolbar a 4px top margin that nothing paints, so whatever obs settings left there in the window backing store stayed on screen above it; the gap is drawn as a top border in the stage colour instead
	const QString contextStyle = QStringLiteral(
		"#contextContainer, #contextSubContainer { background:%1; color:%2; }"
		"#contextContainer { margin-top:0px; border-top:4px solid %6; border-top-left-radius:0px; border-top-right-radius:0px; }"
		"QLabel { color:%2; } QPushButton { background:%3; color:%2; border:1px solid %4; }"
		"QPushButton:hover { background:%4; } QPushButton:disabled { color:%5; }")
		.arg(g_menuPanel.name(), g_notifyTipFg.name(), g_menuField.name(), g_menuBorderStrong.name(), g_menuMuted.name(), g_chromeBg.name());
	if (QWidget *context = mw->findChild<QWidget *>(QStringLiteral("contextContainer"))) {
		if (context->styleSheet() != contextStyle)
			context->setStyleSheet(contextStyle);
	}
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
	QRect pageRect;
	if (QWidget *central = mw->centralWidget()) {
		stage = central->geometry();
		if (QLayout *layout = central->layout())
			stage = stage.marginsRemoved(layout->contentsMargins());
		// a page reaches the right and bottom edges of the window, over the strip the stage keeps there for resizing, and takes that resizing over itself (the shell-nav partial); the preview and obs settings keep the strip, since they cannot
		pageRect = stage;
		pageRect.setRight(central->geometry().right());
		pageRect.setBottom(central->geometry().bottom());
	}
	const bool coverAll = g_clipsFullscreenActive && ShellShowsClips(g_shellView);
	if (g_shellHost) {
		g_shellHost->setGeometry(coverAll ? mw->rect() : ShellIsPageView(g_shellView) ? pageRect : stage);
		ShellTintBrowserWindows(mw, g_shellHost->browser);
	}
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
