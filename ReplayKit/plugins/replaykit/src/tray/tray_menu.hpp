// custom rows and behaviour of the obs tray menu
#pragma once

#include <QAction>
#include <QJsonArray>
#include <QMenu>
#include <QPointer>

namespace rk {

// share previews checked state is re-read from the helper every time the menu opens, since obs only builds this menu once
extern QPointer<QAction> g_sharePreviewAction;
// last /share-preview "available" -- false means the cable + hidden projector were never set up, so a click offers to install instead of toggling
extern bool g_sharePreviewAvailable;
void InstallTrayMenu();
// the tray menu with the row at index in its hover look (counted over the rows that show, the separators left out), or none for -1, so it can be drawn to a picture; rows gets the text and the place of each row, and the colours of the menu are asked for again, so a second picture a moment later shows them. null while there is no tray menu
QMenu *TrayMenuForPicture(int hover, QJsonArray *rows);

} // namespace rk
