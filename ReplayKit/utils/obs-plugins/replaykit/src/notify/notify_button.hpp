// notification bell and its caption placement
#pragma once

#include <QByteArray>
#include <QEnterEvent>
#include <QEvent>
#include <QList>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QPointer>
#include <QRect>
#include <QTimer>
#include <QWidget>
#include <QtGlobal>

namespace rk {

class AnchoredPanel;

class NotifyButton : public QWidget {
public:
	// mirrors .clip-sort-trigger exactly: 6px pad, 16px glyph, 3px gap, 12px chevron, 6px pad, 6px corner radius; kDividerZone is the only thing past the pill -- the hairline to the system minimize button and its breathing room
	static constexpr int kPillWidth = 43;
	static constexpr int kDividerZone = 8;
	static constexpr int kWidth = kPillWidth + kDividerZone;
	static constexpr int kHeight = 28;
	static constexpr int kRadius = 6;

	// embedIn != nullptr makes this a plain child of that widget (the clean shell title bar) instead of a tool window parked over the os caption; target stays the window that owns the notification panel either way
	explicit NotifyButton(QWidget *target, QWidget *embedIn = nullptr);
	~NotifyButton() override;

	QWidget *target() const { return m_target; }
	bool IsEmbedded() const { return m_embedded; }
	// screen rect of the pill; a child widget has no frame of its own, so it is mapped instead of read off frameGeometry
	QRect GlobalRect() const;
	// each bell owns its panel: a parentless Qt::Tool is auto-hidden whenever the application deactivates, and one shared panel cannot be owned by all three windows at once, so there is one per bell kept alive after first use
	AnchoredPanel *EnsurePanel();
	AnchoredPanel *Panel() const { return m_panel; }

protected:
	void enterEvent(QEnterEvent *event) override;
	void leaveEvent(QEvent *event) override;

	void ShowTip();
	static void HideTip();

	// css does this for the sort trigger; here it is a plain 90ms ramp driven off one timer so the fill matches
	void StartHoverFade();
	void mousePressEvent(QMouseEvent *event) override;
	// windows eats the first click on a window whose owner is not active, spending it on activation instead of delivering it -- that is the click that sometimes did nothing; answering MA_NOACTIVATE keeps the press coming through without this tool window taking activation at all
	bool nativeEvent(const QByteArray &, void *message, qintptr *result) override;
	void paintEvent(QPaintEvent *) override;

private:
	QWidget *m_target = nullptr;
	bool m_embedded = false;
	bool m_hover = false;
	qreal m_hoverFade = 0.0;
	QTimer *m_fadeTimer = nullptr;
	AnchoredPanel *m_panel = nullptr;
};

extern QList<QPointer<NotifyButton>> g_notifyButtons;
void PositionNotifyButton(NotifyButton *btn);
void PositionAllNotifyButtons();
void AttachNotifyButton(QWidget *target);
void RefreshNotifyBadges();

} // namespace rk
