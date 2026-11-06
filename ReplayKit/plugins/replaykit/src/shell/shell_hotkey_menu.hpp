// menu that hangs off the clip and record badges
#pragma once

#include <QWidget>

namespace rk {

class AnchoredPanel;
class ShellBadge;

// one cef window shared by both badges: the right button on a badge opens it under that badge, where the key of that action can be picked or the settings page for the keys opened. the badge draws itself as the open tab of the menu, so the two read as one shape
class ShellHotkeyMenu {
public:
	enum class Kind { Clip, Record };

	explicit ShellHotkeyMenu(QWidget *owner);
	~ShellHotkeyMenu();
	ShellHotkeyMenu(const ShellHotkeyMenu &) = delete;
	ShellHotkeyMenu &operator=(const ShellHotkeyMenu &) = delete;

	// opens the menu under the badge, or closes it when it is already open for that badge
	void Toggle(ShellBadge *badge, Kind kind);
	// builds the window of the menu while the pointer is over a badge; its page starts with the warm-up of the title bar, or with the first open
	void Prepare(ShellBadge *badge, Kind kind);
	// re-places an open menu under its badge after the title bar laid out again
	void Follow();
	void Hide();
	// the panel of the menu, once the first hover or the warm-up has made it
	AnchoredPanel *Panel() const { return m_panel; }

private:
	// the address is the state of the menu: which badge it hangs off, how wide the tab is, and a count that makes every open a different address, so the page sees a change even when the badge is the same
	static QString UrlFor(Kind kind, int tabWidth, int opens);

	QWidget *m_owner = nullptr;
	AnchoredPanel *m_panel = nullptr;
	int m_opens = 0;
};

} // namespace rk
