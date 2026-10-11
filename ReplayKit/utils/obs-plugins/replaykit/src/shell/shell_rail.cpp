// icon rail of the clean shell
#include "prelude.hpp"
#include "shell/shell_rail.hpp"

#include "app/obs_actions.hpp"
#include "core/draw_util.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

// svg glyphs for the rail, same 24 grid the dock icons use. fill is irrelevant, only the painted shape is kept and recoloured
const char kRailHomeSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 -960 960 960"><path d="M240-200h120v-240h240v240h120v-360L480-740 240-560v360Zm-80 80v-480l320-240 320 240v480H520v-240h-80v240H160Z"/></svg>)SVG";
const char kRailClipsSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm16-4H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-8 12.5v-9l6 4.5-6 4.5z"/></svg>)SVG";
const char kRailProjectorSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M17 10.5V7c0-.55-.45-1-1-1H4c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h12c.55 0 1-.45 1-1v-3.5l4 4v-11l-4 4z"/></svg>)SVG";
const char kRailShareSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="1 1 14 14"><path d="M3.25 2C2.56 2 2 2.56 2 3.25v6.5C2 10.44 2.56 11 3.25 11H7v1.5H4.75a.75.75 0 0 0 0 1.5h6.5a.75.75 0 0 0 0-1.5H9V11h3.75c.69 0 1.25-.56 1.25-1.25v-6.5C14 2.56 13.44 2 12.75 2h-9.5zM3.5 3.5h9v6h-9v-6zM6.5 4.85v3.3l3.3-1.65-3.3-1.65z"/></svg>)SVG";
const char kRailGearSvg[] =
	R"SVG(<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"/></svg>)SVG";

} // namespace

ShellRail::ShellRail(QWidget *parent)
	: QWidget(parent)
{
	setMouseTracking(true);
	m_items = {
		{Home, kRailHomeSvg, sizeof(kRailHomeSvg) - 1, QStringLiteral("Home"), false},
		{Clips, kRailClipsSvg, sizeof(kRailClipsSvg) - 1, QStringLiteral("Clips"), false},
		{Projector, kRailProjectorSvg, sizeof(kRailProjectorSvg) - 1, QStringLiteral("Projector"), false},
		{Share, kRailShareSvg, sizeof(kRailShareSvg) - 1, QStringLiteral("Share Preview"), false},
		{Settings, kRailGearSvg, sizeof(kRailGearSvg) - 1, QStringLiteral("Settings"), true},
	};
	for (Item &item : m_items)
		item.renderer = new QSvgRenderer(QByteArray(item.svg, item.svgLen), this);
	m_storage = new ShellStorage(this);
	m_storage->hide();
}

void ShellRail::SetStorage(const ShellStorage::Info &info)
{
	m_storage->SetInfo(info);
	PlaceStorage();
}

void ShellRail::resizeEvent(QResizeEvent *event)
{
	QWidget::resizeEvent(event);
	PlaceStorage();
}

void ShellRail::PlaceStorage()
{
	int firstBottom = -1;
	int lastTop = -1;
	for (int i = 0; i < m_items.size(); ++i) {
		if (m_items[i].bottom) {
			if (firstBottom < 0)
				firstBottom = i;
		} else {
			lastTop = i;
		}
	}
	if (firstBottom < 0 || lastTop < 0) {
		m_storage->hide();
		return;
	}
	const int y = int(ItemRect(firstBottom).top()) - ShellStorage::kHeight - 2;
	const bool fits = y >= int(ItemRect(lastTop).bottom()) + 6;
	// one pixel short of the right edge, which is the hairline of the rail
	m_storage->setGeometry(0, y, width() - 1, ShellStorage::kHeight);
	m_storage->setVisible(m_storage->GetInfo().known && fits);
}

void ShellRail::SetSelected(int id)
{
	if (id == m_selected)
		return;
	m_selected = id;
	update();
}

void ShellRail::SetShare(bool available, bool enabled)
{
	if (available == m_shareAvailable && enabled == m_shareOn)
		return;
	m_shareAvailable = available;
	m_shareOn = enabled;
	update();
}

void ShellRail::leaveEvent(QEvent *event)
{
	QWidget::leaveEvent(event);
	SetHover(-1);
}

void ShellRail::mouseMoveEvent(QMouseEvent *event)
{
	SetHover(HitItem(event->position().toPoint()));
}

void ShellRail::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton)
		return;
	m_pressed = HitItem(event->position().toPoint());
	update();
	if (g_notifyTip)
		g_notifyTip->hide();
}

void ShellRail::mouseReleaseEvent(QMouseEvent *event)
{
	const int hit = HitItem(event->position().toPoint());
	const int pressed = m_pressed;
	m_pressed = -1;
	update();
	if (event->button() != Qt::LeftButton || hit < 0 || hit != pressed)
		return;
	Activate(m_items[hit].id);
}

void ShellRail::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	p.fillRect(rect(), g_chromeBar);

	const qreal dpr = devicePixelRatioF();
	for (int i = 0; i < m_items.size(); ++i) {
		const Item &item = m_items[i];
		const QRectF r = ItemRect(i);
		const bool selected = item.id == m_selected;
		const bool hover = i == m_hover;
		const bool down = i == m_pressed;
		if (selected || hover || down) {
			p.setPen(Qt::NoPen);
			p.setBrush(ChromeInk(selected ? 0.16 : down ? 0.13 : 0.08));
			p.drawEllipse(r);
		}
		const QRectF glyph(r.center().x() - 11, r.center().y() - 11, 22, 22);
		QColor ink = ChromeInk(selected || hover || down ? 1.0 : 0.62);
		p.drawPixmap(glyph.topLeft(), TintedSvgPixmap(*item.renderer, glyph.size(), dpr, ink));
		// a green dot on the share icon while the preview is live, like a status light
		if (item.id == Share && ShareOn()) {
			p.setPen(QPen(g_chromeBar, 2.0));
			p.setBrush(g_chromeSuccess);
			p.drawEllipse(QRectF(glyph.right() - 5, glyph.top() - 2, 9, 9));
		}
	}
}

QRectF ShellRail::ItemRect(int index) const
{
	const qreal d = 44.0;
	const qreal pitch = 52.0;
	int bottomCount = 0;
	int slot = 0;
	for (int i = 0; i < m_items.size(); ++i)
		if (m_items[i].bottom)
			++bottomCount;
	bool bottom = m_items[index].bottom;
	for (int i = 0; i < index; ++i)
		if (m_items[i].bottom == bottom)
			++slot;
	qreal cy;
	if (bottom)
		cy = height() - 8.0 - d / 2.0 - (bottomCount - 1 - slot) * pitch;
	else
		cy = 8.0 + d / 2.0 + slot * pitch;
	return QRectF(width() / 2.0 - d / 2.0, cy - d / 2.0, d, d);
}

int ShellRail::HitItem(const QPoint &pt) const
{
	for (int i = 0; i < m_items.size(); ++i)
		if (ItemRect(i).contains(pt))
			return i;
	return -1;
}

void ShellRail::SetHover(int index)
{
	if (index == m_hover)
		return;
	m_hover = index;
	update();
	if (index < 0) {
		if (g_notifyTip)
			g_notifyTip->Leave();
		return;
	}
	if (!g_notifyTip)
		g_notifyTip = new NotifyTip();
	QString tip = m_items[index].tip;
	if (m_items[index].id == Share)
		tip = !m_shareAvailable ? QStringLiteral("Set up Share Preview") : m_shareOn ? QStringLiteral("Share Preview: on") : QStringLiteral("Share Preview: off");
	g_notifyTip->ShowFor(tip, QRect(mapToGlobal(ItemRect(index).topLeft().toPoint()), ItemRect(index).size().toSize()), NotifyTip::Side::Right);
}

void ShellRail::Activate(int id)
{
	switch (id) {
	case Home:
		ShellReselect(ShellView::Home);
		ShellActivate(ShellView::Home);
		break;
	case Projector:
		ShellActivate(ShellView::Projector);
		break;
	case Clips:
		ShellReselect(ShellView::Clips);
		ShellOpenView(ShellView::Clips, false);
		break;
	case Settings:
		ShellOpenView(ShellView::Settings, false);
		break;
	case Share:
		if (!m_shareAvailable) {
			PromptInstallSharePreview();
			break;
		}
		m_shareOn = !m_shareOn;
		update();
		ToggleSharePreview(m_shareOn);
		break;
	}
}

} // namespace rk
