// opens one of the obs menu bar menus on request
#pragma once

namespace rk {

// the menus of the obs menu bar in the order the bar holds them (File, Edit, View, Docks, Profile, Scene Collection, Tools, Help); the one at this position drops open at the pointer
void ShowObsMenu(int index);
// polishes the menus of the obs menu bar and gives their native windows the colour of the menu, so the first popup of each is not a white frame; runs once, after obs has loaded
void PrepareObsMenus();

} // namespace rk
