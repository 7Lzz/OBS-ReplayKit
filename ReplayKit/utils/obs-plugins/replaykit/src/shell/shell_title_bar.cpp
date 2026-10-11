// title bar of the clean shell
#include "prelude.hpp"
#include "shell/shell_title_bar.hpp"

#include "app/obs_actions.hpp"
#include "core/app_icon.hpp"
#include "core/theme.hpp"
#include "notify/notify_button.hpp"
#include "shell/shell_badge.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_history.hpp"
#include "shell/shell_hotkey_menu.hpp"
#include "shell/shell_nav_button.hpp"
#include "shell/shell_projector.hpp"
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

ShellTitleBar::ShellTitleBar(QMainWindow *mw)
	: QWidget(mw), m_mw(mw)
{
	// the arrows of a browser, after the logo: the shell keeps the history and they walk it
	m_back = new ShellNavButton(ShellNavButton::Kind::Back, this);
	m_back->onPress = []() { ShellHistoryGo(-1); };
	m_forward = new ShellNavButton(ShellNavButton::Kind::Forward, this);
	m_forward->onPress = []() { ShellHistoryGo(1); };
	m_hotkeys = new ShellHotkeyMenu(mw);
	m_clip = new ShellBadge(this);
	m_clip->SetLabel(QStringLiteral("Clip"));
	m_clip->onClick = [this]() {
		m_hotkeys->Hide();
		if (obs_frontend_replay_buffer_active())
			obs_frontend_replay_buffer_save();
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
	const bool clipping = obs_frontend_replay_buffer_active();
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
	AnchoredPanel *panels[] = {m_bell->EnsurePanel(), m_hotkeys->Panel(), m_account->EnsurePanel()};
	for (AnchoredPanel *panel : panels) {
		if (!panel || panel->Warmed())
			continue;
		if (!panel->Warming())
			panel->Warm();
		return true;
	}
	return false;
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
	right -= 10;

	// the arrows right after the logo cell, then the badges; a badge that would run into the right group is hidden rather than overlapped
	int x = kShellRailW + 2;
	m_back->move(x, (h - m_back->height()) / 2);
	x += m_back->width() + 2;
	m_forward->move(x, (h - m_forward->height()) / 2);
	x += m_forward->width() + 14;
	for (ShellBadge *badge : {m_clip, m_rec}) {
		badge->move(x, (h - badge->height()) / 2);
		badge->setVisible(x + badge->width() <= right);
		x += badge->width() + 8;
	}
	// a badge that changed width or moved takes its menu along
	if (m_hotkeys)
		m_hotkeys->Follow();
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

	// logo in the rail column: the real obs logo, or the icon the appearance tab picked, the same one the taskbar shows
	const bool active = m_mw && m_mw->isActiveWindow();
	const qreal dpr = devicePixelRatioF();
	const QPixmap logo = ShellLogoPixmap(26, dpr);
	if (!logo.isNull()) {
		p.setOpacity(active ? 1.0 : 0.6);
		p.drawPixmap(QPointF((kShellRailW - 26) / 2.0, (height() - 26) / 2.0), logo);
	}
}

} // namespace rk
