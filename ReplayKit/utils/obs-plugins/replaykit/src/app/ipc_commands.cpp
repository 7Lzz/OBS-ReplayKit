// what each line the helper sends over the pipe does
#include "prelude.hpp"
#include "app/ipc_commands.hpp"

#include "app/obs_actions.hpp"
#include "app/obs_menu.hpp"
#include "app/sdk_commands.hpp"
#include "app/settings_poll.hpp"
#include "core/app_icon.hpp"
#include "core/ipc_pipe.hpp"
#include "core/state.hpp"
#include "core/window_state.hpp"
#include "notify/notify_button.hpp"
#include "shell/shell_grip.hpp"
#include "shell/shell_history.hpp"
#include "shell/shell_notify_reader.hpp"
#include "shell/shell_page.hpp"
#include "shell/shell_state.hpp"
#include "shell/shell_storage_modal.hpp"
#include "shell/shell_types.hpp"
#include "shell/shell_views.hpp"
#include "ui/anchored_panel.hpp"
#include "windows/clips_window.hpp"
#include "windows/obs_settings.hpp"
#include "windows/settings_window.hpp"
#include "windows/setup_window.hpp"

namespace rk {

void PipeDispatchLine(const std::string &line)
{
	if (line == "OPENCLIPS") {
		QMetaObject::invokeMethod(g_callbacks, []() { ShowClips(); }, Qt::QueuedConnection);
	} else if (line == "OPENSETTINGS" || line.rfind("OPENSETTINGS ", 0) == 0) {
		// an optional tab to land on. the name goes into a script for the shell page, so only a short lowercase word passes and anything else opens the settings where they were left
		const std::string page = line.size() > 13 ? line.substr(13) : std::string();
		const bool valid = !page.empty() && page.size() <= 20 && std::all_of(page.begin(), page.end(), [](unsigned char c) { return c >= 'a' && c <= 'z'; });
		const QString tab = valid ? QString::fromStdString(page) : QString();
		QMetaObject::invokeMethod(g_callbacks, [tab]() {
			// a menu that asked for this is done with its job, and would sit on top of the page it opened
			AnchoredPanel::HideAll();
			if (g_shellOn && !tab.isEmpty())
				ShellOpenSettingsPage(tab);
			else
				ShowSettings();
		}, Qt::QueuedConnection);
	} else if (line == "OPENSTORAGE") {
		QMetaObject::invokeMethod(g_callbacks, []() { StorageModalOpen(); }, Qt::QueuedConnection);
	} else if (line == "CLOSESTORAGE") {
		QMetaObject::invokeMethod(g_callbacks, []() { StorageModalClose(); }, Qt::QueuedConnection);
	} else if (line == "GROUPINPUTOVERLAY") {
		QMetaObject::invokeMethod(g_callbacks, []() { GroupInputOverlay(); }, Qt::QueuedConnection);
	} else if (line == "OPENOBSSETTINGS") {
		QMetaObject::invokeMethod(g_callbacks, []() { OpenObsSettings(); }, Qt::QueuedConnection);
	} else if (line.rfind("OBSMENU ", 0) == 0) {
		// the position of one of the obs menu bar menus, one or two digits
		const std::string digits = line.substr(8);
		if (!digits.empty() && digits.size() <= 2 && std::all_of(digits.begin(), digits.end(), [](unsigned char c) { return c >= '0' && c <= '9'; })) {
			const int index = std::stoi(digits);
			QMetaObject::invokeMethod(g_callbacks, [index]() { ShowObsMenu(index); }, Qt::QueuedConnection);
		}
	} else if (line.rfind("SDK ", 0) == 0) {
		// a command of the local command channel; it is queued to the interface thread and its answer goes back to the helper over http
		SdkDispatch(line.substr(4));
	} else if (line == "OPENSETUP") {
		QMetaObject::invokeMethod(g_callbacks, []() { ShowSetup(false); }, Qt::QueuedConnection);
	} else if (line == "OPENSETUPQUIET") {
		QMetaObject::invokeMethod(g_callbacks, []() { ShowSetup(true); }, Qt::QueuedConnection);
	} else if (line.rfind("SETICON ", 0) == 0) {
		QString iconPath = QString::fromUtf8(line.substr(8).c_str());
		QMetaObject::invokeMethod(g_callbacks, [iconPath]() { ApplyAppIcon(iconPath); }, Qt::QueuedConnection);
	} else if (line.rfind("SETICONDOT ", 0) == 0) {
		bool on = line.substr(11) != "0";
		QMetaObject::invokeMethod(
			g_callbacks, [on]() { g_recordingDotEnabled = on; RefreshTrayIcon(); }, Qt::QueuedConnection);
	} else if (line.rfind("RKICON ", 0) == 0) {
		QString p = QString::fromUtf8(line.substr(7).c_str());
		QMetaObject::invokeMethod(
			g_callbacks, [p]() { g_rkIconPath = p; RefreshAppIcon(); }, Qt::QueuedConnection);
	} else if (line.rfind("CLIPSFULLSCREEN ", 0) == 0) {
		bool active = line.substr(16) == "1";
		QMetaObject::invokeMethod(g_callbacks, [active]() {
			if (g_shellOn) {
				ShellSetClipsFullscreen(active);
				return;
			}
			g_clipsFullscreenActive = active;
			if (!active) ScheduleClipsGeometrySave();
		}, Qt::QueuedConnection);
	} else if (line.rfind("CLOSEVIEW ", 0) == 0) {
		// the helper relays a page asking to close itself (settings OK / Cancel, the wizard finishing); only meaningful for the pages the clean shell embeds
		const std::string which = line.substr(10);
		QMetaObject::invokeMethod(g_callbacks, [which]() {
			if (!g_shellOn)
				return;
			if (which == "settings")
				ShellCloseView(ShellView::Settings);
			else if (which == "setup") {
				SetupPendingChanged(false);
				ShellCloseView(ShellView::Setup);
			} else if (which == "clips")
				ShellCloseView(ShellView::Clips);
		}, Qt::QueuedConnection);
	} else if (line.rfind("VIEWSHOWN ", 0) == 0) {
		// the shell browser reporting which page it now shows, so it is lifted above the preview only once that page is really on screen
		const std::string which = line.substr(10);
		QMetaObject::invokeMethod(g_callbacks, [which]() {
			if (g_shellOn)
				ShellHostShown(which);
		}, Qt::QueuedConnection);
	} else if (line.rfind("NOTIFYREADER ", 0) == 0) {
		const std::string id = line.substr(13);
		const bool safe = !id.empty() && id.size() <= 64 && std::all_of(id.begin(), id.end(), [](char c) { return std::isalnum((unsigned char)c) || c == '-' || c == '_'; });
		if (safe) {
			QMetaObject::invokeMethod(g_callbacks, [id]() {
				// the card sits over the obs window, which only the clean layout keeps the bell on; the classic layout has bells on the clips and settings windows too, so there the panel opens the row itself
				if (!g_shellOn) {
					for (const QPointer<NotifyButton> &bell : g_notifyButtons) {
						if (bell && bell->Panel() && bell->Panel()->IsOpen())
							bell->Panel()->RunScript(QString::fromStdString("window.__replaykitReaderUnavailable && window.__replaykitReaderUnavailable('" + id + "');"));
					}
					return;
				}
				NotifyReaderOpen(id);
			}, Qt::QueuedConnection);
		}
	} else if (line == "NOTIFYREADERCLOSE") {
		QMetaObject::invokeMethod(g_callbacks, []() { NotifyReaderClose(); }, Qt::QueuedConnection);
	} else if (line.rfind("SETTINGSDIRTY ", 0) == 0) {
		const bool dirty = line.substr(14) == "1";
		QMetaObject::invokeMethod(g_callbacks, [dirty]() { g_settingsDirty = dirty; }, Qt::QueuedConnection);
	} else if (line.rfind("SETTINGSLEAVE ", 0) == 0) {
		// the answer of the unsaved-changes prompt of the settings page, with the view that was held back
		const std::string target = line.substr(14);
		QMetaObject::invokeMethod(g_callbacks, [target]() {
			if (g_shellOn)
				ShellLeaveSettings(target);
		}, Qt::QueuedConnection);
	} else if (line.rfind("SHELLNAV ", 0) == 0) {
		// a page of the shell saw alt + an arrow, or a side button of the mouse: the history is kept here, so the page only says which way
		const std::string which = line.substr(9);
		const int direction = which == "back" ? -1 : which == "forward" ? 1 : 0;
		if (direction != 0) {
			QMetaObject::invokeMethod(g_callbacks, [direction]() {
				if (g_shellOn)
					ShellHistoryRequest(direction);
			}, Qt::QueuedConnection);
		}
	} else if (line.rfind("SHELLPONG ", 0) == 0) {
		// the shell page answering a ping of the plugin, with the number it was sent with
		const std::string digits = line.substr(10);
		if (!digits.empty() && digits.size() <= 9 && std::all_of(digits.begin(), digits.end(), [](unsigned char c) { return c >= '0' && c <= '9'; })) {
			const int n = std::stoi(digits);
			QMetaObject::invokeMethod(g_callbacks, [n]() { ShellHostPong(n); }, Qt::QueuedConnection);
		}
	} else if (line == "POLLSETTINGS") {
		// a theme being previewed in the settings tab: read the colours now instead of on the next second
		QMetaObject::invokeMethod(g_callbacks, []() { PollHelperSettings(); }, Qt::QueuedConnection);
	} else if (line == "RELEASEOUTPUTS") {
		// the helper is about to start the replay buffer or a recording through obs-websocket and waits for the ack
		QMetaObject::invokeMethod(g_callbacks, []() {
			ShellStartOutputWhenFree([]() { g_pipeSendOutputsReleasedAck.store(true); });
		}, Qt::QueuedConnection);
	} else if (line == "SETTINGSAPPLIED") {
		QMetaObject::invokeMethod(g_callbacks, []() {
			if (g_shellOn)
				ShellDropParkedObsSettings();
		}, Qt::QueuedConnection);
	} else if (line.rfind("CONFIRM ", 0) == 0) {
		std::string kind = line.substr(8);
		QMetaObject::invokeMethod(
			g_callbacks, [kind]() { HandleConfirmRequest(kind); }, Qt::QueuedConnection);
	} else if (line == "SHELLRESIZE right" || line == "SHELLRESIZE bottom" || line == "SHELLRESIZE corner") {
		const Qt::Edges edges = line == "SHELLRESIZE right" ? Qt::Edges(Qt::RightEdge) : line == "SHELLRESIZE bottom" ? Qt::Edges(Qt::BottomEdge) : (Qt::RightEdge | Qt::BottomEdge);
		QMetaObject::invokeMethod(g_callbacks, [edges]() { ShellStartEdgeResize(edges); }, Qt::QueuedConnection);
	} else if (line == "ALLOWCLOSE") {
		// a real restart/exit is coming -- let the next WM_CLOSE through the close-to-tray filter for 60s, and ack so the helper knows the filter saw it before it posts the close.
		g_allowCloseUntilMs.store(GetTickCount64() + 60000);
		g_pipeSendAllowCloseAck.store(true);
	}
}

} // namespace rk
