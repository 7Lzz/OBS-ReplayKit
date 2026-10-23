// obs module entry points of the native replaykit plugin -- everything else lives in the rk namespace under src/ (the window shell, tray menu, notification bell, hotkeys and the pipe to the helper)
#include "prelude.hpp"

#include "app/clip_buffer.hpp"
#include "app/frontend_events.hpp"
#include "app/main_window_filter.hpp"
#include "app/sdk_commands.hpp"
#include "core/async.hpp"
#include "core/crash_reporter.hpp"
#include "core/ipc_pipe.hpp"
#include "core/main_window.hpp"
#include "core/open_clips_hotkey.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "layout/ui_layout.hpp"
#include "overlays/bongo.hpp"
#include "shell/shell.hpp"

OBS_DECLARE_MODULE()

bool obs_module_load(void)
{
	using namespace rk;
	RegisterBongo();
	g_callbacks = new QObject();
	ClipBufferInit();
	StartWorkers();
	StartReplayKitCrashReporter();
	InstallOpenClipsHotkeyFilter();
	obs_frontend_add_event_callback(OnFrontendEvent, nullptr);
	StartPipeServer();
	return true;
}

// runs inside obs init, after every module has loaded but before the main window is first shown, which is the one point the frameless flag can still be set without qt recreating the native window. the layout for this whole process is decided here (see g_shellOn); the parts of the clean layout that need no dock (menu bar, preview strip, stage margin, title bar, rail) land before the first paint instead of flashing the stock chrome. the docks themselves wait for FINISHED_LOADING
void obs_module_post_load(void)
{
	using namespace rk;
	InitLayoutAtStartup();
	if (g_wantedLayout == UiLayout::Clean && !g_shellBootSkipped) {
		QSettings(WindowStateIniPath(), QSettings::IniFormat).setValue("shellBootPending", true);
		if (ShellEnableFrame(ObsMainWindowQt())) {
			ApplyLayoutChrome(true);
			ShellBuild();
		}
	}
}

void obs_module_unload(void)
{
	using namespace rk;
	obs_frontend_remove_event_callback(OnFrontendEvent, nullptr);
	SdkStopFocusTrace();
	StopPipeServer();
	StopWorkers();
	// destroy queued callbacks before their code is unloaded with this module.
	delete g_callbacks;
	g_callbacks = nullptr;
	ForgetProjectorTimer();
	RemoveNotifyTrackFilter();
	RemoveOpenClipsHotkey();
	ClipBufferShutdown();
	RemoveMainWindowCloseFilter();
	StopReplayKitCrashReporter();
}
