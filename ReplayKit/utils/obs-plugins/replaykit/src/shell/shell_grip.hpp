// resize corner of the clean shell
#pragma once

#include <QMouseEvent>
#include <QPaintEvent>
#include <QPolygon>
#include <QWidget>

namespace rk {

// the resize corner, six dots over the bottom right of whatever view is showing. a native child window so it can sit above the preview and the browser (both native windows), and it starts the system resize itself because those same native windows would swallow a hit-test answer for this corner
class ShellGrip : public QWidget {
public:
	explicit ShellGrip(QWidget *parent);

protected:
	void mousePressEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;
};

// a page of the shell grabbed at its right edge, bottom edge or the corner between them: the window is resized by the resize loop of windows itself, as its own frame would be, which repaints as it goes. nothing happens once the button is already up, or for a maximized or fullscreen window
void ShellStartEdgeResize(Qt::Edges edges);

// the sliver between the square top left corner of the stage and its arc, as the polygon the corner window is cut to and the dip is kept clear of
QPolygon ShellCornerEar();

// the rounded top left corner of the stage. the stage is a native window, so what lies outside the curve is a small native window of its own in the colour of the title bar and the rail, cut to the sliver between the square corner and the arc
class ShellCorner : public QWidget {
public:
	explicit ShellCorner(QWidget *parent);

protected:
	void paintEvent(QPaintEvent *) override;
};

} // namespace rk
