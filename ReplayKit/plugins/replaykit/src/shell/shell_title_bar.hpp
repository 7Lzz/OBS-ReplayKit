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
#include "shell/shell_title_button.hpp"

namespace rk {

class NotifyButton;
class ShellBadge;
class ShellHotkeyMenu;
class ShellQuickMenu;
class ShellNavButton;
class ShellWinButton;

// the logo in the cell above the rail, a button with a small arrow that opens the quick action menu under it; the arrow turns over while the menu is open
class ShellLogoButton : public ShellTitleButton {
public:
	static constexpr int kWidth = 50;
	explicit ShellLogoButton(QWidget *parent);

protected:
	void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) override;
};

// the yellow badge left of the bell, its icon and then "ReplayKit <version> Available": a newer release was found while obs was open, and a click opens the update window, since only the check at startup updates by itself. the obs kind is the grey one left of it, "OBS <version> Available" from the development check for obs releases, and a click opens the notes of that release, since replaykit decides when obs is updated
class ShellUpdateButton : public ShellTitleButton {
public:
	enum class Kind { ReplayKit, Obs };
	ShellUpdateButton(Kind kind, QWidget *parent);
	// the version that is ready, or empty to take the button away
	void SetVersion(const QString &version);
	const QString &Version() const { return m_version; }

protected:
	void PaintGlyph(QPainter &p, const QRectF &pill, qreal hover) override;

private:
	static QFont LabelFont();
	QString Label() const { return (m_kind == Kind::Obs ? QStringLiteral("OBS %1 Available") : QStringLiteral("ReplayKit %1 Available")).arg(m_version); }

	Kind m_kind;
	QString m_version;
};

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
	// the release the update check found while obs was open, empty when there is none
	void SetUpdateReady(const QString &version);
	// the newer obs the development check found, empty when there is none or the check is off
	void SetObsUpdateReady(const QString &version);
	void RestoreTitle();
	// the menu of the circle is up, so the shell reads the account every second instead of every half minute
	bool AccountPanelOpen() const;
	// starts the browsers of the menus that hang off the bar out of sight, one after another, so the first open of each finds a page that is up; true while one of them is still to come or on its way
	bool WarmNext();
	bool TogglePopup(const QString &name);

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
	static constexpr int kLogoInset = 12;
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
	ShellLogoButton *m_logo = nullptr;
	ShellQuickMenu *m_quick = nullptr;
	ShellUpdateButton *m_update = nullptr;
	ShellUpdateButton *m_obsUpdate = nullptr;
	NotifyButton *m_bell = nullptr;
	ShellAccountButton *m_account = nullptr;
	ShellWinButton *m_min = nullptr;
	ShellWinButton *m_max = nullptr;
	ShellWinButton *m_close = nullptr;
};

} // namespace rk
