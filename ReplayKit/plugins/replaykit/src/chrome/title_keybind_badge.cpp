// keybind chips beside the caption title
#include "prelude.hpp"
#include "chrome/title_keybind_badge.hpp"

#include "core/keybind_label.hpp"
#include "notify/notify_button.hpp"

namespace rk {

namespace {

// no dwm attribute exposes the icon/title rect the way DWMWA_CAPTION_BUTTON_BOUNDS exposes the system buttons, so the text width is measured instead: lfCaptionFont from the non-client metrics is the exact font windows draws the title in, and windowTitle() is the same string qt already keeps the native caption in sync with
QFont CaptionTitleFont()
{
	NONCLIENTMETRICSW ncm = {};
	ncm.cbSize = sizeof(ncm);
	QFont fallback("Segoe UI");
	fallback.setPixelSize(12);
	if (!SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0))
		return fallback;
	QFont f(QString::fromWCharArray(ncm.lfCaptionFont.lfFaceName));
	f.setPixelSize(ncm.lfCaptionFont.lfHeight != 0 ? qAbs(ncm.lfCaptionFont.lfHeight) : 12);
	f.setBold(ncm.lfCaptionFont.lfWeight >= FW_BOLD);
	return f;
}

// same event set as NotifyAnchorFilter, plus WindowTitleChange -- this badges x position depends on the titles own measured width, so a title edit (profile switch, recording state) has to reflow it and not just a move or resize
class TitleKeybindAnchorFilter : public QObject {
public:
	TitleKeybindAnchorFilter(QObject *parent, TitleKeybindBadge *badge) : QObject(parent), m_badge(badge) {}

protected:
	bool eventFilter(QObject *, QEvent *event) override
	{
		switch (event->type()) {
		case QEvent::Move:
		case QEvent::Resize:
		case QEvent::Show:
		case QEvent::WindowStateChange:
		case QEvent::WindowActivate:
		case QEvent::WindowTitleChange:
			if (m_badge)
				PositionTitleKeybindBadge(m_badge.data());
			break;
		case QEvent::Hide:
		case QEvent::Close:
			if (m_badge)
				m_badge->hide();
			break;
		default:
			break;
		}
		return false;
	}

private:
	QPointer<TitleKeybindBadge> m_badge;
};

} // namespace

TitleKeybindBadge::TitleKeybindBadge(QWidget *target)
	: QWidget(target), m_target(target)
{
	setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
	setAttribute(Qt::WA_TranslucentBackground);
	setAttribute(Qt::WA_ShowWithoutActivating);
	// click-through like the notification tip -- this only ever displays keybinds, so a press here should drag the caption underneath instead of getting eaten by this widget
	setAttribute(Qt::WA_TransparentForMouseEvents);
	setFixedHeight(kHeight);
}

bool TitleKeybindBadge::HasLabels() const
{
	return !m_recordLabel.isEmpty() || !m_clipLabel.isEmpty();
}

void TitleKeybindBadge::SetLabels(const QString &recordLabel, const QString &clipLabel)
{
	if (recordLabel == m_recordLabel && clipLabel == m_clipLabel)
		return;
	m_recordLabel = recordLabel;
	m_clipLabel = clipLabel;
	update();
}

QList<QPair<QString, QString>> TitleKeybindBadge::Entries() const
{
	QList<QPair<QString, QString>> entries;
	if (!m_clipLabel.isEmpty())
		entries.append({QStringLiteral("Clip"), m_clipLabel});
	if (!m_recordLabel.isEmpty())
		entries.append({QStringLiteral("Recording"), m_recordLabel});
	return entries;
}

int TitleKeybindBadge::ContentWidth() const
{
	QFontMetrics fm(ChipFont());
	QFontMetrics nameFm(NameFont());
	auto entries = Entries();
	int w = kDividerGapBefore + 1 + kDividerGapAfter;
	for (int i = 0; i < entries.size(); i++) {
		if (i > 0)
			w += kChipGap;
		w += nameFm.horizontalAdvance(entries[i].first) + kLabelChipGap;
		w += fm.horizontalAdvance(entries[i].second) + kChipPadX * 2;
	}
	return w;
}

void TitleKeybindBadge::paintEvent(QPaintEvent *)
{
	QPainter p(this);
	p.setRenderHint(QPainter::Antialiasing, true);
	QFont chipFont = ChipFont();
	QFont nameFont = NameFont();
	QFontMetrics fm(chipFont);
	QFontMetrics nameFm(nameFont);
	qreal x = kDividerGapBefore;
	// same hairline NotifyButton draws next to the system minimize button -- the 0.5 offset is what it uses to land a 1px pen on a single pixel column; without it antialiasing splits the stroke across two columns, which is what made this read as thicker and paler than the source instead of a crisp match
	qreal dividerTop = (height() - kDividerLength) / 2.0;
	p.setPen(QPen(QColor(255, 255, 255, 38), 1.0));
	p.setBrush(Qt::NoBrush);
	p.drawLine(QPointF(x - 0.5, dividerTop), QPointF(x - 0.5, dividerTop + kDividerLength));
	x += 1 + kDividerGapAfter;
	auto entries = Entries();
	for (int i = 0; i < entries.size(); i++) {
		if (i > 0)
			x += kChipGap;
		// bold white name (Recording/Clipping) same as the tray rows own nameLabel, paired tight against its chip
		p.setFont(nameFont);
		p.setPen(QColor(255, 255, 255));
		qreal nameW = nameFm.horizontalAdvance(entries[i].first);
		p.drawText(QRectF(x, 0.0, nameW, height()), Qt::AlignVCenter | Qt::AlignLeft, entries[i].first);
		x += nameW + kLabelChipGap;

		p.setFont(chipFont);
		qreal chipW = fm.horizontalAdvance(entries[i].second) + kChipPadX * 2;
		QRectF chip(x, 0.0, chipW, height());
		p.setPen(Qt::NoPen);
		// same rgba(0,0,0,70)/rgba(255,255,255,160) the tray rows own chipLabel uses, so both keybind chips match
		p.setBrush(QColor(0, 0, 0, 70));
		p.drawRoundedRect(chip, kChipRadius, kChipRadius);
		p.setPen(QColor(255, 255, 255, 160));
		p.drawText(chip, Qt::AlignCenter, entries[i].second);
		x += chipW;
	}
}

QFont TitleKeybindBadge::ChipFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(10);
	return f;
}

QFont TitleKeybindBadge::NameFont()
{
	QFont f("Segoe UI");
	f.setPixelSize(11);
	f.setBold(true);
	return f;
}

QList<QPointer<TitleKeybindBadge>> g_titleKeybindBadges;
QString g_titleRecordLabel;
QString g_titleClipLabel;

// mirrors PositionNotifyButton but anchored off the left edge instead of DWMWA_CAPTION_BUTTON_BOUNDS, since nothing analogous exists for the icon/title side -- hides itself whenever the native title (dynamic, sometimes very long on the main obs window) would leave no real room before the bell or the system buttons, rather than overlapping
void PositionTitleKeybindBadge(TitleKeybindBadge *badge)
{
	QWidget *target = badge ? badge->target() : nullptr;
	if (!target)
		return;
	if (!target->isVisible() || target->isMinimized() || !badge->HasLabels()) {
		badge->hide();
		return;
	}
	HWND hwnd = (HWND)target->winId();
	RECT frame = {};
	if (!GetWindowRect(hwnd, &frame)) {
		badge->hide();
		return;
	}
	RECT buttons = {};
	if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, &buttons, sizeof(buttons))) ||
	    buttons.right <= buttons.left) {
		badge->hide();
		return;
	}
	int captionTop = frame.top + buttons.top;
	int captionHeight = buttons.bottom - buttons.top;
	if (captionHeight < TitleKeybindBadge::kHeight) {
		badge->hide();
		return;
	}

	// stop short of whichever comes first, our own bell or the system buttons, so a long obs title (profile + scene collection can run long) just drops the badge instead of drawing over either one
	int rightLimit = frame.left + buttons.left;
	for (auto &btn : g_notifyButtons) {
		if (btn && btn->target() == target && btn->isVisible())
			rightLimit = qMin(rightLimit, btn->x());
	}
	rightLimit -= TitleKeybindBadge::kEdgeMargin;

	int textLeft = frame.left + TitleKeybindBadge::kIconLeftMargin + GetSystemMetrics(SM_CXSMICON) +
		       TitleKeybindBadge::kIconTextGap;
	int textWidth = QFontMetrics(CaptionTitleFont()).horizontalAdvance(target->windowTitle());
	int badgeLeft = textLeft + textWidth;
	int badgeWidth = badge->ContentWidth();
	if (badgeLeft + badgeWidth > rightLimit) {
		badge->hide();
		return;
	}
	int y = captionTop + (captionHeight - TitleKeybindBadge::kHeight) / 2;
	if (badge->width() != badgeWidth)
		badge->setFixedWidth(badgeWidth);
	if (badge->pos() != QPoint(badgeLeft, y))
		badge->move(badgeLeft, y);
	if (!badge->isVisible())
		badge->show();
}

void AttachTitleKeybindBadge(QWidget *target)
{
	if (!target)
		return;
	for (auto &existing : g_titleKeybindBadges)
		if (existing && existing->target() == target)
			return;
	TitleKeybindBadge *badge = new TitleKeybindBadge(target);
	target->installEventFilter(new TitleKeybindAnchorFilter(target, badge));
	g_titleKeybindBadges.append(QPointer<TitleKeybindBadge>(badge));
	badge->SetLabels(g_titleRecordLabel, g_titleClipLabel);
	PositionTitleKeybindBadge(badge);
}

void PositionAllTitleKeybindBadges()
{
	for (auto &badge : g_titleKeybindBadges)
		if (badge)
			PositionTitleKeybindBadge(badge.data());
}

// same /settings body the hotkey + theme poll already fetches, so this costs no extra request -- recording and clipping are global hotkeys regardless of which window has focus, so every title bar shows the same pair
void RefreshTitleKeybindBadges(const std::string &settingsBody)
{
	QString recordLabel = QString::fromStdString(KeybindLabelFromSettingsJson(settingsBody, "recordingKeybind"));
	QString clipLabel = QString::fromStdString(KeybindLabelFromSettingsJson(settingsBody, "clipKeybind"));
	if (recordLabel == g_titleRecordLabel && clipLabel == g_titleClipLabel)
		return;
	g_titleRecordLabel = recordLabel;
	g_titleClipLabel = clipLabel;
	for (auto &badge : g_titleKeybindBadges) {
		if (!badge)
			continue;
		badge->SetLabels(recordLabel, clipLabel);
		PositionTitleKeybindBadge(badge.data());
	}
}

} // namespace rk
