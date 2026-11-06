// puts the shell browser to sleep on a blank page while the obs window stays hidden, and loads it again when the window comes back
#pragma once

#include <string>

class QCefWidget;
class QWidget;

namespace rk {

// takes browserSleepSeconds from the settings poll; a body without it leaves the time as it was, and nothing sleeps before the helper has said it once
void ShellSleepApplySettings(const std::string &settingsBody);
// every show, hide and minimize of the obs window: a hidden window starts the wait, a shown one wakes the browser
void ShellSleepWindowChanged();
// follows the title of the shell browser, which is how its page says it may sleep
void ShellSleepWatch(QWidget *owner, QCefWidget *browser);
bool ShellAsleep();

} // namespace rk
