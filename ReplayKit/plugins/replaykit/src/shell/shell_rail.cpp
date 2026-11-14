// icon rail of the clean shell
#include "prelude.hpp"
#include "shell/shell_rail.hpp"

#include "app/obs_actions.hpp"
#include "core/draw_util.hpp"
#include "core/locale.hpp"
#include "core/theme.hpp"
#include "notify/notify_tip.hpp"
#include "shell/shell_title_button.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

ShellRail::ShellRail(QWidget *parent)
	: QWidget(parent)
{
	setAttribute(Qt::WA_OpaquePaintEvent, true);
	setMouseTracking(true);
	m_items = {
		{Home, "rail-home.svg", Tr("Home"), false},
		{Clips, "rail-clips.svg", Tr("Clips"), false},
		{Projector, "rail-projector.svg", Tr("Projector"), false},
		{Share, "rail-share-preview.svg", Tr("Share Preview"), false},
		{Settings, "rail-settings.svg", Tr("Settings"), true},
	};
	for (Item &item : m_items)
		item.renderer = new QSvgRenderer(IconPath(QStringLiteral("nav/") + QString::fromLatin1(item.iconFile)), this);
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
	const int y = int(ItemRect(firstBottom).top()) - ShellStorage::kHeight - RailButtonGap;
	const bool fits = y >= int(ItemRect(lastTop).bottom()) + RailButtonGap;
	m_storage->setGeometry(0, y, width(), ShellStorage::kHeight);
	m_storage->setVisible(m_storage->GetInfo().known && fits);
}

void ShellRail::SetSelected(int id)
{
	if (id == m_selected)
		return;
	m_selected = id;
	update();
}

void ShellRail::SyncLock()
{
	const bool locked = ShellSetupLocked();
	if (locked) {
		SetHover(-1);
		m_pressed = -1;
	}
	m_storage->setEnabled(!locked);
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
	SetHover(ShellSetupLocked() ? -1 : HitItem(event->position().toPoint()));
}

void ShellRail::mousePressEvent(QMouseEvent *event)
{
	if (event->button() != Qt::LeftButton || ShellSetupLocked())
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
	const bool locked = ShellSetupLocked();
	if (locked)
		p.setOpacity(0.4);
	for (int i = 0; i < m_items.size(); ++i) {
		const Item &item = m_items[i];
		const QRectF r = ItemRect(i);
		const bool selected = !locked && item.id == m_selected;
		const bool hover = i == m_hover;
		const bool down = i == m_pressed;
		if (selected || hover || down) {
			// the rounded square of the buttons of the title bar, not a circle
			p.setPen(Qt::NoPen);
			p.setBrush(ChromeInk(selected ? 0.16 : down ? 0.13 : 0.08));
			p.drawRoundedRect(r, ShellTitleButton::kRadius, ShellTitleButton::kRadius);
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
	const qreal d = RailButtonSize;
	const qreal pitch = RailButtonSize + RailButtonGap;
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
		tip = !m_shareAvailable ? Tr("Set up Share Preview") : m_shareOn ? Tr("Share Preview: on") : Tr("Share Preview: off");
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
