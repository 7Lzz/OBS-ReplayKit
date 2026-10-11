// the card that frees up space, drawn over a dimmed obs window
#include "prelude.hpp"
#include "shell/shell_storage_modal.hpp"

#include <QJsonObject>
#include <QScreen>

#include "core/cef_host.hpp"
#include "core/dev_log.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

void StorageModalOpenFresh();

namespace {

constexpr int kCardWidth = 600;
constexpr int kCardHeight = 620;
// the card keeps this much of the obs window showing around it
constexpr int kCardMargin = 40;
// dwm attributes from the windows 11 sdk, spelled out so an older sdk still builds; a windows that does not know them ignores the call
constexpr DWORD kCornerPreference = 33;
constexpr DWORD kBorderColor = 34;
constexpr DWORD kCornerRound = 2;

// the dim over the whole obs window. it has to be a window of its own: the preview and the cef pages are native child windows, so a plain child widget painted over them just shows black, while dwm composites a translucent top-level over them. the card cannot be a child of it for the same reason, a layered window does not show native children, so the card is a second window owned by this one
class Curtain : public QWidget {
public:
	explicit Curtain(QWidget *host) : QWidget(host)
	{
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground, true);
		setAttribute(Qt::WA_ShowWithoutActivating, true);
	}

protected:
	void paintEvent(QPaintEvent *) override { QPainter(this).fillRect(rect(), QColor(0, 0, 0, 150)); }
	// a click on the dim is a click outside the card
	void mousePressEvent(QMouseEvent *) override { StorageModalClose(); }
	// the title bar under the dim stays a place to drag the window from, and the dim and the card follow it
	bool nativeEvent(const QByteArray &, void *message, qintptr *result) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_NCHITTEST || !ShellPointIsCaption(msg->lParam))
			return false;
		*result = HTTRANSPARENT;
		return true;
	}
};

QPointer<Curtain> g_curtain;
QPointer<QWidget> g_card;
QPointer<QCefWidget> g_browser;
QObject *g_hostFilter = nullptr;

void Sync()
{
	QWidget *host = g_curtain ? g_curtain->parentWidget() : nullptr;
	if (!host || !g_card)
		return;
	const QRect stage(host->mapToGlobal(QPoint(0, 0)), host->size());
	g_curtain->setGeometry(stage);
	const int w = qMax(420, qMin(kCardWidth, stage.width() - kCardMargin * 2));
	const int h = qMax(360, qMin(kCardHeight, stage.height() - kCardMargin * 2));
	g_card->setGeometry(stage.center().x() - w / 2, stage.center().y() - h / 2, w, h);
}

// the same placement done natively, in one batch so the dim and the card land together. it runs from the message that moves the window, before qt has taken in the new rectangle, so what qt holds for the window is a step behind and only windows knows where it is now; physical pixels all the way
void FollowWindowRect()
{
	QWidget *host = g_curtain ? g_curtain->parentWidget() : nullptr;
	RECT wr = {};
	if (!host || !g_card || !g_shellHwnd || IsIconic(g_shellHwnd) || !GetWindowRect(g_shellHwnd, &wr))
		return;
	const qreal dpr = host->devicePixelRatioF();
	const int hostW = int(wr.right - wr.left);
	const int hostH = int(wr.bottom - wr.top);
	const int w = qRound(qMax(420, qMin(kCardWidth, qRound(hostW / dpr) - kCardMargin * 2)) * dpr);
	const int h = qRound(qMax(360, qMin(kCardHeight, qRound(hostH / dpr) - kCardMargin * 2)) * dpr);
	const int x = int(wr.left) + (hostW - w) / 2;
	const int y = int(wr.top) + (hostH - h) / 2;
	HWND curtain = (HWND)g_curtain->winId();
	HWND card = (HWND)g_card->winId();
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

// keeps the dim and the card on the obs window while it moves or resizes, and takes them away when it hides or minimizes
class HostFilter : public QObject {
protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (g_curtain && watched == g_curtain->parentWidget()) {
			switch (event->type()) {
			case QEvent::Move:
			case QEvent::Resize:
				// only while the card is up; it is also made to wait out of sight for its page, and a placement would bring it onto the window
				if (StorageModalIsOpen())
					FollowWindowRect();
				break;
			case QEvent::Hide:
				StorageModalClose();
				break;
			case QEvent::WindowStateChange:
				if (g_curtain->parentWidget()->isMinimized())
					StorageModalClose();
				break;
			default:
				break;
			}
		}
		return false;
	}
};

void RunScript(const char *script)
{
	if (g_browser)
		g_browser->executeJavaScript(script);
}

// the card is a plain top-level window, so windows 11 rounds its corners and draws its border itself; a page inside it could not have round corners of its own, cef windows are opaque
void StyleCard()
{
	HWND hwnd = (HWND)g_card->winId();
	const DWORD round = kCornerRound;
	DwmSetWindowAttribute(hwnd, kCornerPreference, &round, sizeof(round));
	const COLORREF border = RGB(g_notifyBorder.red(), g_notifyBorder.green(), g_notifyBorder.blue());
	DwmSetWindowAttribute(hwnd, kBorderColor, &border, sizeof(border));
}

// the page of the card gets the warm-up the menus of the title bar get: the browser inside a cef widget is only made when the widget is first shown, so the card is shown out of sight until its page says it is up. an open that comes before that waits for it
bool g_cardReady = false;
bool g_cardWarming = false;
bool g_cardGaveUp = false;
bool g_cardBlind = false;
bool g_cardOpenWaits = false;
int g_cardRun = 0;
qint64 g_cardWarmMs = -1;
int g_cardHeals = 0;
QElapsedTimer g_cardClock;

void EndCardWarm()
{
	g_cardWarming = false;
	if (g_card) {
		g_card->hide();
		g_card->setAttribute(Qt::WA_ShowWithoutActivating, false);
	}
	// the page starts its polling when it loads, the way it does when the card opens, and a card that is not up has no use for it
	RunScript("window.__replaykitHideStorage && window.__replaykitHideStorage();");
	if (!g_cardOpenWaits)
		return;
	g_cardOpenWaits = false;
	StorageModalOpen();
}

void OnCardPageReady()
{
	g_cardReady = true;
	if (!g_cardWarming)
		return;
	g_cardWarmMs = g_cardClock.elapsed();
	blog(LOG_INFO, "[replaykit] storage card page is up after %lld ms", g_cardWarmMs);
	// left up a moment longer, so the page has been drawn to the window once before it is hidden
	const int run = g_cardRun;
	QTimer::singleShot(kCefWarmSettleMs, g_card.data(), [run]() {
		if (g_cardWarming && run == g_cardRun)
			EndCardWarm();
	});
}

bool EnsureBuilt()
{
	if (g_card)
		return true;
	QWidget *host = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!g_cef || !g_cef->initialized() || !host)
		return false;

	g_curtain = new Curtain(host);
	g_card = new QWidget(g_curtain);
	g_card->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	g_card->setWindowTitle(QStringLiteral("ReplayKit Storage"));
	g_card->resize(kCardWidth, kCardHeight);
	// paints under cef before its first frame, so the gap is the card colour rather than white
	g_card->setAutoFillBackground(true);
	QPalette pal = g_card->palette();
	pal.setColor(QPalette::Window, g_menuPanel);
	g_card->setPalette(pal);
	QVBoxLayout *layout = new QVBoxLayout(g_card);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(g_card, "http://127.0.0.1:8767/storage-view", nullptr);
	layout->addWidget(browser);
	g_browser = browser;
	g_cardBlind = !CefWatchPageReady(browser, g_card, []() { OnCardPageReady(); });
	g_card->createWinId();
	StyleCard();

	g_hostFilter = new HostFilter();
	host->installEventFilter(g_hostFilter);
	return true;
}

void StartCardWarm()
{
	if (g_cardReady || g_cardGaveUp || g_cardWarming)
		return;
	QWidget *host = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!host || !host->isVisible() || host->isMinimized() || !EnsureBuilt())
		return;
	g_cardWarming = true;
	g_cardWarmMs = -1;
	g_cardClock.start();
	const int run = ++g_cardRun;
	g_card->setAttribute(Qt::WA_ShowWithoutActivating, true);
	g_card->move(CefOutOfSight(host));
	g_card->show();
	blog(LOG_INFO, "[replaykit] storage card warm-up started%s", g_cardBlind ? " (its page cannot report, waiting a fixed time)" : "");
	// the wait is bounded, so a page that never says it is up cannot hold the card back for long
	QTimer::singleShot(g_cardBlind ? kCefWarmBlindMs : kCefWarmCapMs, g_card.data(), [run]() {
		if (!g_cardWarming || run != g_cardRun)
			return;
		if (g_cardBlind) {
			g_cardReady = true;
		} else {
			g_cardGaveUp = true;
			blog(LOG_WARNING, "[replaykit] the page of the storage card did not say it was up in time, so it opens as it is");
		}
		EndCardWarm();
	});
}

// looks at the screen over the card and says whether the page painted it: the page fills the card with its colour out to the edges, so a card with none of that colour at the points just inside its edge has a browser that put nothing on the window and the dim shows through. points that another window covers do not count, and a grab that is all black says nothing
bool CardLooksEmpty()
{
	if (!g_card || !g_card->isVisible() || !g_card->screen())
		return false;
	QScreen *screen = g_card->screen();
	const QRect area = g_card->geometry().translated(-screen->geometry().topLeft());
	const QImage shot = screen->grabWindow(0, area.x(), area.y(), area.width(), area.height()).toImage();
	if (shot.isNull())
		return false;
	const double scale = double(shot.width()) / qMax(1, area.width());
	const int w = area.width();
	const int h = area.height();
	const QPoint spots[] = {{w / 2, 6}, {w / 4, 6}, {3 * w / 4, 6}, {w / 2, h - 6}, {w / 4, h - 6}, {3 * w / 4, h - 6}, {6, h / 2}, {w - 6, h / 2}};
	HWND card = (HWND)g_card->winId();
	int counted = 0;
	bool allBlack = true;
	for (const QPoint &spot : spots) {
		const QPoint global = g_card->geometry().topLeft() + spot;
		const qreal dpr = g_card->devicePixelRatioF();
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
void CheckCardPainted(int run)
{
	if (run != g_cardRun || !StorageModalIsOpen())
		return;
	if (!CardLooksEmpty()) {
		g_cardHeals = 0;
		return;
	}
	if (g_cardHeals >= 2) {
		blog(LOG_WARNING, "[replaykit] the storage card is still empty after being built again, leaving it");
		return;
	}
	g_cardHeals++;
	blog(LOG_WARNING, "[replaykit] the storage card opened empty, building it again (%d)", g_cardHeals);
	StorageModalOpenFresh();
}

} // namespace

void StorageModalPrepare()
{
	EnsureBuilt();
}

bool StorageModalWarmNext()
{
	if (g_cardReady || g_cardGaveUp)
		return false;
	if (!g_cardWarming)
		StartCardWarm();
	return true;
}

QJsonObject StorageModalWarmReport()
{
	return QJsonObject{{"built", !g_card.isNull()}, {"ready", g_cardReady}, {"warming", g_cardWarming}, {"gaveUp", g_cardGaveUp}, {"blind", g_cardBlind}, {"warmMs", double(g_cardWarmMs)}};
}

bool StorageModalIsOpen()
{
	return g_curtain && g_curtain->isVisible();
}

void StorageModalFollow()
{
	if (StorageModalIsOpen())
		FollowWindowRect();
}

void StorageModalOpen()
{
	if (StorageModalIsOpen())
		return;
	QWidget *host = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!host || !host->isVisible() || host->isMinimized())
		return;
	// any panel that hangs off the title bar would sit on top of the dim
	AnchoredPanel::HideAll();
	if (g_notifyTip)
		g_notifyTip->hide();
	if (!EnsureBuilt())
		return;
	// the page has to be up before the card is, or the card opens onto a white window, so the open waits for it
	if (!g_cardReady && !g_cardGaveUp) {
		g_cardOpenWaits = true;
		StartCardWarm();
		if (g_cardWarming)
			return;
		// the window of obs is not on screen, so there is nothing to wait for
		g_cardOpenWaits = false;
	}
	DevLog("storage card opened");
	const int run = g_cardRun;
	StyleCard();
	Sync();
	g_curtain->show();
	g_card->show();
	g_card->raise();
	g_card->activateWindow();
	if (g_browser)
		g_browser->setFocus();
	RunScript("window.__replaykitShowStorage && window.__replaykitShowStorage();");
	QTimer::singleShot(500, g_card.data(), [run]() { CheckCardPainted(run); });
}

// the card is thrown away and built again, and the open that comes of it waits for the new page like any first open
void StorageModalOpenFresh()
{
	StorageModalClose();
	StorageModalDestroy();
	g_cardReady = g_cardWarming = g_cardGaveUp = g_cardBlind = false;
	g_cardOpenWaits = true;
	StartCardWarm();
}

void StorageModalClose()
{
	g_cardOpenWaits = false;
	if (!StorageModalIsOpen())
		return;
	RunScript("window.__replaykitHideStorage && window.__replaykitHideStorage();");
	DevLog("storage card closed");
	g_card->hide();
	g_curtain->hide();
	if (QWidget *host = g_curtain->parentWidget())
		host->activateWindow();
	// the figure in the rail follows what was done in the card
	ShellStoragePoll();
}

// reads the screen where the card is: the page fills the card with its colour right out to the edges, so when none of the points just inside the edge has it the browser is putting nothing on the window and the dim shows through
QJsonObject StorageModalProbe()
{
	return QJsonObject{{"open", StorageModalIsOpen()}, {"looksEmpty", CardLooksEmpty()}, {"heals", g_cardHeals}};
}

void StorageModalDestroy()
{
	if (g_hostFilter) {
		delete g_hostFilter;
		g_hostFilter = nullptr;
	}
	if (g_curtain)
		delete g_curtain.data();
}

} // namespace rk
