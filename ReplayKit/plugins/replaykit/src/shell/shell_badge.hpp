// clip and record badges of the shell title bar
#pragma once

#include <QEnterEvent>
#include <QEvent>
#include <QFont>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QRect>
#include <QString>
#include <QStringList>
#include <QWidget>
#include <functional>

namespace rk {

// a rounded chip with the key caps of a keybind and a label, clickable. the title bar owns two of these: clip (save the replay buffer) and record (start / stop)
class ShellBadge : public QWidget {
public:
	static constexpr int kHeight = 34;
	static constexpr int kPadX = 8;
	static constexpr int kChipH = 22;
	static constexpr int kChipPadX = 6;
	static constexpr int kChipGap = 4;
	static constexpr int kTabRadius = 9;

	std::function<void()> onClick;
	// the right button, which opens the hotkey menu that hangs off the badge
	std::function<void()> onContextMenu;
	// the pointer came over the badge, so its menu can be built before it is asked for
	std::function<void()> onEnter;

	explicit ShellBadge(QWidget *parent);

	void SetKeys(const QString &keybind);
	void SetLabel(const QString &label);
	// live = red and pulsing (recording), dim = the action is unavailable right now (clipping off)
	void SetState(bool live, bool dim, bool paused, const QString &timer);
	// armed = the thing the badge starts is on and ready (clipping): a green light and a faint green edge, so it reads at a glance that clips are being kept
	void SetArmed(bool armed);
	void SetTip(const QString &tip);
	void Pulse(bool on);
	// drawn as the open tab of the menu that hangs off it: the panel colour with the bottom edge left off, so the two read as one shape
	void SetOpen(bool open);
	bool IsOpen() const { return m_open; }
	// screen rect of the badge, where the menu hangs from
	QRect GlobalRect() const;

protected:
	void enterEvent(QEnterEvent *event) override;
	void leaveEvent(QEvent *event) override;
	void mousePressEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	static QFont ChipFont();
	static QFont LabelFont();
	void SizeToContent();

	QStringList m_keys;
	QString m_label;
	QString m_timer;
	QString m_tip;
	bool m_live = false;
	bool m_dim = false;
	bool m_armed = false;
	bool m_paused = false;
	bool m_pulseOn = true;
	bool m_hover = false;
	bool m_down = false;
	bool m_open = false;
};

} // namespace rk
