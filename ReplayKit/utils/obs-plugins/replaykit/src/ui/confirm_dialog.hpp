// themed confirm sheet drawn over the obs window
#pragma once

#include <QString>

namespace rk {

bool ShowReplayKitConfirm(const QString &title, const QString &body, const QString &okText, bool danger);
// the obs window moved or changed size while a sheet is up: the sheet goes with it, placed from the window rectangle windows reports
void ConfirmDialogFollow();

} // namespace rk
