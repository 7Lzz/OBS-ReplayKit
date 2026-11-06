// the strip along the top of the projector view
#pragma once

#include <QPaintEvent>
#include <QResizeEvent>
#include <QWidget>

namespace rk {

class ShellBarToggle;

// the strip under the title bar in the projector view, drawn like the tab bar of the clips view: the ground of the stage with a hairline under it. it holds the switch that turns the preview on and off and the lock that keeps the scene and the overlay from being moved by accident. a plain widget above the preview, so a click never has to get past a native window
class ShellProjectorBar : public QWidget {
public:
	explicit ShellProjectorBar(QWidget *parent);

	// preview is what was chosen, held is studio mode keeping it on, locked is the scene locked against moving
	void SetState(bool preview, bool held, bool locked);

protected:
	void paintEvent(QPaintEvent *) override;
	void resizeEvent(QResizeEvent *event) override;

private:
	void Place();

	ShellBarToggle *m_preview = nullptr;
	ShellBarToggle *m_lock = nullptr;
};

} // namespace rk
