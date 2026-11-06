// page list of the embedded obs settings
#pragma once

#include <QDialog>
#include <QElapsedTimer>
#include <QEvent>
#include <QFont>
#include <QHash>
#include <QLineEdit>
#include <QList>
#include <QListWidget>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QPixmap>
#include <QPoint>
#include <QPointer>
#include <QRect>
#include <QRectF>
#include <QResizeEvent>
#include <QSet>
#include <QShowEvent>
#include <QString>
#include <QSvgRenderer>
#include <QTimer>
#include <QWidget>
#include <QtGlobal>

namespace rk {

// the left column of the obs settings once the dialog is folded into the window. it stands in for the list of pages of the dialog, which stays built (hidden) and keeps deciding what shows, and it is drawn like the list of the replaykit settings, with the way back to those at the foot
class ShellObsNav : public QWidget {
public:
	ShellObsNav(QListWidget *source, QDialog *dialog, QWidget *parent);

protected:
	// the picture is taken again on every show, so an app icon that was changed meanwhile shows up
	void showEvent(QShowEvent *event) override;
	void resizeEvent(QResizeEvent *event) override;
	void hideEvent(QHideEvent *event) override;
	// escape in the search box empties it
	bool eventFilter(QObject *watched, QEvent *event) override;
	void leaveEvent(QEvent *event) override;
	void mouseMoveEvent(QMouseEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	static constexpr int kGithub = -3;
	static constexpr int kNone = -2;
	static constexpr int kBack = -1;
	static constexpr int kRowH = 36;
	static constexpr int kIconPx = 17;
	static constexpr int kPadX = 12;
	// label starts after the icon and its gap, on every row, so a row without an icon lines up with the others
	static constexpr int kTextX = kPadX + kIconPx + 10;
	// the css transition of a tab, between its 120ms nudge and its 90ms wash
	static constexpr qreal kHoverMs = 110.0;
	// the weights the replaykit settings ask for in css: a page is 500 and the one showing 600, a section label is 700; segoe ui has no 500 face, so both of the first two draw its semibold
	static constexpr QFont::Weight kRowWeight = QFont::DemiBold;
	static constexpr QFont::Weight kRowWeightSelected = QFont::DemiBold;
	// hairlines that bring the outline fill to the ink the css text has in the browser, taken from captures of the two side by side
	static constexpr qreal kRowStroke = 0.25;
	static constexpr qreal kHeadStroke = 0.1;
	static constexpr qreal kVersionStroke = 0.25;
	// the search box stands at the top of the panel like the one of the replaykit settings, the heading under it and the pages under that
	static constexpr int kSearchTop = 10;
	static constexpr int kSearchH = 30;
	static constexpr int kHeadTop = 50;
	static constexpr int kRowsTop = 80;

	struct Row {
		int index;
		QRect rect;
		QString text;
	};

	static QFont RowFont(bool selected);
	static QFont HeadFont();
	static QFont VersionFont();

	QRectF PanelRect() const;
	void PlaceSearch();
	// looks through the labels and switches of every page for the text, dims the pages with nothing in them, and marks what matches on the page that shows. jump moves to the first page with a match when the shown one has none
	void ApplySearch(bool jump);
	void MarkShownPage();
	void ClearMarks();
	void InstallHitStyle();
	// the query is shared with the search box of the replaykit settings through the helper: a change is sent after a short pause, and the shared one is read whenever this column comes on screen
	void SendSearch();
	void PullSearch();

	QList<Row> Rows() const;
	// the version and the github mark stand on the rule above the way back, where the sidebar of the replaykit settings has them; a window too short to leave them room drops them rather than drawing over the last page
	QRectF GithubRect() const;
	bool VersionRowFits(const QList<Row> &rows) const;
	int HitRow(const QPoint &pt) const;
	void SetHover(int index);
	// moves every row toward hovered or at rest over the length of the css transition, and lets go of the rows that are back at rest
	void StepHover();

	QPointer<QListWidget> m_source;
	QPointer<QDialog> m_dialog;
	QLineEdit *m_search = nullptr;
	// the pages that hold a match while a search is on
	QSet<int> m_hits;
	bool m_searching = false;
	bool m_hitStyle = false;
	QTimer m_sendTimer;
	QList<QPointer<QWidget>> m_marked;
	int m_hover = kNone;
	int m_pressed = kNone;
	// how far each row has eased toward hovered, 0 to 1; only rows that are moving or hovered are kept
	QHash<int, qreal> m_hoverT;
	QTimer m_ease;
	QElapsedTimer m_easeClock;
	QPixmap m_logo;
	QSvgRenderer m_github;
};

} // namespace rk
