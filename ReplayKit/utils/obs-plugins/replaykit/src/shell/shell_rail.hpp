// icon rail of the clean shell
#pragma once

#include <QEvent>
#include <QList>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QPoint>
#include <QRectF>
#include <QString>
#include <QResizeEvent>
#include <QSvgRenderer>
#include <QWidget>

#include "shell/shell_storage.hpp"

namespace rk {

// the left column: icon-only, a filled circle behind the page that is showing, a tip beside each icon on hover. the top of the window (logo, badges, controls) is the title bar above it, painted in the same colour so the two read as one L-shaped frame
class ShellRail : public QWidget {
public:
	enum Id { Home = 0, Clips = 1, Projector = 2, Share = 3, Settings = 4 };

	explicit ShellRail(QWidget *parent);

	void SetSelected(int id);
	void SyncLock();
	void SetShare(bool available, bool enabled);
	bool ShareOn() const { return m_shareAvailable && m_shareOn; }
	// the size of the library, drawn just above the settings button
	void SetStorage(const ShellStorage::Info &info);
	// true while the ring shows a compression, which is polled every second instead of every ten
	bool StorageCompressing() const { return m_storage && m_storage->GetInfo().compressing; }

protected:
	void leaveEvent(QEvent *event) override;
	void resizeEvent(QResizeEvent *event) override;
	void mouseMoveEvent(QMouseEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	struct Item {
		int id;
		const char *svg;
		size_t svgLen;
		QString tip;
		bool bottom;
		QSvgRenderer *renderer = nullptr;
	};

	// round hit area 44 wide on a 52 pitch; the last group hangs off the bottom edge so settings stays put however tall the window is
	QRectF ItemRect(int index) const;
	int HitItem(const QPoint &pt) const;
	void SetHover(int index);
	void Activate(int id);
	// sits on the bottom group, and steps aside when the window is too short to leave it room under the top group
	void PlaceStorage();

	QList<Item> m_items;
	int m_selected = Home;
	int m_hover = -1;
	int m_pressed = -1;
	bool m_shareAvailable = false;
	bool m_shareOn = false;
	ShellStorage *m_storage = nullptr;
};

} // namespace rk
