// the strip along the top of the projector view
#include "prelude.hpp"
#include "shell/shell_projector_bar.hpp"

#include <QEasingCurve>
#include <QVariantAnimation>

#include "core/draw_util.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_projector.hpp"
#include "shell/shell_title_button.hpp"
#include "shell/shell_types.hpp"

namespace rk {

namespace {

constexpr int kButtonH = 32;
// the tab bar of the clips view pads its row by 14 px at the sides
constexpr int kPad = 14;
constexpr int kGap = 8;
constexpr int kIconSide = 18;
// the same switch as the ones in the settings: 40 by 22 with a 16 px knob 3 px in from the edge
constexpr int kSwitchW = 40;
constexpr int kSwitchH = 22;
constexpr int kSlideMs = 300;
// the track takes its colour more slowly than the knob moves, and the hover and the press are the 130 ms of every button
constexpr int kTintMs = 220;
constexpr int kGrowMs = 130;

// glyphs stroked like the icons of the rail; the colour is put on when they are drawn
const char kEyeSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#000" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12z"/><circle cx="12" cy="12" r="3.1"/></svg>)SVG";
const char kEyeOffSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#000" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M9.9 5.7A9.6 9.6 0 0 1 12 5.5C18 5.5 21.5 12 21.5 12a17 17 0 0 1-2.6 3.5"/><path d="M6.6 6.9A17 17 0 0 0 2.5 12S6 18.5 12 18.5a9.5 9.5 0 0 0 4.5-1.1"/><path d="M9.9 9.9a3.1 3.1 0 0 0 4.2 4.2"/><path d="M3 3l18 18"/></svg>)SVG";
const char kLockSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#000" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="4.5" y="10.5" width="15" height="10" rx="2.2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/><path d="M12 14.6v2.2"/></svg>)SVG";
const char kUnlockedSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#000" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="4.5" y="10.5" width="15" height="10" rx="2.2"/><path d="M8 10.5V7.5a4 4 0 0 1 7.6-1.7"/><path d="M12 14.6v2.2"/></svg>)SVG";

QFont LabelFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(13);
	f.setWeight(QFont::DemiBold);
	f.setHintingPreference(QFont::PreferNoHinting);
	return f;
}

QColor Blend(const QColor &a, const QColor &b, qreal t)
{
	return QColor::fromRgbF(a.redF() + (b.redF() - a.redF()) * t, a.greenF() + (b.greenF() - a.greenF()) * t, a.blueF() + (b.blueF() - a.blueF()) * t);
}

// colours blended with their alpha, which the plain blend above leaves out
QColor BlendAlpha(const QColor &a, const QColor &b, qreal t)
{
	QColor c;
	c.setRgbF(a.redF() + (b.redF() - a.redF()) * t, a.greenF() + (b.greenF() - a.greenF()) * t, a.blueF() + (b.blueF() - a.blueF()) * t, a.alphaF() + (b.alphaF() - a.alphaF()) * t);
	return c;
}

// the knob goes over with a small overshoot, the same curve as the knob of the settings
QEasingCurve SlideCurve()
{
	QEasingCurve curve(QEasingCurve::BezierSpline);
	curve.addCubicBezierSegment(QPointF(0.34, 1.45), QPointF(0.5, 1.0), QPointF(1.0, 1.0));
	return curve;
}

// css ease, which the colour of the track of a switch in the settings goes by
QEasingCurve TintCurve()
{
	QEasingCurve curve(QEasingCurve::BezierSpline);
	curve.addCubicBezierSegment(QPointF(0.25, 0.1), QPointF(0.25, 1.0), QPointF(1.0, 1.0));
	return curve;
}

// ease_pop, the curve of the hover and the press of every button
QEasingCurve PopCurve()
{
	QEasingCurve curve(QEasingCurve::BezierSpline);
	curve.addCubicBezierSegment(QPointF(0.2, 0.7), QPointF(0.3, 1.0), QPointF(1.0, 1.0));
	return curve;
}

} // namespace

// one switch of the strip: an icon, a word and, for the preview, a small switch that shows the state at a glance. the hover wash and the tip are those of the title bar buttons
class ShellBarToggle : public ShellTitleButton {
public:
	enum class Kind { Preview, Lock };

	ShellBarToggle(Kind kind, QWidget *parent) : ShellTitleButton(WidthFor(kind), parent), m_kind(kind)
	{
		setFixedHeight(kButtonH);
		// the switch of the preview moves the way the switches of the pages do: the knob slides with a small overshoot, the track takes the accent a little slower, and the whole switch grows under the pointer and shrinks under the press with the knob stretching
		Wire(m_slide, m_pos, SlideCurve(), kSlideMs);
		Wire(m_tintAnim, m_tint, TintCurve(), kTintMs);
		Wire(m_growAnim, m_grow, PopCurve(), kGrowMs);
		Wire(m_pressAnim, m_press, PopCurve(), kGrowMs);
		UpdateTip();
	}

	// on is the preview showing or the scene locked; held is studio mode keeping the preview on, which the switch cannot change
	void SetState(bool on, bool held)
	{
		const bool first = !m_seen;
		m_seen = true;
		if (on == m_on && held == m_held)
			return;
		const bool moved = on != m_on;
		m_on = on;
		m_held = held;
		UpdateTip();
		if (moved && m_kind == Kind::Preview)
			Slide(on, first);
		update();
	}

	bool On() const { return m_on; }
	bool Held() const { return m_held; }

protected:
	void enterEvent(QEnterEvent *event) override
	{
		ShellTitleButton::enterEvent(event);
		if (m_kind == Kind::Preview)
			Run(m_growAnim, m_grow, 1.0, kGrowMs);
	}

	void leaveEvent(QEvent *event) override
	{
		ShellTitleButton::leaveEvent(event);
		if (m_kind == Kind::Preview)
			Run(m_growAnim, m_grow, 0.0, kGrowMs);
	}

	// the switch of the preview works on the release of the button, the way a switch of a page does, and shows the press while it is held; the lock keeps the press of the other buttons of the strip
	void mousePressEvent(QMouseEvent *event) override
	{
		if (m_kind != Kind::Preview) {
			ShellTitleButton::mousePressEvent(event);
			return;
		}
		if (event->button() != Qt::LeftButton)
			return;
		if (g_notifyTip)
			g_notifyTip->hide();
		m_down = true;
		Run(m_pressAnim, m_press, 1.0, kGrowMs);
		event->accept();
	}

	void mouseReleaseEvent(QMouseEvent *event) override
	{
		if (m_kind != Kind::Preview || !m_down) {
			QWidget::mouseReleaseEvent(event);
			return;
		}
		m_down = false;
		Run(m_pressAnim, m_press, 0.0, kGrowMs);
		event->accept();
		if (event->button() == Qt::LeftButton && rect().contains(event->position().toPoint()) && onPress)
			onPress();
	}

	void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) override
	{
		static QSvgRenderer eye{QByteArray::fromRawData(kEyeSvg, qsizetype(sizeof(kEyeSvg) - 1))};
		static QSvgRenderer eyeOff{QByteArray::fromRawData(kEyeOffSvg, qsizetype(sizeof(kEyeOffSvg) - 1))};
		static QSvgRenderer locked{QByteArray::fromRawData(kLockSvg, qsizetype(sizeof(kLockSvg) - 1))};
		static QSvgRenderer unlocked{QByteArray::fromRawData(kUnlockedSvg, qsizetype(sizeof(kUnlockedSvg) - 1))};

		const bool preview = m_kind == Kind::Preview;
		// what is switched on reads brighter than what is off, and the lock takes the accent while it holds, since that is the state that has to be noticed
		QColor ink = m_on ? (preview ? g_notifyTipFg : g_menuAccentLight) : g_menuMuted;
		ink = Blend(ink, g_notifyTipFg, hover * 0.45);
		if (m_held)
			p.setOpacity(0.5);
		QSvgRenderer &glyph = preview ? (m_on ? eye : eyeOff) : (m_on ? locked : unlocked);
		const QPointF iconAt(pill.left() + 11, pill.center().y() - kIconSide / 2.0);
		p.drawPixmap(iconAt, TintedSvgPixmap(glyph, QSizeF(kIconSide, kIconSide), devicePixelRatioF(), ink));
		const QRectF textBox(iconAt.x() + kIconSide + 8, pill.top(), TextWidth(Label(preview)) + 2, pill.height());
		DrawPageText(p, textBox, Qt::AlignLeft | Qt::AlignVCenter, Label(preview), LabelFont(), ink, 0.25);
		if (preview) {
			const QRectF track(pill.right() - 12 - kSwitchW, pill.center().y() - kSwitchH / 2.0, kSwitchW, kSwitchH);
			// 1.04 under the pointer and .96 under the press, about the middle of the switch
			const qreal scale = (1.0 + 0.04 * m_grow) * (1.0 - m_press) + 0.96 * m_press;
			p.save();
			p.translate(track.center());
			p.scale(scale, scale);
			p.translate(-track.center());
			const qreal tint = qBound(0.0, m_tint, 1.0);
			const qreal here = qBound(0.0, m_pos, 1.0);
			const QColor lit = g_menuAccent;
			p.setPen(Qt::NoPen);
			p.setBrush(BlendAlpha(ChromeInk(0.20 + 0.10 * m_grow), BlendAlpha(lit, lit.lighter(112), m_grow), tint));
			p.drawRoundedRect(track, kSwitchH / 2.0, kSwitchH / 2.0);
			// the knob stretches under the press, and on a switch that is on it stretches back toward the middle so it stays inside
			const qreal height = kSwitchH - 6.0;
			const qreal knob = height + 5.0 * m_press;
			const qreal x = track.left() + 3.0 + (kSwitchW - kSwitchH) * m_pos - 5.0 * m_press * here;
			p.setBrush(QColor(0, 0, 0, 56));
			p.drawRoundedRect(QRectF(x, track.top() + 4.0, knob, height), height / 2.0, height / 2.0);
			p.setBrush(BlendAlpha(ChromeInk(0.82), g_menuOnAccent, tint));
			p.drawRoundedRect(QRectF(x, track.top() + 3.0, knob, height), height / 2.0, height / 2.0);
			p.restore();
		}
		p.setOpacity(1.0);
	}

private:
	// the lock says Locked while it holds and Lock while it does not, and the wider word is what the button is sized for so it never changes width
	QString Label(bool preview) const
	{
		return preview ? QStringLiteral("Preview") : (m_on ? QStringLiteral("Locked") : QStringLiteral("Lock"));
	}

	static qreal TextWidth(const QString &text) { return QFontMetricsF(LabelFont()).horizontalAdvance(text); }

	static int WidthFor(Kind kind)
	{
		const qreal text = TextWidth(kind == Kind::Preview ? QStringLiteral("Preview") : QStringLiteral("Locked"));
		return qCeil(11 + kIconSide + 8 + text + 12 + (kind == Kind::Preview ? kSwitchW + 12 : 0));
	}

	// one of the 0 to 1 values the switch is drawn from follows its animation, and the switch is drawn again with every step
	void Wire(QVariantAnimation &anim, qreal &value, const QEasingCurve &curve, int ms)
	{
		anim.setDuration(ms);
		anim.setEasingCurve(curve);
		connect(&anim, &QVariantAnimation::valueChanged, this, [this, &value](const QVariant &v) {
			value = v.toReal();
			update();
		});
	}

	// from where the value is now to where it is going
	static void Run(QVariantAnimation &anim, qreal from, qreal to, int ms)
	{
		anim.stop();
		anim.setDuration(ms);
		anim.setStartValue(from);
		anim.setEndValue(to);
		anim.start();
	}

	// the knob slides to the new state; a switch that is not on screen, or that is only being given its first state, has nothing to animate and goes there at once
	void Slide(bool on, bool first)
	{
		const qreal to = on ? 1.0 : 0.0;
		m_slide.stop();
		m_tintAnim.stop();
		if (first || !isVisible()) {
			m_pos = to;
			m_tint = to;
			return;
		}
		Run(m_slide, m_pos, to, kSlideMs);
		Run(m_tintAnim, m_tint, to, kTintMs);
	}

	void UpdateTip()
	{
		if (m_kind == Kind::Preview)
			SetTip(m_held ? QStringLiteral("Studio Mode keeps the preview on") : m_on ? QStringLiteral("Turn the preview off") : QStringLiteral("Turn the preview on"));
		else
			SetTip(m_on ? QStringLiteral("Unlock the preview") : QStringLiteral("Lock the preview so the scene and the overlay cannot be moved by accident"));
	}

	Kind m_kind;
	bool m_on = false;
	bool m_held = false;
	bool m_seen = false;
	bool m_down = false;
	// where the knob is, 0 off to 1 on, a little past either end during the overshoot; how far the track has taken the accent; how far the pointer is over the switch; how far it is pressed
	qreal m_pos = 0.0;
	qreal m_tint = 0.0;
	qreal m_grow = 0.0;
	qreal m_press = 0.0;
	QVariantAnimation m_slide;
	QVariantAnimation m_tintAnim;
	QVariantAnimation m_growAnim;
	QVariantAnimation m_pressAnim;
};

ShellProjectorBar::ShellProjectorBar(QWidget *parent) : QWidget(parent)
{
	m_preview = new ShellBarToggle(ShellBarToggle::Kind::Preview, this);
	m_preview->onPress = [this]() {
		if (ShellStudioModeOn())
			return;
		// the switch answers the click at once and the stage follows through its dip
		const bool target = !ShellPreviewTarget();
		m_preview->SetState(target, false);
		ShellProjectorSetPreview(target);
	};
	m_lock = new ShellBarToggle(ShellBarToggle::Kind::Lock, this);
	m_lock->onPress = []() { ShellProjectorSetLocked(!ShellSceneLocked()); };
	Place();
}

void ShellProjectorBar::SetState(bool preview, bool held, bool locked)
{
	m_preview->SetState(preview, held);
	m_lock->SetState(locked, false);
}

void ShellProjectorBar::Place()
{
	// the row sits in the height above the hairline, the way the tab group of the clips bar sits in its own
	const int y = (height() - 1 - kButtonH) / 2;
	m_preview->move(kPad, y);
	m_lock->move(m_preview->x() + m_preview->width() + kGap, y);
}

void ShellProjectorBar::resizeEvent(QResizeEvent *event)
{
	QWidget::resizeEvent(event);
	Place();
}

void ShellProjectorBar::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.fillRect(rect(), g_chromeBg);
	p.fillRect(QRect(0, height() - 1, width(), 1), g_notifyBorder);
}

} // namespace rk
