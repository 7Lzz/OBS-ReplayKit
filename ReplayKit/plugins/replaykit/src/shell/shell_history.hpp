// back and forward through the views of the clean shell, the way a browser does it
#pragma once

#include "shell/shell_types.hpp"

namespace rk {

// the view the shell has come to, noted unless the change is a step of the history itself
void ShellHistoryNote(ShellView view);
bool ShellCanGoBack();
bool ShellCanGoForward();
// one step back (-1) or forward (1); false when there is nowhere to go or the view would not let go
bool ShellHistoryGo(int direction);
// the same step asked for by a key, a side button of the mouse or a page: the three ways in can see one press each, so a second request in the same direction a moment after the first is the same press and is dropped
void ShellHistoryRequest(int direction);
// starts with the view the shell opens on, and watches the keys and the side buttons of the mouse over the parts of the window that are made of qt widgets
void ShellHistoryStart();
void ShellHistoryStop();

} // namespace rk
