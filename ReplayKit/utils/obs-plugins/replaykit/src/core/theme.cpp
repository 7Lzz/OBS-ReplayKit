// theme colours the native chrome paints with
#include "prelude.hpp"
#include "core/theme.hpp"

namespace rk {

QColor g_notifySurface = QColor(0x2F, 0x32, 0x3C);
QColor g_notifyBorder = QColor(0x46, 0x4B, 0x59);
QColor g_notifyTipBg = QColor(0x13, 0x14, 0x1A);
QColor g_notifyTipFg = QColor(0xFF, 0xFF, 0xFF);
QColor g_menuAccent = QColor(0x28, 0x4C, 0xB8);
QColor g_menuAccentLight = QColor(0x47, 0x6B, 0xD7);
QColor g_menuOnAccent = QColor(0xFF, 0xFF, 0xFF);
QColor g_menuPanel = QColor(0x27, 0x2A, 0x33);
QColor g_menuField = QColor(0x3C, 0x40, 0x4D);
QColor g_menuFieldHover = QColor(0x46, 0x4B, 0x59);
QColor g_menuBorderStrong = QColor(0x5B, 0x62, 0x73);
QColor g_menuDanger = QColor(0xE3, 0x3B, 0x57);
QColor g_menuMuted = QColor(0x96, 0x96, 0x96);
QColor g_chromeBg = QColor(0x1E, 0x1F, 0x22);
QColor g_chromeBar = QColor(0x16, 0x16, 0x18);
QColor g_chromeSuccess = QColor(0x23, 0xA5, 0x5A);
QColor g_chromeWarning = QColor(0xE2, 0xA3, 0x3B);

// ink for the chrome is the theme text colour at some opacity, so every theme (light ones included) reads without a colour of its own per state
QColor ChromeInk(qreal alpha)
{
	QColor c = g_notifyTipFg;
	c.setAlphaF(alpha);
	return c;
}

} // namespace rk
