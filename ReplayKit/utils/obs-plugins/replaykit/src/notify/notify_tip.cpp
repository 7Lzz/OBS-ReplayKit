// native twin of the css tooltip
#include "prelude.hpp"
#include "notify/notify_tip.hpp"

#include <cmath>

#include "core/draw_util.hpp"
#include "core/theme.hpp"

namespace rk {

namespace {

// body and arrow as one outline, the way the page draws it: a rounded box with the arrow cut into one edge, so the ground and the ring are laid down once and nothing runs across the base of the arrow; at is where the arrow points along the edge it stands on, x for the top edge and y for the left one
QPainterPath TipOutline(const QRectF &body, qreal radius, qreal arrow, bool right, qreal at)
{
	const qreal d = radius * 2.0;
	QPainterPath path;
	path.moveTo(body.left() + radius, body.top());
	if (!right) {
		path.lineTo(at - arrow, body.top());
		path.lineTo(at, body.top() - arrow);
		path.lineTo(at + arrow, body.top());
	}
	path.lineTo(body.right() - radius, body.top());
	path.arcTo(QRectF(body.right() - d, body.top(), d, d), 90, -90);
	path.lineTo(body.right(), body.bottom() - radius);
	path.arcTo(QRectF(body.right() - d, body.bottom() - d, d, d), 0, -90);
	path.lineTo(body.left() + radius, body.bottom());
	path.arcTo(QRectF(body.left(), body.bottom() - d, d, d), 270, -90);
	if (right) {
		path.lineTo(body.left(), at + arrow);
		path.lineTo(body.left() - arrow, at);
		path.lineTo(body.left(), at - arrow);
	}
	path.lineTo(body.left(), body.top() + radius);
	path.arcTo(QRectF(body.left(), body.top(), d, d), 180, -90);
	path.closeSubpath();
	return path;
}

} // namespace

NotifyTip::NotifyTip()
	: QWidget(nullptr)
{
	setWindowFlags(Qt::ToolTip | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	setAttribute(Qt::WA_TranslucentBackground);
	setAttribute(Qt::WA_ShowWithoutActivating);
	setAttribute(Qt::WA_TransparentForMouseEvents);
	// deliberately NOT setFont(): obs installs an app-wide qss, and a stylesheet font-size overrides a widget font, which is what made this tip render larger than every other one; TipFont() is used directly for metrics and painting instead
	m_anim = new QTimer(this);
	m_anim->setInterval(16);
	connect(m_anim, &QTimer::timeout, this, [this]() {
		const qreal elapsed = m_since.isValid() ? m_since.elapsed() : qMax(kPop, kFade);
		const qreal r = qMin(qreal(1.0), elapsed / qreal(kPop));
		// an ease-out that stands in for ease_pop
		m_pop = m_popFrom + (m_popTo - m_popFrom) * (1.0 - qPow(1.0 - r, 3.0));
		const qreal f = qMin(qreal(1.0), elapsed / qreal(kFade));
		setWindowOpacity(m_opacityFrom + (m_popTo - m_opacityFrom) * f);
		if (r >= 1.0 && f >= 1.0) {
			m_anim->stop();
			if (m_popTo <= 0.0)
				QWidget::hide();
		}
		update();
	});
}

void NotifyTip::Animate(qreal to)
{
	m_popFrom = m_pop;
	m_popTo = to;
	m_opacityFrom = windowOpacity();
	m_since.start();
	m_anim->start();
}

void NotifyTip::Leave()
{
	if (!isVisible() || m_popTo <= 0.0)
		return;
	Animate(0.0);
}

void NotifyTip::ShowFor(const QString &text, const QRect &anchorGlobal, Side side)
{
	m_text = text;
	m_side = side;
	m_settled = QPixmap();
	// a tip that is still up, or on its way out, moves to the new place and goes on from where it is, as the page tip does when the pointer passes from one control to the next
	const bool wasUp = isVisible();
	const QFontMetricsF fm(TipFont());
	int w = qRound(fm.horizontalAdvance(text)) + kPadX * 2;
	int bodyH = qRound(fm.ascent()) + qRound(fm.descent()) + kPadY * 2;

	if (side == Side::Right) {
		setFixedSize(w + kArrow + kShadow * 2, bodyH + kShadow * 2);
		int centre = anchorGlobal.center().y();
		int top = centre - bodyH / 2;
		int left = anchorGlobal.right() + 1 + kGap - kArrow;
		if (QScreen *screen = QGuiApplication::screenAt(anchorGlobal.center())) {
			QRect avail = screen->availableGeometry();
			top = qBound(avail.top() + kEdge, top, avail.bottom() - bodyH - kEdge);
			left = qMin(left, avail.right() - (w + kArrow) - kEdge);
		}
		m_arrowY = qBound(qreal(8.0), qreal(centre - top), qreal(bodyH - 8.0));
		move(left - kShadow, top - kShadow);
	} else {
		setFixedSize(w + kShadow * 2, bodyH + kArrow + kShadow * 2);

		int centre = anchorGlobal.center().x();
		int left = centre - w / 2;
		// the arrow lives above the body, so the widget starts kArrow higher than the css tips top edge
		int top = anchorGlobal.bottom() + 1 + kGap - kArrow;
		if (QScreen *screen = QGuiApplication::screenAt(anchorGlobal.center())) {
			QRect avail = screen->availableGeometry();
			left = qBound(avail.left() + kEdge, left, avail.right() - w - kEdge);
			top = qMin(top, avail.bottom() - (bodyH + kArrow) - kEdge);
		}
		m_arrowX = qBound(qreal(10.0), qreal(centre - left), qreal(w - 10.0));
		move(left - kShadow, top - kShadow);
	}

	if (!wasUp) {
		m_pop = 0.0;
		setWindowOpacity(0.0);
	}
	show();
	raise();
	Animate(1.0);
}

void NotifyTip::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	const qreal dpr = devicePixelRatioF();
	const QSize px(qRound(width() * dpr), qRound(height() * dpr));
	// the finished tip is painted once into a bitmap and the grow-in only scales that bitmap, the way a compositor scales a layer; text drawn through a scale transform is rasterized unhinted and laid out again hinted on the last frame, which showed as the letters shifting a pixel when the tip settled
	if (m_settled.size() != px || !qFuzzyCompare(m_settled.devicePixelRatio(), dpr)) {
		m_settled = QPixmap(px);
		m_settled.setDevicePixelRatio(dpr);
		m_settled.fill(Qt::transparent);
		QPainter q(&m_settled);
		PaintSettled(q);
	}
	if (m_pop >= 1.0) {
		p.drawPixmap(0, 0, m_settled);
		return;
	}

	// grow-in and the leave that undoes it: translateY(3px) scale(.96) and none, about top center; beside a control (Side::Right) it grows out of the arrow edge instead
	const bool right = m_side == Side::Right;
	qreal scale = 0.96 + 0.04 * m_pop;
	qreal dy = 3.0 * (1.0 - m_pop);
	const QPointF origin = right ? QPointF(qreal(kShadow + kArrow), height() / 2.0) : QPointF(kShadow + (width() - kShadow * 2) / 2.0, qreal(kShadow));
	p.setRenderHint(QPainter::SmoothPixmapTransform, true);
	p.translate(origin);
	p.scale(scale, scale);
	p.translate(-origin);
	if (right)
		p.translate(-dy, 0);
	else
		p.translate(0, dy);
	p.drawPixmap(0, 0, m_settled);
}

QFont NotifyTip::TipFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setWeight(kTipWeight);
	f.setHintingPreference(QFont::PreferNoHinting);
	return f;
}

void NotifyTip::PaintSettled(QPainter &p)
{
	p.setRenderHint(QPainter::Antialiasing, true);
	const bool right = m_side == Side::Right;
	const QRectF body = right ? QRectF(kShadow + kArrow, kShadow, width() - kShadow * 2 - kArrow, height() - kShadow * 2) : QRectF(kShadow, kShadow + kArrow, width() - kShadow * 2, height() - kShadow * 2 - kArrow);
	const QPainterPath shape = TipOutline(body, kRadius, kArrow, right, kShadow + (right ? m_arrowY : m_arrowX));

	// box-shadow 0 3px 10px rgba(0,0,0,.42) is the box moved 3 down and blurred with a gaussian of sigma 5; a QGraphicsEffect on a translucent tool window does not composite reliably, so it is drawn as boxes a pixel apart whose alphas are worked out to stack into that curve, from 3 px inside the shadow edge, where the body leaves a strip of it showing under the bottom, out to the 10 px of margin; a css shadow is never drawn inside the box, so the boxes are cut out of it and do not show thru the ground
	QPainterPath box;
	box.addRoundedRect(body, kRadius, kRadius);
	QPainterPath outside;
	outside.addRect(QRectF(rect()));
	p.save();
	p.setClipPath(outside.subtracted(box));
	p.setPen(Qt::NoPen);
	const qreal sigma = kShadow / 2.0;
	const auto reach = [sigma](qreal distance) { return 0.42 * 0.5 * std::erfc(distance / (sigma * std::sqrt(2.0))); };
	for (int i = kShadow; i >= -3; --i) {
		const qreal outer = i == kShadow ? 0.0 : reach(i + 0.5);
		const qreal alpha = 1.0 - (1.0 - reach(i - 0.5)) / (1.0 - outer);
		const QRectF grown = body.translated(0, 3).adjusted(-i, -i, i, i);
		if (alpha <= 0.0 || grown.width() <= 0.0 || grown.height() <= 0.0)
			continue;
		const qreal radius = qMax(0.0, qreal(kRadius + i));
		QPainterPath blurred;
		blurred.addRoundedRect(grown, radius, radius);
		p.setBrush(QColor::fromRgbF(0, 0, 0, alpha));
		p.drawPath(blurred);
	}
	p.restore();

	// mirrors #rk-tip in _tooltip.html: the ground is 88% opaque so the controls behind still read thru it, and it is laid down once over body and arrow together so the arrow joins the body without a seam
	QColor tipFill = g_notifyTipBg;
	tipFill.setAlphaF(0.88);
	p.setPen(Qt::NoPen);
	p.setBrush(tipFill);
	p.drawPath(shape);
	// the hairline of the css tip, the text colour at .16, drawn inside the edge so the size of the tip does not change: a stroke twice as wide as the line, cut to the shape
	QColor edge = g_notifyTipFg;
	edge.setAlphaF(0.16);
	p.save();
	p.setClipPath(shape);
	QPen ring(edge, 2.0);
	ring.setJoinStyle(Qt::MiterJoin);
	p.setPen(ring);
	p.setBrush(Qt::NoBrush);
	p.drawPath(shape);
	p.restore();
	DrawPageText(p, body, Qt::AlignHCenter, m_text, TipFont(), g_notifyTipFg, kTipStroke);
}

NotifyTip *g_notifyTip = nullptr;

} // namespace rk
