// places our controls dock, hides the native one and keeps the dock title renamed
#pragma once

#include <QTimer>

namespace rk {

void ApplyControlsDockTweaks();
void PatchBrowserDockTitleInUserIni();

extern QTimer *g_dockTweakTimer;

} // namespace rk
