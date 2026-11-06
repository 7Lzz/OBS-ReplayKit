// opens one of the obs menu bar menus on request
#include "prelude.hpp"
#include "app/obs_menu.hpp"

#include "core/main_window.hpp"

namespace rk {

namespace {

// the menus of the menu bar in the order the bar holds them
QList<QMenu *> BarMenus()
{
	QList<QMenu *> menus;
	QMainWindow *mw = ObsMainWindowQt();
	QWidget *bar = mw ? mw->menuWidget() : nullptr;
	if (!bar)
		return menus;
	for (QAction *action : bar->actions()) {
		if (action->menu())
			menus.append(action->menu());
	}
	return menus;
}

} // namespace

// the menu bar of obs collapses to nothing in the clean layout, and its menus are opened from the advanced tab of the replaykit settings instead. they are the menus the bar holds, so profiles, scene collections and the dock list keep their live contents, and they drop open at the pointer, which is still over the button that asked
void ShowObsMenu(int index)
{
	const QList<QMenu *> menus = BarMenus();
	if (index < 0)
		return;
	if (index >= menus.size()) {
		blog(LOG_WARNING, "[replaykit] no obs menu at position %d, the bar holds %d", index, int(menus.size()));
		return;
	}
	// only Tools and Help open from here; the others rearrange the window, its docks, profiles and scene collections underneath the replaykit layout, and the settings page greys their buttons out
	const QString name = menus[index]->objectName();
	if (name != QLatin1String("menuTools") && name != QLatin1String("menuBasic_MainMenu_Help")) {
		blog(LOG_WARNING, "[replaykit] obs menu %s is not opened from the settings page", name.toUtf8().constData());
		return;
	}
	menus[index]->popup(QCursor::pos());
}

// the first popup of a menu builds its native window and applies the style sheet while it is being shown, and a native window is the colour of its window class until the first paint reaches it, which for a menu is white. so the menus are polished, sized and drawn once here, and the class of their windows is given the colour the menu itself is drawn in
void PrepareObsMenus()
{
	const QList<QMenu *> menus = BarMenus();
	HBRUSH brush = nullptr;
	for (QMenu *menu : menus) {
		menu->ensurePolished();
		menu->sizeHint();
		const QImage drawn = menu->grab().toImage();
		if (!brush && drawn.width() > 8 && drawn.height() > 8) {
			const QColor ground = drawn.pixelColor(drawn.width() / 2, 2);
			brush = CreateSolidBrush(RGB(ground.red(), ground.green(), ground.blue()));
		}
		menu->createWinId();
		if (brush)
			SetClassLongPtrW((HWND)menu->winId(), GCLP_HBRBACKGROUND, (LONG_PTR)brush);
	}
	blog(LOG_INFO, "[replaykit] %d obs menus made ready for their first popup%s", int(menus.size()), brush ? "" : " (no colour could be read from them)");
}

} // namespace rk
