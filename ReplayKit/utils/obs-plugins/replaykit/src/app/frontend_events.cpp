// reacts to obs frontend events and wires the plugin together
#include "prelude.hpp"
#include "app/frontend_events.hpp"
#include "core/idle_memory.hpp"

#include <QDir>
#include <QFile>
#include <QFileInfo>

#include "app/clip_buffer.hpp"
#include "app/main_window_filter.hpp"
#include "app/obs_menu.hpp"
#include "app/settings_poll.hpp"
#include "chrome/caption_track_filter.hpp"
#include "chrome/title_keybind_badge.hpp"
#include "core/app_icon.hpp"
#include "core/cef_host.hpp"
#include "core/ipc_pipe.hpp"
#include "core/main_window.hpp"
#include "core/open_clips_hotkey.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "layout/controls_dock.hpp"
#include "layout/ui_layout.hpp"
#include "notify/notify_button.hpp"
#include "notify/notify_panel.hpp"
#include "ui/anchored_panel.hpp"
#include "ui/popup_host.hpp"
#include "shell/shell.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_storage_modal.hpp"
#include "tray/tray_menu.hpp"

namespace rk {

namespace {

// synchronously deletes our cef widgets before obs_module_unload, mirroring how OBSBasic.cpp deletes extraBrowsers early, to dodge upstream race obsproject/obs-browser#353 where cef can still be mid-teardown when the browser-manager thread is joined -- direct delete not deleteLater becuase a deferred one wouldnt run before applicationShutdown anyway
void CloseCefWidgetsBeforeShutdown()
{
	IdleMemory::Stop();
	AnchoredPanel::DestroyAll();
	StorageModalDestroy();
	NotifyReaderDestroy();
	PopupHost::Destroy();
	if (g_shellTimer)
		g_shellTimer->stop();
	if (g_shellHost)
		delete g_shellHost.data();
	if (g_setupWindow)
		delete g_setupWindow.data();
	if (g_clipsWindow)
		delete g_clipsWindow.data();
	if (g_settingsWindow)
		delete g_settingsWindow.data();
	if (g_prewarmWindow)
		delete g_prewarmWindow.data();
}

// obs saves a screenshot where it saves recordings, which is the clip folder; it is moved into the Screenshots folder there, the one the clips page lists, keeping the name obs gave it. a file obs still holds is tried again a few times, and a Screenshots folder that is a link is left alone the way the helper leaves it alone
void FileScreenshot(const QString &path, int tries)
{
	const QFileInfo shot(path);
	if (!shot.isFile() || shot.suffix().compare(QStringLiteral("png"), Qt::CaseInsensitive) != 0)
		return;
	const QString folderPath = shot.dir().filePath(QStringLiteral("Screenshots"));
	const QFileInfo folder(folderPath);
	if (folder.isSymLink() || folder.isJunction() || (folder.exists() && !folder.isDir())) {
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: the Screenshots folder is not a plain folder");
		return;
	}
	if (!folder.exists() && !shot.dir().mkdir(QStringLiteral("Screenshots"))) {
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: the Screenshots folder could not be made");
		return;
	}
	QString target = QDir(folderPath).filePath(shot.fileName());
	for (int n = 2; QFileInfo::exists(target) && n < 1000; ++n)
		target = QDir(folderPath).filePath(QStringLiteral("%1 (%2).png").arg(shot.completeBaseName()).arg(n));
	if (QFile::rename(path, target)) {
		blog(LOG_INFO, "[replaykit] screenshot filed in the Screenshots folder");
		return;
	}
	if (tries > 0)
		QTimer::singleShot(250, g_callbacks, [path, tries]() { FileScreenshot(path, tries - 1); });
	else
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: it could not be moved");
}

QTimer *g_projectorPublishTimer = nullptr;
QTimer *g_notifyTimer = nullptr;
NotifyTrackFilter *g_notifyTrackFilter = nullptr;

} // namespace

void OnFrontendEvent(enum obs_frontend_event event, void *)
{
	if (event == OBS_FRONTEND_EVENT_EXIT) {
		CefCloseForShutdown();
		PatchBrowserDockTitleInUserIni(); // fires after obs already wrote user.ini, so this rename sticks
		PrepareDocksForNextLaunch();
		if (g_projectorPublishTimer)
			g_projectorPublishTimer->stop();
		if (g_dockTweakTimer)
			g_dockTweakTimer->stop();
		if (g_openClipsHotkeyTimer)
			g_openClipsHotkeyTimer->stop();
		if (g_clipsGeoSaveTimer)
			g_clipsGeoSaveTimer->stop();
		if (g_obsGeoSaveTimer)
			g_obsGeoSaveTimer->stop();
		if (g_geoPollTimer)
			g_geoPollTimer->stop();
		ShellStop();
		ClipBufferShutdown();
		ClearShellBootMark();
		// final geometry capture while the windows are still up and positioned (CloseCefWidgets deletes Clips next).
		if (g_clipsWindow)
			SaveClipsWindowGeometry();
		if (g_mainWindow)
			SaveWindowGeometry("obsMainWindow", g_mainWindow);
		CloseCefWidgetsBeforeShutdown();
		return;
	}

	ClipBufferOnFrontendEvent(event);
	if (event == OBS_FRONTEND_EVENT_SCREENSHOT_TAKEN) {
		char *last = obs_frontend_get_last_screenshot();
		const QString path = QString::fromUtf8(last ? last : "");
		bfree(last);
		if (!path.isEmpty())
			FileScreenshot(path, 8);
		return;
	}
	// recording / replay-buffer state drives the red dot on the tray icon, the only surface that wears it
	// another profile has its own hotkeys and clip length
	if (event == OBS_FRONTEND_EVENT_PROFILE_CHANGED)
		RequestFullSettingsSync();
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED || event == OBS_FRONTEND_EVENT_RECORDING_STOPPED ||
	    event == OBS_FRONTEND_EVENT_RECORDING_PAUSED || event == OBS_FRONTEND_EVENT_RECORDING_UNPAUSED ||
	    event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED || event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED ||
	    event == OBS_FRONTEND_EVENT_STREAMING_STARTED || event == OBS_FRONTEND_EVENT_STREAMING_STOPPED ||
	    event == OBS_FRONTEND_EVENT_VIRTUALCAM_STARTED || event == OBS_FRONTEND_EVENT_VIRTUALCAM_STOPPED) {
		QMetaObject::invokeMethod(g_callbacks, []() { RefreshTrayIcon(); }, Qt::QueuedConnection);
		return;
	}

	if (event != OBS_FRONTEND_EVENT_FINISHED_LOADING)
		return;

	PrewarmCefBrowser();
	PublishMainWindow();
	InstallMainWindowCloseFilter();
	// obs restores its saved dock state during init, so the dock half of the layout has to wait for this event; the chrome half already ran from obs_module_post_load and is simply re-asserted here
	ApplyLayout();
	// the browser dock is built well after FINISHED_LOADING (cef init) and obs keeps re-showing the native one / re-titling ours, so this runs forever on a slow tick. each call is a cheap findChildren + a couple of string compares.
	ApplyControlsDockTweaks();
	if (!g_dockTweakTimer) {
		g_dockTweakTimer = new QTimer(g_callbacks);
		QObject::connect(g_dockTweakTimer, &QTimer::timeout, g_callbacks, []() { ApplyControlsDockTweaks(); });
		g_dockTweakTimer->start(4000);
	}
	g_openClipsHotkeyTimer = new QTimer(g_callbacks);
	QObject::connect(g_openClipsHotkeyTimer, &QTimer::timeout, g_callbacks, []() { PollHelperSettings(); });
	g_openClipsHotkeyTimer->start(1000);
	PollHelperSettings();

	// 250ms matches the helpers own poll cadence when its waiting for a projector to appear -- the pipe thread forwards this snapshot to the helper at the same rate.
	g_projectorPublishTimer = new QTimer(g_callbacks);
	QObject::connect(g_projectorPublishTimer, &QTimer::timeout, g_callbacks, []() { PublishProjectorWindows(); });
	g_projectorPublishTimer->start(250);
	PublishProjectorWindows();

	// the bell in obss own title bar. the Clips and Settings windows get theirs as they are created. the clean shell has its own title bar with the bell and keybind badges built in, so the caption overlays are only for the classic layout
	if (g_shellOn) {
		ShellStart();
	} else {
		AttachNotifyButton(g_mainWindow);
		AttachTitleKeybindBadge(g_mainWindow);
	}
	if (!g_notifyTrackFilter) {
		g_notifyTrackFilter = new NotifyTrackFilter();
		qApp->installNativeEventFilter(g_notifyTrackFilter);
	}
	// unread count, plus a slow re-position pass so a caption move no qt event covered (dpi change, a snap) still settles
	g_notifyTimer = new QTimer(g_callbacks);
	QObject::connect(g_notifyTimer, &QTimer::timeout, g_callbacks, []() {
		PollNotificationCount();
		PositionAllNotifyButtons();
		PositionAllTitleKeybindBadges();
	});
	g_notifyTimer->start(3000);
	PollNotificationCount();

	InstallTrayMenu();
	// the menus of the obs menu bar are polished before anybody opens one, once the interface has settled
	QTimer::singleShot(3000, g_callbacks, []() { PrepareObsMenus(); });
}

// the timer is a child of the callback object, which is deleted on unload, so only the pointer is dropped here
void ForgetProjectorTimer()
{
	g_projectorPublishTimer = nullptr;
}

// the app-wide filter would otherwise keep running against the button and badge lists after unload
void RemoveNotifyTrackFilter()
{
	if (!g_notifyTrackFilter)
		return;
	if (qApp)
		qApp->removeNativeEventFilter(g_notifyTrackFilter);
	delete g_notifyTrackFilter;
	g_notifyTrackFilter = nullptr;
}

} // namespace rk
