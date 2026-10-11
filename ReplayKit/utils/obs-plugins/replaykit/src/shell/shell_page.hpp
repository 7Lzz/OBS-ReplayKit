// the browser host that holds the shell pages
#pragma once

#include <QHideEvent>
#include <QPointer>
#include <QShowEvent>
#include <QWidget>
#include <string>

#include "browser-panel.hpp"

namespace rk {

void ShellRunJs(QCefWidget *browser, const std::string &js);

// the one browser the clean layout keeps inside the obs window: its page (shell.html) holds home, clips, settings and setup as iframes that stay loaded, so a view change is a flip inside the page and no native window is ever hidden or shown again. same rule as the popup windows it replaces: hidden, never destroyed, so the cef browser is not torn down mid-session
class ShellPage : public QWidget {
public:
	QPointer<QCefWidget> browser;

	explicit ShellPage(QWidget *parent);

protected:
	// minimize, tray and restore reach the pages inside as a shown / hidden signal, so they stop polling while nobody can see them
	void showEvent(QShowEvent *event) override;
	void hideEvent(QHideEvent *event) override;
};

} // namespace rk
