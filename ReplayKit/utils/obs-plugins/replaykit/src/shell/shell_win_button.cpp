// minimize, maximize and close buttons of the shell title bar
#include "prelude.hpp"
#include "shell/shell_win_button.hpp"

#include "core/main_window.hpp"
#include "core/theme.hpp"
#include "shell/shell_frame.hpp"
#include "shell/shell_types.hpp"

namespace rk {

ShellWinButton::ShellWinButton(Kind kind, QWidget *parent)
	: QWidget(parent), m_kind(kind)
{
	setFixedSize(kShellCtlW, kShellTitleH);
}

void ShellWinButton::SetMaximized(bool maximized)
{
	if (maximized == m_maximized)
		return;
	m_maximized = maximized;
	update();
}

void ShellWinButton::enterEvent(QEnterEvent *event)
{
	QWidget::enterEvent(event);
	m_hover = true;
	update();
}

void ShellWinButton::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	m_hover = false;
	m_down = false;
	update();
}

void ShellWinButton::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton)
		return;
	m_down = true;
	update();
}

void ShellWinButton::mouseReleaseEvent(QMouseEvent *event)
{
	const bool fire = m_down && event->button() == Qt::LeftButton && rect().contains(event->position().toPoint());
	m_down = false;
	update();
	if (!fire)
		return;
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw)
		return;
	switch (m_kind) {
	case Kind::Min:
		mw->showMinimized();
		break;
	case Kind::Max:
		ShellToggleMaximize(mw);
		break;
	case Kind::Close:
		mw->close();
		break;
	}
}

void ShellWinButton::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	const bool close = m_kind == Kind::Close;
	if (m_hover || m_down) {
		if (close) {
			QColor red(0xE8, 0x11, 0x23);
			red.setAlphaF(m_down ? 0.8 : 1.0);
			p.fillRect(rect(), red);
		} else {
			p.fillRect(rect(), ChromeInk(m_down ? 0.14 : 0.09));
		}
	}
	const QColor ink = close && m_hover ? QColor(255, 255, 255) : ChromeInk(0.9);
	const qreal cx = width() / 2.0;
	const qreal cy = height() / 2.0;
	QPen pen(ink, 1.0);
	p.setPen(pen);
	p.setBrush(Qt::NoBrush);
	switch (m_kind) {
	case Kind::Min:
		p.drawLine(QPointF(cx - 5, cy + 0.5), QPointF(cx + 5, cy + 0.5));
		break;
	case Kind::Max:
		if (m_maximized) {
			// restore glyph: the back square shows only its top and right edges behind the front one
			p.drawRect(QRectF(cx - 5 + 0.5, cy - 3 + 0.5, 8, 8));
			p.drawPolyline(QPolygonF({QPointF(cx - 3 + 0.5, cy - 3 + 0.5), QPointF(cx - 3 + 0.5, cy - 5 + 0.5),
						  QPointF(cx + 5 + 0.5, cy - 5 + 0.5), QPointF(cx + 5 + 0.5, cy + 3 + 0.5),
						  QPointF(cx + 3 + 0.5, cy + 3 + 0.5)}));
		} else {
			p.drawRect(QRectF(cx - 5 + 0.5, cy - 5 + 0.5, 9, 9));
		}
		break;
	case Kind::Close: {
		// the cross is drawn from the middle of a device pixel with arms of whole device pixels and flat ends, so each stroke is one lit pixel per row and the cross is symmetric the way the glyph of windows is; from the corner of a pixel, or with the default square ends, the strokes come out two half lit pixels wide with longer tips on one side at 100 percent scale
		const qreal dpr = devicePixelRatioF();
		const qreal mx = (qFloor(cx * dpr) + 0.5) / dpr;
		const qreal my = (qFloor(cy * dpr) + 0.5) / dpr;
		const qreal arm = qRound(5 * dpr) / dpr;
		pen.setCapStyle(Qt::FlatCap);
		p.setPen(pen);
		p.setRenderHint(QPainter::Antialiasing, true);
		p.drawLine(QPointF(mx - arm, my - arm), QPointF(mx + arm, my + arm));
		p.drawLine(QPointF(mx + arm, my - arm), QPointF(mx - arm, my + arm));
		break;
	}
	}
}

} // namespace rk
