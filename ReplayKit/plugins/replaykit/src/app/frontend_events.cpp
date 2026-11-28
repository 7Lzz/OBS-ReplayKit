// reacts to obs frontend events and wires the plugin together
#include "prelude.hpp"
#include "app/frontend_events.hpp"
#include "core/idle_memory.hpp"

#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>

#include "app/clip_buffer.hpp"
#include "app/capture_context.hpp"
#include "capture/capture_switch.hpp"
#include "app/main_window_filter.hpp"
#include "app/obs_menu.hpp"
#include "app/obs_requests.hpp"
#include "app/settings_poll.hpp"
#include "app/single_instance.hpp"
#include "app/tools_actions.hpp"
#include "chrome/caption_track_filter.hpp"
#include "chrome/title_keybind_badge.hpp"
#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/cef_host.hpp"
#include "core/helper_client.hpp"
#include "core/helper_launcher.hpp"
#include "core/ipc_pipe.hpp"
#include "core/json_lite.hpp"
#include "core/main_window.hpp"
#include "core/open_clips_hotkey.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "layout/controls_dock.hpp"
#include "layout/ui_layout.hpp"
#include "overlays/focus_card.hpp"
#include "core/graphics_device.hpp"
#include "notify/clip_sound.hpp"
#include "notify/clip_toast.hpp"
#include "notify/notify_button.hpp"
#include "share/share_link.hpp"
#include "share/share_monitoring.hpp"
#include "notify/notify_panel.hpp"
#include "ui/anchored_panel.hpp"
#include "ui/popup_host.hpp"
#include "shell/shell.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_done_popup.hpp"
#include "app/encoder_guard.hpp"
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
	DonePopupDestroy();
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

// file the screenshot where the gallery of the helper looks, even if obs saved it in a subfolder
void FileScreenshot(const QString &path, const QString &folderPath, int tries, qint64 captureAt)
{
	const QFileInfo shot(path);
	if (!shot.isFile() || shot.isSymLink() || shot.isJunction() || shot.suffix().compare(QStringLiteral("png"), Qt::CaseInsensitive) != 0)
		return;
	if (!QDir::isAbsolutePath(folderPath)) {
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: the clip library is unavailable");
		return;
	}
	if (QDir::cleanPath(shot.absolutePath()).compare(QDir::cleanPath(folderPath), Qt::CaseInsensitive) == 0) {
		CaptureContextFile(shot.absoluteFilePath().toStdString(), captureAt);
		return;
	}
	QFileInfo folder(folderPath);
	if (folder.isSymLink() || folder.isJunction() || (folder.exists() && !folder.isDir())) {
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: the Screenshots folder is not a plain folder");
		return;
	}
	if (!folder.exists() && !folder.absoluteDir().mkdir(folder.fileName()) && !QFileInfo(folderPath).isDir()) {
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: the Screenshots folder could not be made");
		return;
	}
	folder.refresh();
	if (!folder.isDir() || folder.isSymLink() || folder.isJunction())
		return;
	QString target = QDir(folderPath).filePath(shot.fileName());
	for (int n = 2; QFileInfo::exists(target) && n < 1000; ++n)
		target = QDir(folderPath).filePath(QStringLiteral("%1 (%2).png").arg(shot.completeBaseName()).arg(n));
	if (QFile::rename(path, target)) {
		CaptureContextFile(target.toStdString(), captureAt);
		blog(LOG_INFO, "[replaykit] screenshot filed in the Screenshots folder");
		return;
	}
	if (tries > 0)
		QTimer::singleShot(250, g_callbacks, [path, folderPath, tries, captureAt]() { FileScreenshot(path, folderPath, tries - 1, captureAt); });
	else
		blog(LOG_WARNING, "[replaykit] screenshot left where obs saved it: it could not be moved");
}

QTimer *g_notifyTimer = nullptr;
NotifyTrackFilter *g_notifyTrackFilter = nullptr;
// the notify and dock ticks stopped while the window is hidden, which showing it again starts; never set again once obs is closing
bool g_hiddenHeld = false;
bool g_exiting = false;
bool g_recStartNotified = false;
bool g_recStopNotified = false;
bool g_recRunning = false;

// a slow re-position pass so a caption move no qt event covered (dpi change, a snap) still settles; the unread count comes from the helper, which says when it changes
void NotifyTick()
{
	PositionAllNotifyButtons();
	PositionAllTitleKeybindBadges();
}

// the helper finishes a file obs has just written (the silent discord track, the hevc tag); a helper that is down at that moment finds the clip on its next pass through the library
void TellHelperFileSaved(const char *path)
{
	const QString file = QString::fromUtf8(path ? path : "");
	if (file.isEmpty())
		return;
	const std::string body = QJsonDocument(QJsonObject{{"path", QDir::toNativeSeparators(file)}}).toJson(QJsonDocument::Compact).toStdString();
	RunAsync([body]() { HttpRequest("POST", "/clips/saved", body.c_str(), 3500); });
}

} // namespace

void OnFrontendEvent(enum obs_frontend_event event, void *)
{
	if (event == OBS_FRONTEND_EVENT_SCRIPTING_SHUTDOWN) {
		g_exiting = true;
		CaptureSwitchStop();
		StopSingleInstanceWatch();
		StopGraphicsWatch();
		ShareLinkStop();
		FocusCardStop();
		ClipBufferShutdown();
		return;
	}
	if (event == OBS_FRONTEND_EVENT_EXIT) {
		g_exiting = true;
		ObsRequestsStop();
		StopSingleInstanceWatch();
		CaptureContextStop();
		CefCloseForShutdown();
		PatchBrowserDockTitleInUserIni(); // fires after obs already wrote user.ini, so this rename sticks
		PrepareDocksForNextLaunch();
		if (g_dockTweakTimer)
			g_dockTweakTimer->stop();
		if (g_clipsGeoSaveTimer)
			g_clipsGeoSaveTimer->stop();
		if (g_obsGeoSaveTimer)
			g_obsGeoSaveTimer->stop();
		if (g_geoPollTimer)
			g_geoPollTimer->stop();
		ShellStop();
		FocusCardStop();
		ClipBufferShutdown();
		ClearShellBootMark();
		// final geometry capture while the windows are still up and positioned (CloseCefWidgets deletes Clips next).
		if (g_clipsWindow)
			SaveClipsWindowGeometry();
		if (g_mainWindow)
			SaveWindowGeometry("obsMainWindow", g_mainWindow);
		CloseCefWidgetsBeforeShutdown();
		HelperLauncherStop();
		return;
	}

	ClipBufferOnFrontendEvent(event);
	CaptureContextEvent(event);
	if (event == OBS_FRONTEND_EVENT_SCENE_COLLECTION_CHANGED)
		FocusCardEnsureScenes();
	if (event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED) {
		if (!ClipBufferConsumePredictedSave()) {
			const double duration = ClipBufferPredictedMemoryDuration();
			ClipSoundPlay(ClipSound::ClipSaved, [duration]() { ClipToastShow(ClipToastKind::Clip, duration); });
		}
		char *path = obs_frontend_get_last_replay();
		CaptureContextReplayFile(path ? path : "");
		TellHelperFileSaved(path);
		bfree(path);
	}
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTING) {
		g_recStartNotified = true;
		g_recStopNotified = false;
		ClipSoundPlay(ClipSound::RecordingStarted, []() { ClipToastShow(ClipToastKind::RecordingStarted); });
	}
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED) {
		if (!g_recStartNotified)
			ClipSoundPlay(ClipSound::RecordingStarted, []() { ClipToastShow(ClipToastKind::RecordingStarted); });
		g_recStartNotified = false;
		g_recStopNotified = false;
		g_recRunning = true;
	}
	if (event == OBS_FRONTEND_EVENT_RECORDING_STOPPING) {
		if (!g_recStopNotified) {
			ClipSoundPlay(ClipSound::RecordingStopped, []() { ClipToastShow(ClipToastKind::RecordingStopped); });
			g_recStopNotified = true;
		}
	}
	if (event == OBS_FRONTEND_EVENT_RECORDING_STOPPED) {
		if (g_recRunning && !g_recStopNotified)
			ClipSoundPlay(ClipSound::RecordingStopped, []() { ClipToastShow(ClipToastKind::RecordingStopped); });
		g_recStartNotified = false;
		g_recStopNotified = false;
		g_recRunning = false;
		char *path = obs_frontend_get_last_recording();
		TellHelperFileSaved(path);
		bfree(path);
	}
	if (event == OBS_FRONTEND_EVENT_SCREENSHOT_TAKEN) {
		const qint64 captureAt = qint64(GetTickCount64());
		char *last = obs_frontend_get_last_screenshot();
		const QString path = QString::fromUtf8(last ? last : "");
		bfree(last);
		if (!path.isEmpty())
			RunAsync([path, captureAt]() {
				const QString folder = QString::fromStdString(ExtractJsonStringField(HttpRequest("GET", "/screenshots/folder", nullptr, 3500), "folder"));
				QMetaObject::invokeMethod(g_callbacks, [path, folder, captureAt]() { FileScreenshot(path, folder, 8, captureAt); }, Qt::QueuedConnection);
			});
		return;
	}
	// another profile has its own hotkeys and clip length
	if (event == OBS_FRONTEND_EVENT_PROFILE_CHANGED)
		RequestFullSettingsSync();
	// recording / replay-buffer state drives the red dot on the tray icon, the only surface that wears it
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED || event == OBS_FRONTEND_EVENT_RECORDING_STOPPED ||
	    event == OBS_FRONTEND_EVENT_RECORDING_PAUSED || event == OBS_FRONTEND_EVENT_RECORDING_UNPAUSED ||
	    event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED || event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED ||
	    event == OBS_FRONTEND_EVENT_STREAMING_STARTED || event == OBS_FRONTEND_EVENT_STREAMING_STOPPED ||
	    event == OBS_FRONTEND_EVENT_VIRTUALCAM_STARTED || event == OBS_FRONTEND_EVENT_VIRTUALCAM_STOPPED) {
		QMetaObject::invokeMethod(g_callbacks, []() {
			RefreshTrayIcon();
			// the shell tick counts the recording time, so a recording keeps it going while the window is hidden
			MainWindowShownChanged(g_mainWindow);
		}, Qt::QueuedConnection);
		return;
	}

	if (event != OBS_FRONTEND_EVENT_FINISHED_LOADING)
		return;

	PrewarmCefBrowser();
	ReportEncoders();
	CaptureContextStart();
	FocusCardStart();
	ShareMonitoringStart();
	CaptureSwitchStart();
	ToolsActionsAddMenu();
	// the clip buffer the settings ask for, in memory or on disk, starts once obs has finished loading
	QMetaObject::invokeMethod(g_callbacks, []() { ClipBufferStart(); }, Qt::QueuedConnection);
	PublishMainWindow();
	InstallMainWindowCloseFilter();
	StartSingleInstanceWatch();
	// obs restores its saved dock state during init, so the dock half of the layout has to wait for this event; the chrome half already ran from obs_module_post_load and is simply re-asserted here
	ApplyLayout();
	// the browser dock is built well after FINISHED_LOADING (cef init) and obs keeps re-showing the native one / re-titling ours, so this runs on a slow tick while the window shows. each call is a cheap findChildren + a couple of string compares.
	ApplyControlsDockTweaks();
	if (!g_dockTweakTimer) {
		g_dockTweakTimer = new QTimer(g_callbacks);
		QObject::connect(g_dockTweakTimer, &QTimer::timeout, g_callbacks, []() { ApplyControlsDockTweaks(); });
		g_dockTweakTimer->start(4000);
	}
	SettingsPollStart();

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
	g_notifyTimer = new QTimer(g_callbacks);
	QObject::connect(g_notifyTimer, &QTimer::timeout, g_callbacks, NotifyTick);
	g_notifyTimer->start(3000);
	PollNotificationCount();

	InstallTrayMenu();
	// the menus of the obs menu bar are polished before anybody opens one, once the interface has settled
	QTimer::singleShot(3000, g_callbacks, []() { PrepareObsMenus(); });
	// obs can start hidden in the tray, which sends no hide event once the ticks exist
	MainWindowShownChanged(g_mainWindow);
}

void MainWindowShownChanged(QWidget *window)
{
	if (g_exiting || !window)
		return;
	const bool shown = window->isVisible() && !window->isMinimized();
	ShellSetWindowShown(shown);
	if (!g_notifyTimer || !g_dockTweakTimer)
		return;
	if (!shown) {
		if (!g_hiddenHeld) {
			g_notifyTimer->stop();
			g_dockTweakTimer->stop();
			g_hiddenHeld = true;
		}
		return;
	}
	if (!g_hiddenHeld)
		return;
	g_hiddenHeld = false;
	g_notifyTimer->start();
	g_dockTweakTimer->start();
	ApplyControlsDockTweaks();
	NotifyTick();
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
