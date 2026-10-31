// title bar of the clean shell
#include "prelude.hpp"
#include "shell/shell_title_bar.hpp"

#include <QPainterPath>
#include <QRegularExpression>

#include "app/clip_buffer.hpp"
#include "app/obs_actions.hpp"
#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "notify/notify_button.hpp"
#include "notify/notify_panel.hpp"
#include "shell/shell_badge.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_history.hpp"
#include "shell/shell_hotkey_menu.hpp"
#include "shell/shell_nav_button.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_quick_menu.hpp"
#include "shell/shell_state.hpp"
#include "ui/anchored_panel.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "shell/shell_win_button.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

// keeps a paused recording out of the timer: the clock only runs while obs reports it as running
bool g_recWasActive = false;
qint64 g_recMs = 0;
quint64 g_recLastTickMs = 0;

// the replay length the way the length field of the settings words it: 45s, 1m, 1m 30s
QString ShellClipLengthText(int seconds)
{
	if (seconds < 60)
		return QString("%1s").arg(seconds);
	const int rest = seconds % 60;
	return rest == 0 ? QString("%1m").arg(seconds / 60) : QString("%1m %2s").arg(seconds / 60).arg(rest);
}

} // namespace

namespace {

constexpr qreal kUpdatePadLeft = 9.0;
constexpr qreal kUpdateIcon = 14.0;
constexpr qreal kUpdateGap = 7.0;
constexpr qreal kUpdatePadRight = 11.0;

} // namespace

ShellLogoButton::ShellLogoButton(QWidget *parent) : ShellTitleButton(kWidth, parent)
{
	SetTip(QStringLiteral("Quick actions"));
}

void ShellLogoButton::PaintGlyph(QPainter &p, const QRectF &pill, qreal hover)
{
	// the real obs logo, or the icon the appearance tab picked, the same one the taskbar shows; it keeps its look when the window is not in front, the way the rest of the bar does
	const qreal dpr = devicePixelRatioF();
	constexpr int kLogo = 22;
	const QPixmap logo = ShellLogoPixmap(kLogo, dpr);
	const QPointF at(pill.left() + 6.0, pill.center().y() - kLogo / 2.0);
	if (!logo.isNull())
		p.drawPixmap(at, logo);
	// the arrow points down and turns to point up while the menu hangs open under the button
	const QPointF c(pill.right() - 10.0, pill.center().y());
	QPen pen(ChromeInk(0.7 + 0.3 * hover), 1.5, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin);
	p.save();
	p.setPen(pen);
	p.setBrush(Qt::NoBrush);
	p.translate(c);
	p.rotate(180.0 * OpenTurn());
	QPainterPath arrow;
	arrow.moveTo(-3.5, -1.75);
	arrow.lineTo(0.0, 1.75);
	arrow.lineTo(3.5, -1.75);
	p.drawPath(arrow);
	p.restore();
}

ShellUpdateButton::ShellUpdateButton(Kind kind, QWidget *parent) : ShellTitleButton(kHeight, parent), m_kind(kind)
{
	hide();
	if (kind == Kind::Obs) {
		SetTip(QStringLiteral("Click to see what changed"));
		onPress = [this]() {
			// the version goes into the address as it is, so only a plain release number is let through
			if (!QRegularExpression(QStringLiteral("^\\d{1,4}\\.\\d{1,4}\\.\\d{1,4}$")).match(m_version).hasMatch())
				return;
			QDesktopServices::openUrl(QUrl(QStringLiteral("https://github.com/obsproject/obs-studio/releases/tag/") + m_version));
		};
		return;
	}
	SetTip(QStringLiteral("Click to install"));
	onPress = [this]() {
		const QString version = m_version;
		// the version goes into the address as it is, so only the characters a release tag uses are let through
		if (version.isEmpty() || version.contains(QRegularExpression(QStringLiteral("[^0-9A-Za-z.-]"))))
			return;
		const std::string path = "/update/open-prompt?version=" + version.toStdString();
		RunAsync([path]() { HttpRequest("POST", path.c_str(), 8767, "", 5000); });
	};
}

void ShellUpdateButton::SetVersion(const QString &version)
{
	if (version == m_version)
		return;
	m_version = version;
	if (!version.isEmpty())
		setFixedWidth(qCeil(kUpdatePadLeft + kUpdateIcon + kUpdateGap + QFontMetricsF(LabelFont()).horizontalAdvance(Label()) + kUpdatePadRight));
	if (version.isEmpty())
		ClearHover();
	setVisible(!version.isEmpty());
	update();
}

void ShellUpdateButton::PaintGlyph(QPainter &p, const QRectF &pill, qreal hover)
{
	// a wash under the glyph at all times, a little stronger under the pointer: the warning colour for replaykit, the grey of the theme ink for obs
	const bool obs = m_kind == Kind::Obs;
	QColor wash = obs ? ChromeInk(0.08 + 0.07 * hover) : g_chromeWarning;
	if (!obs)
		wash.setAlphaF(0.16 + 0.12 * hover);
	p.setPen(Qt::NoPen);
	p.setBrush(wash);
	p.drawRoundedRect(pill, kRadius, kRadius);
	// an arrow down into a tray, then the words
	const QPointF c(pill.left() + kUpdatePadLeft + kUpdateIcon / 2.0, pill.center().y());
	QPen pen(obs ? ChromeInk(0.62 + 0.18 * hover) : g_chromeWarning, 1.8, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin);
	p.setPen(pen);
	p.setBrush(Qt::NoBrush);
	p.drawLine(QPointF(c.x(), c.y() - 6.0), QPointF(c.x(), c.y() + 2.0));
	QPainterPath head;
	head.moveTo(c.x() - 4.0, c.y() - 2.0);
	head.lineTo(c.x(), c.y() + 2.0);
	head.lineTo(c.x() + 4.0, c.y() - 2.0);
	p.drawPath(head);
	QPainterPath tray;
	tray.moveTo(c.x() - 6.0, c.y() + 2.5);
	tray.lineTo(c.x() - 6.0, c.y() + 6.0);
	tray.lineTo(c.x() + 6.0, c.y() + 6.0);
	tray.lineTo(c.x() + 6.0, c.y() + 2.5);
	p.drawPath(tray);
	p.setFont(LabelFont());
	const qreal textX = pill.left() + kUpdatePadLeft + kUpdateIcon + kUpdateGap;
	p.drawText(QRectF(textX, pill.top(), pill.right() - textX, pill.height()), Qt::AlignVCenter | Qt::AlignLeft, Label());
}

QFont ShellUpdateButton::LabelFont()
{
	QFont f(QStringLiteral("Segoe UI"));
	f.setPixelSize(12);
	f.setWeight(QFont::DemiBold);
	return f;
}

ShellTitleBar::ShellTitleBar(QMainWindow *mw)
	: QWidget(mw), m_mw(mw)
{
	QObject::connect(qGuiApp, &QGuiApplication::focusWindowChanged, this, [this](QWindow *) { update(); });
	m_originalTitle = mw->windowTitle();
	QObject::connect(mw, &QWidget::windowTitleChanged, this, [this](const QString &title) {
		if (m_settingTitle)
			return;
		m_originalTitle = title;
		if (g_shellOn && !m_page.isEmpty())
			SetPage(m_page);
	});
	// the arrows of a browser, after the logo: the shell keeps the history and they walk it
	m_back = new ShellNavButton(ShellNavButton::Kind::Back, this);
	m_back->onPress = []() { ShellHistoryGo(-1); };
	m_forward = new ShellNavButton(ShellNavButton::Kind::Forward, this);
	m_forward->onPress = []() { ShellHistoryGo(1); };
	m_hotkeys = new ShellHotkeyMenu(mw);
	m_quick = new ShellQuickMenu(mw);
	m_logo = new ShellLogoButton(this);
	m_logo->onPress = [this]() { m_quick->Toggle(m_logo); };
	m_clip = new ShellBadge(this);
	m_clip->SetLabel(QStringLiteral("Clip"));
	m_clip->onClick = [this]() {
		m_hotkeys->Hide();
		if (ClipBufferActive())
			ClipBufferSave();
		else
			ShellOpenSettingsPage(QStringLiteral("output"));
	};
	m_clip->onContextMenu = [this]() { m_hotkeys->Toggle(m_clip, ShellHotkeyMenu::Kind::Clip); };
	m_clip->onEnter = [this]() { m_hotkeys->Prepare(m_clip, ShellHotkeyMenu::Kind::Clip); };
	m_rec = new ShellBadge(this);
	m_rec->SetLabel(QStringLiteral("Record"));
	m_rec->onClick = [this]() {
		m_hotkeys->Hide();
		ToggleRecording();
	};
	m_rec->onContextMenu = [this]() { m_hotkeys->Toggle(m_rec, ShellHotkeyMenu::Kind::Record); };
	m_rec->onEnter = [this]() { m_hotkeys->Prepare(m_rec, ShellHotkeyMenu::Kind::Record); };

	m_update = new ShellUpdateButton(ShellUpdateButton::Kind::ReplayKit, this);
	m_obsUpdate = new ShellUpdateButton(ShellUpdateButton::Kind::Obs, this);
	m_bell = new NotifyButton(mw, this);
	g_notifyButtons.append(QPointer<NotifyButton>(m_bell));
	m_account = new ShellAccountButton(mw, this);

	m_min = new ShellWinButton(ShellWinButton::Kind::Min, this);
	m_max = new ShellWinButton(ShellWinButton::Kind::Max, this);
	m_close = new ShellWinButton(ShellWinButton::Kind::Close, this);
	RefreshStates();
}

ShellTitleBar::~ShellTitleBar()
{
	delete m_hotkeys;
	delete m_quick;
}

void ShellTitleBar::RestoreTitle()
{
	m_settingTitle = true;
	if (m_mw && !m_originalTitle.isEmpty())
		m_mw->setWindowTitle(m_originalTitle);
}

void ShellTitleBar::SetPage(const QString &page)
{
	if (m_page != page) {
		m_page = page;
		update();
	}
	const QString title = QStringLiteral("OBS ReplayKit \u2014 %1").arg(page);
	if (m_mw && m_mw->windowTitle() != title) {
		m_settingTitle = true;
		m_mw->setWindowTitle(title);
		m_settingTitle = false;
	}
}

void ShellTitleBar::SetUpdateReady(const QString &version)
{
	if (version == m_update->Version())
		return;
	m_update->SetVersion(version);
	LayoutChildren();
}

void ShellTitleBar::SetObsUpdateReady(const QString &version)
{
	if (version == m_obsUpdate->Version())
		return;
	m_obsUpdate->SetVersion(version);
	LayoutChildren();
}

void ShellTitleBar::SetLabels(const QString &recordKeybind, const QString &clipKeybind, int replaySeconds)
{
	m_clip->SetKeys(clipKeybind);
	m_rec->SetKeys(recordKeybind);
	if (replaySeconds > 0)
		m_clip->SetLabel(QStringLiteral("Clip %1").arg(ShellClipLengthText(replaySeconds)));
	LayoutChildren();
}

void ShellTitleBar::RefreshStates()
{
	const bool clipping = ClipBufferActive();
	const bool recording = obs_frontend_recording_active();
	const bool paused = recording && obs_frontend_recording_paused();

	const quint64 now = GetTickCount64();
	if (recording && !g_recWasActive) {
		g_recMs = 0;
		g_recLastTickMs = now;
	}
	if (recording && !paused && g_recLastTickMs)
		g_recMs += qint64(now - g_recLastTickMs);
	g_recLastTickMs = now;
	g_recWasActive = recording;

	m_clip->SetState(false, !clipping, false, QString());
	m_clip->SetArmed(clipping);
	m_clip->SetTip(clipping ? QStringLiteral("Save a clip") : QStringLiteral("Clipping is off. Click to open settings"));
	QString timer;
	if (recording) {
		const qint64 s = g_recMs / 1000;
		timer = s >= 3600 ? QString("%1:%2:%3").arg(s / 3600).arg((s / 60) % 60, 2, 10, QChar('0')).arg(s % 60, 2, 10, QChar('0'))
				  : QString("%1:%2").arg(s / 60, 2, 10, QChar('0')).arg(s % 60, 2, 10, QChar('0'));
	}
	m_rec->SetLabel(paused ? QStringLiteral("Paused") : recording ? QStringLiteral("Recording") : QStringLiteral("Record"));
	m_rec->SetState(recording, false, paused, timer);
	m_rec->SetTip(recording ? QStringLiteral("Stop recording") : QStringLiteral("Start recording"));
	m_rec->Pulse((now / 600) % 2 == 0);
	m_max->SetMaximized(m_mw && (ShellIsMaximized() || m_mw->isMaximized()));
	// views that came and went (the obs settings are built and torn down by obs) change where the arrows can go
	SetNavigation(ShellCanGoBack(), ShellCanGoForward());
	// obs has other ways to turn the preview off (its own menus) that this window never hears about, so the strip and the cover follow the real state. not while this bar is still being built
	if (g_shellTitle.data() == this)
		ShellProjectorRefresh();
	LayoutChildren();
}

void ShellTitleBar::SetNavigation(bool canGoBack, bool canGoForward)
{
	m_back->SetAvailable(canGoBack);
	m_forward->SetAvailable(canGoForward);
}

void ShellTitleBar::SetAccount(const ShellAccountButton::Account &account)
{
	m_account->SetAccount(account);
}

bool ShellTitleBar::AccountPanelOpen() const
{
	return m_account && m_account->Panel() && m_account->Panel()->IsShown();
}

bool ShellTitleBar::WarmNext()
{
	// the two badges share one menu, which is made for the clip badge
	m_hotkeys->Prepare(m_clip, ShellHotkeyMenu::Kind::Clip);
	AnchoredPanel *panels[] = {m_bell->EnsurePanel(), m_hotkeys->Panel(), m_account->EnsurePanel(), m_quick->Prepare()};
	for (AnchoredPanel *panel : panels) {
		if (!panel || panel->Warmed())
			continue;
		if (!panel->Warming())
			panel->Warm();
		return true;
	}
	return false;
}

bool ShellTitleBar::TogglePopup(const QString &name)
{
	if (name == QLatin1String("account")) m_account->TogglePanel();
	else if (name == QLatin1String("notifications")) ShowNotificationPanel(m_bell);
	else if (name == QLatin1String("clip")) m_hotkeys->Toggle(m_clip, ShellHotkeyMenu::Kind::Clip);
	else if (name == QLatin1String("record")) m_hotkeys->Toggle(m_rec, ShellHotkeyMenu::Kind::Record);
	else if (name == QLatin1String("quick")) m_quick->Toggle(m_logo);
	else return false;
	return true;
}

bool ShellTitleBar::IsCaptionAt(const QPoint &inTitle) const
{
	return !childAt(inTitle);
}

void ShellTitleBar::LayoutChildren()
{
	const int h = height();
	int right = width();
	m_close->move(right - kShellCtlW, 0);
	m_max->move(right - kShellCtlW * 2, 0);
	m_min->move(right - kShellCtlW * 3, 0);
	right -= kShellCtlW * 3 + kDividerGap + kBellGap;
	m_account->move(right - m_account->width(), (h - m_account->height()) / 2);
	right -= m_account->width() + 2;
	m_bell->move(right - m_bell->width(), (h - m_bell->height()) / 2);
	right -= m_bell->width() + 2;
	// the update badges that are switched on, whether or not the window is on screen right now, the obs one left of the replaykit one
	for (ShellUpdateButton *badge : {m_update, m_obsUpdate}) {
		if (badge->isHidden())
			continue;
		badge->move(right - badge->width(), (h - badge->height()) / 2);
		right -= badge->width() + 2;
	}
	right -= 10;

	// the logo button stands a little in from the edge of the window, so its menu does not hug the edge, and the arrows follow it
	m_logo->move(kLogoInset, (h - m_logo->height()) / 2);
	// the arrows right after the logo, then the badges; a badge that would run into the right group is hidden rather than overlapped
	int x = qMax(kShellRailW + 2, m_logo->x() + m_logo->width() + 6);
	m_back->move(x, (h - m_back->height()) / 2);
	x += m_back->width() + 2;
	m_forward->move(x, (h - m_forward->height()) / 2);
	x += m_forward->width() + 14;
	for (ShellBadge *badge : {m_clip, m_rec}) {
		badge->move(x, (h - badge->height()) / 2);
		badge->setVisible(x + badge->width() <= right);
		x += badge->width() + 8;
	}
	m_pageRect = QRect(x + 8, 0, qMax(0, right - x - 8), h);
	// a badge that changed width or moved takes its menu along
	if (m_hotkeys)
		m_hotkeys->Follow();
	if (m_quick)
		m_quick->Follow();
	// the bell too: an embedded control has no window of its own to tell its panel that it moved
	AnchoredPanel::FollowAll();
}

void ShellTitleBar::resizeEvent(QResizeEvent *event)
{
	QWidget::resizeEvent(event);
	LayoutChildren();
}

void ShellTitleBar::mousePressEvent(QMouseEvent *event)
{
	if (event->button() == Qt::LeftButton && m_mw && ShellIsMaximized()) {
		m_pressPos = event->globalPosition().toPoint();
		m_pressArmed = true;
		event->accept();
		return;
	}
	// a press on the bare bar is a click away from any menu that hangs off it
	AnchoredPanel::HideAll(true);
	QWidget::mousePressEvent(event);
}

void ShellTitleBar::mouseMoveEvent(QMouseEvent *event)
{
	const QPoint global = event->globalPosition().toPoint();
	if (m_pressArmed && (event->buttons() & Qt::LeftButton) && m_mw && ShellIsMaximized() &&
	    (global - m_pressPos).manhattanLength() >= GetSystemMetrics(SM_CXDRAG)) {
		m_pressArmed = false;
		POINT cursor = {};
		GetCursorPos(&cursor);
		ShellRestoreDrag(m_mw, cursor);
		return;
	}
	QWidget::mouseMoveEvent(event);
}

void ShellTitleBar::mouseReleaseEvent(QMouseEvent *event)
{
	// a press on the maximized bar that never turned into a drag was a click, and closes the menus that hang off the bar; a drag leaves them open, they follow the window
	if (m_pressArmed)
		AnchoredPanel::HideAll(true);
	m_pressArmed = false;
	QWidget::mouseReleaseEvent(event);
}

void ShellTitleBar::mouseDoubleClickEvent(QMouseEvent *event)
{
	if (event->button() == Qt::LeftButton && m_mw && ShellIsMaximized()) {
		m_pressArmed = false;
		ShellToggleMaximize(m_mw);
		event->accept();
		return;
	}
	QWidget::mouseDoubleClickEvent(event);
}

void ShellTitleBar::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.fillRect(rect(), g_chromeBar);
	// a short vertical hairline sets the window controls apart from the bell, the way medal separates its own; centred between the circle of the account and the minimize glyph, and the edge of the wash of the minimize button, see kDividerGap
	p.fillRect(QRect(width() - kShellCtlW * 3 - kDividerGap, (height() - kDividerH) / 2, 1, kDividerH), ChromeInk(0.13));

	// the name of the page keeps its colour when the window is not in front, so nothing in the bar greys out
	p.setPen(ChromeInk(0.8));
	p.drawText(m_pageRect, Qt::AlignLeft | Qt::AlignVCenter, p.fontMetrics().elidedText(m_page, Qt::ElideRight, m_pageRect.width()));
}

} // namespace rk
