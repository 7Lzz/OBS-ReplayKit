// icon buttons of the title bar
#include "prelude.hpp"
#include "shell/shell_title_button.hpp"

#include "core/theme.hpp"
#include "notify/notify_tip.hpp"

namespace rk {

ShellTitleButton::ShellTitleButton(int width, QWidget *parent) : QWidget(parent)
{
	setFixedSize(width, kHeight);
	setCursor(Qt::PointingHandCursor);
}

void ShellTitleButton::SetTip(const QString &tip)
{
	m_tip = tip;
}

void ShellTitleButton::SetOpen(bool open)
{
	// the arrow follows every call, since a panel opened again while it rolls up finds the tab still open and the arrow already turning back
	AnimateTurn(this, m_turnAnim, m_turn, open ? 1.0 : 0.0);
	if (open == m_open)
		return;
	m_open = open;
	update();
}

void ShellTitleButton::TurnClosed()
{
	AnimateTurn(this, m_turnAnim, m_turn, 0.0);
}

QEasingCurve SmoothEase()
{
	QEasingCurve curve(QEasingCurve::BezierSpline);
	curve.addCubicBezierSegment(QPointF(0.16, 1.0), QPointF(0.3, 1.0), QPointF(1.0, 1.0));
	return curve;
}

void AnimateTurn(QWidget *widget, QVariantAnimation *&anim, qreal &value, qreal target)
{
	if (!anim) {
		anim = new QVariantAnimation(widget);
		anim->setDuration(200);
		anim->setEasingCurve(SmoothEase());
		qreal *slot = &value;
		QObject::connect(anim, &QVariantAnimation::valueChanged, widget, [widget, slot](const QVariant &v) {
			*slot = v.toReal();
			widget->update();
		});
	}
	// a turn already on its way there is left to finish, so the close a roll reports at its end does not start it over
	if (anim->state() == QAbstractAnimation::Running && qFuzzyCompare(anim->endValue().toReal() + 1.0, target + 1.0))
		return;
	anim->stop();
	if (qFuzzyCompare(value + 1.0, target + 1.0))
		return;
	anim->setStartValue(value);
	anim->setEndValue(target);
	anim->start();
}

void ShellTitleButton::ClearHover()
{
	if (!m_hover)
		return;
	m_hover = false;
	StartFade();
	if (g_notifyTip)
		g_notifyTip->Leave();
}

void ShellTitleButton::enterEvent(QEnterEvent *event)
{
	QWidget::enterEvent(event);
	m_hover = true;
	StartFade();
	// the tip of a button whose panel is open would sit on top of that panel
	if (m_open || m_tip.isEmpty())
		return;
	if (!g_notifyTip)
		g_notifyTip = new NotifyTip();
	// kept inside the window, so the tip of a button at its edge does not hang out past it
	g_notifyTip->ShowFor(m_tip, GlobalRect(), NotifyTip::Side::Below, window() ? window()->frameGeometry() : QRect());
}

void ShellTitleButton::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	m_hover = false;
	StartFade();
	if (g_notifyTip)
		g_notifyTip->Leave();
}

void ShellTitleButton::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton)
		return;
	if (g_notifyTip)
		g_notifyTip->hide();
	event->accept();
	if (onPress)
		onPress();
}

void ShellTitleButton::StartFade()
{
	if (!m_fadeTimer) {
		m_fadeTimer = new QTimer(this);
		m_fadeTimer->setInterval(16);
		connect(m_fadeTimer, &QTimer::timeout, this, [this]() {
			const qreal step = 16.0 / 90.0;
			const qreal target = m_hover ? 1.0 : 0.0;
			if (m_fade < target)
				m_fade = qMin(target, m_fade + step);
			else
				m_fade = qMax(target, m_fade - step);
			if (qFuzzyCompare(m_fade + 1.0, target + 1.0))
				m_fadeTimer->stop();
			update();
		});
	}
	m_fadeTimer->start();
}

void ShellTitleButton::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	const QRectF pill(0.5, 0.5, width() - 1.0, height() - 1.0);
	if (m_open) {
		// the tab of its panel: the ground of the panel header with a border that has no bottom edge, so the tab and the header row under it read as one shape. it runs to the very bottom of the button, where the panel starts, or its last row is only half painted and shows as a dark line between the two
		const qreal bottom = height();
		QPainterPath tab;
		tab.moveTo(pill.left(), bottom);
		tab.lineTo(pill.left(), pill.top() + kRadius);
		tab.quadTo(pill.left(), pill.top(), pill.left() + kRadius, pill.top());
		tab.lineTo(pill.right() - kRadius, pill.top());
		tab.quadTo(pill.right(), pill.top(), pill.right(), pill.top() + kRadius);
		tab.lineTo(pill.right(), bottom);
		p.setPen(Qt::NoPen);
		p.setBrush(g_menuPanel);
		p.drawPath(tab);
		p.setPen(QPen(g_notifyBorder, 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawPath(tab);
	} else if (m_fade > 0.001) {
		p.setPen(Qt::NoPen);
		QColor fill = g_notifySurface;
		fill.setAlphaF(m_fade);
		p.setBrush(fill);
		p.drawRoundedRect(pill, kRadius, kRadius);
	}
	PaintGlyph(p, pill, m_fade);
}

} // namespace rk
