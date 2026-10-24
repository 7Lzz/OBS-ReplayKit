// quick action menu that hangs off the logo of the title bar
#pragma once

#include <QString>
#include <QWidget>

namespace rk {

class AnchoredPanel;
class ShellTitleButton;

// one menu under the logo whose rows follow the view that shows: the page of the view says what it offers through the helper (the editor its exit, screenshot, render and import, settings its save and cancel), and the plugin adds what it does itself (save a clip, start or stop recording, take a screenshot, and the preview or obs settings of the native views). a pick comes back over the pipe and Run carries it out
class ShellQuickMenu {
public:
	explicit ShellQuickMenu(QWidget *owner);
	~ShellQuickMenu();
	ShellQuickMenu(const ShellQuickMenu &) = delete;
	ShellQuickMenu &operator=(const ShellQuickMenu &) = delete;

	// opens the menu under the logo, or closes it when it is open; the list of the page is read from the helper first
	void Toggle(ShellTitleButton *button);
	// builds the window of the menu ahead of the first open, for the warm-up of the title bar
	AnchoredPanel *Prepare();
	void Follow();
	// a pick from the menu, for the view it was made in
	static void Run(const QString &view, const QString &id);

private:
	void Open(ShellTitleButton *button, const std::string &pageList);

	QWidget *m_owner = nullptr;
	AnchoredPanel *m_panel = nullptr;
	int m_opens = 0;
	bool m_asking = false;
};

// the view that shows, the way the helper and the pages name it
QString ShellViewName();

} // namespace rk
