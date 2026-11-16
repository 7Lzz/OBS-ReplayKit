// the share window card drawn from a helper request
#include "prelude.hpp"
#include "share/share_card.hpp"
#include "core/draw_util.hpp"
#include "core/ui_font.hpp"

#include <QFileInfo>
#include <QFontMetricsF>
#include <QImage>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPainter>
#include <QSvgRenderer>

#include <cmath>
#include <iterator>

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

// how the helper says the card is drawn apart from the theme: sizes in pixels of a 1080 pixel tall card at size 100, and the plate and light text colours
struct Look {
	qreal logo = 0.0;
	qreal text = 0.0;
	qreal plateX = 0.0;
	qreal plateY = 0.0;
	qreal plateRadius = 0.0;
	QColor plate;
	QColor lightText;
};

bool ReadLook(const QJsonObject &given, Look &out)
{
	qreal *fields[] = {&out.logo, &out.text, &out.plateX, &out.plateY, &out.plateRadius};
	const char *names[] = {"logo", "text", "plateX", "plateY", "plateRadius"};
	for (size_t i = 0; i < std::size(fields); ++i) {
		const QJsonValue value = given.value(QLatin1String(names[i]));
		if (!value.isDouble() || !std::isfinite(value.toDouble()) || value.toDouble() <= 0.0)
			return false;
		*fields[i] = value.toDouble();
	}
	const QJsonValue opacity = given.value(QStringLiteral("plateOpacity"));
	out.plate = QColor(given.value(QStringLiteral("plateColor")).toString());
	out.lightText = QColor(given.value(QStringLiteral("lightText")).toString());
	if (!out.plate.isValid() || !out.lightText.isValid() || !opacity.isDouble() || opacity.toDouble() < 0.0 || opacity.toDouble() > 255.0)
		return false;
	out.plate.setAlpha(qRound(opacity.toDouble()));
	return true;
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
	if (background != QStringLiteral("theme") && background != QStringLiteral("image") && background != QStringLiteral("lastframe"))
		return Refuse("unknown background");
	const QColor ground(request.value(QStringLiteral("ground")).toString());
	const QColor ink(request.value(QStringLiteral("ink")).toString());
	if (!ground.isValid() || !ink.isValid())
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
	// over the last frame the window draws the frame dimmed and this on top, so all around the text stays clear
	card.fill(background == QStringLiteral("lastframe") ? QColor(Qt::transparent) : ground);
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

	if (icon) {
		QSvgRenderer svg(IconPath(QStringLiteral("fallback/obs-icon.svg")));
		if (!svg.isValid())
			return Refuse("the obs icon could not be read");
		const qreal side = look.logo * unit * iconAt.scale / 100.0;
		painter.save();
		PlaceAt(painter, iconAt, width, height);
		svg.render(&painter, QRectF(-side / 2.0, -side / 2.0, side, side));
		painter.restore();
	}
	if (!text.isEmpty()) {
		const qreal size = textAt.scale / 100.0;
		QFont font(UiFontFamily());
		font.setPixelSize(qMax(8, qRound(look.text * unit * size)));
		font.setWeight(QFont::DemiBold);
		const QFontMetricsF metrics(font);
		const qreal textWidth = metrics.horizontalAdvance(text);
		const qreal textHeight = metrics.height();
		painter.save();
		PlaceAt(painter, textAt, width, height);
		// a picked image can be any colour under the text, so it gets a dark plate to stay readable
		if (background == QStringLiteral("image")) {
			const qreal padX = look.plateX * unit * size;
			const qreal padY = look.plateY * unit * size;
			const qreal radius = look.plateRadius * unit * size;
			painter.setPen(Qt::NoPen);
			painter.setBrush(look.plate);
			painter.drawRoundedRect(QRectF(-textWidth / 2.0 - padX, -textHeight / 2.0 - padY, textWidth + 2 * padX, textHeight + 2 * padY), radius, radius);
		}
		painter.setFont(font);
		painter.setPen(background == QStringLiteral("theme") ? ink : look.lightText);
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
