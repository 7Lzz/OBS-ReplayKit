// the back and forward arrows of the title bar
#include "prelude.hpp"
#include "shell/shell_nav_button.hpp"

#include "core/draw_util.hpp"
#include "core/theme.hpp"

namespace rk {

namespace {

// the arrows as they were drawn, on the 24 grid like the rest of the icons; only the shape is kept and recoloured
const char kBackSvg[] =
	R"SVG(<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M9.04038 5.3998C9.33034 5.11028 9.81006 5.11017 10.0999 5.3998C10.3899 5.6898 10.3899 6.17035 10.0999 6.46035L5.30991 11.2504H20.5003C20.9102 11.2506 21.2503 11.5905 21.2503 12.0004C21.2502 12.4102 20.9101 12.7502 20.5003 12.7504H5.30991L10.0999 17.5404C10.3898 17.8304 10.3898 18.31 10.0999 18.6C9.95995 18.75 9.75967 18.8207 9.56967 18.8207C9.37981 18.8206 9.19028 18.7499 9.04038 18.6L2.97006 12.5307C2.68006 12.2407 2.68006 11.7601 2.97006 11.4701L9.04038 5.3998Z" fill="currentColor"/></svg>)SVG";
const char kForwardSvg[] =
	R"SVG(<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path d="M14.9624 18.6035C14.6724 18.893 14.1927 18.8932 13.9028 18.6035C13.6128 18.3135 13.6128 17.833 13.9028 17.543L18.6929 12.7529L3.50244 12.7529C3.0926 12.7527 2.75244 12.4128 2.75244 12.0029C2.75254 11.5931 3.09265 11.2531 3.50244 11.2529L18.6929 11.2529L13.9028 6.46289C13.6129 6.17291 13.6129 5.6933 13.9028 5.40332C14.0428 5.25332 14.2431 5.18262 14.4331 5.18262C14.623 5.18269 14.8125 5.25346 14.9624 5.40332L21.0327 11.4727C21.3227 11.7627 21.3227 12.2432 21.0327 12.5332L14.9624 18.6035Z" fill="currentColor"/></svg>)SVG";

constexpr int kGlyph = 20;

} // namespace

ShellNavButton::ShellNavButton(Kind kind, QWidget *parent) : ShellTitleButton(kWidth, parent)
{
	const bool back = kind == Kind::Back;
	m_svg = new QSvgRenderer(QByteArray(back ? kBackSvg : kForwardSvg), this);
	SetTip(back ? QStringLiteral("Back (Alt + Left)") : QStringLiteral("Forward (Alt + Right)"));
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
