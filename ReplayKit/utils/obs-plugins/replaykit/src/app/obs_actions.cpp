// obs actions the tray menu, the dock pages and the shell trigger
#include "prelude.hpp"
#include "app/obs_actions.hpp"

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "tray/tray_menu.hpp"
#include "ui/confirm_dialog.hpp"

namespace rk {

namespace {

// generic obs-lifecycle confirm (Exit / Restart). plain QMessageBox on purpose -- it should read like obss own dialogs, not the in-obs themed sheet. non-modal + single-instance: pressing the tray item again drops the box that is showing and pops a fresh one instead of stacking or being ignored.
QPointer<QMessageBox> g_lifecycleBox;

void ShowLifecycleConfirm(const QString &title, const QString &body, const char *route)
{
	if (g_lifecycleBox)
		g_lifecycleBox->reject(); // its finished() -> deleteLater below; QPointer nulls itself

	auto *box = new QMessageBox(QMessageBox::Question, title, body, QMessageBox::Yes | QMessageBox::No, nullptr);
	box->setDefaultButton(QMessageBox::No);
	std::string r(route);
	QObject::connect(box, &QMessageBox::finished, box, [r](int result) {
		if (result == QMessageBox::Yes)
			RunAsync([r]() { HttpRequest("POST", r.c_str(), 8767, nullptr, 5000); });
	});
	QObject::connect(box, &QMessageBox::finished, box, &QObject::deleteLater);
	g_lifecycleBox = box;
	box->show();
	box->raise();
	box->activateWindow();
}

// the helper answers an action it could not start with {"ok":false,"message":...}. whoever pressed Set Up sees why nothing happened, since the confirm that started it is already gone
void ReportActionFailure(const QString &title, const std::string &response)
{
	if (JsonBoolField(response, "ok", false))
		return;
	std::string message = ExtractJsonStringField(response, "message");
	QString text = message.empty() ? QObject::tr("ReplayKit did not answer. Restart OBS and try again.") : QString::fromStdString(message);
	QMetaObject::invokeMethod(
		g_callbacks,
		[title, text]() {
			auto *box = new QMessageBox(QMessageBox::Warning, title, text, QMessageBox::Ok, nullptr);
			QObject::connect(box, &QMessageBox::finished, box, &QObject::deleteLater);
			box->show();
			box->raise();
			box->activateWindow();
		},
		Qt::QueuedConnection);
}

} // namespace

// one confirm request from the dock (via the ipc CONFIRM verb) or the tray. runs the matching action on ok. ShowReplayKitConfirm owns the no-stacking guard, so a repeat while one is open just returns false here.
void HandleConfirmRequest(const std::string &kind)
{
	if (kind == "restart-obs") {
		// the home page offers this when it cannot reach obs; RestartObs asks first and does the close and reopen through the helper
		RestartObs();
		return;
	}
	bool ok = false;
	std::string route;
	if (kind == "sharepreview-setup") {
		ok = ShowReplayKitConfirm(
			QObject::tr("Set up Discord screenshare?"),
			QObject::tr("This installs an audio cable driver and a hidden OBS projector window so Discord "
				    "and other apps can screen-share OBS's output.\n\nOBS will close while it installs."),
			QObject::tr("Set Up"), false);
		route = "/install-discord-screenshare";
	} else if (kind == "streamable-relogin") {
		ok = ShowReplayKitConfirm(
			QObject::tr("Force re-login to Streamable?"),
			QObject::tr("This closes OBS, wipes the Streamable cookies, and relaunches OBS as admin so you "
				    "can sign in as a different account.\n\nSave any unsaved scene work first."),
			QObject::tr("Continue"), true);
		route = "/restart-obs-clean";
	}
	if (!ok || route.empty())
		return;
	// the setup can first have to fetch its own setup file when that is gone, so the answer is waited for longer than the quick restart routes
	const bool setup = kind == "sharepreview-setup";
	RunAsync([route, setup]() {
		std::string response = HttpRequest("POST", route.c_str(), 8767, nullptr, setup ? 180000 : 8000);
		if (setup)
			ReportActionFailure(QObject::tr("Discord screenshare setup"), response);
	});
}

// share preview is off because the cable + hidden projector were never set up. offer to do it now -- same flow as the dock Install path; on ok the helper closes OBS, installs, and turns Share Preview on.
void PromptInstallSharePreview()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	HandleConfirmRequest("sharepreview-setup");
}

// posts to the same /share-preview route the dock uses so the projector/audio-monitoring logic stays in one place -- fire and forget, since aboutToShow re-reads the real state next open so a failed toggle just looks unchanged
void ToggleSharePreview(bool checked)
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	RunAsync([checked]() {
		std::string json = checked ? "{\"enabled\":true}" : "{\"enabled\":false}";
		std::string body = HttpRequest("POST", "/share-preview", 8767, json.c_str(), 8000);
		bool enabled = JsonBoolField(body, "enabled", !checked);
		QMetaObject::invokeMethod(
			g_callbacks,
			[enabled]() {
				if (g_sharePreviewAction)
					g_sharePreviewAction->setChecked(enabled);
			},
			Qt::QueuedConnection);
	});
}

// posts to the helpers /restart-obs, a plain close-and-reopen (not the settings pages signed-out "clean" restart) -- confirms first since it force-kills obs rather than stopping gracefully, then fires and forgets since obs is about to die anyway.
void RestartObs()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	ShowLifecycleConfirm(
		QObject::tr("Restart OBS"),
		QObject::tr("This will close and reopen OBS. Any active recording or stream will be stopped. Continue?"),
		"/restart-obs");
}

// opens obss fixed crash-report folder directly in explorer rather than guessing "the latest one", mkpath first since a machine that never crashed wont have the folder yet
void OpenCrashLogsFolder()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	QString path = qEnvironmentVariable("APPDATA") + "/obs-studio/crashes";
	QDir().mkpath(path);
	QDesktopServices::openUrl(QUrl::fromLocalFile(path));
}

// obss own exit never confirmed and sits right where an upward-opening menu leaves the cursor, an easy accidental click the debounce cant catch since its aimed right, not just early -- same confirm-first shape as RestartObs. posts to the helpers /exit-obs instead of calling mainWindow->close() directly so this gets the same graceful-close-then-force-kill-if-needed handling restart already gets (see Stop-ReplayKitObsForRestart), which is what actually lets obs save window position/state on the way out.
void ConfirmedExit()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	ShowLifecycleConfirm(
		QObject::tr("Exit OBS"),
		QObject::tr("This will close OBS. Any active recording or stream will be stopped. Continue?"),
		"/exit-obs");
}

// obss own isEnabled() on sysTrayRecord/sysTrayReplayBuffer cannot be trusted (confirmed via logging: both reported enabled=0 at click time despite obs_frontend_..._active() proving the underlying feature was genuinely toggleable -- obs only calls setEnabled on sysTrayReplayBuffer from inside ResetOutputs, gated on whether the output handler happened to already have a replay buffer object at that exact moment, never re-enabled later, and sysTrayRecord has no setEnabled call anywhere in obs at all), so these drive the toggle directly through the same stable public entry points obs itself and every obs-websocket-style integration use, instead of routing through the actions own (occasionally-lying) enabled/triggered state.
void ToggleRecording()
{
	bool active = obs_frontend_recording_active();
	blog(LOG_INFO, "[replaykit] ToggleRecording: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_recording_stop() : obs_frontend_recording_start();
}

void ToggleReplayBuffer()
{
	bool active = obs_frontend_replay_buffer_active();
	blog(LOG_INFO, "[replaykit] ToggleReplayBuffer: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_replay_buffer_stop() : obs_frontend_replay_buffer_start();
}

} // namespace rk
