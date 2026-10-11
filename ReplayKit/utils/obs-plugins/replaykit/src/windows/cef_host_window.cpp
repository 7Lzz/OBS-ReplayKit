// top-level window that hosts a cef page and is hidden, never destroyed
#include "prelude.hpp"
#include "windows/cef_host_window.hpp"

#include "ui/anchored_panel.hpp"

namespace rk {

namespace {

// cef does not reliably tell a page that its own window was hidden or shown again (clips.html and settings.html both carry fallbacks for that), so the window says it outright as a dom event the page can listen for
void TellPageWindowShown(QCefWidget *browser, bool shown)
{
	if (!browser)
		return;
	browser->executeJavaScript(shown ? "window.dispatchEvent(new CustomEvent('replaykit-window', { detail: { shown: true } }));"
					 : "window.dispatchEvent(new CustomEvent('replaykit-window', { detail: { shown: false } }));");
}

} // namespace

void CefHostWindow::closeEvent(QCloseEvent *event)
{
	hide();
	AnchoredPanel::HideOwnedBy(this);
	event->ignore();
}

void CefHostWindow::showEvent(QShowEvent *event)
{
	QWidget::showEvent(event);
	blog(LOG_INFO, "[replaykit] '%s' window shown", windowTitle().toUtf8().constData());
	TellPageWindowShown(browser, true);
}

void CefHostWindow::hideEvent(QHideEvent *event)
{
	QWidget::hideEvent(event);
	blog(LOG_INFO, "[replaykit] '%s' window hidden", windowTitle().toUtf8().constData());
	TellPageWindowShown(browser, false);
}

} // namespace rk
