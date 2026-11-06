// theme colours the native chrome paints with
#pragma once

#include <QColor>
#include <QtGlobal>

namespace rk {

// the dock --grey5 / --grey3 pair the clips sort trigger paints with, resolved from the active appearance theme by the helper and refreshed on the same /settings poll the tray rows use. stock-theme values until that first answer lands.
extern QColor g_notifySurface;
extern QColor g_notifyBorder;
extern QColor g_notifyTipBg;
extern QColor g_notifyTipFg;
// resolved theme colours from the same /settings menuColors, for the confirm dialog. stock values until the first poll. panel/field/fieldHover/borderStrong/danger/muted mirror the dock .btn tokens so the dialog matches the rest of the ui.
extern QColor g_menuAccent;
extern QColor g_menuAccentLight;
extern QColor g_menuOnAccent;
extern QColor g_menuPanel;
extern QColor g_menuField;
extern QColor g_menuFieldHover;
extern QColor g_menuBorderStrong;
extern QColor g_menuDanger;
extern QColor g_menuMuted;
// the clean shell chrome: window ground, the ground of the title bar and rail, and the green of a live status light, resolved from the same menuColors
extern QColor g_chromeBg;
extern QColor g_chromeBar;
extern QColor g_chromeSuccess;
// the amber of the warning colour, the one the left of the input sensitivity meter has
extern QColor g_chromeWarning;
QColor ChromeInk(qreal alpha);

} // namespace rk
