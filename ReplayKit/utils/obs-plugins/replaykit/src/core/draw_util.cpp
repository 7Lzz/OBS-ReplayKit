// painting helpers shared by the native chrome
#include "prelude.hpp"
#include "core/draw_util.hpp"

namespace rk {

// renders an svg into a pixmap and recolours every painted pixel, since QSvgRenderer has no css context for currentColor and the shell chrome follows the theme text colour instead of the fixed white the caption glyphs use. the pixmap carries the device ratio, so drawing it at a logical position stays sharp on scaled displays
QPixmap TintedSvgPixmap(QSvgRenderer &svg, const QSizeF &logicalSize, qreal dpr, const QColor &color)
{
	QPixmap pm(QSize(qCeil(logicalSize.width() * dpr), qCeil(logicalSize.height() * dpr)));
	pm.setDevicePixelRatio(dpr);
	pm.fill(Qt::transparent);
	QPainter p(&pm);
	p.setRenderHint(QPainter::Antialiasing, true);
	svg.render(&p, QRectF(QPointF(0, 0), logicalSize));
	p.setCompositionMode(QPainter::CompositionMode_SourceIn);
	p.fillRect(QRectF(QPointF(0, 0), logicalSize), color);
	return pm;
}

// how the pages draw their text: segoe ui laid out unhinted with fractional advances, on a rasterizer that gives it a heavier stem than an outline fill of the same face. the native chrome that has to sit beside those pages fills the outline of the text and strokes it with a hairline of the given width, which matches both the width and the weight of the css text
void DrawPageText(QPainter &p, const QRectF &rect, Qt::Alignment align, const QString &text, QFont font, const QColor &color, qreal stroke)
{
	font.setHintingPreference(QFont::PreferNoHinting);
	const QFontMetricsF fm(font);
	const qreal x = align.testFlag(Qt::AlignHCenter) ? rect.left() + (rect.width() - fm.horizontalAdvance(text)) / 2.0 : rect.left();
	// a browser centers the line box (rounded ascent plus rounded descent) in the row and puts the baseline on a whole pixel; so does this
	const qreal line = qRound(fm.ascent()) + qRound(fm.descent());
	const qreal baseline = qRound(rect.top() + (rect.height() - line) / 2.0 + qRound(fm.ascent()));
	QPainterPath outline;
	outline.addText(QPointF(x, baseline), font, text);
	p.save();
	p.setRenderHint(QPainter::Antialiasing, true);
	p.setPen(Qt::NoPen);
	p.setBrush(color);
	p.drawPath(outline);
	if (stroke > 0.0) {
		p.setPen(QPen(color, stroke, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
		p.setBrush(Qt::NoBrush);
		p.drawPath(outline);
	}
	p.restore();
}

} // namespace rk
