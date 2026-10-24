// native twin of the css tooltip
#pragma once

#include <QElapsedTimer>
#include <QFont>
#include <QPaintEvent>
#include <QPainter>
#include <QPixmap>
#include <QRect>
#include <QString>
#include <QTimer>
#include <QWidget>
#include <QtGlobal>

namespace rk {

// native twin of the shared _tooltip partial (#rk-tip), for what the plugin draws itself and has no page behind: the title bar, the rail and the list of the obs settings, so the css cannot be reused -- every number here mirrors that file and has to move with it, the ground and the hairline included. click-through, never focused
class NotifyTip : public QWidget {
public:
	NotifyTip();

	enum class Side { Below, Right };

	// anchor is the control in global coords. mirrors positionBulkTip: 8px below it, arrow on the controls centre clamped 10px inside the tip, and the whole box kept 6px clear of the screen edge. Side::Right is for the icon rail: the tip sits beside the control with the arrow on its left edge
	// within, when given, is the window the tip stays inside, besides the screen
	void ShowFor(const QString &text, const QRect &anchorGlobal, Side side = Side::Below, const QRect &within = QRect());

	// the tip goes the way the page tip does when the pointer leaves it, fading and settling back over the same 100 and 130 ms; hide() takes it off at once, for when something else opens in its place
	void Leave();

protected:
	void paintEvent(QPaintEvent *) override;

private:
	static constexpr int kPadX = 8;
	static constexpr int kPadY = 5;
	static constexpr int kRadius = 3;
	static constexpr int kArrow = 5;
	static constexpr int kGap = 8;
	static constexpr int kEdge = 6;
	static constexpr int kShadow = 10;
	static constexpr int kPop = 130;   // --anim_press
	static constexpr int kFade = 100;  // opacity 100ms linear
	// mirrors #rk-tip in _tooltip.html: 11px, weight 500, which segoe ui resolves to its semibold face
	static constexpr QFont::Weight kTipWeight = QFont::DemiBold;
	// hairline that brings the outline fill to the ink the css text has in the browser, taken from captures of the two side by side
	static constexpr qreal kTipStroke = 0.25;
	static QFont TipFont();

	// the tip at rest: shadow rings, ground, arrow and text, with no animation transform
	void PaintSettled(QPainter &p);

	// runs the pop and the opacity from where they are to shown (1) or gone (0), the way a css transition runs from the current value, so a tip that is asked for again while it leaves comes back from where it was; a leave hides the window when it is done
	void Animate(qreal to);

	QString m_text;
	Side m_side = Side::Below;
	qreal m_arrowX = 0.0;
	qreal m_arrowY = 0.0;
	qreal m_pop = 1.0;
	qreal m_popFrom = 1.0;
	qreal m_popTo = 1.0;
	qreal m_opacityFrom = 1.0;
	QPixmap m_settled;
	QTimer *m_anim = nullptr;
	QElapsedTimer m_since;
};

extern NotifyTip *g_notifyTip;

} // namespace rk
