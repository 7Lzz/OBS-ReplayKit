// icon buttons of the title bar
#pragma once

#include <QEasingCurve>
#include <QEnterEvent>
#include <QEvent>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QPainter>
#include <QRect>
#include <QRectF>
#include <QString>
#include <QTimer>
#include <QVariantAnimation>
#include <QWidget>
#include <functional>

namespace rk {

// a button of the title bar drawn with the hover of the bell: a wash of the menu surface that fades in over 90ms, and a tab shape while the panel it opens is up. what sits inside the pill is up to the subclass
class ShellTitleButton : public QWidget {
public:
	static constexpr int kHeight = 28;
	static constexpr int kRadius = 6;

	ShellTitleButton(int width, QWidget *parent);

	void SetTip(const QString &tip);
	// the panel of this button is open, so the pill is the tab of that panel
	void SetOpen(bool open);
	// turns the arrow back while the tab stays, for a panel that has started to roll up
	void TurnClosed();
	bool IsOpen() const { return m_open; }
	// 0 closed to 1 open, eased over the same 200ms as the chevron of the clips sort trigger
	qreal OpenTurn() const { return m_turn; }
	QRect GlobalRect() const { return QRect(mapToGlobal(QPoint(0, 0)), size()); }
	// runs on the press, the way the bell opens its panel
	std::function<void()> onPress;

protected:
	void enterEvent(QEnterEvent *event) override;
	void leaveEvent(QEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

	// pill is the rectangle of the button inset by half a pixel for the hairline; hover is the fade of the wash, 0 to 1
	virtual void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) = 0;
	bool Hovered() const { return m_hover; }
	// the pointer is taken to have left, for a button that stopped taking it while it was on it
	void ClearHover();

private:
	void StartFade();

	QString m_tip;
	bool m_hover = false;
	bool m_open = false;
	qreal m_fade = 0.0;
	QTimer *m_fadeTimer = nullptr;
	qreal m_turn = 0.0;
	QVariantAnimation *m_turnAnim = nullptr;
};

// the --ease_smooth curve of the pages, cubic-bezier(.16, 1, .3, 1)
QEasingCurve SmoothEase();
// turns value from where it stands to target over the 200ms of --anim_menu, repainting widget on every step
void AnimateTurn(QWidget *widget, QVariantAnimation *&anim, qreal &value, qreal target);

} // namespace rk
