// clip and record badges of the shell title bar
#include "prelude.hpp"
#include "shell/shell_badge.hpp"

#include "core/theme.hpp"
#include "notify/notify_tip.hpp"

namespace rk {

ShellBadge::ShellBadge(QWidget *parent)
	: QWidget(parent)
{
	setFixedHeight(kHeight);
	setCursor(Qt::PointingHandCursor);
	SizeToContent();
}

void ShellBadge::SetKeys(const QString &keybind)
{
	QStringList keys = keybind.isEmpty() ? QStringList() : keybind.split('+', Qt::SkipEmptyParts);
	if (keys == m_keys)
		return;
	m_keys = keys;
	SizeToContent();
}

void ShellBadge::SetLabel(const QString &label)
{
	if (label == m_label)
		return;
	m_label = label;
	SizeToContent();
}

void ShellBadge::SetState(bool live, bool dim, bool paused, const QString &timer)
{
	if (live == m_live && dim == m_dim && paused == m_paused && timer == m_timer)
		return;
	const bool widthChanges = live != m_live || timer.size() != m_timer.size() || paused != m_paused;
	m_live = live;
	m_dim = dim;
	m_paused = paused;
	m_timer = timer;
	if (widthChanges)
		SizeToContent();
	update();
}

void ShellBadge::SetArmed(bool armed)
{
	if (armed == m_armed)
		return;
	m_armed = armed;
	SizeToContent();
	update();
}

void ShellBadge::SetTip(const QString &tip)
{
	m_tip = tip;
}

void ShellBadge::Pulse(bool on)
{
	if (on == m_pulseOn)
		return;
	m_pulseOn = on;
	if (m_live)
		update();
}

void ShellBadge::SetOpen(bool open)
{
	if (open == m_open)
		return;
	m_open = open;
	update();
}

QRect ShellBadge::GlobalRect() const
{
	return QRect(mapToGlobal(QPoint(0, 0)), size());
}

void ShellBadge::enterEvent(QEnterEvent *event)
{
	QWidget::enterEvent(event);
	m_hover = true;
	update();
	if (onEnter)
		onEnter();
	// the menu hangs right under the badge, so a tip would sit on top of it
	if (!m_tip.isEmpty() && !m_open) {
		if (!g_notifyTip)
			g_notifyTip = new NotifyTip();
		g_notifyTip->ShowFor(m_tip, QRect(mapToGlobal(QPoint(0, 0)), size()));
	}
}

void ShellBadge::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	m_hover = false;
	m_down = false;
	update();
	if (g_notifyTip)
		g_notifyTip->Leave();
}

void ShellBadge::mousePressEvent(QMouseEvent *event)
{
	if (event->button() == Qt::RightButton) {
		if (g_notifyTip)
			g_notifyTip->hide();
		if (onContextMenu)
			onContextMenu();
		return;
	}
	if (event->button() != Qt::LeftButton)
		return;
	m_down = true;
	update();
	if (g_notifyTip)
		g_notifyTip->hide();
}

void ShellBadge::mouseReleaseEvent(QMouseEvent *event)
{
	const bool fire = m_down && event->button() == Qt::LeftButton && rect().contains(event->position().toPoint());
	m_down = false;
	update();
	if (fire && onClick)
		onClick();
}

void ShellBadge::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	const QRectF pill(0.5, 0.5, width() - 1.0, height() - 1.0);
	const QColor danger = g_menuDanger;

	if (m_open) {
		// the tab of the menu under it: the panel header ground and a border on the top and the sides, running down to the very last row so it meets the border of the panel below
		const qreal bottom = height();
		QPainterPath tab;
		tab.moveTo(pill.left(), bottom);
		tab.lineTo(pill.left(), pill.top() + kTabRadius);
		tab.quadTo(pill.left(), pill.top(), pill.left() + kTabRadius, pill.top());
		tab.lineTo(pill.right() - kTabRadius, pill.top());
		tab.quadTo(pill.right(), pill.top(), pill.right(), pill.top() + kTabRadius);
		tab.lineTo(pill.right(), bottom);
		p.setPen(Qt::NoPen);
		p.setBrush(g_menuPanel);
		p.drawPath(tab);
		p.setPen(QPen(g_notifyBorder, 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawPath(tab);
	} else {
		// resting surface is barely there; hover and press step it up, and a live recording washes the whole chip red
		QColor fill = ChromeInk(m_down ? 0.16 : m_hover ? 0.11 : 0.06);
		QColor edge = ChromeInk(0.0);
		if (m_live) {
			fill = danger;
			fill.setAlphaF(m_down ? 0.34 : m_hover ? 0.28 : 0.20);
			edge = danger;
			edge.setAlphaF(0.55);
		} else if (m_armed) {
			// clipping is on: the chip takes a little of the status green, less than a recording takes of the red
			fill = g_chromeSuccess;
			fill.setAlphaF(m_down ? 0.26 : m_hover ? 0.2 : 0.13);
			edge = g_chromeSuccess;
			edge.setAlphaF(0.42);
		}
		p.setPen((m_live || m_armed) ? QPen(edge, 1.0) : Qt::NoPen);
		p.setBrush(fill);
		p.drawRoundedRect(pill, 9, 9);
	}

	const qreal ink = m_dim ? 0.42 : 1.0;
	qreal x = kPadX;
	const qreal midY = height() / 2.0;

	QFont chipFont = ChipFont();
	QFontMetrics chipFm(chipFont);
	for (const QString &key : m_keys) {
		const qreal chipW = chipFm.horizontalAdvance(key) + kChipPadX * 2;
		const QRectF chip(x, midY - kChipH / 2.0, chipW, kChipH);
		p.setPen(Qt::NoPen);
		p.setBrush(m_live ? QColor(0, 0, 0, 60) : ChromeInk(0.10 * ink));
		p.drawRoundedRect(chip, 6, 6);
		p.setFont(chipFont);
		p.setPen(ChromeInk(0.85 * ink));
		p.drawText(chip, Qt::AlignCenter, key);
		x += chipW + kChipGap;
	}
	if (!m_keys.isEmpty())
		x += kChipGap;

	if (m_live) {
		const qreal d = 8.0;
		QColor dot = danger;
		dot.setAlphaF(m_paused ? 0.45 : (m_pulseOn ? 1.0 : 0.5));
		p.setPen(Qt::NoPen);
		p.setBrush(dot);
		p.drawEllipse(QRectF(x, midY - d / 2.0, d, d));
		x += d + 7.0;
	}

	if (m_armed && !m_live) {
		const qreal d = 8.0;
		p.setPen(Qt::NoPen);
		p.setBrush(g_chromeSuccess);
		p.drawEllipse(QRectF(x, midY - d / 2.0, d, d));
		x += d + 7.0;
	}

	QFont labelFont = LabelFont();
	QFontMetrics labelFm(labelFont);
	p.setFont(labelFont);
	p.setPen(ChromeInk(0.95 * ink));
	p.drawText(QRectF(x, 0, labelFm.horizontalAdvance(m_label) + 2, height()), Qt::AlignVCenter | Qt::AlignLeft, m_label);
	x += labelFm.horizontalAdvance(m_label);

	if (m_live && !m_timer.isEmpty()) {
		x += 8.0;
		QFont timerFont = labelFont;
		timerFont.setWeight(QFont::Normal);
		p.setFont(timerFont);
		p.setPen(ChromeInk(0.75));
		p.drawText(QRectF(x, 0, QFontMetrics(timerFont).horizontalAdvance(m_timer) + 2, height()), Qt::AlignVCenter | Qt::AlignLeft, m_timer);
	}
}

QFont ShellBadge::ChipFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setWeight(QFont::DemiBold);
	return f;
}

QFont ShellBadge::LabelFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(13);
	f.setWeight(QFont::DemiBold);
	return f;
}

void ShellBadge::SizeToContent()
{
	QFontMetrics chipFm(ChipFont());
	QFontMetrics labelFm(LabelFont());
	int w = kPadX;
	for (const QString &key : m_keys)
		w += chipFm.horizontalAdvance(key) + kChipPadX * 2 + kChipGap;
	if (!m_keys.isEmpty())
		w += kChipGap;
	if (m_live || m_armed)
		w += 8 + 7;
	w += labelFm.horizontalAdvance(m_label);
	if (m_live && !m_timer.isEmpty()) {
		// a fixed-width slot for the clock so the badge does not jitter every second
		w += 8 + labelFm.horizontalAdvance(QString(m_timer.size(), '0'));
	}
	w += kPadX + 4;
	setFixedWidth(w);
}

} // namespace rk
