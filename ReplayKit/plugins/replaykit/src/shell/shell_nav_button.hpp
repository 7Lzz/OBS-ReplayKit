// the back and forward arrows of the title bar
#pragma once

#include <QEnterEvent>
#include <QMouseEvent>
#include <QPainter>
#include <QRectF>
#include <QSvgRenderer>

#include "shell/shell_title_button.hpp"

namespace rk {

// an arrow in the pill the other buttons of the title bar have. with nowhere to go it is dimmed, takes no hover and shows no tip, like the arrow of a browser that has no history
class ShellNavButton : public ShellTitleButton {
public:
	enum class Kind { Back, Forward };
	static constexpr int kWidth = 34;

	ShellNavButton(Kind kind, QWidget *parent);
	void SetAvailable(bool available);
	bool Available() const { return m_available; }

protected:
	void enterEvent(QEnterEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) override;

private:
	bool m_available = false;
	QSvgRenderer *m_svg = nullptr;
};

} // namespace rk
