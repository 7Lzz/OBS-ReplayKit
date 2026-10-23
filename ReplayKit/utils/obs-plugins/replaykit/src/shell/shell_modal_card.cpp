// a card drawn over a dimmed obs window, the window the storage card and the notification reader live in
#include "prelude.hpp"
#include "shell/shell_modal_card.hpp"

#include <QScreen>

#include "core/cef_host.hpp"
#include "core/dev_log.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_views.hpp"
#include "ui/anchored_panel.hpp"
#include "ui/popup_host.hpp"

namespace rk {

namespace {

// the card keeps this much of the obs window showing around it
constexpr int kCardMargin = 40;
// dwm attributes from the windows 11 sdk, spelled out so an older sdk still builds; a windows that does not know them ignores the call
constexpr DWORD kCornerPreference = 33;
constexpr DWORD kBorderColor = 34;
constexpr DWORD kCornerRound = 2;
// the dim fades in under the card as it opens, and out once the card is gone when it closes
constexpr int kFadeInMs = 220;
constexpr int kFadeOutMs = 180;

QWidget *ObsHost()
{
	return g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
}

// the dim over the whole obs window. it has to be a window of its own: the preview and the cef pages are native child windows, so a plain child widget painted over them just shows black, while dwm composites a translucent top-level over them. the card cannot be a child of it for the same reason, a layered window does not show native children, so the card is a second window owned by this one
class Curtain : public QWidget {
public:
	Curtain(QWidget *host, ModalCard *owner) : QWidget(host), m_owner(owner)
	{
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground, true);
		setAttribute(Qt::WA_ShowWithoutActivating, true);
	}

protected:
	void paintEvent(QPaintEvent *) override { QPainter(this).fillRect(rect(), QColor(0, 0, 0, qRound(150 * m_level))); }
	// a click on the dim is a click outside the card
	void mousePressEvent(QMouseEvent *) override { m_owner->OnPagePressedOutside(); }
	// the title bar under the dim stays a place to drag the window from, and the dim and the card follow it
	bool nativeEvent(const QByteArray &, void *message, qintptr *result) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_NCHITTEST || !ShellPointIsCaption(msg->lParam))
			return false;
		*result = HTTRANSPARENT;
		return true;
	}

public:
	qreal Level() const { return m_level; }
	void SetLevel(qreal level)
	{
		m_level = level;
		update();
	}

private:
	ModalCard *m_owner;
	qreal m_level = 1.0;
};

// keeps the dim and the card on the obs window while it moves or resizes, and takes them away when it hides or minimizes
class HostFilter : public QObject {
public:
	HostFilter(QWidget *host, ModalCard *owner) : m_host(host), m_owner(owner) {}

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_host)
			return false;
		switch (event->type()) {
		case QEvent::Move:
		case QEvent::Resize:
			m_owner->OnHostMoved();
			break;
		case QEvent::Hide:
			m_owner->OnHostGone();
			break;
		case QEvent::WindowStateChange:
			if (m_host && m_host->isMinimized())
				m_owner->OnHostGone();
			break;
		default:
			break;
		}
		return false;
	}

private:
	QPointer<QWidget> m_host;
	ModalCard *m_owner;
};

} // namespace

ModalCard::ModalCard(ModalCardSpec spec) : m_spec(std::move(spec)) {}

void ModalCard::Sync()
{
	QWidget *host = m_curtain ? m_curtain->parentWidget() : nullptr;
	if (!host || !m_card)
		return;
	const QRect stage(host->mapToGlobal(QPoint(0, 0)), host->size());
	m_curtain->setGeometry(stage);
	const int w = qMax(420, qMin(m_spec.width, stage.width() - kCardMargin * 2));
	const int h = qMax(360, qMin(m_spec.height, stage.height() - kCardMargin * 2));
	m_card->setGeometry(stage.center().x() - w / 2, stage.center().y() - h / 2, w, h);
}

// the same placement done natively, in one batch so the dim and the card land together. it runs from the message that moves the window, before qt has taken in the new rectangle, so what qt holds for the window is a step behind and only windows knows where it is now; physical pixels all the way
void ModalCard::FollowWindowRect()
{
	QWidget *host = m_curtain ? m_curtain->parentWidget() : nullptr;
	RECT wr = {};
	if (!host || !m_card || !g_shellHwnd || IsIconic(g_shellHwnd) || !GetWindowRect(g_shellHwnd, &wr))
		return;
	const qreal dpr = host->devicePixelRatioF();
	const int hostW = int(wr.right - wr.left);
	const int hostH = int(wr.bottom - wr.top);
	const int w = qRound(qMax(420, qMin(m_spec.width, qRound(hostW / dpr) - kCardMargin * 2)) * dpr);
	const int h = qRound(qMax(360, qMin(m_spec.height, qRound(hostH / dpr) - kCardMargin * 2)) * dpr);
	const int x = int(wr.left) + (hostW - w) / 2;
	const int y = int(wr.top) + (hostH - h) / 2;
	HWND curtain = (HWND)m_curtain->winId();
	HWND card = (HWND)m_card->winId();
	const UINT flags = SWP_NOZORDER | SWP_NOACTIVATE;
	HDWP batch = BeginDeferWindowPos(2);
	if (batch)
		batch = DeferWindowPos(batch, curtain, nullptr, int(wr.left), int(wr.top), hostW, hostH, flags);
	if (batch)
		batch = DeferWindowPos(batch, card, nullptr, x, y, w, h, flags);
	if (batch && EndDeferWindowPos(batch))
		return;
	SetWindowPos(curtain, nullptr, int(wr.left), int(wr.top), hostW, hostH, flags);
	SetWindowPos(card, nullptr, x, y, w, h, flags);
}

void ModalCard::OnHostMoved()
{
	// only while the card is up; it is also made to wait out of sight for its page, and a placement would bring it onto the window
	if (IsOpen())
		FollowWindowRect();
}

void ModalCard::RunScript(const std::string &script)
{
	PopupHost::Run(m_spec.url, QString::fromStdString(script));
}

// the card is a plain top-level window, so windows 11 rounds its corners and draws its border itself; a page inside it could not have round corners of its own, cef windows are opaque
void ModalCard::StyleCard()
{
	HWND hwnd = (HWND)m_card->winId();
	const DWORD round = kCornerRound;
	DwmSetWindowAttribute(hwnd, kCornerPreference, &round, sizeof(round));
	const COLORREF border = RGB(g_notifyBorder.red(), g_notifyBorder.green(), g_notifyBorder.blue());
	DwmSetWindowAttribute(hwnd, kBorderColor, &border, sizeof(border));
}

void ModalCard::EndCardWarm()
{
	m_warming = false;
	if (PopupHost::Owns(this) && m_card) {
		m_card->setAttribute(Qt::WA_ShowWithoutActivating, false);
	}
	PopupHost::Release(this);
	if (ShellSetupLocked())
		ShellReclaimForeground();
	// the page starts its polling when it loads, the way it does when the card opens, and a card that is not up has no use for it
	RunScript(m_spec.hideScript);
	if (!m_openWaits)
		return;
	m_openWaits = false;
	const std::string script = m_waitingScript;
	m_waitingScript.clear();
	Open(script);
}

void ModalCard::OnCardPageReady()
{
	m_ready = true;
	if (!m_warming)
		return;
	m_warmMs = m_clock.elapsed();
	blog(LOG_INFO, "[replaykit] %s page is up after %lld ms", m_spec.name, m_warmMs);
	// left up a moment longer, so the page has been drawn to the window once before it is hidden
	const int run = m_run;
	QTimer::singleShot(kCefWarmSettleMs, m_curtain.data(), [this, run]() {
		if (m_warming && run == m_run)
			EndCardWarm();
	});
}

bool ModalCard::EnsureBuilt()
{
	m_ready = PopupHost::Ready(m_spec.url);
	if (m_card)
		return true;
	QWidget *host = ObsHost();
	if (!CefOpen() || !g_cef->initialized() || !host)
		return false;

	m_curtain = new Curtain(host, this);
	m_card = PopupHost::Prepare(host);
	m_browser = PopupHost::Browser();
	if (!m_card) {
		delete m_curtain.data();
		return false;
	}

	m_hostFilter = new HostFilter(host, this);
	host->installEventFilter(m_hostFilter);
	return true;
}

void ModalCard::StartCardWarm()
{
	m_ready = PopupHost::Ready(m_spec.url);
	if (m_ready || m_gaveUp || m_warming)
		return;
	if (PopupHost::Occupied() && !PopupHost::Owns(this) && !m_openWaits)
		return;
	QWidget *host = ObsHost();
	if (!host || !host->isVisible() || host->isMinimized() || !EnsureBuilt())
		return;
	if (!PopupHost::Acquire(this, m_curtain, m_spec.url, QSize(680, 640), [this]() {
		m_warming = false;
		++m_run;
		m_openWaits = false;
		m_waitingScript.clear();
		Close(false);
		PopupHost::Release(this);
	}))
		return;
	m_warming = true;
	m_warmMs = -1;
	m_clock.start();
	const int run = ++m_run;
	m_card->setAttribute(Qt::WA_ShowWithoutActivating, true);
	m_card->move(CefOutOfSight(host));
	m_card->show();
	PopupHost::SetOwner(this, m_curtain);
	WaitForPage(run);
	blog(LOG_INFO, "[replaykit] %s warm-up started%s", m_spec.name, m_blind ? " (its page cannot report, waiting a fixed time)" : "");
	// the wait is bounded, so a page that never says it is up cannot hold the card back for long
	QTimer::singleShot(m_blind ? kCefWarmBlindMs : kCefWarmCapMs, m_curtain.data(), [this, run]() {
		if (!m_warming || run != m_run)
			return;
		if (m_blind) {
			m_ready = true;
		} else {
			m_gaveUp = true;
			blog(LOG_WARNING, "[replaykit] the page of the %s did not say it was up in time, so it opens as it is", m_spec.name);
		}
		EndCardWarm();
	});
}

void ModalCard::WaitForPage(int run)
{
	if (!m_warming || run != m_run || !PopupHost::Owns(this))
		return;
	if (PopupHost::Ready(m_spec.url)) {
		OnCardPageReady();
		return;
	}
	QTimer::singleShot(25, m_curtain.data(), [this, run]() { WaitForPage(run); });
}

// looks at the screen over the card and says whether the page painted it: the page fills the card with its colour out to the edges, so a card with none of that colour at the points just inside its edge has a browser that put nothing on the window and the dim shows through. points that another window covers do not count, and a grab that is all black says nothing
bool ModalCard::CardLooksEmpty()
{
	if (!m_card || !m_card->isVisible() || !m_card->screen())
		return false;
	QScreen *screen = m_card->screen();
	const QRect area = m_card->geometry().translated(-screen->geometry().topLeft());
	const QImage shot = screen->grabWindow(0, area.x(), area.y(), area.width(), area.height()).toImage();
	if (shot.isNull())
		return false;
	const double scale = double(shot.width()) / qMax(1, area.width());
	const int w = area.width();
	const int h = area.height();
	const QPoint spots[] = {{w / 2, 6}, {w / 4, 6}, {3 * w / 4, 6}, {w / 2, h - 6}, {w / 4, h - 6}, {3 * w / 4, h - 6}, {6, h / 2}, {w - 6, h / 2}};
	HWND card = (HWND)m_card->winId();
	int counted = 0;
	bool allBlack = true;
	for (const QPoint &spot : spots) {
		const QPoint global = m_card->geometry().topLeft() + spot;
		const qreal dpr = m_card->devicePixelRatioF();
		const POINT pt = {LONG(global.x() * dpr), LONG(global.y() * dpr)};
		HWND at = WindowFromPoint(pt);
		if (!at || GetAncestor(at, GA_ROOT) != card)
			continue;
		const QColor c = shot.pixelColor(qBound(0, int(spot.x() * scale), shot.width() - 1), qBound(0, int(spot.y() * scale), shot.height() - 1));
		counted++;
		if (c.red() || c.green() || c.blue())
			allBlack = false;
		if (qAbs(c.red() - g_menuPanel.red()) < 12 && qAbs(c.green() - g_menuPanel.green()) < 12 && qAbs(c.blue() - g_menuPanel.blue()) < 12)
			return false;
	}
	return counted >= 4 && !allBlack;
}

// a browser that was hidden before its first frame reached the window can stay empty for good, so a card that shows nothing is built again and opened onto a page that has drawn; twice at most, then it is left as it is
void ModalCard::CheckCardPainted(int run)
{
	if (run != m_run || !IsOpen())
		return;
	if (!CardLooksEmpty()) {
		m_heals = 0;
		return;
	}
	if (m_heals >= 2) {
		blog(LOG_WARNING, "[replaykit] the %s is still empty after its page was loaded again, leaving it", m_spec.name);
		return;
	}
	m_heals++;
	blog(LOG_WARNING, "[replaykit] the %s opened empty, loading its page again (%d)", m_spec.name, m_heals);
	// the page is loaded again where it is, in the window that stays up: the window and its browser are shared with the menus of the title bar, and hiding it to build it again could leave all of them empty. the open replays once the page is back
	PopupHost::Reload(m_spec.url);
	RunScript(m_lastScript);
	QTimer::singleShot(1500, m_curtain.data(), [this, run]() { CheckCardPainted(run); });
}

void ModalCard::Prepare()
{
	EnsureBuilt();
}

bool ModalCard::WarmNext()
{
	m_ready = PopupHost::Ready(m_spec.url);
	if (m_ready || m_gaveUp)
		return false;
	if (!m_warming)
		StartCardWarm();
	return true;
}

QJsonObject ModalCard::WarmReport() const
{
	return QJsonObject{{"built", !m_card.isNull()}, {"ready", m_ready}, {"warming", m_warming}, {"gaveUp", m_gaveUp}, {"blind", m_blind}, {"warmMs", double(m_warmMs)}};
}

bool ModalCard::IsOpen() const
{
	return PopupHost::Owns(this) && m_curtain && m_curtain->isVisible() && !m_closing;
}

void ModalCard::Follow()
{
	if (IsOpen())
		FollowWindowRect();
}

void ModalCard::Open(const std::string &showScript)
{
	const std::string script = showScript.empty() ? m_spec.showScript : showScript;
	if (ShellSetupLocked())
		return;
	// a card that is up stays as it is, unless the open names what it should show now
	if (IsOpen()) {
		RunScript(showScript);
		return;
	}
	QWidget *host = ObsHost();
	if (!host || !host->isVisible() || host->isMinimized())
		return;
	// any panel that hangs off the title bar would sit on top of the dim
	AnchoredPanel::HideAll();
	if (g_notifyTip)
		g_notifyTip->hide();
	if (!EnsureBuilt())
		return;
	// the page has to be up before the card is, or the card opens onto a white window, so the open waits for it
	if (!m_ready && !m_gaveUp) {
		m_openWaits = true;
		m_waitingScript = script;
		StartCardWarm();
		if (m_warming)
			return;
		// the window of obs is not on screen, so there is nothing to wait for
		m_openWaits = false;
		m_waitingScript.clear();
	}
	if (!PopupHost::Acquire(this, m_curtain, m_spec.url, QSize(m_spec.width, m_spec.height), [this]() {
		m_warming = false;
		++m_run;
		m_openWaits = false;
		m_waitingScript.clear();
		Close(false);
		PopupHost::Release(this);
	}))
		return;
	m_card->setWindowTitle(m_spec.windowTitle);
	QPalette ground = m_card->palette();
	ground.setColor(QPalette::Window, g_menuPanel);
	m_card->setPalette(ground);
	DevLog("%s opened", m_spec.name);
	m_lastScript = script;
	const int run = m_run;
	StyleCard();
	Sync();
	// a dim still fading out from the last close fades back in from where it is
	m_closing = false;
	if (!m_curtain->isVisible())
		static_cast<Curtain *>(m_curtain.data())->SetLevel(0.0);
	m_curtain->show();
	m_card->show();
	FadeCurtain(1.0, kFadeInMs);
	PopupHost::SetOwner(this, m_curtain);
	m_card->raise();
	m_card->activateWindow();
	if (m_browser)
		m_browser->setFocus();
	RunScript(script);
	QTimer::singleShot(500, m_curtain.data(), [this, run]() { CheckCardPainted(run); });
}

void ModalCard::FadeCurtain(qreal level, int ms, std::function<void()> done)
{
	if (m_fade)
		m_fade->stop();
	auto *curtain = static_cast<Curtain *>(m_curtain.data());
	if (!curtain) {
		if (done)
			done();
		return;
	}
	auto *fade = new QVariantAnimation(curtain);
	fade->setStartValue(curtain->Level());
	fade->setEndValue(level);
	fade->setDuration(ms);
	fade->setEasingCurve(level > 0.5 ? QEasingCurve::OutCubic : QEasingCurve::InOutQuad);
	QObject::connect(fade, &QVariantAnimation::valueChanged, curtain, [curtain](const QVariant &value) { curtain->SetLevel(value.toReal()); });
	if (done)
		QObject::connect(fade, &QVariantAnimation::finished, curtain, std::move(done));
	m_fade = fade;
	fade->start(QAbstractAnimation::DeleteWhenStopped);
}

// the card goes at once, so it never shows half faded or empty, and is closed from here on; only the dim fades out after it
void ModalCard::Close(bool animate)
{
	m_openWaits = false;
	m_waitingScript.clear();
	if (!IsOpen())
		return;
	RunScript(m_spec.hideScript);
	DevLog("%s closed", m_spec.name);
	PopupHost::Release(this);
	if (QWidget *host = m_curtain->parentWidget())
		host->activateWindow();
	if (m_spec.onClosed)
		m_spec.onClosed();
	if (!animate) {
		HideCurtain();
		return;
	}
	m_closing = true;
	FadeCurtain(0.0, kFadeOutMs, [this]() {
		if (m_closing)
			HideCurtain();
	});
}

void ModalCard::HideCurtain()
{
	// stopped rather than deleted, since this can run from the finished signal of that same fade, and a stopped fade deletes itself later
	if (m_fade)
		m_fade->stop();
	m_closing = false;
	if (m_curtain)
		m_curtain->hide();
}

// reads the screen where the card is: the page fills the card with its colour right out to the edges, so when none of the points just inside the edge has it the browser is putting nothing on the window and the dim shows through
QJsonObject ModalCard::Probe()
{
	return QJsonObject{{"open", IsOpen()}, {"looksEmpty", CardLooksEmpty()}, {"heals", m_heals}};
}

void ModalCard::Destroy()
{
	Close(false);
	HideCurtain();
	PopupHost::Release(this);
	++m_run;
	m_warming = false;
	if (m_hostFilter) {
		delete m_hostFilter;
		m_hostFilter = nullptr;
	}
	if (m_curtain)
		delete m_curtain.data();
	m_card.clear();
	m_browser.clear();
}

} // namespace rk
