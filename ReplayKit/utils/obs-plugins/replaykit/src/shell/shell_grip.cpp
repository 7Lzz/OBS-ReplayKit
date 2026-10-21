// resize corner of the clean shell
#include "prelude.hpp"
#include "shell/shell_grip.hpp"

#include "core/main_window.hpp"
#include "core/theme.hpp"
#include "shell/shell_types.hpp"

namespace rk {

ShellGrip::ShellGrip(QWidget *parent)
	: QWidget(parent)
{
	setAttribute(Qt::WA_NativeWindow, true);
	setFixedSize(kShellGrip, kShellGrip);
	setCursor(Qt::SizeFDiagCursor);
	// a child window cannot be translucent, so the corner that faces the stage is rounded off with a window region instead
	const double pi = 3.14159265358979323846;
	const int r = 8;
	QPolygon shape;
	for (int i = 0; i <= 6; ++i) {
		const double a = pi + (pi / 2.0) * i / 6.0;
		shape << QPoint(qRound(r + r * std::cos(a)), qRound(r + r * std::sin(a)));
	}
	shape << QPoint(kShellGrip, 0) << QPoint(kShellGrip, kShellGrip) << QPoint(0, kShellGrip);
	setMask(QRegion(shape));
}

void ShellGrip::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton)
		return;
	if (QWindow *handle = window()->windowHandle())
		handle->startSystemResize(Qt::RightEdge | Qt::BottomEdge);
	event->accept();
}

void ShellGrip::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.fillRect(rect(), g_chromeBg);
	p.setRenderHint(QPainter::Antialiasing, true);
	p.setPen(Qt::NoPen);
	p.setBrush(ChromeInk(0.55));
	// rows from the bottom edge, three dots then two then one, all flush to the right: the same triangle the pages used to draw
	for (int row = 0; row < 3; ++row)
		for (int col = 0; col < 3 - row; ++col)
			p.drawEllipse(QPointF(kShellGrip - 5.0 - col * 4.0, kShellGrip - 5.0 - row * 4.0), 1.15, 1.15);
}

void ShellStartEdgeResize(Qt::Edges edges)
{
	QMainWindow *mw = ObsMainWindowQt();
	if (!mw || mw->isMaximized() || mw->isFullScreen() || !mw->windowHandle())
		return;
	if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
		return;
	mw->windowHandle()->startSystemResize(edges);
}

QPolygon ShellCornerEar()
{
	const double pi = 3.14159265358979323846;
	const int r = kShellStageR;
	QPolygon shape;
	shape << QPoint(0, 0) << QPoint(r, 0);
	for (int i = 1; i <= 8; ++i) {
		const double a = pi * 1.5 - (pi / 2.0) * i / 8.0;
		shape << QPoint(qRound(r + r * std::cos(a)), qRound(r + r * std::sin(a)));
	}
	return shape;
}

ShellCorner::ShellCorner(QWidget *parent)
	: QWidget(parent)
{
	setAttribute(Qt::WA_NativeWindow, true);
	setFixedSize(kShellStageR, kShellStageR);
	// a child window cannot be translucent, so the piece that stays is cut with a window region
	setMask(QRegion(ShellCornerEar()));
}

void ShellCorner::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.fillRect(rect(), g_chromeBar);
}

} // namespace rk
