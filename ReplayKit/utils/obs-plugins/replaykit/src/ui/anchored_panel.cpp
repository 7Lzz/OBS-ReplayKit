// frameless cef window that hangs off a title bar button
#include "prelude.hpp"
#include "ui/anchored_panel.hpp"

#include <QEasingCurve>
#include <QJsonArray>
#include <QJsonObject>
#include <QRegion>

#include "core/cef_host.hpp"
#include "core/dev_log.hpp"
#include "core/main_window.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"

namespace rk {

namespace {

// the window is cut to a height that moves, so the whole panel unrolls under its button and rolls back up, the way the clips sort menu does inside its page. the window is cut rather than the page because the panel-coloured fill the window paints under the page is out of reach of a clip inside the page
constexpr int kUnrollMs = 190;
constexpr int kRollUpMs = 150;
// a step every few milliseconds, each one cheap, so the roll keeps up with a display that refreshes faster than the 60 of a plain animation timer
constexpr int kRollTickMs = 4;

// the unroll uses the curve the dock menus open on (--ease_smooth). the roll up accelerates instead: that same curve run backwards spends its last 100ms or so as a bar a few pixels tall under the button, a tail the clips sort menu hides by fading and this window does not
QEasingCurve RollCurve(bool up)
{
	QEasingCurve curve(QEasingCurve::BezierSpline);
	if (up)
		curve.addCubicBezierSegment(QPointF(0.4, 0.0), QPointF(1.0, 1.0), QPointF(1.0, 1.0));
	else
		curve.addCubicBezierSegment(QPointF(0.16, 1.0), QPointF(0.3, 1.0), QPointF(1.0, 1.0));
	return curve;
}

// dismissal watches for a real click landing outside the panel rather than for activation: cef keeps focus in its own hwnd, so the panel is often never the foreground window and any activation-based test hides it instantly. the same GetAsyncKeyState polling the resize grip uses, edge-triggered so a held button fires once
QTimer *g_dismissTimer = nullptr;
QElapsedTimer g_shownAt;
bool g_mouseWasDown = false;
bool g_ignoreOpeningPress = false;

bool AnyButtonDown()
{
	return ((GetAsyncKeyState(VK_LBUTTON) | GetAsyncKeyState(VK_RBUTTON)) & 0x8000) != 0;
}

} // namespace

QList<AnchoredPanel *> AnchoredPanel::s_panels;
AnchoredPanel *AnchoredPanel::s_active = nullptr;

AnchoredPanel::AnchoredPanel(QWidget *owner, const QString &title, const QString &url, const QSize &size)
	: m_owner(owner), m_title(title), m_url(url), m_size(size)
{
	s_panels.append(this);
}

AnchoredPanel::~AnchoredPanel()
{
	s_panels.removeAll(this);
	if (s_active == this)
		StopClickAway();
	StopRoll();
	delete m_rollTimer.data();
	if (m_window)
		delete m_window.data();
}

bool AnchoredPanel::IsShown() const
{
	// a window that is up out of sight for its page to start is not a panel on screen
	return m_window && m_window->isVisible() && !m_warming;
}

// the window is owned by the window its button sits in: an owned top-level always sits directly above its owner and never over unrelated apps, and Qt does not hide an unrelated parentless tool with it
bool AnchoredPanel::EnsureBuilt()
{
	if (m_window)
		return true;
	// a widget created before cef has finished starting is the one that comes up white the first time
	if (!CefOpen() || !g_cef->initialized() || !m_owner)
		return false;
	QWidget *panel = new QWidget(m_owner);
	panel->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	panel->setWindowTitle(m_title);
	panel->resize(m_size);
	// paints under cef before its first frame, so the gap is the panel colour rather than white
	panel->setAutoFillBackground(true);
	QPalette pal = panel->palette();
	pal.setColor(QPalette::Window, g_notifySurface);
	panel->setPalette(pal);

	QVBoxLayout *layout = new QVBoxLayout(panel);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(panel, m_url.toStdString(), nullptr);
	layout->addWidget(browser);
	m_window = panel;
	m_browser = browser;
	m_blind = !CefWatchPageReady(browser, panel, [this]() { OnPageReady(); });
	return true;
}

void AnchoredPanel::SetUrl(const QString &url)
{
	m_url = url;
	if (m_browser)
		m_browser->setURL(url.toStdString());
}

void AnchoredPanel::RunScript(const QString &script)
{
	if (m_browser)
		m_browser->executeJavaScript(script.toStdString());
}

// the browser inside a cef widget is only made when the widget is first shown, so a panel that was built without being shown still opened onto a white window that its page then had to fill. shown out of sight until the page says it is up, the first open finds what every later one does
void AnchoredPanel::Warm()
{
	if (m_ready || m_gaveUp || m_warming)
		return;
	QWidget *owner = m_owner;
	if (!owner || !owner->isVisible() || owner->isMinimized() || !EnsureBuilt())
		return;
	QWidget *panel = m_window;
	m_warming = true;
	m_warmMs = -1;
	m_warmClock.start();
	const int run = ++m_warmRun;
	panel->setAttribute(Qt::WA_ShowWithoutActivating, true);
	panel->move(CefOutOfSight(owner));
	panel->show();
	blog(LOG_INFO, "[replaykit] panel warm-up started: %s%s", m_title.toUtf8().constData(), m_blind ? " (its page cannot report, waiting a fixed time)" : "");
	// the wait is bounded, so a page that never says it is up cannot hold an open back for long
	QTimer::singleShot(m_blind ? kCefWarmBlindMs : kCefWarmCapMs, panel, [this, run]() {
		if (!m_warming || run != m_warmRun)
			return;
		if (m_blind) {
			m_ready = true;
		} else {
			m_gaveUp = true;
			blog(LOG_WARNING, "[replaykit] the page of a panel did not say it was up in time, so it opens as it is: %s", m_title.toUtf8().constData());
		}
		EndWarm();
	});
}

void AnchoredPanel::OnPageReady()
{
	m_ready = true;
	if (!m_warming)
		return;
	m_warmMs = m_warmClock.elapsed();
	blog(LOG_INFO, "[replaykit] panel page is up after %lld ms: %s", m_warmMs, m_title.toUtf8().constData());
	// left up a moment longer, so the page has been drawn to the window once before it is hidden
	const int run = m_warmRun;
	QTimer::singleShot(kCefWarmSettleMs, m_window.data(), [this, run]() {
		if (m_warming && run == m_warmRun)
			EndWarm();
	});
}

void AnchoredPanel::EndWarm()
{
	m_warming = false;
	if (m_window) {
		m_window->hide();
		m_window->setAttribute(Qt::WA_ShowWithoutActivating, false);
	}
	if (!m_pendingOpen)
		return;
	std::function<void()> open = std::move(m_pendingOpen);
	m_pendingOpen = nullptr;
	open();
}

QJsonArray AnchoredPanel::WarmReport()
{
	QJsonArray out;
	for (const AnchoredPanel *panel : s_panels) {
		QJsonObject one;
		one["panel"] = panel->m_title;
		one["built"] = !panel->m_window.isNull();
		one["ready"] = panel->m_ready;
		one["warming"] = panel->m_warming;
		one["gaveUp"] = panel->m_gaveUp;
		one["blind"] = panel->m_blind;
		one["warmMs"] = double(panel->m_warmMs);
		out.append(one);
	}
	return out;
}

void AnchoredPanel::StopRoll()
{
	if (m_rollTimer)
		m_rollTimer->stop();
	// nothing of a roll that was cut short runs afterwards
	m_rollDone = nullptr;
}

void AnchoredPanel::SetReveal(int height)
{
	QWidget *panel = m_window;
	if (!panel)
		return;
	const qreal dpr = panel->devicePixelRatioF();
	HRGN region = CreateRectRgn(0, 0, qMax(1, qRound(panel->width() * dpr)), qMax(1, qRound(height * dpr)));
	// the system owns the region once it is set; no redraw is asked for, the window keeps what it drew and only shows less of it
	if (region && !SetWindowRgn((HWND)panel->winId(), region, FALSE))
		DeleteObject(region);
}

void AnchoredPanel::ClearReveal()
{
	if (m_window)
		SetWindowRgn((HWND)m_window->winId(), nullptr, TRUE);
}

void AnchoredPanel::Roll(int toHeight, std::function<void()> done)
{
	if (!m_window)
		return;
	StopRoll();
	if (!m_rollTimer) {
		m_rollTimer = new QTimer(g_callbacks);
		// a precise timer is a multimedia timer on windows, so the steps come at the interval asked for and not at the 15 ms of the system tick
		m_rollTimer->setTimerType(Qt::PreciseTimer);
		m_rollTimer->setInterval(kRollTickMs);
		QObject::connect(m_rollTimer, &QTimer::timeout, g_callbacks, [this]() { RollTick(); });
	}
	const bool up = toHeight < m_rollHeight;
	m_rollFrom = m_rollHeight;
	m_rollTo = toHeight;
	m_rollDuration = up ? kRollUpMs : kUnrollMs;
	m_rollCurve = RollCurve(up);
	m_rollDone = std::move(done);
	m_rollStarted = false;
	m_rollFrames = 0;
	m_rollWorstGapMs = 0;
	m_rollTimer->start();
}

void AnchoredPanel::RollTick()
{
	// the clock starts at the first step and not at the call that asked for the roll: opening a window does work after that call (showing it, waking its page), and a clock that had run through it would start the roll with a jump
	if (!m_rollStarted) {
		m_rollStarted = true;
		m_rollClock.start();
		m_rollLastMs = 0;
	}
	const qint64 now = m_rollClock.elapsed();
	m_rollWorstGapMs = qMax(m_rollWorstGapMs, now - m_rollLastMs);
	m_rollLastMs = now;
	++m_rollFrames;
	const qreal t = qMin<qreal>(1.0, qreal(now) / qreal(m_rollDuration));
	const int height = qMax(1, qRound(m_rollFrom + (m_rollTo - m_rollFrom) * m_rollCurve.valueForProgress(t)));
	if (height != m_rollHeight) {
		m_rollHeight = height;
		SetReveal(height);
	}
	if (t < 1.0)
		return;
	// the callback is taken before it runs, since it can start the next roll
	m_rollTimer->stop();
	std::function<void()> done = std::move(m_rollDone);
	m_rollDone = nullptr;
	DevLog("panel roll %s: %d steps in %lld ms, longest gap %lld ms: %s", m_rollTo < m_rollFrom ? "up" : "down", m_rollFrames, now, m_rollWorstGapMs, m_title.toUtf8().constData());
	if (done)
		done();
}

// a panel that hangs off the obs window is placed from the rectangle windows reports for that window. qt learns of a move a message after the native one that drives this, so a panel placed from what qt still holds trails the window by a step all through a drag. the tab is measured against the window origin as qt holds it, where both come from the same state, so the offset is right whatever that state is; from there on it is all physical pixels
bool AnchoredPanel::PlaceFromWindowRect(const QRect &tab)
{
	QMainWindow *mw = ObsMainWindowQt();
	RECT wr = {};
	if (!g_shellOn || !mw || m_owner.data() != mw || !g_shellHwnd || IsIconic(g_shellHwnd) || !GetWindowRect(g_shellHwnd, &wr))
		return false;
	QWidget *panel = m_window;
	const qreal dpr = mw->devicePixelRatioF();
	const qreal panelDpr = panel->devicePixelRatioF();
	const QPoint offset = tab.topLeft() - mw->mapToGlobal(QPoint(0, 0));
	const int tabLeft = int(wr.left) + qRound(offset.x() * dpr);
	const int tabTop = int(wr.top) + qRound(offset.y() * dpr);
	const int tabWidth = qRound(tab.width() * dpr);
	const int tabHeight = qRound(tab.height() * dpr);
	const int panelWidth = qRound(panel->width() * panelDpr);
	const int panelHeight = qRound(panel->height() * panelDpr);
	int x = m_anchor.edge == Edge::Right ? tabLeft + tabWidth - panelWidth : tabLeft;
	int y = tabTop + tabHeight - qRound(m_anchor.seam * dpr);
	MONITORINFO info = {};
	info.cbSize = sizeof(info);
	const POINT middle = {tabLeft + tabWidth / 2, tabTop + tabHeight / 2};
	if (GetMonitorInfoW(MonitorFromPoint(middle, MONITOR_DEFAULTTONEAREST), &info)) {
		const int gap = qRound(4 * dpr);
		x = qMax(int(info.rcWork.left) + gap, qMin(x, int(info.rcWork.right) - panelWidth - gap));
		y = qMax(int(info.rcWork.top) + gap, qMin(y, int(info.rcWork.bottom) - panelHeight - gap));
	}
	SetWindowPos((HWND)panel->winId(), nullptr, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
	return true;
}

// flush under the button with the edge on the side its tab sits on. the button is positioned in screen coordinates already, which mapToGlobal on a caption tool window mixes with client coordinates and left the panel visibly detached on some caption configurations
void AnchoredPanel::Follow()
{
	QWidget *panel = m_window;
	if (!panel || !m_anchor.tabRect)
		return;
	const QRect tab = m_anchor.tabRect();
	if (tab.isNull())
		return;
	if (!PlaceFromWindowRect(tab)) {
		int x = m_anchor.edge == Edge::Right ? tab.left() + tab.width() - panel->width() : tab.left();
		int y = tab.bottom() + 1 - m_anchor.seam;
		QScreen *screen = QGuiApplication::screenAt(tab.center());
		if (!screen)
			screen = QGuiApplication::primaryScreen();
		if (screen) {
			const QRect avail = screen->availableGeometry();
			x = qMax(avail.left() + 4, qMin(x, avail.right() - panel->width() - 4));
			y = qMax(avail.top() + 4, qMin(y, avail.bottom() - panel->height() - 4));
		}
		panel->move(x, y);
	}
	// the page stops its top border where the tab starts, so it has to know how wide the tab is
	if (tab.width() != m_tabWidth) {
		m_tabWidth = tab.width();
		RunScript(QStringLiteral("document.documentElement.style.setProperty('--tab-width','%1px');").arg(m_tabWidth));
	}
}

void AnchoredPanel::Toggle(const Anchor &anchor, const QString &script)
{
	if (IsOpen() && m_anchor.key == anchor.key) {
		// a second press on the button closes it again, same as any menu
		Hide(true);
		return;
	}
	// a press on the control whose open is still waiting for the page takes that open back
	if (m_pendingOpen && m_pendingKey == anchor.key) {
		m_pendingOpen = nullptr;
		return;
	}
	if (!EnsureBuilt())
		return;
	// a page that is not up yet would open onto a white window, so the open waits for it; once it is up every open is at once
	if (!Warmed()) {
		m_pendingKey = anchor.key;
		m_pendingOpen = [this, anchor, script]() { Toggle(anchor, script); };
		Warm();
		if (m_warming)
			return;
		// the window it hangs off is not on screen, so there is nothing to wait for
		m_pendingOpen = nullptr;
	}

	// only one at a time, so shut whichever other panel is open. the one that hangs off another control of its own gives way at once too, and then opens fresh for this one
	for (AnchoredPanel *other : s_panels)
		if (other != this && other->IsShown())
			other->Hide(false);
	if (IsShown() && m_anchor.key != anchor.key)
		Hide(false);

	m_anchor = anchor;
	m_tabWidth = -1;
	DevLog("panel opened: %s", m_title.toUtf8().constData());
	const bool wasShowing = m_window->isVisible();
	m_closing = false;
	if (!wasShowing) {
		// created without being shown, so the cut is there before the first frame
		m_window->createWinId();
		m_rollHeight = 1;
		SetReveal(m_rollHeight);
	}
	Follow();
	m_window->show();
	// showing can cause Qt to apply an initial platform placement, so it is anchored once more afterwards to keep the seam exact
	Follow();
	const QPointer<QWidget> rolling = m_window;
	Roll(m_window->height(), [this, rolling]() {
		if (rolling)
			ClearReveal();
	});
	m_window->raise();
	m_window->activateWindow();
	// the trigger must sit above its menu, just like the sort trigger has the higher z-index that hides the shared border
	if (m_anchor.raiseControl)
		m_anchor.raiseControl();
	if (m_anchor.setOpen)
		m_anchor.setOpen(true);
	StartClickAway();
	// the page stays loaded between opens, so it has to re-read its data and replay the open itself
	if (!script.isEmpty())
		RunScript(script);
}

void AnchoredPanel::Finish()
{
	m_closing = false;
	DevLog("panel closed: %s", m_title.toUtf8().constData());
	if (m_window) {
		m_window->hide();
		ClearReveal();
	}
	if (m_anchor.setOpen)
		m_anchor.setOpen(false);
	if (m_anchor.onHidden)
		m_anchor.onHidden();
}

void AnchoredPanel::Hide(bool animate)
{
	if (s_active == this)
		StopClickAway();
	if (animate && IsShown()) {
		if (!m_closing) {
			m_closing = true;
			Roll(1, [this]() { Finish(); });
		}
		return;
	}
	StopRoll();
	Finish();
}

// a press that opened the panel can still be in flight on the first tick, and one held longer than a tick must not count as a click away either, so the watch waits for the button to come up first
void AnchoredPanel::StartClickAway()
{
	if (!g_dismissTimer) {
		g_dismissTimer = new QTimer(g_callbacks);
		QObject::connect(g_dismissTimer, &QTimer::timeout, g_callbacks, []() { DismissTick(); });
	}
	s_active = this;
	g_shownAt.start();
	g_mouseWasDown = AnyButtonDown();
	g_ignoreOpeningPress = true;
	g_dismissTimer->start(60);
}

void AnchoredPanel::StopClickAway()
{
	s_active = nullptr;
	if (g_dismissTimer)
		g_dismissTimer->stop();
}

void AnchoredPanel::DismissTick()
{
	AnchoredPanel *panel = s_active;
	if (!panel || !panel->IsShown())
		return;
	const bool down = AnyButtonDown();
	if (g_ignoreOpeningPress) {
		g_mouseWasDown = down;
		if (!down)
			g_ignoreOpeningPress = false;
		return;
	}
	const bool pressed = down && !g_mouseWasDown;
	g_mouseWasDown = down;
	if (!pressed)
		return;
	if (g_shownAt.isValid() && g_shownAt.elapsed() < 150)
		return;
	POINT pt;
	if (!GetCursorPos(&pt))
		return;
	RECT panelRect;
	if (GetWindowRect((HWND)panel->m_window->winId(), &panelRect) && PtInRect(&panelRect, pt))
		return;
	// a press on the button itself is its own toggle, not a dismiss
	if (panel->m_anchor.owns && panel->m_anchor.owns(pt, QCursor::pos()))
		return;
	// a press on the caption or the sizing frame of the window may be the start of a drag, and the menus go along with the window then; the frame tells a click from a drag when the press is over and dismisses only for a click
	if (ShellPointIsFrame(pt))
		return;
	panel->Hide(true);
}

int AnchoredPanel::ShownCount()
{
	int count = 0;
	for (AnchoredPanel *panel : s_panels)
		if (panel->IsShown())
			++count;
	return count;
}

void AnchoredPanel::FollowAll()
{
	for (AnchoredPanel *panel : s_panels)
		if (panel->IsShown())
			panel->Follow();
}

void AnchoredPanel::DismissForPress(bool animate)
{
	AnchoredPanel *panel = s_active;
	if (!panel || !panel->IsShown())
		return;
	POINT pt;
	if (!GetCursorPos(&pt))
		return;
	RECT panelRect;
	if (GetWindowRect((HWND)panel->m_window->winId(), &panelRect) && PtInRect(&panelRect, pt))
		return;
	if (panel->m_anchor.owns && panel->m_anchor.owns(pt, QCursor::pos()))
		return;
	// a press that starts a native loop gets no roll, it would be left frozen half way
	panel->Hide(animate);
}

void AnchoredPanel::HideOwnedBy(QWidget *owner)
{
	for (AnchoredPanel *panel : s_panels) {
		if (panel->m_owner != owner)
			continue;
		panel->m_pendingOpen = nullptr;
		if (panel->IsShown())
			panel->Hide(false);
	}
}

void AnchoredPanel::HideAll(bool animate)
{
	for (AnchoredPanel *panel : s_panels) {
		panel->m_pendingOpen = nullptr;
		if (panel->IsShown())
			panel->Hide(animate);
	}
}

void AnchoredPanel::DestroyAll()
{
	StopClickAway();
	for (AnchoredPanel *panel : s_panels) {
		panel->StopRoll();
		if (panel->m_window)
			delete panel->m_window.data();
	}
}

void AnchoredPanel::SetGround(const QColor &color)
{
	for (AnchoredPanel *panel : s_panels) {
		if (!panel->m_window)
			continue;
		QPalette pal = panel->m_window->palette();
		pal.setColor(QPalette::Window, color);
		panel->m_window->setPalette(pal);
	}
}

} // namespace rk
