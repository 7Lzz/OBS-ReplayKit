// places the title bar, rail and pages in the obs window
#pragma once

#include <QEvent>
#include <QMainWindow>
#include <QObject>

#include "shell/shell_types.hpp"

namespace rk {

void ShellRepaintChrome();
void ShellTintPreview();
bool ShellShowsClips(ShellView view);
void ShellLayout();
void ShellLayoutViews();

// keeps the title bar, rail and embedded pages in step with the window, and binds the native frame the moment the window handle exists
class ShellHostFilter : public QObject {
public:
	explicit ShellHostFilter(QMainWindow *mw) : QObject(mw), m_mw(mw) {}

protected:
	bool eventFilter(QObject *watched, QEvent *event) override;

private:
	QMainWindow *m_mw;
};

} // namespace rk
