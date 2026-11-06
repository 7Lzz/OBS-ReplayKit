// opens the obs settings dialog
#pragma once

#include <QElapsedTimer>

namespace rk {

// counted from the request to open the obs settings, so the log shows where the seconds of building the dialog go
extern QElapsedTimer g_obsBuildTimer;
void OpenObsSettingsRaw();
void OpenObsSettings();

} // namespace rk
