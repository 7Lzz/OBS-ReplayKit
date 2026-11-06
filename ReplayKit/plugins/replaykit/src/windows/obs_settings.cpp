// opens the obs settings dialog
#include "prelude.hpp"
#include "windows/obs_settings.hpp"

#include "core/state.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"

namespace rk {

QElapsedTimer g_obsBuildTimer;

// triggers OBSs own Settings dialog for the merged controls dock. the action is named "action_Settings" in window-basic-main.ui (locale-independent); fall back to the auto-connected private slot if the name ever changes.
void OpenObsSettingsRaw()
{
	if (!g_obsBuildTimer.isValid())
		g_obsBuildTimer.start();
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	if (QAction *act = mw->findChild<QAction *>("action_Settings")) {
		act->trigger();
		return;
	}
	QMetaObject::invokeMethod(mw, "on_action_Settings_triggered", Qt::QueuedConnection);
}

// in the clean layout the dialog is folded into the main window (see ObsSettingsEmbedFilter), so the shell decides when and where it opens
void OpenObsSettings()
{
	if (g_shellOn) {
		ShellOpenView(ShellView::ObsSettings, false);
		return;
	}
	OpenObsSettingsRaw();
}

} // namespace rk
