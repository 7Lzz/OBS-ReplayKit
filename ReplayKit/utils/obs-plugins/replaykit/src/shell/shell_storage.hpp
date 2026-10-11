// storage figure above the settings button of the rail
#pragma once

#include <QEnterEvent>
#include <QEvent>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QString>
#include <QWidget>

namespace rk {

// a small ring that fills with the size of the library, with the size and the limit under it, the way the storage figure of medal sits in its rail. a click opens the card that frees up space
class ShellStorage : public QWidget {
public:
	static constexpr int kHeight = 70;

	// what the helper said about the library; the texts arrive formatted, so this widget only draws them
	struct Info {
		bool known = false;
		double fraction = 0.0;
		QString used;
		QString limit;
		// ok, low (little room left on the drive) or over (past the limit that was set)
		QString state;
		// while clips are being compressed the same ring shows how far that has got, in the accent colour, with the percent and the clip count in place of the sizes
		bool compressing = false;
		int compressPercent = 0;
		QString compressText;
	};

	explicit ShellStorage(QWidget *parent);

	void SetInfo(const Info &info);
	const Info &GetInfo() const { return m_info; }

protected:
	void enterEvent(QEnterEvent *event) override;
	void leaveEvent(QEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	Info m_info;
	bool m_hover = false;
	bool m_down = false;
};

// asks the helper for the numbers and hands them to the rail; one request at a time, and a slow helper cannot stack them up
void ShellStoragePoll();

} // namespace rk
