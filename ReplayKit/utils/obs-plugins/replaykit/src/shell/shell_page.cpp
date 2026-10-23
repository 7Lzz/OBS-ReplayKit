// the browser host that holds the shell pages
#include "prelude.hpp"
#include "shell/shell_page.hpp"

#include "core/cef_host.hpp"
#include "core/theme.hpp"
#include "shell/shell_views.hpp"

namespace rk {

// runs a script in the shell browser. the page defines RKShell, so every call is guarded to be harmless if it lands before the page exists
void ShellRunJs(QCefWidget *browser, const std::string &js)
{
	RunBrowserScript(browser, js);
}

ShellPage::ShellPage(QWidget *parent)
	: QWidget(parent)
{
	// the chrome colour under cef until its first frame, so the gap is the window colour and not a white flash
	setAutoFillBackground(true);
	QPalette pal = palette();
	pal.setColor(QPalette::Window, g_chromeBg);
	setPalette(pal);
}

void ShellPage::showEvent(QShowEvent *event)
{
	QWidget::showEvent(event);
	ShellRunJs(browser, "window.RKShell && RKShell.windowShown(true);");
	ShellHostWindowShown(true);
}

void ShellPage::hideEvent(QHideEvent *event)
{
	QWidget::hideEvent(event);
	ShellRunJs(browser, "window.RKShell && RKShell.windowShown(false);");
	ShellHostWindowShown(false);
}

} // namespace rk
