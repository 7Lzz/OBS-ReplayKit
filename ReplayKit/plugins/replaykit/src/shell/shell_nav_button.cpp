// the back and forward arrows of the title bar
#include "prelude.hpp"
#include "shell/shell_nav_button.hpp"

#include "core/draw_util.hpp"
#include "core/locale.hpp"
#include "core/theme.hpp"

namespace rk {

namespace {

// the arrows as they were drawn, on the 24 grid like the rest of the icons; only the shape is kept and recoloured

constexpr int kGlyph = 20;

} // namespace

ShellNavButton::ShellNavButton(Kind kind, QWidget *parent) : ShellTitleButton(kWidth, parent)
{
	const bool back = kind == Kind::Back;
	m_svg = new QSvgRenderer(IconPath(back ? QStringLiteral("nav/history-back.svg") : QStringLiteral("nav/history-forward.svg")), this);
	SetTip(back ? Tr("Back (Alt + Left)") : Tr("Forward (Alt + Right)"));
	setCursor(Qt::ArrowCursor);
}

void ShellNavButton::SetAvailable(bool available)
{
	if (available == m_available)
		return;
	m_available = available;
	setCursor(available ? Qt::PointingHandCursor : Qt::ArrowCursor);
	// a pointer that was on the button when it ran out of places has nothing to hover any more
	if (!available)
		ClearHover();
	update();
}

void ShellNavButton::enterEvent(QEnterEvent *event)
{
	if (!m_available) {
		QWidget::enterEvent(event);
		return;
	}
	ShellTitleButton::enterEvent(event);
}

void ShellNavButton::mousePressEvent(QMouseEvent *event)
{
	if (!m_available) {
		event->accept();
		return;
	}
	ShellTitleButton::mousePressEvent(event);
}

void ShellNavButton::PaintGlyph(QPainter &p, const QRectF &pill, qreal hover)
{
	const qreal dpr = devicePixelRatioF();
	const qreal ink = m_available ? 0.82 + 0.18 * hover : 0.3;
	const QRectF glyph(pill.center().x() - kGlyph / 2.0, pill.center().y() - kGlyph / 2.0, kGlyph, kGlyph);
	p.drawPixmap(glyph.topLeft(), TintedSvgPixmap(*m_svg, glyph.size(), dpr, ChromeInk(ink)));
}

} // namespace rk
