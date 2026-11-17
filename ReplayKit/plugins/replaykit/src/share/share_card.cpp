// the share window card drawn from a helper request
#include "prelude.hpp"
#include "share/share_card.hpp"
#include "core/draw_util.hpp"
#include "core/ui_font.hpp"

#include <QFileInfo>
#include <QFontMetricsF>
#include <QImage>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPainter>
#include <QRadialGradient>
#include <QSvgRenderer>
#include <QtMath>

#include <cmath>
#include <vector>

namespace rk {

namespace {

constexpr int kMaxSide = 8192;
constexpr int kMaxTextLength = 80;

bool Refuse(const char *why)
{
	blog(LOG_WARNING, "[replaykit] the share card was not drawn: %s", why);
	return false;
}

// where an item of the card goes: its centre in percent of the card, its size in percent of the default size, its turn in degrees; the helper holds the ranges, so only a value that cannot be drawn is refused here
struct Placement {
	qreal x = 0.0;
	qreal y = 0.0;
	qreal scale = 0.0;
	qreal rotation = 0.0;
};

bool ReadPlacement(const QJsonObject &layout, const char *name, Placement &out)
{
	const QJsonObject item = layout.value(QLatin1String(name)).toObject();
	const QJsonValue x = item.value(QStringLiteral("x")), y = item.value(QStringLiteral("y"));
	const QJsonValue scale = item.value(QStringLiteral("scale")), rotation = item.value(QStringLiteral("rotation"));
	if (!x.isDouble() || !y.isDouble() || !scale.isDouble() || !rotation.isDouble())
		return false;
	out = {x.toDouble(), y.toDouble(), scale.toDouble(), rotation.toDouble()};
	return std::isfinite(out.x) && std::isfinite(out.y) && std::isfinite(out.scale) && std::isfinite(out.rotation) && out.scale > 0.0;
}

// a circle of the backdrop: its centre in fractions of the card and its radius in fractions of the card width
struct Circle {
	qreal x = 0.0;
	qreal y = 0.0;
	qreal radius = 0.0;
};

// how the helper says the card is drawn apart from the theme: sizes in pixels of a 1080 pixel tall card at size 100, the text weight, how strong the logo shows, the plate and light text colours, and the backdrop of the theme card
struct Look {
	qreal logo = 0.0;
	qreal text = 0.0;
	qreal textWeight = 0.0;
	qreal plateX = 0.0;
	qreal plateY = 0.0;
	qreal plateRadius = 0.0;
	qreal logoOpacity = 0.0;
	QColor plate;
	QColor lightText;
	qreal shade = 0.0;
	qreal glow = 0.0;
	qreal rim = 0.0;
	qreal blur = 0.0;
	std::vector<Circle> shades;
	std::vector<Circle> glows;
};

bool ReadNumber(const QJsonObject &given, const char *name, qreal low, qreal high, qreal &out)
{
	const QJsonValue value = given.value(QLatin1String(name));
	if (!value.isDouble() || !std::isfinite(value.toDouble()) || value.toDouble() < low || value.toDouble() > high)
		return false;
	out = value.toDouble();
	return true;
}

bool ReadCircles(const QJsonValue &given, std::vector<Circle> &out)
{
	if (!given.isArray())
		return false;
	for (const QJsonValue &item : given.toArray()) {
		const QJsonArray triple = item.toArray();
		if (triple.size() != 3 || !triple[0].isDouble() || !triple[1].isDouble() || !triple[2].isDouble())
			return false;
		Circle circle{triple[0].toDouble(), triple[1].toDouble(), triple[2].toDouble()};
		if (!std::isfinite(circle.x) || !std::isfinite(circle.y) || !std::isfinite(circle.radius) || circle.radius <= 0.0)
			return false;
		out.push_back(circle);
	}
	return true;
}

bool ReadLook(const QJsonObject &given, Look &out)
{
	constexpr qreal kHuge = 100000.0;
	qreal opacity = 0.0;
	if (!ReadNumber(given, "logo", 1.0, kHuge, out.logo) || !ReadNumber(given, "text", 1.0, kHuge, out.text) ||
	    !ReadNumber(given, "textWeight", 100.0, 900.0, out.textWeight) || !ReadNumber(given, "logoOpacity", 0.0, 1.0, out.logoOpacity) ||
	    !ReadNumber(given, "plateX", 0.0, kHuge, out.plateX) || !ReadNumber(given, "plateY", 0.0, kHuge, out.plateY) ||
	    !ReadNumber(given, "plateRadius", 0.0, kHuge, out.plateRadius) || !ReadNumber(given, "plateOpacity", 0.0, 255.0, opacity))
		return false;
	out.plate = QColor(given.value(QStringLiteral("plateColor")).toString());
	out.lightText = QColor(given.value(QStringLiteral("lightText")).toString());
	if (!out.plate.isValid() || !out.lightText.isValid())
		return false;
	out.plate.setAlpha(qRound(opacity));
	const QJsonObject backdrop = given.value(QStringLiteral("backdrop")).toObject();
	return ReadNumber(backdrop, "shade", 0.0, 1.0, out.shade) && ReadNumber(backdrop, "glow", 0.0, 1.0, out.glow) &&
	       ReadNumber(backdrop, "rim", 0.0, kHuge, out.rim) && ReadNumber(backdrop, "blur", 0.0, kHuge, out.blur) &&
	       ReadCircles(backdrop.value(QStringLiteral("shades")), out.shades) && ReadCircles(backdrop.value(QStringLiteral("glows")), out.glows);
}

QColor Mix(const QColor &from, const QColor &to, qreal amount)
{
	return QColor::fromRgbF(float(from.redF() + (to.redF() - from.redF()) * amount), float(from.greenF() + (to.greenF() - from.greenF()) * amount),
				float(from.blueF() + (to.blueF() - from.blueF()) * amount), float(from.alphaF() + (to.alphaF() - from.alphaF()) * amount));
}

// the ground of the theme card: the shade circles, then the circles whose rim glows in the accent colour with a soft band inside it
void DrawBackdrop(QPainter &painter, const Look &look, const QColor &ground, const QColor &accent, int width, qreal unit, int height)
{
	painter.save();
	painter.setPen(Qt::NoPen);
	painter.setBrush(Mix(ground, QColor(Qt::black), look.shade));
	for (const Circle &circle : look.shades)
		painter.drawEllipse(QPointF(circle.x * width, circle.y * height), circle.radius * width, circle.radius * width);
	const QColor inner = Mix(ground, accent, look.glow);
	for (const Circle &circle : look.glows) {
		const QPointF centre(circle.x * width, circle.y * height);
		const qreal radius = circle.radius * width;
		QRadialGradient gradient(centre, radius);
		gradient.setColorAt(0.0, inner);
		gradient.setColorAt(qMax(0.0, 1.0 - look.blur * unit / radius), inner);
		gradient.setColorAt(qMax(0.0, 1.0 - look.rim * unit / radius), Mix(inner, accent, 0.5));
		gradient.setColorAt(1.0, accent);
		painter.setBrush(gradient);
		painter.drawEllipse(centre, radius, radius);
	}
	painter.restore();
}

// the outline logo in one colour: drawn white from its svg, then given the colour through its own alpha
QImage TintedLogo(QSvgRenderer &svg, qreal side, QColor colour, qreal opacity)
{
	const int size = qMax(1, qCeil(side));
	QImage logo(size, size, QImage::Format_ARGB32_Premultiplied);
	logo.fill(Qt::transparent);
	QPainter painter(&logo);
	painter.setRenderHints(QPainter::Antialiasing | QPainter::SmoothPixmapTransform);
	svg.render(&painter, QRectF(0.0, 0.0, size, size));
	painter.setCompositionMode(QPainter::CompositionMode_SourceIn);
	colour.setAlphaF(float(opacity));
	painter.fillRect(logo.rect(), colour);
	painter.end();
	return logo;
}

// moves the painter to the centre of an item and turns it, so the item draws around the origin
void PlaceAt(QPainter &painter, const Placement &at, int width, int height)
{
	painter.translate(at.x / 100.0 * width, at.y / 100.0 * height);
	painter.rotate(at.rotation);
}

// the card replaces the old one in one step, so the window never reads half a file
bool SaveReplacing(const QImage &image, const QString &out)
{
	const QString temporary = out + QStringLiteral(".tmp");
	if (!image.save(temporary, "PNG"))
		return false;
	if (!MoveFileExW(reinterpret_cast<const wchar_t *>(QDir::toNativeSeparators(temporary).utf16()),
			 reinterpret_cast<const wchar_t *>(QDir::toNativeSeparators(out).utf16()), MOVEFILE_REPLACE_EXISTING)) {
		QFile::remove(temporary);
		return false;
	}
	return true;
}

} // namespace

bool RenderShareCard(const std::string &json)
{
	QJsonParseError error{};
	const QJsonDocument document = QJsonDocument::fromJson(QByteArray::fromStdString(json), &error);
	if (error.error != QJsonParseError::NoError || !document.isObject())
		return Refuse("the request is not a json object");
	const QJsonObject request = document.object();

	// only ever the card file in the replaykit folder, whatever the request names
	const QString out = QDir::cleanPath(request.value(QStringLiteral("out")).toString());
	const QString root = QDir::cleanPath(qEnvironmentVariable("APPDATA") + QStringLiteral("/obs-studio/obs-replayKit")) + QStringLiteral("/");
	if (!out.startsWith(root, Qt::CaseInsensitive) || !out.endsWith(QStringLiteral("/share_cover_card.png"), Qt::CaseInsensitive))
		return Refuse("the card path is outside the replaykit folder");
	const int width = request.value(QStringLiteral("width")).toInt();
	const int height = request.value(QStringLiteral("height")).toInt();
	if (width < 16 || height < 16 || width > kMaxSide || height > kMaxSide)
		return Refuse("the card size is out of range");
	const QString background = request.value(QStringLiteral("background")).toString();
	if (background != QStringLiteral("theme") && background != QStringLiteral("image") && background != QStringLiteral("video") && background != QStringLiteral("lastframe"))
		return Refuse("unknown background");
	// the window draws the last frame or the video under the card, so the card leaves them clear around what it draws
	const bool overPicture = background == QStringLiteral("lastframe") || background == QStringLiteral("video");
	const QColor ground(request.value(QStringLiteral("ground")).toString());
	const QColor ink(request.value(QStringLiteral("ink")).toString());
	const QColor accent(request.value(QStringLiteral("accent")).toString());
	if (!ground.isValid() || !ink.isValid() || !accent.isValid())
		return Refuse("the theme colours are not valid colours");
	const QString text = request.value(QStringLiteral("text")).toString().left(kMaxTextLength);
	const bool icon = request.value(QStringLiteral("icon")).toBool();
	const bool fill = request.value(QStringLiteral("fit")).toString() == QStringLiteral("fill");
	const QJsonObject layout = request.value(QStringLiteral("layout")).toObject();
	Placement iconAt, textAt;
	if (!ReadPlacement(layout, "icon", iconAt) || !ReadPlacement(layout, "text", textAt))
		return Refuse("the card layout is not valid");
	Look look;
	if (!ReadLook(request.value(QStringLiteral("look")).toObject(), look))
		return Refuse("the card look is not valid");

	QImage card(width, height, QImage::Format_ARGB32);
	card.fill(overPicture ? QColor(Qt::transparent) : ground);
	QPainter painter(&card);
	painter.setRenderHints(QPainter::Antialiasing | QPainter::SmoothPixmapTransform | QPainter::TextAntialiasing);
	const qreal unit = height / 1080.0;

	if (background == QStringLiteral("theme"))
		DrawBackdrop(painter, look, ground, accent, width, unit, height);
	if (background == QStringLiteral("image")) {
		const QImage picture(request.value(QStringLiteral("image")).toString());
		if (picture.isNull())
			return Refuse("the picked image could not be read");
		const qreal fitScale = qMin(qreal(width) / picture.width(), qreal(height) / picture.height());
		const qreal fillScale = qMax(qreal(width) / picture.width(), qreal(height) / picture.height());
		const QSizeF drawn = QSizeF(picture.size()) * (fill ? fillScale : fitScale);
		painter.drawImage(QRectF((width - drawn.width()) / 2.0, (height - drawn.height()) / 2.0, drawn.width(), drawn.height()), picture);
	}

	// the logo and the text take the theme text colour on the theme card and the light text over a picture, a video or the last frame
	const QColor foreground = background == QStringLiteral("theme") ? ink : look.lightText;
	if (icon) {
		QSvgRenderer svg(IconPath(QStringLiteral("share/obs-logo-outline.svg")));
		if (!svg.isValid())
			return Refuse("the obs logo could not be read");
		const qreal side = look.logo * unit * iconAt.scale / 100.0;
		const QImage logo = TintedLogo(svg, side, foreground, look.logoOpacity);
		painter.save();
		PlaceAt(painter, iconAt, width, height);
		painter.drawImage(QRectF(-side / 2.0, -side / 2.0, side, side), logo);
		painter.restore();
	}
	if (!text.isEmpty()) {
		const qreal size = textAt.scale / 100.0;
		QFont font(UiFontFamily());
		font.setPixelSize(qMax(8, qRound(look.text * unit * size)));
		font.setWeight(QFont::Weight(qRound(look.textWeight)));
		const QFontMetricsF metrics(font);
		const qreal textWidth = metrics.horizontalAdvance(text);
		const qreal textHeight = metrics.height();
		painter.save();
		PlaceAt(painter, textAt, width, height);
		// a picked image or video can be any colour under the text, so it gets a dark plate to stay readable
		if (background == QStringLiteral("image") || background == QStringLiteral("video")) {
			const qreal padX = look.plateX * unit * size;
			const qreal padY = look.plateY * unit * size;
			const qreal radius = look.plateRadius * unit * size;
			painter.setPen(Qt::NoPen);
			painter.setBrush(look.plate);
			painter.drawRoundedRect(QRectF(-textWidth / 2.0 - padX, -textHeight / 2.0 - padY, textWidth + 2 * padX, textHeight + 2 * padY), radius, radius);
		}
		painter.setFont(font);
		painter.setPen(foreground);
		painter.drawText(QRectF(-textWidth, -textHeight / 2.0, 2 * textWidth, textHeight), Qt::AlignHCenter | Qt::AlignVCenter, text);
		painter.restore();
	}
	painter.end();

	QDir().mkpath(QFileInfo(out).absolutePath());
	if (!SaveReplacing(card, out))
		return Refuse("the card could not be saved");
	blog(LOG_INFO, "[replaykit] share card drawn: %dx%d, %s background", width, height, background.toUtf8().constData());
	return true;
}

} // namespace rk
