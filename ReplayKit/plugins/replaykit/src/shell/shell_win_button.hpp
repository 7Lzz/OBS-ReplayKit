// minimize, maximize and close buttons of the shell title bar
#pragma once

#include <QEnterEvent>
#include <QEvent>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QWidget>

namespace rk {

// min / max / close, drawn by hand since the frameless window has none. close goes through the normal close event so close-to-tray and the exit checks of obs still run
class ShellWinButton : public QWidget {
public:
	enum class Kind { Min, Max, Close };

	ShellWinButton(Kind kind, QWidget *parent);

	void SetMaximized(bool maximized);

protected:
	void enterEvent(QEnterEvent *event) override;
	void leaveEvent(QEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	Kind m_kind;
	bool m_maximized = false;
	bool m_hover = false;
	bool m_down = false;
};

} // namespace rk
