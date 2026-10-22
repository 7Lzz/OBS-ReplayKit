// title bar of the clean shell
#pragma once

#include <QMainWindow>
#include <QMouseEvent>
#include <QPaintEvent>
#include <QPoint>
#include <QResizeEvent>
#include <QRect>
#include <QString>
#include <QWidget>

#include "shell/shell_account.hpp"

namespace rk {

class NotifyButton;
class ShellBadge;
class ShellHotkeyMenu;
class ShellNavButton;
class ShellWinButton;

// logo cell above the rail column, the back and forward arrows and the badges after it, the bell + account circle + window controls on the right. everything else is caption: the frame filter answers HTCAPTION for any point over this widget that no child covers
class ShellTitleBar : public QWidget {
public:
	explicit ShellTitleBar(QMainWindow *mw);
	~ShellTitleBar() override;

	// keybind chips and the clip length come from the same /settings poll the caption badges used
	void SetLabels(const QString &recordKeybind, const QString &clipKeybind, int replaySeconds);

	// runs on the shell tick: the badges follow the real obs output state rather than events, so they can never drift from it
	void RefreshStates();

	// true when the point lands on empty title bar, so the frame filter can hand it to windows as a caption drag. the badges and buttons are children and answer HTCLIENT instead
	bool IsCaptionAt(const QPoint &inTitle) const;

	void LayoutChildren();
	// who is signed in to streamable, for the circle next to the bell
	void SetAccount(const ShellAccountButton::Account &account);
	// the arrows after the logo: one is dimmed while the history has nowhere to go that way
	void SetNavigation(bool canGoBack, bool canGoForward);
	void SetPage(const QString &page);
	void RestoreTitle();
	// the menu of the circle is up, so the shell reads the account every second instead of every half minute
	bool AccountPanelOpen() const;
	// starts the browsers of the menus that hang off the bar out of sight, one after another, so the first open of each finds a page that is up; true while one of them is still to come or on its way
	bool WarmNext();

protected:
	void resizeEvent(QResizeEvent *event) override;
	// a maximized window is not dragged by the system from here (the frame filter hands this area to the widget while maximized), so the drag that restores it, and the double click that does the same, are handled here
	void mousePressEvent(QMouseEvent *event) override;
	void mouseMoveEvent(QMouseEvent *event) override;
	void mouseReleaseEvent(QMouseEvent *event) override;
	void mouseDoubleClickEvent(QMouseEvent *event) override;
	void paintEvent(QPaintEvent *) override;

private:
	static constexpr int kDividerH = 24;
	// the line sits right against the minimize cell, so its hover wash starts at the line and the line reads as the edge of it, and the bell ends kBellGap before the line: the circle of the account is then about 18 px from the line, the same as the minimize glyph is on the other side (18 px into its cell)
	static constexpr int kDividerGap = 1;
	static constexpr int kBellGap = 9;
	QMainWindow *m_mw = nullptr;
	QString m_page;
	QString m_originalTitle;
	QRect m_pageRect;
	bool m_settingTitle = false;
	QPoint m_pressPos;
	bool m_pressArmed = false;
	ShellNavButton *m_back = nullptr;
	ShellNavButton *m_forward = nullptr;
	ShellBadge *m_clip = nullptr;
	ShellBadge *m_rec = nullptr;
	// the menu the right button opens under either badge
	ShellHotkeyMenu *m_hotkeys = nullptr;
	NotifyButton *m_bell = nullptr;
	ShellAccountButton *m_account = nullptr;
	ShellWinButton *m_min = nullptr;
	ShellWinButton *m_max = nullptr;
	ShellWinButton *m_close = nullptr;
};

} // namespace rk
