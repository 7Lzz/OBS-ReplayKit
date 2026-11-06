// places the title bar, rail and pages in the obs window
#include "prelude.hpp"
#include "shell/shell_layout.hpp"

#include "core/draw_util.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_sleep.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "ui/anchored_panel.hpp"

#include <QCheckBox>
#include <QRegularExpression>
#include <QSaveFile>
#include <QStandardPaths>
#include <QWindowStateChangeEvent>

namespace rk {

namespace {

HBRUSH g_pageBrush = nullptr;
QRgb g_pageBrushColor = 0;

QString SourceResizeStyle()
{
	const QString dir = QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/scratch/source-resize");
	if (!QDir().mkpath(dir)) {
		blog(LOG_WARNING, "[replaykit] the source resize indicator folder could not be made");
		return {};
	}
	QStringList images;
	for (int hover = 0; hover < 2; ++hover) {
		const QString path = dir + QLatin1Char('/') + g_notifyTipFg.name().mid(1) + QStringLiteral("-46x10-") + QString::number(hover) + QStringLiteral(".svg");
		if (!QFile::exists(path)) {
			QFile indicator(IconPath(QStringLiteral("ui/source-resize.svg")));
			if (!indicator.open(QIODevice::ReadOnly)) {
				blog(LOG_WARNING, "[replaykit] the source resize indicator asset could not be read");
				return {};
			}
			QSaveFile file(path);
			const QByteArray svg = QString::fromUtf8(indicator.readAll())
				.arg(g_notifyTipFg.name(), hover ? QStringLiteral(".55") : QStringLiteral(".22")).toUtf8();
			if (!file.open(QIODevice::WriteOnly) || file.write(svg) != svg.size() || !file.commit()) {
				blog(LOG_WARNING, "[replaykit] the source resize indicator could not be written");
				return {};
			}
		}
		images.append(QDir::fromNativeSeparators(path));
	}
	// only the pill of the bar changes under the pointer, as on the resizer of the editor timeline (.tl-resizer); the obs theme outlines the whole separator and moves it by a margin on hover, which looked like a button
	return QStringLiteral("QMainWindow#OBSBasic::separator { width:10px; height:10px; background:%1; border:none; margin:0px; }"
			      "QMainWindow#OBSBasic::separator:hover { background:%1; border:none; margin:0px; }"
			      "QMainWindow#OBSBasic::separator:horizontal { image:url(\"%2\"); image-position:center; }"
			      "QMainWindow#OBSBasic::separator:horizontal:hover { image:url(\"%3\"); }")
		.arg(g_chromeBg.name(), images[0], images[1]);
}

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
// the arrow of the dropdowns of the pages (nav/combo-arrow), in a colour of the theme and turned over for an open dropdown, written to the scratch folder of replaykit, since a qss image cannot be recoloured; made again only when the colour changes. empty when it cannot be written, and the dropdown then keeps the arrow of the obs theme
QString TintedComboArrow(const QColor &ink, bool open)
{
	const QString dir = QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/scratch/combo-arrow");
	const QString path = dir + QStringLiteral("/%1-%2.svg").arg(ink.name().mid(1), open ? QStringLiteral("up") : QStringLiteral("down"));
	if (QFile::exists(path))
		return path;
	QFile in(IconPath(QStringLiteral("nav/combo-arrow.svg")));
	if (!QDir().mkpath(dir) || !in.open(QIODevice::ReadOnly))
		return {};
	QString svg = QString::fromUtf8(in.readAll()).replace(QStringLiteral("currentColor"), ink.name());
	if (open)
		svg.replace(QRegularExpression(QStringLiteral("(<path )")), QStringLiteral("<path transform=\"rotate(180 8 8)\" "));
	QSaveFile out(path);
	const QByteArray bytes = svg.toUtf8();
	if (!out.open(QIODevice::WriteOnly) || out.write(bytes) != bytes.size() || !out.commit())
		return {};
	return path;
}

// the shared checkmark (action/check) in a colour of the theme, for the picked row of a dropdown, the way the dropdowns of the pages mark it; empty when it cannot be written
QString TintedCheck(const QColor &ink)
{
	const QString dir = QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/scratch/combo-arrow");
	const QString path = dir + QStringLiteral("/check-%1.svg").arg(ink.name().mid(1));
	if (QFile::exists(path))
		return path;
	QFile in(IconPath(QStringLiteral("action/check.svg")));
	if (!QDir().mkpath(dir) || !in.open(QIODevice::ReadOnly))
		return {};
	// a 24 by 14 strip with a 14 px check at its left and 10 px of nothing after it, so a row that puts it at its right edge shows the check where the dropdowns of the pages do (14 px, 10 px in from the right)
	QString body = QString::fromUtf8(in.readAll()).replace(QStringLiteral("currentColor"), ink.name());
	body.replace(QRegularExpression(QStringLiteral("<svg[^>]*>")), QStringLiteral("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"14\" viewBox=\"0 0 41.14 24\" fill=\"%1\">").arg(ink.name()));
	QSaveFile out(path);
	const QByteArray bytes = body.toUtf8();
	if (!out.open(QIODevice::WriteOnly) || out.write(bytes) != bytes.size() || !out.commit())
		return {};
	return path;
}

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

// obs-browser places the window of a browser at the origin of its container once, when it makes it, and every later resize keeps whatever position it has; the shell browser was once found shifted far up and to the left with no move of ours, which left the page showing only its bottom right corner, so it is put back and the offset is logged
void ShellKeepBrowserAtOrigin(QWidget *browser)
{
	if (!browser)
		return;
	auto check = [](HWND hwnd, LPARAM) -> BOOL {
		wchar_t name[32] = {};
		if (!GetClassNameW(hwnd, name, 32) || wcscmp(name, L"CefBrowserWindow") != 0)
			return TRUE;
		RECT rc;
		HWND parent = GetParent(hwnd);
		if (!parent || !GetWindowRect(hwnd, &rc))
			return FALSE;
		MapWindowPoints(HWND_DESKTOP, parent, reinterpret_cast<POINT *>(&rc), 2);
		if (rc.left != 0 || rc.top != 0) {
			blog(LOG_WARNING, "[replaykit] the shell browser window sat at %ld,%ld in its container, put back at 0,0", rc.left, rc.top);
			SetWindowPos(hwnd, nullptr, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
		}
		return FALSE;
	};
	EnumChildWindows((HWND)browser->winId(), check, 0);
}

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
		"QDockWidget#sourcesDock, #dockWidgetContents_6 { border:none; }"
		"QDockWidget::title { background:%1; color:%3; border:none; padding-left:12px; }"
		"#sourcesFrame { border:none; border-radius:8px; }"
		"#sourcesToolbar { background:transparent; color:%3; border:none; padding:4px 6px; }"
		// the + - and gear take the hover of the buttons of the editor toolbar (.tl-btn: the text colour at 10%)
		"#sourcesToolbar QToolButton:hover { background:%2; border-radius:6px; }"
		"SourceTree { border:none; border-radius:8px; padding:6px 14px; alternate-background-color:%1; selection-background-color:%4; selection-color:%5; }"
		"SourceTree QScrollBar:vertical { background:transparent; border:none; width:8px; margin:0px; }"
		"SourceTree QScrollBar::handle:vertical { background:%7; min-height:24px; border-radius:4px; }"
		"SourceTree QScrollBar::handle:vertical:hover { background:%8; }"
		"SourceTree QScrollBar::add-line:vertical, SourceTree QScrollBar::sub-line:vertical { height:0px; border:none; background:transparent; }"
		"SourceTree QScrollBar::add-page:vertical, SourceTree QScrollBar::sub-page:vertical { background:transparent; border:none; }"
		"SourceTreeItem { background:transparent; color:%3; }"
		// the last rows stop above the resize corner, which sits over the bottom right of the window
		"SourceTree { padding-bottom:%6px; }"
		// a row lights up under the pointer the way a row of a list of the editor does (.ed-preset: the text colour at 6%)
		"SourceTree::item:hover:!selected { background:%9; border-radius:5px; }"
		"SourceTree::item:selected { background:%4; border-radius:5px; }"
		"SourceTreeItem[rkSelected=\"true\"], SourceTreeItem[rkSelected=\"true\"] QLabel { color:%5; }")
		// qt fills the lowest placeholder present with the first value whatever its number, so every number from 1 to 9 is used here
		.arg(g_chromeBg.name(), ChromeInk(0.10).name(QColor::HexArgb), g_notifyTipFg.name(), g_menuAccent.name(), g_menuOnAccent.name(), QString::number(kShellGrip), ChromeInk(0.26).name(QColor::HexArgb), ChromeInk(0.46).name(QColor::HexArgb), ChromeInk(0.06).name(QColor::HexArgb))
		+ InkedIndicatorRules(g_menuOnAccent);
	if (QWidget *sources = mw->findChild<QWidget *>(QStringLiteral("sourcesDock"))) {
		if (sources->styleSheet() != sourcesStyle)
			sources->setStyleSheet(sourcesStyle);
		HookSourceSelection(sources);
	}
	// the source settings use the editor toolbar's panel mix, rounded outline and inset ends
	const QColor editorPanel(qRound(g_menuPanel.red() * 0.45 + g_chromeBg.red() * 0.55),
				 qRound(g_menuPanel.green() * 0.45 + g_chromeBg.green() * 0.55),
				 qRound(g_menuPanel.blue() * 0.45 + g_chromeBg.blue() * 0.55));
	QWidget *central = mw->centralWidget();
	const QMargins stageMargins = central && central->layout() ? central->layout()->contentsMargins() : QMargins();
	const int leftGap = qMax(0, 12 - stageMargins.left());
	const int rightGap = qMax(0, 12 - stageMargins.right());
	if (central) {
		central->setAutoFillBackground(true);
		QPalette palette = central->palette();
		if (palette.color(QPalette::Window) != g_chromeBg) {
			palette.setColor(QPalette::Window, g_chromeBg);
			central->setPalette(palette);
		}
	}
	// the buttons are the .btn of the editor (--button_bg with a border of the same colour, --button_bg_hover with the stronger border under the pointer, the window ground while pressed, 30 px tall, 4 px corners, 12 px text) and the dropdown is the .combo-button of the pages (32 px, a divider before the arrow, the accent around it while open, the list on the panel colour)
	const QString arrowDown = TintedComboArrow(g_notifyTipFg, false);
	const QString arrowUp = TintedComboArrow(g_notifyTipFg, true);
	QString contextStyle = QStringLiteral(
		"#contextContainer { background:%1; color:%2; margin:0px; border:1px solid %3; border-radius:8px; }"
		"#contextSubContainer { background:transparent; color:%2; border:none; }"
		"QLabel { color:%2; font-size:12px; }"
		// the dividers of the bar are one line of the border colour; obs draws them as a sunken groove, two lines of two colours
		"QFrame#line, QFrame#line_3 { border:none; background:%3; min-width:1px; max-width:1px; margin:6px 4px; }"
		// the theme of obs gives a button a fixed height of its own and margins above and below it, which pushed the 30 px button of the editor off the middle of the bar; height and margins are set here outright
		"QPushButton { background:%3; color:%2; border:1px solid %3; border-radius:4px; height:28px; min-height:28px; max-height:28px; margin:0px; padding:0px 12px; font-size:12px; }"
		"QPushButton:hover { background:%6; border-color:%4; } QPushButton:pressed { background:%7; } QPushButton:disabled { color:%5; }"
		"QComboBox { background:%3; color:%2; border:1px solid %3; border-radius:4px; height:30px; min-height:30px; max-height:30px; margin:0px; padding:0px 37px 0px 9px; font-size:12px; }"
		"QComboBox:hover { border-color:%4; }"
		"QComboBox:on { background:%7; border-color:%8; }"
		"QComboBox::drop-down { subcontrol-origin:border; subcontrol-position:top right; width:28px; border:none; border-left:1px solid %9; }"
		"QComboBox QAbstractItemView { background:%9; color:%2; border:1px solid %3; padding:3px; outline:0px; selection-background-color:%8; selection-color:%10; }"
		// the rows of the list: the 28 px rows of the dropdowns of the pages, the accent for the picked one and the hover colour of a button for the one under the pointer, set outright since the obs theme paints both its own way and outlines the picked row
		"QComboBox QAbstractItemView::item { min-height:28px; padding:0px 30px 0px 10px; border:none; border-radius:3px; margin:0px; color:%2; background:transparent; }"
		"QComboBox QAbstractItemView::item:hover { background:%6; color:%2; border:none; }"
		"QComboBox QAbstractItemView::item:selected { background:%8; color:%10; border:none; }")
		.arg(editorPanel.name(), g_notifyTipFg.name(), g_menuField.name(), g_menuBorderStrong.name(), g_menuMuted.name(), g_notifyBorder.name(), g_chromeBg.name(), g_menuAccent.name(), g_menuPanel.name())
		.arg(g_menuOnAccent.name());
	const QString check = TintedCheck(g_menuOnAccent);
	if (!check.isEmpty())
		contextStyle += QStringLiteral("QComboBox QAbstractItemView::item:selected { background-image:url(%1); background-repeat:no-repeat; background-position:right center; background-origin:border; background-clip:border; }").arg(QDir::fromNativeSeparators(check));
	if (!arrowDown.isEmpty() && !arrowUp.isEmpty())
		contextStyle += QStringLiteral("QComboBox::down-arrow { image:url(%1); width:16px; height:16px; } QComboBox::down-arrow:on { image:url(%2); }")
			.arg(QDir::fromNativeSeparators(arrowDown), QDir::fromNativeSeparators(arrowUp));
	// obs fixes the name of the selected source at 160 px and cuts a longer one off; it takes the width of its name instead
	if (QLabel *name = mw->findChild<QLabel *>(QStringLiteral("contextSourceLabel"))) {
		if (name->maximumWidth() != QWIDGETSIZE_MAX) {
			name->setMinimumWidth(0);
			name->setMaximumWidth(QWIDGETSIZE_MAX);
			name->setSizePolicy(QSizePolicy::Preferred, name->sizePolicy().verticalPolicy());
		}
	}
	if (QWidget *context = mw->findChild<QWidget *>(QStringLiteral("contextContainer"))) {
		QWidget *host = context->parentWidget();
		if (host && host->objectName() == QLatin1String("sourceSettingsHost") && host->layout())
			host->layout()->setContentsMargins(leftGap, 4, rightGap, 8);
		if (context->styleSheet() != contextStyle)
			context->setStyleSheet(contextStyle);
	}
	const QString resizeStyle = SourceResizeStyle();
	if (!resizeStyle.isEmpty()) {
		const QString previous = mw->property("rkSourceResizeStyle").toString();
		QString style = mw->styleSheet();
		if (previous != resizeStyle || !style.contains(resizeStyle)) {
			if (!previous.isEmpty())
				style.remove(previous);
			mw->setProperty("rkSourceResizeStyle", resizeStyle);
			mw->setStyleSheet(style + resizeStyle);
		}
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
	const bool page = !coverAll && ShellIsPageView(g_shellView);
	if (g_shellHost) {
		g_shellHost->setGeometry(coverAll ? mw->rect() : page ? pageRect : stage);
		ShellTintBrowserWindows(mw, g_shellHost->browser);
		ShellKeepBrowserAtOrigin(g_shellHost->browser);
	}
	if (g_viewObsHost)
		g_viewObsHost->setGeometry(stage);
	// the curtain covers all of the browser it stands in for: a page also spans the resize strip, where a browser made fresh shows black until its first frame
	if (g_shellCurtain)
		g_shellCurtain->setGeometry(page ? pageRect : stage);
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
			// a sleeping page starts loading before the window is drawn, under the stage cover
			ShellSleepWindowChanged();
			ShellPrepareShow();
			ShellResetHostProbe();
			ShellBindHwnd();
			ShellLayout();
			ShellTintPreview();
			QTimer::singleShot(0, g_callbacks, []() { ShellMakeMaximizeReal(); });
			if (g_shellHost && !g_hostReady && !g_hostVisibleTimer.isValid())
				g_hostVisibleTimer.start();
			// let the restored renderer resume before checking whether it still answers
			QTimer::singleShot(5000, g_callbacks, []() { ShellProbeHost(); });
			if (g_shellPendingSetup) {
				g_shellPendingSetup = false;
				QTimer::singleShot(0, g_callbacks, []() { ShellOpenView(ShellView::Setup, true); });
			}
			break;
		case QEvent::Hide:
			ShellResetHostProbe();
			ShellSleepWindowChanged();
			break;
		case QEvent::Resize:
		case QEvent::WindowStateChange:
			if (event->type() == QEvent::WindowStateChange) {
				const auto *state = static_cast<QWindowStateChangeEvent *>(event);
				if ((state->oldState() & Qt::WindowMinimized) && m_mw->isVisible() && !m_mw->isMinimized())
					ShellPrepareShow();
				ShellResetHostProbe();
				ShellSleepWindowChanged();
				if (g_shellHost) {
					const bool minimizedEditor = m_mw->isMinimized() && ShellEditorActive();
					const bool showing = m_mw->isVisible() && !m_mw->isMinimized();
					ShellRunJs(g_shellHost->browser, showing ? "window.RKShell && RKShell.windowShown(true);" : minimizedEditor ? "window.RKShell && RKShell.windowShown(false, true);" : "window.RKShell && RKShell.windowShown(false);");
				}
				ShellSyncFrameMargins(g_shellHwnd);
				// whoever asked for the maximize (obs restoring its saved state, a shortcut) went through qt, which only resizes
				QTimer::singleShot(0, g_callbacks, []() { ShellMakeMaximizeReal(); });
				// a window that was minimized may have lost its page meanwhile
				if (!m_mw->isMinimized())
					QTimer::singleShot(5000, g_callbacks, []() { ShellProbeHost(); });
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
