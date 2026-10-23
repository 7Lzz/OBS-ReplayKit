// custom rows and behaviour of the obs tray menu
#include "prelude.hpp"
#include "tray/tray_menu.hpp"

#include <QJsonObject>

#include "app/clip_buffer.hpp"
#include "app/obs_actions.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/keybind_label.hpp"
#include "core/state.hpp"
#include "windows/clips_window.hpp"
#include "windows/settings_window.hpp"

namespace rk {

namespace {

void PinMenuAboveTaskbar(QMenu *menu);

// the ink of a hovered row: the text colour, which is what obs keeps on a selected item, unless a light accent asks for dark ink
QString HoverInk(const QString &onAccent, const QString &text)
{
	const bool darkInk = !onAccent.isEmpty() && onAccent.compare(QStringLiteral("#FFFFFF"), Qt::CaseInsensitive) != 0;
	return darkInk ? onAccent : text;
}

// hidden, not removed, each time the menu opens -- see InstallTrayMenu for why removeAction was the wrong tool here
QPointer<QAction> g_previewProjectorAction;
QPointer<QAction> g_programProjectorAction;

// obss own native record/replay-buffer actions -- hidden (not removed) behind TrayActionRow, same as g_previewProjectorAction/g_programProjectorAction above; kept alive only as the structural anchor insertAction positions the custom rows against.
QPointer<QAction> g_nativeRecordAction;
QPointer<QAction> g_nativeReplayBufferAction;

// blocks right-click activate (obss tray menu allows it like a native context menu) and fast-double-click debounce bypass for every item, including obss own unguarded exit -- scoped to the menu itself, not qApp, since an app-wide filter caused a confirmed 2026-08-10 crash by also catching cef widget events mid-teardown, and confirmed 2026-08-12 that a fast double-click delivers its second click as QEvent::MouseButtonDblClick, not a second press/release, which an earlier version never checked for
class TrayMenuGuard : public QObject {
public:
	explicit TrayMenuGuard(QMenu *menu) : QObject(menu), m_menu(menu) { menu->installEventFilter(this); }

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_menu)
			return false;
		const bool isClick = event->type() == QEvent::MouseButtonPress || event->type() == QEvent::MouseButtonRelease ||
				      event->type() == QEvent::MouseButtonDblClick;
		if (!isClick)
			return false;
		if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
			return true;
		return static_cast<QMouseEvent *>(event)->button() == Qt::RightButton;
	}

private:
	QMenu *m_menu;
};

enum class TrayRowKind { Clips, Recording, ReplayBuffer };

// custom row (name + a darker rounded keybind chip) for record/clipping -- a first attempt at this was reverted because it never aligned with native items no matter the margin (19/9/2/5/15px all tried), and research confirmed why: this menu used to render through windows own native platform menu bridge, which has no real support for QWidgetAction at all. OnFrontendEvent now clears setContextMenu() and pops this same trayMenu manually instead (see the tray->setContextMenu(nullptr) block), so this widget goes through qts own menu layout/paint path like everything else in it -- alignment is a fresh, real question now, not a fight against a bridge that was never going to cooperate. click handling stays debounced across both plausible delivery paths (the widgets own mouseReleaseEvent and the wrapping actions triggered()) since its still not certain in advance which one fires in a popped-not-native menu either.
class TrayActionRow : public QWidget {
public:
	TrayActionRow(TrayRowKind kind, QMenu *menu, QWidget *parent = nullptr) : QWidget(parent), m_kind(kind), m_menu(menu)
	{
		setAttribute(Qt::WA_Hover, true);
		setCursor(Qt::PointingHandCursor);
		// object name, not a type selector -- this class lives in an anon namespace with no Q_OBJECT, so qss sees it as plain "QWidget"; the id keeps the rules off the child labels
		setObjectName("rkTrayActionRow");
		// minimum (not the QWidget default) means qt treats our sizeHint as a floor it can grow past but never shrink below -- structural insurance against the reported clipping/overlap, instead of only reacting to it after the fact once the chip already got squeezed.
		setSizePolicy(QSizePolicy::Minimum, QSizePolicy::Fixed);

		nameLabel = new QLabel(this);
		nameLabel->setObjectName("rkTrayRowName");
		chipLabel = new QLabel(this);
		ApplyThemeColors(QString(), QString(), QString(), QString());
		// as tight as still legible -- reads as a small badge, not a second label competing for space.
		chipLabel->setStyleSheet("background-color: rgba(0, 0, 0, 70); border-radius: 3px; padding: 0px 4px; font-size: 9px; color: rgba(255, 255, 255, 160);");
		chipLabel->setVisible(false);

		auto *layout = new QHBoxLayout(this);
		// bracketed against screenshots: 20px measured left of sibling items, 32px measured right of them -- this is the midpoint, not a computed value.
		layout->setContentsMargins(26, 5, 10, 5);
		layout->setSpacing(6);
		// chip sits right next to the name, not pushed to the far right edge -- the stretch goes after both so any leftover space (e.g. qmenu matching this row to a wider sibling item) ends up as trailing empty space instead of a gap between name and chip.
		layout->addWidget(nameLabel);
		layout->addWidget(chipLabel);
		layout->addStretch();
	}

	QLabel *nameLabel;
	QLabel *chipLabel;

	void SetAction(QWidgetAction *a) { m_action = a; }
	QWidgetAction *action() const { return m_action; }

	// store the resolved theme colours and paint the rest state -- everything is a DIRECT stylesheet on the row / the label (no descendant selectors, no dynamic properties, no QMenu::hovered, which reports the wrong action for a QWidgetAction row so the highlight sticks on the plain item next to it); the rows own HoverEnter/HoverLeave, from WA_Hover, the same events that power :hover, drive SetHovered(). the hover look is the one obs draws on a selected item of a menu, the accent as the fill and its lighter step as the edge, so this row and the plain items next to it read as one
	void ApplyThemeColors(const QString &accent, const QString &accentLight, const QString &onAccent, const QString &text)
	{
		m_fill = accent.isEmpty() ? QStringLiteral("palette(highlight)") : accent;
		m_edge = accentLight.isEmpty() ? m_fill : accentLight;
		// hardcoded white, not palette(window-text): obs themes via qss not qpalette, so the palette role is often qt-default black -- and this is only the value for the sub-100ms before the first /settings fetch lands anyway
		m_inkRest = text.isEmpty() ? QStringLiteral("#FFFFFF") : text;
		m_inkActive = HoverInk(onAccent, m_inkRest);
		SetHovered(m_hovered);
	}

	void SetHovered(bool on)
	{
		m_hovered = on;
		setStyleSheet(on ? QStringLiteral("#rkTrayActionRow { border-radius: 4px; background-color: %1; border: 1px solid %2; }").arg(m_fill, m_edge)
				 : QStringLiteral("#rkTrayActionRow { border-radius: 4px; border: 1px solid transparent; }"));
		nameLabel->setStyleSheet(QStringLiteral("color: %1;").arg(on ? m_inkActive : m_inkRest));
	}

	void SetKeybindLabel(const std::string &label)
	{
		chipLabel->setVisible(!label.empty());
		chipLabel->setText(QString::fromStdString(label));
		// this rows sizeHint just changed (chip went from hidden/empty to a real label or back) -- updateGeometry() is qts actual mechanism for "go re-ask for my size", not a manual setMinimumWidth guess; SizePolicy::Minimum from the constructor is what makes sure that re-asked-for size is never shrunk back below.
		updateGeometry();
		if (m_menu) {
			// the reported "first open only, hotkey box overlaps text" bug: qmenu only lays out each actions geometry on first show and caches it after that, so on the very first open the async keybind fetch (this function) lands after that cache is already set and adjustSize() alone does not force a re-measure. a fake resize event is qts own documented way to flip the internal dirty flag that forces that recompute.
			QResizeEvent fakeResize(QSize(1, 1), m_menu->size());
			qApp->sendEvent(m_menu, &fakeResize);
			m_menu->adjustSize();
			PinMenuAboveTaskbar(m_menu);
		}
	}

	void ProxyTrigger()
	{
		if (m_lastTrigger.isValid() && m_lastTrigger.elapsed() < 250)
			return;
		m_lastTrigger.start();
		if (m_kind == TrayRowKind::Clips)
			ShowClips();
		else if (m_kind == TrayRowKind::Recording)
			ToggleRecording();
		else
			ToggleReplayBuffer();
		if (m_menu)
			m_menu->close();
	}

protected:
	void mouseReleaseEvent(QMouseEvent *event) override
	{
		if (event->button() == Qt::LeftButton)
			ProxyTrigger();
		QWidget::mouseReleaseEvent(event);
	}

	// HoverEnter/HoverLeave come from WA_Hover and pair up reliably (QEvent::Enter/Leave did not -- Leave never arrived, so an earlier version stuck dark); on enter we also make this the menus active action so the plain item the mouse came from drops its native selection highlight instead of staying stuck lit
	bool event(QEvent *e) override
	{
		if (e->type() == QEvent::HoverEnter) {
			SetHovered(true);
			if (m_menu && m_action)
				m_menu->setActiveAction(m_action);
		} else if (e->type() == QEvent::HoverLeave) {
			SetHovered(false);
		}
		return QWidget::event(e);
	}

private:
	TrayRowKind m_kind;
	QMenu *m_menu;
	QElapsedTimer m_lastTrigger;
	QWidgetAction *m_action = nullptr;
	bool m_hovered = false;
	QString m_fill;
	QString m_edge;
	QString m_inkRest;
	QString m_inkActive;
};

QPointer<TrayActionRow> g_recordRow;
QPointer<TrayActionRow> g_replayBufferRow;
QPointer<TrayActionRow> g_clipsRow;
QPointer<QMenu> g_trayMenu;
QString g_trayMenuStyle;

// the plain items are drawn by the theme of obs, which follows a change of the replaykit theme only after a restart, while the custom rows read the colours as the helper has them now; the same fill, edge and ink go on the plain items as well, so every row of the menu hovers alike. set again only when it changed, since a new style sheet makes the menu polish itself
void ApplyMenuHover(const QString &accent, const QString &accentLight, const QString &onAccent, const QString &text)
{
	const QString ink = HoverInk(onAccent, text);
	if (!g_trayMenu || accent.isEmpty() || accentLight.isEmpty() || ink.isEmpty())
		return;
	const QString style = QStringLiteral("QMenu::item:selected { background-color: %1; border-color: %2; color: %3; }").arg(accent, accentLight, ink);
	if (style == g_trayMenuStyle)
		return;
	g_trayMenuStyle = style;
	g_trayMenu->setStyleSheet(style);
}

void RefreshActionRowText()
{
	if (g_recordRow)
		g_recordRow->nameLabel->setText(obs_frontend_recording_active() ? "Stop Recording" : "Start Recording");
	if (g_replayBufferRow)
		g_replayBufferRow->nameLabel->setText(ClipBufferActive() ? "Stop Clipping" : "Start Clipping");
}

// obs builds the tray menu once and never rebuilds it, so this refreshes the share-preview checkbox and the record/clipping rows keybind chip right before each show instead of polling on a timer nobody is watching
void RefreshDynamicMenuState()
{
	RefreshActionRowText();
	if (g_clipsRow || g_recordRow || g_replayBufferRow) {
		RunAsync([]() {
			std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 500);
			std::string clipsLabel = KeybindLabelFromSettingsJson(settingsBody, "openClipsKeybind");
			std::string clipLabel = KeybindLabelFromSettingsJson(settingsBody, "clipKeybind");
			std::string recordingLabel = KeybindLabelFromSettingsJson(settingsBody, "recordingKeybind");
			std::string menuColors = ExtractJsonObjectField(settingsBody, "menuColors");
			std::string text = ExtractJsonStringField(menuColors, "text");
			std::string accent = ExtractJsonStringField(menuColors, "accent");
			std::string accentLight = ExtractJsonStringField(menuColors, "accentLight");
			std::string onAccent = ExtractJsonStringField(menuColors, "onAccent");
			QMetaObject::invokeMethod(
				g_callbacks,
				[clipsLabel, clipLabel, recordingLabel, text, accent, accentLight, onAccent]() {
					const QString t = QString::fromStdString(text);
					const QString a = QString::fromStdString(accent);
					const QString al = QString::fromStdString(accentLight);
					const QString oa = QString::fromStdString(onAccent);
					ApplyMenuHover(a, al, oa, t);
					if (g_clipsRow) {
						g_clipsRow->ApplyThemeColors(a, al, oa, t);
						g_clipsRow->SetKeybindLabel(clipsLabel);
					}
					if (g_replayBufferRow) {
						g_replayBufferRow->ApplyThemeColors(a, al, oa, t);
						g_replayBufferRow->SetKeybindLabel(clipLabel);
					}
					if (g_recordRow) {
						g_recordRow->ApplyThemeColors(a, al, oa, t);
						g_recordRow->SetKeybindLabel(recordingLabel);
					}
				},
				Qt::QueuedConnection);
		});
	}
	if (!g_sharePreviewAction)
		return;
	RunAsync([]() {
		std::string getBody = HttpRequest("GET", "/share-preview", 8767, nullptr, 500);
		bool available = JsonBoolField(getBody, "available", false);
		bool enabled = JsonBoolField(getBody, "enabled", false);
		QMetaObject::invokeMethod(
			g_callbacks,
			[available, enabled]() {
				if (!g_sharePreviewAction)
					return;
				g_sharePreviewAvailable = available;
				// stays enabled when unavailable so the click can offer to set Share Preview up
				g_sharePreviewAction->setEnabled(true);
				g_sharePreviewAction->setChecked(available && enabled);
			},
			Qt::QueuedConnection);
	});
}

// only pulls the menu up when it would actually overlap the taskbar (y), or back onto the screen when it would run off the right edge (x) -- this used to pin the bottom edge to the taskbar top unconditionally, which was wrong for a tray icon sitting in the hidden-icons flyout (a separate window that floats well above the taskbar): the menu snapped down to the taskbar instead of staying near the flyout it was actually opened from. checking against the menus own natural popup() position instead of always recomputing from the taskbar makes it adapt to wherever the icon actually is. skips the move() entirely when already at the target position -- re-positioning an already-visible native popup is a plausible cause of a reported "clicking outside the menu doesnt close it" bug (moving a shown popups hwnd can desync qts click-outside-to-dismiss tracking on windows), so this only touches geometry when it actually needs correcting.
void PinMenuAboveTaskbar(QMenu *menu)
{
	QScreen *screen = QGuiApplication::screenAt(menu->pos());
	if (!screen)
		return;
	QRect avail = screen->availableGeometry();
	int menuHeight = menu->sizeHint().height();
	int naturalY = menu->pos().y();
	int targetY = (naturalY + menuHeight > avail.bottom() + 1) ? qMax(avail.top(), avail.bottom() + 1 - menuHeight) : naturalY;
	int menuWidth = menu->sizeHint().width();
	int targetX = qMin(menu->pos().x(), qMax(avail.left(), avail.right() + 1 - menuWidth));
	if (menu->pos().x() == targetX && menu->pos().y() == targetY)
		return;
	menu->move(targetX, targetY);
}

} // namespace

QPointer<QAction> g_sharePreviewAction;
bool g_sharePreviewAvailable = false;

// obs builds the tray menu once, so the custom rows and the guarded actions are put into it here, after the frontend has finished loading
void InstallTrayMenu()
{
	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	if (!tray)
		return;
	QMenu *trayMenu = tray->contextMenu();
	if (!trayMenu)
		return;
	g_trayMenu = trayMenu;

	// obs wires this same trayMenu to the tray icon via setContextMenu(), which on windows hands rendering off to a native platform menu bridge that has no support for QWidgetAction at all (confirmed via research, and by five alignment attempts on a QWidgetAction row never converging) -- clearing that association and popping the same menu manually on a right-click keeps every existing action/signal/submenu intact but renders through qts own (non-native) menu painting, which is what actually supports a custom widget row correctly. obss own activated->IconActivated connection (left-click show/hide) is untouched; this only adds a second listener that acts on Context specifically.
	tray->setContextMenu(nullptr);
	QObject::connect(tray, &QSystemTrayIcon::activated, trayMenu, [trayMenu](QSystemTrayIcon::ActivationReason reason) {
		if (reason == QSystemTrayIcon::Context)
			trayMenu->popup(QCursor::pos());
	});

	new TrayMenuGuard(trayMenu);

	// obs builds this menu once in SystemTrayInit() and never rebuilds the top level (only the projector submenus contents refresh per click), so anything hidden below stays hidden without needing to be redone on every open View Clips uses the same custom row as recording/clipping so its current ReplayKit binding is visible in the tray.
	auto *clipsRow = new TrayActionRow(TrayRowKind::Clips, trayMenu);
	auto *clipsRowAction = new QWidgetAction(trayMenu);
	clipsRowAction->setDefaultWidget(clipsRow);
	clipsRow->SetAction(clipsRowAction);
	QObject::connect(clipsRowAction, &QAction::triggered, trayMenu, [clipsRow]() {
		if (clipsRow)
			clipsRow->ProxyTrigger();
	});
	clipsRow->nameLabel->setText(QObject::tr("View Clips"));
	g_clipsRow = clipsRow;

	QAction *sharePreview = new QAction(QObject::tr("Share Preview"), trayMenu);
	sharePreview->setCheckable(true);
	QObject::connect(sharePreview, &QAction::triggered, trayMenu, [](bool checked) {
		// not set up yet -- the checkbox flip is meaningless, so undo it and offer the install instead
		if (!g_sharePreviewAvailable) {
			if (g_sharePreviewAction)
				g_sharePreviewAction->setChecked(false);
			PromptInstallSharePreview();
			return;
		}
		ToggleSharePreview(checked);
	});
	g_sharePreviewAction = sharePreview;

	QAction *customSettings = new QAction(QObject::tr("ReplayKit Settings"), trayMenu);
	QObject::connect(customSettings, &QAction::triggered, trayMenu, []() { ShowSettings(); });

	QObject::connect(trayMenu, &QMenu::aboutToShow, trayMenu, [trayMenu]() {
		g_menuShownTimer.start();
		RefreshDynamicMenuState();
		// belt-and-suspenders re-hide on top of the one-time setVisible(false) below -- free today since obss updateSysTrayProjectorMenu() doesnt touch these actions visibility, but it runs every click so worth guarding anyway
		if (g_previewProjectorAction) g_previewProjectorAction->setVisible(false);
		if (g_programProjectorAction) g_programProjectorAction->setVisible(false);

		// confirmed 2026-08-16 this alone didnt stick since qts native platform code can finish positioning the menu after aboutToShow returns and silently override an early move() -- kept it anyway (free if it does nothing) and queued a second attempt after native show finishes as the standard workaround. bumped 10ms to 50ms to give qts own positioning more headroom to finish first -- see the comment on PinMenuAboveTaskbar for why an unnecessary post-show move is suspected in a "clicking outside doesnt close the menu" report.
		PinMenuAboveTaskbar(trayMenu);
		QTimer::singleShot(50, trayMenu, [trayMenu]() { PinMenuAboveTaskbar(trayMenu); });
	});

	// the rows drive their own hover look off HoverEnter/HoverLeave; this only guarantees they end up un-hovered once the menu closes, in case a HoverLeave was missed on the way out
	QObject::connect(trayMenu, &QMenu::aboutToHide, trayMenu, []() {
		if (g_clipsRow) g_clipsRow->SetHovered(false);
		if (g_recordRow) g_recordRow->SetHovered(false);
		if (g_replayBufferRow) g_replayBufferRow->SetHovered(false);
	});

	// lands right after "hide", before obss first separator, so it reads as part of the window-visibility group instead of mixed into streaming/recording actions
	QList<QAction *> actions = trayMenu->actions();
	QAction *before = actions.size() > 1 ? actions.at(1) : nullptr;
	trayMenu->insertAction(before, clipsRowAction);
	trayMenu->insertAction(before, sharePreview);
	trayMenu->insertAction(before, customSettings);

	// obs localizes every one of these labels, so matching them by text (the original approach) silently found none of the three on non-english obs -- exit stays last regardless of language since qt tr() only changes the rendered string, not the action-insertion order SystemTrayInit runs in, and the projector entries are the only submenu-bearing actions in this menu, always preview before program for the same reason. setVisible(false) not removeAction(): a first attempt used removeAction() and shipped a suspected obs freeze on the first right-click; setVisible(false) on menuAction() is the qt-documented-correct way to hide a submenu entry without detaching it from trayMenu or disturbing obss own updateSysTrayProjectorMenu() rebuilds.
	QList<QAction *> trayActions = trayMenu->actions();
	QAction *exitAction = nullptr;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (!(*it)->isSeparator()) {
			exitAction = *it;
			break;
		}
	}
	QList<QAction *> projectorSubmenuActions;
	for (QAction *action : trayActions) {
		if (action != exitAction && action->menu())
			projectorSubmenuActions << action;
	}
	if (projectorSubmenuActions.size() >= 1) {
		g_previewProjectorAction = projectorSubmenuActions.at(0);
		g_previewProjectorAction->setVisible(false);
	}
	if (projectorSubmenuActions.size() >= 2) {
		g_programProjectorAction = projectorSubmenuActions.at(1);
		g_programProjectorAction->setVisible(false);
	}

	// obs always builds {Stream, Record, ReplayBuffer, VirtualCam} as one separator-delimited group immediately before Exit (OBSBasic::SystemTrayInit adds them in this fixed order every time), so walking backward from Exit to the next separator finds Record/ReplayBuffer by position -- locale-independent, unlike the text-matching that silently found nothing on non-english obs (see the projector-matching note above). size()==4 is a sanity check, not a guess: if some future obs version changes this layout, skip the customization entirely rather than misattribute the wrong action to record/clipping.
	QList<QAction *> streamGroup;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (*it == exitAction)
			continue;
		if ((*it)->isSeparator()) {
			if (!streamGroup.isEmpty())
				break;
			continue;
		}
		streamGroup.prepend(*it);
	}
	blog(LOG_INFO, "[replaykit] stream group structural lookup found %d actions (expected 4: Stream, Record, ReplayBuffer, VirtualCam)",
	     (int)streamGroup.size());
	if (streamGroup.size() == 4) {
		g_nativeRecordAction = streamGroup.at(1);
		g_nativeReplayBufferAction = streamGroup.at(2);
		blog(LOG_INFO, "[replaykit] record action: text=\"%s\" enabled=%d", g_nativeRecordAction->text().toUtf8().constData(),
		     g_nativeRecordAction->isEnabled());
		blog(LOG_INFO, "[replaykit] replay-buffer action: text=\"%s\" enabled=%d",
		     g_nativeReplayBufferAction->text().toUtf8().constData(), g_nativeReplayBufferAction->isEnabled());

		// hidden, not removed (matches g_previewProjectorAction/g_programProjectorAction above) -- TrayActionRow proxies clicks straight to ToggleRecording/ToggleReplayBuffer (the direct obs_frontend_..._start/stop calls), so the native actions own isEnabled()/triggered() (confirmed untrustworthy earlier) never come into play at all.
		auto *recordRow = new TrayActionRow(TrayRowKind::Recording, trayMenu);
		auto *recordRowAction = new QWidgetAction(trayMenu);
		recordRowAction->setDefaultWidget(recordRow);
		recordRow->SetAction(recordRowAction);
		QObject::connect(recordRowAction, &QAction::triggered, trayMenu, [recordRow]() {
			if (recordRow)
				recordRow->ProxyTrigger();
		});
		trayMenu->insertAction(g_nativeRecordAction, recordRowAction);
		g_nativeRecordAction->setVisible(false);
		g_recordRow = recordRow;

		auto *replayBufferRow = new TrayActionRow(TrayRowKind::ReplayBuffer, trayMenu);
		auto *replayBufferRowAction = new QWidgetAction(trayMenu);
		replayBufferRowAction->setDefaultWidget(replayBufferRow);
		replayBufferRow->SetAction(replayBufferRowAction);
		QObject::connect(replayBufferRowAction, &QAction::triggered, trayMenu, [replayBufferRow]() {
			if (replayBufferRow)
				replayBufferRow->ProxyTrigger();
		});
		trayMenu->insertAction(g_nativeReplayBufferAction, replayBufferRowAction);
		g_nativeReplayBufferAction->setVisible(false);
		g_replayBufferRow = replayBufferRow;

		RefreshActionRowText();
	}

	// obs wires exit straight to close() with no confirmation, an easy accidental target at the cursors landing spot -- disconnect(receiver=nullptr) drops obss own listener (qts documented way to do so) so we can reconnect our confirm-first version instead of leaving two competing handlers
	if (exitAction) {
		QObject::disconnect(exitAction, &QAction::triggered, nullptr, nullptr);
		QObject::connect(exitAction, &QAction::triggered, trayMenu, []() { ConfirmedExit(); });
	}
	QAction *restartObs = new QAction(QObject::tr("Restart OBS"), trayMenu);
	QObject::connect(restartObs, &QAction::triggered, trayMenu, []() { RestartObs(); });
	QAction *viewCrashLogs = new QAction(QObject::tr("View Crash Logs"), trayMenu);
	QObject::connect(viewCrashLogs, &QAction::triggered, trayMenu, []() { OpenCrashLogsFolder(); });
	trayMenu->insertSeparator(exitAction);
	trayMenu->insertAction(exitAction, restartObs);
	trayMenu->insertAction(exitAction, viewCrashLogs);
}

QMenu *TrayMenuForPicture(int hover, QJsonArray *rows)
{
	QMenu *menu = g_trayMenu;
	if (!menu)
		return nullptr;
	// the colours come from the helper when the menu opens, which a picture never does, so they are asked for here and a picture taken a moment later has them
	RefreshDynamicMenuState();
	menu->ensurePolished();
	menu->adjustSize();
	// a custom row hovers by itself and is set by hand, a plain item is hovered by making it the active action of the menu
	QPointer<TrayActionRow> customRows[] = {g_clipsRow, g_replayBufferRow, g_recordRow};
	for (const QPointer<TrayActionRow> &row : customRows) {
		if (row)
			row->SetHovered(false);
	}
	QAction *picked = nullptr;
	int index = 0;
	for (QAction *action : menu->actions()) {
		if (!action->isVisible() || action->isSeparator())
			continue;
		QString text = action->text();
		for (const QPointer<TrayActionRow> &row : customRows) {
			if (row && row->action() == action)
				text = row->nameLabel->text();
		}
		if (index == hover)
			picked = action;
		if (rows) {
			const QRect place = menu->actionGeometry(action);
			rows->append(QJsonObject{{"index", index}, {"text", text}, {"x", place.x()}, {"y", place.y()}, {"width", place.width()}, {"height", place.height()}});
		}
		++index;
	}
	menu->setActiveAction(picked);
	if (picked) {
		for (const QPointer<TrayActionRow> &row : customRows) {
			if (row && row->action() == picked)
				row->SetHovered(true);
		}
	}
	return menu;
}

} // namespace rk
