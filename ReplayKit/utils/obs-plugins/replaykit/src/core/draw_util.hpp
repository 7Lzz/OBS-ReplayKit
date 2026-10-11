// painting helpers shared by the native chrome
#pragma once

#include <QColor>
#include <QFont>
#include <QPainter>
#include <QPixmap>
#include <QRectF>
#include <QSizeF>
#include <QString>
#include <QSvgRenderer>
#include <QtCore/qnamespace.h>
#include <QtGlobal>

namespace rk {

QPixmap TintedSvgPixmap(QSvgRenderer &svg, const QSizeF &logicalSize, qreal dpr, const QColor &color);
void DrawPageText(QPainter &p, const QRectF &rect, Qt::Alignment align, const QString &text, QFont font, const QColor &color, qreal stroke);

} // namespace rk
