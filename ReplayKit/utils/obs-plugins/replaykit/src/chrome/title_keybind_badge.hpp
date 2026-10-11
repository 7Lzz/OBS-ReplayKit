// keybind chips beside the caption title
#pragma once

#include <QFont>
#include <QList>
#include <QPaintEvent>
#include <QPair>
#include <QPointer>
#include <QString>
#include <QWidget>
#include <string>

namespace rk {

// same darker rounded chip as the tray menus own keybind badge (identical rgba fill/ink), and the divider next to it is the same hairline NotifyButton draws next to the system minimize button
class TitleKeybindBadge : public QWidget {
public:
	static constexpr int kHeight = 20;
	static constexpr int kChipRadius = 3;
	static constexpr int kChipPadX = 5;
	static constexpr int kChipGap = 12;
	static constexpr int kLabelChipGap = 5;
	// bigger than plain breathing room needs to be -- also covers CaptionTitleFonts measured title width running a bit short of the real rendered title on the main obs window
	static constexpr int kDividerGapBefore = 18;
	static constexpr int kDividerGapAfter = 8;
	static constexpr int kEdgeMargin = 6;
	// NotifyButtons own hairline is 16px (its 28px height minus a 6px inset top and bottom) -- kept as the same absolute length here too, just centered in this widgets own shorter height instead of re-deriving from it
	static constexpr int kDividerLength = 16;
	static constexpr int kIconLeftMargin = 10;
	static constexpr int kIconTextGap = 8;

	explicit TitleKeybindBadge(QWidget *target);

	QWidget *target() const { return m_target; }
	bool HasLabels() const;

	void SetLabels(const QString &recordLabel, const QString &clipLabel);

	// non-empty keybinds only -- a cleared keybind drops its whole name+chip pair instead of leaving an orphaned label
	QList<QPair<QString, QString>> Entries() const;

	// divider plus its own margins, then each pairs name label and its measured chip width
	int ContentWidth() const;

protected:
	void paintEvent(QPaintEvent *) override;

private:
	static QFont ChipFont();
	static QFont NameFont();
	QWidget *m_target = nullptr;
	QString m_recordLabel;
	QString m_clipLabel;
};

extern QList<QPointer<TitleKeybindBadge>> g_titleKeybindBadges;
extern QString g_titleRecordLabel;
extern QString g_titleClipLabel;
void PositionTitleKeybindBadge(TitleKeybindBadge *badge);
void AttachTitleKeybindBadge(QWidget *target);
void PositionAllTitleKeybindBadges();
void RefreshTitleKeybindBadges(const std::string &settingsBody);

} // namespace rk
