// notification bell and its caption placement
#include "prelude.hpp"
#include "notify/notify_button.hpp"

#include "core/draw_util.hpp"
#include "core/theme.hpp"
#include "notify/notify_panel.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_title_button.hpp"
#include "ui/anchored_panel.hpp"

namespace rk {

namespace {


// chevron beside the bell -- same glyph and flip-on-open behaviour as the clips sort trigger

// keeps one button glued to its window. every one of these events can move the caption, and the safety-net timer alone lagged visibly while dragging
class NotifyAnchorFilter : public QObject {
public:
	NotifyAnchorFilter(QObject *parent, NotifyButton *btn) : QObject(parent), m_btn(btn) {}

protected:
	bool eventFilter(QObject *, QEvent *event) override
	{
		switch (event->type()) {
		case QEvent::Move:
		case QEvent::Resize:
		case QEvent::Show:
		case QEvent::WindowStateChange:
		case QEvent::WindowActivate:
			if (m_btn) {
				PositionNotifyButton(m_btn.data());
			}
			break;
		case QEvent::Hide:
		case QEvent::Close:
			if (m_btn)
				m_btn->hide();
			break;
		default:
			break;
		}
		return false;
	}

private:
	QPointer<NotifyButton> m_btn;
};

} // namespace

NotifyButton::NotifyButton(QWidget *target, QWidget *embedIn)
	: QWidget(embedIn ? embedIn : target), m_target(target), m_embedded(embedIn != nullptr)
{
	if (m_embedded) {
		setFixedSize(kPillWidth, kHeight);
	} else {
		// owned by the target rather than free-floating: an owned top-level always sits directly above its owner -- including over the owners caption, which is the whole point -- and never over unrelated apps
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground);
		setAttribute(Qt::WA_ShowWithoutActivating);
		setFixedSize(kWidth, kHeight);
	}
	setCursor(Qt::PointingHandCursor);
}

NotifyButton::~NotifyButton()
{
	delete m_panel;
}

AnchoredPanel *NotifyButton::EnsurePanel()
{
	if (!m_panel)
		m_panel = new AnchoredPanel(m_target, QStringLiteral("ReplayKit Notifications"), QStringLiteral("http://127.0.0.1:8767/notifications-view"), QSize(360, 430));
	return m_panel;
}

QRect NotifyButton::GlobalRect() const
{
	return m_embedded ? QRect(mapToGlobal(QPoint(0, 0)), size()) : frameGeometry();
}

void NotifyButton::enterEvent(QEnterEvent *event)
{
	QWidget::enterEvent(event);
	m_hover = true;
	StartHoverFade();
	ShowTip();
}

void NotifyButton::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	m_hover = false;
	StartHoverFade();
	if (g_notifyTip)
		g_notifyTip->Leave();
}

void NotifyButton::ShowTip()
{
	// suppressed while our own panel is open, the same rule showBulkTip applies to the sort trigger
	if (m_panel && m_panel->IsShown())
		return;
	if (!g_notifyTip)
		g_notifyTip = new NotifyTip();
	QRect pillGlobal(mapToGlobal(QPoint(0, 0)), QSize(kPillWidth, height()));
	g_notifyTip->ShowFor("Notifications", pillGlobal);
}

void NotifyButton::HideTip()
{
	if (g_notifyTip)
		g_notifyTip->hide();
}

void NotifyButton::StartHoverFade()
{
	if (!m_fadeTimer) {
		m_fadeTimer = new QTimer(this);
		m_fadeTimer->setInterval(16);
		connect(m_fadeTimer, &QTimer::timeout, this, [this]() {
			const qreal step = 16.0 / 90.0;
			qreal target = m_hover ? 1.0 : 0.0;
			if (m_hoverFade < target)
				m_hoverFade = qMin(target, m_hoverFade + step);
			else
				m_hoverFade = qMax(target, m_hoverFade - step);
			if (qFuzzyCompare(m_hoverFade + 1.0, target + 1.0))
				m_fadeTimer->stop();
			update();
		});
	}
	m_fadeTimer->start();
}

void NotifyButton::mousePressEvent(QMouseEvent *event)
{
	if (event->button() == Qt::LeftButton && event->position().x() < kPillWidth) {
		HideTip();
		ShowNotificationPanel(this);
	}
}

bool NotifyButton::nativeEvent(const QByteArray &, void *message, qintptr *result)
{
	MSG *msg = static_cast<MSG *>(message);
	if (!msg || msg->message != WM_MOUSEACTIVATE)
		return false;
	*result = MA_NOACTIVATE;
	return true;
}

void NotifyButton::SetOpen(bool open)
{
	AnimateTurn(this, m_turnAnim, m_turn, open ? 1.0 : 0.0);
	update();
}

void NotifyButton::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	const bool open = m_panel && m_panel->IsShown();
	const QRectF pill(0.5, 0.5, kPillWidth - 1.0, height() - 1.0);

	// WA_TranslucentBackground makes this a layered window, and those hit-test on alpha: a fully transparent pixel drops the click through to the caption underneath, so at rest only the glyphs own pixels were clickable; one imperceptible pass gives the pill a hit area at all times, and the divider zone is deliberately left out so it still drags the window like the caption it sits on
	p.fillRect(QRectF(0, 0, kPillWidth, height()), QColor(0, 0, 0, 1));

	// surface states copied from .clip-sort-trigger: nothing at rest, the menu surface on hover, and on open the panel header ground (--grey6) plus a border whose bottom edge is left off, so the tab and the header row under it read as one shape
	if (open) {
		QPainterPath tab;
		tab.moveTo(pill.left(), pill.bottom());
		tab.lineTo(pill.left(), pill.top() + kRadius);
		tab.quadTo(pill.left(), pill.top(), pill.left() + kRadius, pill.top());
		tab.lineTo(pill.right() - kRadius, pill.top());
		tab.quadTo(pill.right(), pill.top(), pill.right(), pill.top() + kRadius);
		tab.lineTo(pill.right(), pill.bottom());
		p.setPen(Qt::NoPen);
		p.setBrush(g_menuPanel);
		p.drawPath(tab);
		p.setPen(QPen(g_notifyBorder, 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawPath(tab);
	} else if (m_hoverFade > 0.001) {
		// 90ms linear ramp, the same transition .clip-sort-trigger animates its background-color over
		p.setPen(Qt::NoPen);
		QColor fill = g_notifySurface;
		fill.setAlphaF(m_hoverFade);
		p.setBrush(fill);
		p.drawRoundedRect(pill, kRadius, kRadius);
	}

	// 6px pad then the 16px bell, then a 3px gap and the 12px chevron -- the sort triggers own run of boxes
	const qreal midY = height() / 2.0;
	// the caption bell is always white, the shell one takes the theme text colour so it reads on a light chrome too
	auto renderGlyph = [&](QSvgRenderer &svg, const QRectF &box) {
		// the shared icons are drawn in the theme colour (currentColor), so the bells of the classic layout tint them white the way their own white icons used to be
		p.drawPixmap(box.topLeft(), TintedSvgPixmap(svg, box.size(), devicePixelRatioF(), m_embedded ? g_notifyTipFg : QColor(Qt::white)));
	};
	static QSvgRenderer bellSvg{IconPath(QStringLiteral("nav/bell-outline.svg"))};
	if (bellSvg.isValid()) {
		p.save();
		p.setOpacity(m_hover || open ? 1.0 : 0.8);
		renderGlyph(bellSvg, QRectF(6.0, midY - 8.0, 16.0, 16.0));
		p.restore();
	}
	static QSvgRenderer chevronSvg{IconPath(QStringLiteral("nav/caret-down.svg"))};
	if (chevronSvg.isValid()) {
		QRectF box(25.0, midY - 6.0, 12.0, 12.0);
		p.save();
		p.setOpacity(0.65);
		// the shell bell draws a tinted bitmap, which steps through the turn unless it is sampled smoothly
		p.setRenderHint(QPainter::SmoothPixmapTransform, true);
		// turns on open, same as .clip-sort-wrap.is-open .clip-sort-chevron
		p.translate(box.center());
		p.rotate(180.0 * m_turn);
		p.translate(-box.center());
		renderGlyph(chevronSvg, box);
		p.restore();
	}

	// hairline between us and the system minimize button, same idea as the menu separators in clips -- without it the bell reads as part of the window buttons rather than as our own control. the shell title bar has no system buttons to set it apart from
	if (!m_embedded) {
		p.setPen(QPen(QColor(255, 255, 255, 38), 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawLine(QPointF(width() - 0.5, 6.0), QPointF(width() - 0.5, height() - 6.0));
	}

	if (g_notifyUnread > 0) {
		QString label = g_notifyUnread > 9 ? QString("9+") : QString::number(g_notifyUnread);
		QFont f = font();
		f.setPixelSize(8);
		f.setBold(true);
		p.setFont(f);
		qreal badgeW = label.size() > 1 ? 15.0 : 12.0;
		// over the top right of the bell glyph, which spans x 6 to 22
		QRectF badge(label.size() > 1 ? 12.0 : 14.0, 1.0, badgeW, 12.0);
		p.setPen(Qt::NoPen);
		p.setBrush(QColor(227, 59, 87));
		p.drawRoundedRect(badge, 6, 6);
		p.setPen(QColor(255, 255, 255));
		p.drawText(badge, Qt::AlignCenter, label);
	}
}

QList<QPointer<NotifyButton>> g_notifyButtons;

// parks the button inside the caption, immediately left of the system buttons. DWMWA_CAPTION_BUTTON_BOUNDS is the only reliable source for where those start -- their width changes with dpi, the windows version, and whether the window has a maximize box at all; hidden whenever the target is not a normal visible window, so it can never float alone
void PositionNotifyButton(NotifyButton *btn)
{
	QWidget *target = btn ? btn->target() : nullptr;
	if (!target || btn->IsEmbedded())
		return;
	if (!target->isVisible() || target->isMinimized()) {
		btn->hide();
		return;
	}
	HWND hwnd = (HWND)target->winId();
	RECT frame = {};
	if (!GetWindowRect(hwnd, &frame)) {
		btn->hide();
		return;
	}
	RECT buttons = {};
	if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, &buttons, sizeof(buttons))) ||
	    buttons.right <= buttons.left) {
		btn->hide();
		return;
	}
	// bounds come back window-relative, including the invisible resize border
	int captionTop = frame.top + buttons.top;
	int captionHeight = buttons.bottom - buttons.top;
	if (captionHeight < NotifyButton::kHeight) {
		btn->hide();
		return;
	}
	int x = frame.left + buttons.left - NotifyButton::kWidth - 2;
	int y = captionTop + (captionHeight - NotifyButton::kHeight) / 2;
	if (x < frame.left) {
		btn->hide();
		return;
	}
	// moving the window out from under a press loses it, so hold still while the button is being clicked
	if (btn->rect().contains(btn->mapFromGlobal(QCursor::pos())) && (GetAsyncKeyState(VK_LBUTTON) & 0x8000))
		return;
	if (btn->pos() != QPoint(x, y))
		btn->move(x, y);
	if (!btn->isVisible())
		btn->show();
	if (btn->Panel() && btn->Panel()->IsShown() && btn->Panel()->AnchorKey() == btn)
		btn->Panel()->Follow();
}

void PositionAllNotifyButtons()
{
	for (auto &btn : g_notifyButtons)
		if (btn)
			PositionNotifyButton(btn.data());
}

void AttachNotifyButton(QWidget *target)
{
	if (!target)
		return;
	for (auto &existing : g_notifyButtons)
		if (existing && existing->target() == target)
			return;
	NotifyButton *btn = new NotifyButton(target);
	target->installEventFilter(new NotifyAnchorFilter(target, btn));
	g_notifyButtons.append(QPointer<NotifyButton>(btn));
	PositionNotifyButton(btn);
}

void RefreshNotifyBadges()
{
	for (auto &btn : g_notifyButtons)
		if (btn)
			btn->update();
}

} // namespace rk
