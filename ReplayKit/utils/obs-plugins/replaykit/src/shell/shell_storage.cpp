// storage figure above the settings button of the rail
#include "prelude.hpp"
#include "shell/shell_storage.hpp"

#include "core/async.hpp"
#include "core/draw_util.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage_modal.hpp"

namespace rk {

namespace {

bool g_pollInFlight = false;

constexpr qreal kRingD = 26.0;
constexpr qreal kRingTop = 5.0;
constexpr qreal kRingStroke = 3.0;
// the hairlines that bring the outline fill of small text to the ink the css text has in the browser, the same idea as the labels of the native list
constexpr qreal kTextStroke = 0.2;

// the colour of the ring for how full it is: the green of the live status light while there is room, the amber of the input sensitivity meter toward the end, and a bright red at the end
QColor UsageColour(qreal fraction)
{
	const QColor green = g_chromeSuccess;
	const QColor amber = g_chromeWarning;
	const QColor red = g_menuDanger.lighter(115);
	const auto mix = [](const QColor &a, const QColor &b, qreal t) {
		return QColor::fromRgbF(a.redF() + (b.redF() - a.redF()) * t, a.greenF() + (b.greenF() - a.greenF()) * t, a.blueF() + (b.blueF() - a.blueF()) * t);
	};
	const qreal f = qBound(0.0, fraction, 1.0);
	if (f <= 0.45)
		return green;
	if (f <= 0.75)
		return mix(green, amber, (f - 0.45) / 0.30);
	return mix(amber, red, (f - 0.75) / 0.25);
}

QFont UsedFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setWeight(QFont::DemiBold);
	return f;
}

QFont CompressFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(10);
	f.setWeight(QFont::Medium);
	return f;
}

QFont LimitFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(10);
	f.setWeight(QFont::Medium);
	f.setUnderline(true);
	return f;
}

} // namespace

ShellStorage::ShellStorage(QWidget *parent) : QWidget(parent)
{
	setMouseTracking(true);
	setCursor(Qt::PointingHandCursor);
	setFixedHeight(kHeight);
}

void ShellStorage::SetInfo(const Info &info)
{
	if (info.known == m_info.known && info.used == m_info.used && info.limit == m_info.limit && info.state == m_info.state &&
	    info.compressing == m_info.compressing && info.compressPercent == m_info.compressPercent && info.compressText == m_info.compressText &&
	    qFuzzyCompare(info.fraction + 1.0, m_info.fraction + 1.0))
		return;
	m_info = info;
	update();
}

void ShellStorage::enterEvent(QEnterEvent *event)
{
	QWidget::enterEvent(event);
	m_hover = true;
	update();
	// the card behind it is built now, so it is there when the click comes
	StorageModalPrepare();
	if (!g_notifyTip)
		g_notifyTip = new NotifyTip();
	g_notifyTip->ShowFor(m_info.compressing ? QStringLiteral("Compressing clips %1, %2%").arg(m_info.compressText).arg(m_info.compressPercent) : QStringLiteral("Storage"),
			     QRect(mapToGlobal(QPoint(0, 0)), size()), NotifyTip::Side::Right);
}

void ShellStorage::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	m_hover = false;
	update();
	if (g_notifyTip)
		g_notifyTip->Leave();
}

void ShellStorage::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton)
		return;
	m_down = true;
	update();
	if (g_notifyTip)
		g_notifyTip->hide();
}

void ShellStorage::mouseReleaseEvent(QMouseEvent *event)
{
	const bool fire = m_down && event->button() == Qt::LeftButton && rect().contains(event->position().toPoint());
	m_down = false;
	update();
	if (fire)
		StorageModalOpen();
}

void ShellStorage::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	if (!m_info.known)
		return;
	if (!isEnabled())
		p.setOpacity(0.4);

	const bool busy = m_info.compressing;
	// full is the library at its limit or the drive nearly out of room, the only time the size under the ring takes the red as well; the ring itself shows how full it is all the way
	const bool full = !busy && (m_info.state == QLatin1String("over") || m_info.state == QLatin1String("low") || m_info.fraction >= 0.995);
	if (m_hover || m_down) {
		p.setPen(Qt::NoPen);
		p.setBrush(ChromeInk(m_down ? 0.12 : 0.07));
		p.drawRoundedRect(QRectF(rect()).adjusted(4, 1, -4, -1), 10, 10);
	}

	// the ring: a track all the way round and the share of the library on top of it, from twelve o clock clockwise
	const QRectF ring(width() / 2.0 - kRingD / 2.0, kRingTop, kRingD, kRingD);
	const QRectF arcBox = ring.adjusted(kRingStroke / 2.0, kRingStroke / 2.0, -kRingStroke / 2.0, -kRingStroke / 2.0);
	QPen pen(ChromeInk(0.16), kRingStroke);
	pen.setCapStyle(Qt::RoundCap);
	p.setPen(pen);
	p.setBrush(Qt::NoBrush);
	p.drawEllipse(arcBox);
	const qreal shown = busy ? m_info.compressPercent / 100.0 : m_info.fraction;
	if (shown > 0.0) {
		// a library that exists is never thinner than a dot, so the ring does not look empty next to a huge drive
		const qreal span = qMax(0.035, qMin(1.0, shown)) * 360.0;
		QColor usage = UsageColour(full ? 1.0 : m_info.fraction);
		if (m_hover || m_down)
			usage = usage.lighter(112);
		pen.setColor(busy ? g_menuAccentLight : usage);
		p.setPen(pen);
		p.drawArc(arcBox, 90 * 16, int(-span * 16.0));
	}

	const qreal ink = m_hover || m_down ? 1.0 : 0.66;
	const QRectF usedBox(0, kRingTop + kRingD + 5, width(), 15);
	DrawPageText(p, usedBox, Qt::AlignHCenter | Qt::AlignVCenter, busy ? QStringLiteral("%1%").arg(m_info.compressPercent) : m_info.used, UsedFont(), busy ? QColor(g_menuAccentLight) : full ? g_menuDanger : ChromeInk(ink), kTextStroke);
	const QRectF limitBox(0, usedBox.bottom() - 1, width(), 14);
	DrawPageText(p, limitBox, Qt::AlignHCenter | Qt::AlignVCenter, busy ? m_info.compressText : m_info.limit, busy ? CompressFont() : LimitFont(), ChromeInk(ink * 0.85), kTextStroke);
}

void ShellStoragePoll()
{
	if (g_pollInFlight)
		return;
	g_pollInFlight = true;
	RunAsync([]() {
		const std::string body = HttpRequest("GET", "/storage/summary", 8767, nullptr, 4000);
		ShellStorage::Info info;
		info.known = JsonBoolField(body, "ok", false);
		if (info.known) {
			info.fraction = JsonDoubleField(body, "fraction", 0.0);
			info.used = QString::fromStdString(ExtractJsonStringField(body, "usedText"));
			info.limit = QString::fromStdString(ExtractJsonStringField(body, "limitText"));
			info.state = QString::fromStdString(ExtractJsonStringField(body, "state"));
			info.compressing = JsonBoolField(body, "compressing", false);
			info.compressPercent = JsonIntField(body, "compressPercent", 0);
			info.compressText = QString::fromStdString(ExtractJsonStringField(body, "compressText"));
		}
		QMetaObject::invokeMethod(
			g_callbacks,
			[info]() {
				g_pollInFlight = false;
				// the helper down or answering something else keeps the last figure on screen
				if (!info.known || !g_shellRail)
					return;
				g_shellRail->SetStorage(info);
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
