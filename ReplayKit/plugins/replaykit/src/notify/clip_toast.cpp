// the card that slides in at the top left of the screen when a clip is saved or a recording starts or stops
#include "prelude.hpp"
#include "notify/clip_toast.hpp"

#include <optional>
#include <cmath>
#include <limits>

#include <QEasingCurve>
#include <QPainter>
#include <QPainterPath>
#include <QParallelAnimationGroup>
#include <QPropertyAnimation>
#include <QVariantAnimation>
#include <QScreen>

#include "core/app_icon.hpp"
#include "core/json_lite.hpp"
#include "core/locale.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "core/ui_font.hpp"

namespace rk {

namespace {

constexpr int kHeight = 92;
constexpr int kWellWidth = 104;
constexpr int kIconSize = 58;
constexpr int kTextLeft = 30;
constexpr int kTextRight = 40;
constexpr int kMinWidth = 360;
constexpr int kRadius = 16;
constexpr int kTopOffset = 56;
constexpr int kShownMs = 3200;
constexpr int kInMs = 320;
constexpr int kFadeInMs = 140;
constexpr int kOutMs = 220;

// what the helper last sent; nothing is shown until it has answered once
std::optional<bool> g_clipOn;
std::optional<bool> g_recordingOn;
std::optional<bool> g_hideFromCapture;

// "90" -> "1m 30s", "120" -> "2m", "45" -> "45s", the way the rest of the interface writes a length
QString FormatLength(int seconds)
{
	if (seconds < 60)
		return QStringLiteral("%1s").arg(seconds);
	const int minutes = seconds / 60;
	const int rest = seconds % 60;
	return rest == 0 ? QStringLiteral("%1m").arg(minutes) : QStringLiteral("%1m %2s").arg(minutes).arg(rest);
}

QFont ToastFont()
{
	QFont font;
	// windows lists the semibold face of a family like segoe ui as a family of its own, and a family without one falls back to its own weight
	font.setFamilies({UiFontFamily() + QStringLiteral(" Semibold"), UiFontFamily()});
	font.setPixelSize(26);
	font.setWeight(QFont::DemiBold);
	return font;
}

// the window stays where it is placed and only the card inside it moves, since a monitor to the left of the main one sits in the very space the card slides out into; what slides past the left edge of the window is simply not drawn
class ClipToast : public QWidget {
public:
	ClipToast() : QWidget(nullptr, Qt::Tool | Qt::FramelessWindowHint | Qt::WindowStaysOnTopHint | Qt::WindowDoesNotAcceptFocus | Qt::NoDropShadowWindowHint)
	{
		setAttribute(Qt::WA_TranslucentBackground);
		setAttribute(Qt::WA_ShowWithoutActivating);
		setFocusPolicy(Qt::NoFocus);
		m_timer.setSingleShot(true);
		connect(&m_timer, &QTimer::timeout, this, [this]() { Leave(); });
	}

	// false when the card could not be kept out of captures that asked for it, in which case it is not shown at all
	bool Present(const QString &message, bool hideFromCapture)
	{
		m_message = message;
		QScreen *screen = QGuiApplication::primaryScreen();
		if (!screen)
			return false;
		const QRect area = screen->availableGeometry();
		const int textWidth = QFontMetrics(ToastFont()).horizontalAdvance(m_message);
		const int width = qMin(qMax(kMinWidth, kWellWidth + kTextLeft + textWidth + kTextRight), area.width());
		setGeometry(area.left(), area.top() + kTopOffset, width, kHeight);
		m_icon = ShellLogoPixmap(kIconSize, devicePixelRatioF());

		// the display affinity belongs to the window, so it is set before the card is first drawn; 0x11 is WDA_EXCLUDEFROMCAPTURE, the flag obs uses for its own hide from capture option
		const HWND hwnd = reinterpret_cast<HWND>(winId());
		if (!SetWindowDisplayAffinity(hwnd, hideFromCapture ? 0x11 : 0) && hideFromCapture)
			return false;

		m_timer.stop();
		setWindowOpacity(0.0);
		m_slide = -width;
		show();
		raise();

		auto *group = new QParallelAnimationGroup(this);
		auto *fade = new QPropertyAnimation(this, "windowOpacity");
		fade->setDuration(kFadeInMs);
		fade->setStartValue(0.0);
		fade->setEndValue(1.0);
		auto *slide = SlideAnimation();
		slide->setDuration(kInMs);
		slide->setStartValue(qreal(-width));
		slide->setEndValue(0.0);
		QEasingCurve curve(QEasingCurve::OutBack);
		curve.setOvershoot(0.6);
		slide->setEasingCurve(curve);
		group->addAnimation(fade);
		group->addAnimation(slide);
		Run(group);
		m_timer.start(kShownMs - kOutMs);
		return true;
	}

	void Leave()
	{
		m_timer.stop();
		auto *group = new QParallelAnimationGroup(this);
		auto *fade = new QPropertyAnimation(this, "windowOpacity");
		fade->setDuration(kOutMs);
		fade->setStartValue(windowOpacity());
		fade->setEndValue(0.0);
		auto *slide = SlideAnimation();
		slide->setDuration(kOutMs);
		slide->setStartValue(m_slide);
		slide->setEndValue(qreal(-width()));
		slide->setEasingCurve(QEasingCurve::InCubic);
		group->addAnimation(fade);
		group->addAnimation(slide);
		connect(group, &QAbstractAnimation::finished, this, [this]() { hide(); });
		Run(group);
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.setRenderHint(QPainter::Antialiasing);
		p.translate(m_slide, 0);
		const QRectF card(0, 0, width(), height());

		// square on the left, where the card meets the edge of the screen, and rounded on the right
		QPainterPath outline;
		outline.moveTo(card.left(), card.top());
		outline.lineTo(card.right() - kRadius, card.top());
		outline.arcTo(QRectF(card.right() - 2 * kRadius, card.top(), 2 * kRadius, 2 * kRadius), 90, -90);
		outline.lineTo(card.right(), card.bottom() - kRadius);
		outline.arcTo(QRectF(card.right() - 2 * kRadius, card.bottom() - 2 * kRadius, 2 * kRadius, 2 * kRadius), 0, -90);
		outline.lineTo(card.left(), card.bottom());
		outline.closeSubpath();

		QColor panel = g_menuPanel;
		panel.setAlpha(0xE6);
		p.fillPath(outline, panel);
		QColor well = g_notifyTipBg;
		well.setAlpha(0xE0);
		p.save();
		p.setClipPath(outline);
		p.fillRect(QRectF(card.left(), card.top(), kWellWidth, card.height()), well);
		p.restore();

		// the border runs along the top, the right and the bottom; the left side is the edge of the screen
		QPainterPath edge;
		edge.moveTo(card.left(), card.top() + 0.5);
		edge.lineTo(card.right() - kRadius, card.top() + 0.5);
		edge.arcTo(QRectF(card.right() - 2 * kRadius - 0.5, card.top() + 0.5, 2 * kRadius, 2 * kRadius), 90, -90);
		edge.lineTo(card.right() - 0.5, card.bottom() - kRadius);
		edge.arcTo(QRectF(card.right() - 2 * kRadius - 0.5, card.bottom() - 2 * kRadius - 0.5, 2 * kRadius, 2 * kRadius), 0, -90);
		edge.lineTo(card.left(), card.bottom() - 0.5);
		p.setPen(QPen(g_notifyBorder, 1.0));
		p.drawPath(edge);

		if (!m_icon.isNull()) {
			const QRectF icon((kWellWidth - kIconSize) / 2.0, (kHeight - kIconSize) / 2.0, kIconSize, kIconSize);
			p.setRenderHint(QPainter::SmoothPixmapTransform);
			p.drawPixmap(icon, m_icon, QRectF(m_icon.rect()));
		}
		p.setFont(ToastFont());
		p.setPen(g_notifyTipFg);
		p.drawText(QRectF(kWellWidth + kTextLeft, 0, card.width() - kWellWidth - kTextLeft, card.height()), Qt::AlignVCenter | Qt::AlignLeft, m_message);
	}

	void mouseReleaseEvent(QMouseEvent *event) override
	{
		if (event->button() == Qt::LeftButton)
			Leave();
	}

private:
	// moves the card inside the window; the window itself never moves
	QVariantAnimation *SlideAnimation()
	{
		auto *slide = new QVariantAnimation();
		connect(slide, &QVariantAnimation::valueChanged, this, [this](const QVariant &value) {
			m_slide = value.toReal();
			update();
		});
		return slide;
	}

	void Run(QParallelAnimationGroup *group)
	{
		if (m_group)
			m_group->stop();
		m_group = group;
		group->start(QAbstractAnimation::DeleteWhenStopped);
	}

	QString m_message;
	QPixmap m_icon;
	qreal m_slide = 0;
	QTimer m_timer;
	QPointer<QParallelAnimationGroup> m_group;
};

QPointer<ClipToast> g_toast;

void ShowNow(ClipToastKind kind, double duration)
{
	const bool clip = kind == ClipToastKind::Clip;
	const std::optional<bool> on = clip ? g_clipOn : g_recordingOn;
	if (!on || !*on || !g_hideFromCapture)
		return;
	QString message;
	if (clip) {
		if (std::isfinite(duration) && duration >= 1 && duration <= (std::numeric_limits<int>::max)())
			message = TrMessage("capture.clipSaved", {{"length", FormatLength(int(std::round(duration)))}});
		else if (duration > 0 && duration < 1)
			message = TrMessage("capture.clipSavedUnderSecond");
		else
			message = TrMessage("capture.clipSavedUnknown");
	} else {
		message = kind == ClipToastKind::RecordingStarted ? TrMessage("capture.recordingStarted") : TrMessage("capture.recordingStopped");
	}
	if (!g_toast)
		g_toast = new ClipToast();
	if (!g_toast->Present(message, *g_hideFromCapture))
		g_toast->hide();
}

} // namespace

void ClipToastShow(ClipToastKind kind, double duration)
{
	if (!g_callbacks)
		return;
	QMetaObject::invokeMethod(g_callbacks, [kind, duration]() { ShowNow(kind, duration); }, Qt::QueuedConnection);
}

bool ClipToastEnabled()
{
	return g_clipOn.value_or(false) && g_hideFromCapture.has_value();
}

void ClipToastApplySettings(const std::string &settingsBody)
{
	if (const auto value = JsonBoolIfPresent(settingsBody, "clipNotificationEnabled"))
		g_clipOn = *value;
	if (const auto value = JsonBoolIfPresent(settingsBody, "recordingNotificationEnabled"))
		g_recordingOn = *value;
	if (const auto value = JsonBoolIfPresent(settingsBody, "hideNotificationsFromCapture"))
		g_hideFromCapture = *value;
}

QWidget *ClipToastWidget()
{
	return g_toast && g_toast->isVisible() ? g_toast.data() : nullptr;
}

void ClipToastDestroy()
{
	delete g_toast.data();
}

} // namespace rk

