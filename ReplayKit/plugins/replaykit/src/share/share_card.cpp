// the share window card drawn from a helper request
#include "prelude.hpp"
#include "share/share_card.hpp"
#include "core/draw_util.hpp"
#include "core/shared_values.hpp"
#include "core/state_files.hpp"
#include "core/ui_font.hpp"

#include <QFileInfo>
#include <QFontMetricsF>
#include <QImage>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPainter>
#include <QSvgRenderer>
#include <QtMath>

#include <cmath>

namespace rk {

namespace {

constexpr int kMaxSide = 8192;

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

// how the helper says the card is drawn apart from the theme: sizes in pixels of a 1080 pixel tall card at size 100, the text weight, how strong the logo shows, and the colour of the logo over a picture
struct Look {
	qreal logo = 0.0;
	qreal text = 0.0;
	qreal themeText = 0.0;
	qreal textWeight = 0.0;
	qreal plateX = 0.0;
	qreal plateY = 0.0;
	qreal plateRadius = 0.0;
	qreal logoOpacity = 0.0;
	QColor lightText;
};

bool ReadNumber(const QJsonObject &given, const char *name, qreal low, qreal high, qreal &out)
{
	const QJsonValue value = given.value(QLatin1String(name));
	if (!value.isDouble() || !std::isfinite(value.toDouble()) || value.toDouble() < low || value.toDouble() > high)
		return false;
	out = value.toDouble();
	return true;
}

bool ReadLook(const QJsonObject &given, Look &out)
{
	constexpr qreal kHuge = 100000.0;
	if (!ReadNumber(given, "logo", 1.0, kHuge, out.logo) || !ReadNumber(given, "text", 1.0, kHuge, out.text) ||
	    !ReadNumber(given, "textWeight", 100.0, 900.0, out.textWeight) || !ReadNumber(given, "logoOpacity", 0.0, 1.0, out.logoOpacity) ||
	    !ReadNumber(given, "plateX", 0.0, kHuge, out.plateX) || !ReadNumber(given, "plateY", 0.0, kHuge, out.plateY) ||
	    !ReadNumber(given, "plateRadius", 0.0, kHuge, out.plateRadius) || !ReadNumber(given, "themeText", 1.0, kHuge, out.themeText))
		return false;
	out.lightText = QColor(given.value(QStringLiteral("lightText")).toString());
	return out.lightText.isValid();
}

// a colour of the request with its opacity from 0 to 1 beside it
bool ReadColor(const QJsonObject &request, const char *colour, const char *opacity, QColor &out)
{
	qreal alpha = 0.0;
	out = QColor(request.value(QLatin1String(colour)).toString());
	if (!out.isValid() || !ReadNumber(request, opacity, 0.0, 1.0, alpha))
		return false;
	out.setAlphaF(float(alpha));
	return true;
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
	const QString root = QDir::cleanPath(ReplayKitRootPath()) + QStringLiteral("/");
	if (!out.startsWith(root, Qt::CaseInsensitive) || !out.endsWith(QLatin1Char('/') + QLatin1String(kShareCoverCardFile), Qt::CaseInsensitive))
		return Refuse("the card path is outside the replaykit folder");
	const int width = request.value(QStringLiteral("width")).toInt();
	const int height = request.value(QStringLiteral("height")).toInt();
	if (width < 16 || height < 16 || width > kMaxSide || height > kMaxSide)
		return Refuse("the card size is out of range");
	const QString background = request.value(QStringLiteral("background")).toString();
	if (std::none_of(std::begin(kShareCoverBackgrounds), std::end(kShareCoverBackgrounds), [&background](const char *known) { return background == QLatin1String(known); }))
		return Refuse("unknown background");
	// the window draws the moving theme ground, the last frame or the video under the card, so only a picked image card has a ground of its own
	const bool ownGround = background == QStringLiteral("image");
	const QColor ground(request.value(QStringLiteral("ground")).toString());
	const QColor ink(request.value(QStringLiteral("ink")).toString());
	if (!ground.isValid() || !ink.isValid())
		return Refuse("the theme colours are not valid colours");
	const QString text = request.value(QStringLiteral("text")).toString().left(kShareCoverTextMaxLength);
	const bool icon = request.value(QStringLiteral("icon")).toBool();
	const bool fill = request.value(QStringLiteral("fit")).toString() == QStringLiteral("fill");
	const QJsonObject layout = request.value(QStringLiteral("layout")).toObject();
	Placement iconAt, textAt;
	if (!ReadPlacement(layout, "icon", iconAt) || !ReadPlacement(layout, "text", textAt))
		return Refuse("the card layout is not valid");
	Look look;
	if (!ReadLook(request.value(QStringLiteral("look")).toObject(), look))
		return Refuse("the card look is not valid");
	QColor textColor, boxColor;
	if (!ReadColor(request, "textColor", "textOpacity", textColor) || !ReadColor(request, "boxColor", "boxOpacity", boxColor))
		return Refuse("the text colours are not valid");

	QImage card(width, height, QImage::Format_ARGB32);
	card.fill(ownGround ? ground : QColor(Qt::transparent));
	QPainter painter(&card);
	painter.setRenderHints(QPainter::Antialiasing | QPainter::SmoothPixmapTransform | QPainter::TextAntialiasing);
	const qreal unit = height / 1080.0;

	if (background == QStringLiteral("image")) {
		const QImage picture(request.value(QStringLiteral("image")).toString());
		if (picture.isNull())
			return Refuse("the picked image could not be read");
		const qreal fitScale = qMin(qreal(width) / picture.width(), qreal(height) / picture.height());
		const qreal fillScale = qMax(qreal(width) / picture.width(), qreal(height) / picture.height());
		const QSizeF drawn = QSizeF(picture.size()) * (fill ? fillScale : fitScale);
		painter.drawImage(QRectF((width - drawn.width()) / 2.0, (height - drawn.height()) / 2.0, drawn.width(), drawn.height()), picture);
	}

	// the logo takes the theme text colour on the theme card and the light text over a picture, a video or the last frame; the helper picks the text colour
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
		const qreal textSize = background == QStringLiteral("theme") ? look.themeText : look.text;
		font.setPixelSize(qMax(8, qRound(textSize * unit * size)));
		font.setWeight(QFont::Weight(qRound(look.textWeight)));
		const QFontMetricsF metrics(font);
		const qreal textWidth = metrics.horizontalAdvance(text);
		const qreal textHeight = metrics.height();
		painter.save();
		PlaceAt(painter, textAt, width, height);
		// a picked image or video can be any colour under the text, so the text gets a box behind it to stay readable
		if (background == QStringLiteral("image") || background == QStringLiteral("video")) {
			const qreal padX = look.plateX * unit * size;
			const qreal padY = look.plateY * unit * size;
			const qreal radius = look.plateRadius * unit * size;
			painter.setPen(Qt::NoPen);
			painter.setBrush(boxColor);
			painter.drawRoundedRect(QRectF(-textWidth / 2.0 - padX, -textHeight / 2.0 - padY, textWidth + 2 * padX, textHeight + 2 * padY), radius, radius);
		}
		painter.setFont(font);
		painter.setPen(textColor);
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
