// native tray plugin adding "view clips" to obss tray menu since scripting cant reach the tray icon -- shares obs-browsers cef panel (same cookies/session as the docked clips ui) and stays unparented from the main window so "minimize to tray" cant hide it too

#include <obs-module.h>
#include <obs-frontend-api.h>

#include <QCoreApplication>
#include <QAbstractNativeEventFilter>
#include <QMenu>
#include <QAction>
#include <QSystemTrayIcon>
#include <QList>
#include <QObject>
#include <QString>
#include <QWidget>
#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QLabel>
#include <QWidgetAction>
#include <QPointer>
#include <QTimer>
#include <QElapsedTimer>
#include <QEvent>
#include <QCloseEvent>
#include <QResizeEvent>
#include <QMouseEvent>
#include <QMessageBox>
#include <QDesktopServices>
#include <QUrl>
#include <QDir>
#include <QGuiApplication>
#include <QScreen>
#include <QWindow>
#include <QCursor>
#include <QSaveFile>
#include <QTextStream>

#include "browser-panel.hpp"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <string>
#include <cstring>
#include <cstdlib>
#include <thread>
#include <algorithm>
#include <unordered_map>
#include <vector>

#include <windows.h>

OBS_DECLARE_MODULE()

namespace {

QCef *g_cef = nullptr;
bool g_cefInitTried = false;
QPointer<QWidget> g_clipsWindow;
QPointer<QCefWidget> g_clipsBrowser;
QPointer<QWidget> g_settingsWindow;
QPointer<QWidget> g_prewarmWindow;
constexpr int kOpenClipsHotkeyId = 0x524B;
bool g_openClipsHotkeyRegistered = false;
QAbstractNativeEventFilter *g_openClipsHotkeyFilter = nullptr;
QPointer<QTimer> g_openClipsHotkeyTimer;
std::string g_openClipsHotkeyBinding;
bool g_openClipsHotkeyRequestInFlight = false;
// ui-thread only flag guarding against a second click opening a redundant window while the first ones background check is still talking to the helper
bool g_clipsCheckInFlight = false;
bool g_settingsCheckInFlight = false;

// share previews checked state is re-read from the helper every time the menu opens, since obs only builds this menu once
QPointer<QAction> g_sharePreviewAction;


// hidden, not removed, each time the menu opens -- see OnFrontendEvent for why removeAction was the wrong tool here
QPointer<QAction> g_previewProjectorAction;
QPointer<QAction> g_programProjectorAction;

// obss own native record/replay-buffer actions -- hidden (not removed) behind TrayActionRow, same as g_previewProjectorAction/g_programProjectorAction above; kept alive only as the structural anchor insertAction positions the custom rows against.
QPointer<QAction> g_nativeRecordAction;
QPointer<QAction> g_nativeReplayBufferAction;

// menu opens upward from the cursor so restart obs lands right where an accidental double-clicks second click hits -- every action here bails if triggered within kMenuClickDebounceMs of aboutToShow
constexpr qint64 kMenuClickDebounceMs = 150;
QElapsedTimer g_menuShownTimer;

// background-thread only, can block for real time on a cold start -- waits for cefs background thread so widgets never hit the "first open only" white window race from being created before cef is ready
bool EnsureCefReadyBlocking()
{
	if (!g_cefInitTried) {
		g_cefInitTried = true;
		g_cef = obs_browser_init_panel();
	}
	if (!g_cef)
		return false;
	g_cef->init_browser();
	return g_cef->wait_for_browser_init();
}

// blocking http get to the replaykit helper, worker-thread only since it can hold for up to timeoutMs -- plain winsock to avoid linking qt6network, and checks the json body since /focus-window always returns 200 even when it found nothing
bool HttpFocusWindowSucceeded(const char *path, int port, int timeoutMs)
{
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		return false;

	bool focused = false;
	SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock != INVALID_SOCKET) {
		DWORD timeout = (DWORD)timeoutMs;
		setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
		setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		if (connect(sock, (sockaddr *)&addr, sizeof(addr)) == 0) {
			std::string req = std::string("GET ") + path +
					   " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
			if (send(sock, req.c_str(), (int)req.size(), 0) > 0) {
				char buf[512];
				int n = recv(sock, buf, sizeof(buf) - 1, 0);
				if (n > 0) {
					buf[n] = 0;
					focused = strstr(buf, "\"focused\":true") != nullptr;
				}
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return focused;
}

// same raw-socket approach as above but generalised to any method/path/body -- callers substring-search the small, known-shape response (JsonBoolField) instead of pulling in a real json parser for two fields
std::string HttpRequest(const char *method, const char *path, int port, const char *jsonBody, int timeoutMs)
{
	std::string response;
	WSADATA wsaData;
	if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0)
		return response;

	SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
	if (sock != INVALID_SOCKET) {
		DWORD timeout = (DWORD)timeoutMs;
		setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (const char *)&timeout, sizeof(timeout));
		setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (const char *)&timeout, sizeof(timeout));

		sockaddr_in addr = {};
		addr.sin_family = AF_INET;
		addr.sin_port = htons((u_short)port);
		inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

		if (connect(sock, (sockaddr *)&addr, sizeof(addr)) == 0) {
			std::string body = jsonBody ? jsonBody : "";
			// the helper only accepts same-origin requests on trusted routes -- pretend to be the dock.
			std::string req = std::string(method) + " " + path + " HTTP/1.1\r\n" + "Host: 127.0.0.1\r\n" +
					   "Origin: http://127.0.0.1:" + std::to_string(port) + "\r\n";
			if (!body.empty()) {
				req += "Content-Type: application/json\r\n";
				req += "Content-Length: " + std::to_string(body.size()) + "\r\n";
			}
			req += "Connection: close\r\n\r\n" + body;
			if (send(sock, req.c_str(), (int)req.size(), 0) > 0) {
				char buf[2048];
				int n;
				while ((n = recv(sock, buf, sizeof(buf), 0)) > 0)
					response.append(buf, n);
			}
		}
		closesocket(sock);
	}
	WSACleanup();
	return response;
}

bool JsonBoolField(const std::string &body, const char *field, bool defaultValue)
{
	if (body.find(std::string("\"") + field + "\":true") != std::string::npos)
		return true;
	if (body.find(std::string("\"") + field + "\":false") != std::string::npos)
		return false;
	return defaultValue;
}

// pulls one flat {"key":"OBS_KEY_X","shift":true,...} object out of a larger json blob by field name -- clipKeybind/recordingKeybind never nest anything deeper than string/bool leaves, so a substring search from the fields opening brace to the next closing one is exact, same "no real parser needed for a known shape" approach as JsonBoolField above rather than pulling in a json library for two callers.
std::string ExtractJsonObjectField(const std::string &body, const char *field)
{
	std::string marker = std::string("\"") + field + "\":{";
	size_t start = body.find(marker);
	if (start == std::string::npos)
		return std::string();
	start += marker.size() - 1;
	size_t end = body.find('}', start);
	if (end == std::string::npos)
		return std::string();
	return body.substr(start, end - start + 1);
}

std::string ExtractJsonStringField(const std::string &obj, const char *field)
{
	std::string marker = std::string("\"") + field + "\":\"";
	size_t start = obj.find(marker);
	if (start == std::string::npos)
		return std::string();
	start += marker.size();
	size_t end = obj.find('"', start);
	if (end == std::string::npos)
		return std::string();
	return obj.substr(start, end - start);
}

// mirrors keyLabel() in obs-custom-dock/settings.html exactly, so the tray badge reads the same as the settings docks own hotkey field for the same binding.
std::string KeyLabelFromObsKey(const std::string &obsKey)
{
	static const std::unordered_map<std::string, std::string> named = {
		{"OBS_KEY_BACKSLASH", "\\"}, {"OBS_KEY_SLASH", "/"},        {"OBS_KEY_SPACE", "Space"},
		{"OBS_KEY_RETURN", "Enter"}, {"OBS_KEY_ESCAPE", "Esc"},      {"OBS_KEY_TAB", "Tab"},
		{"OBS_KEY_DELETE", "Delete"}, {"OBS_KEY_BACKSPACE", "Backspace"},
		{"OBS_KEY_UP", "Up"}, {"OBS_KEY_DOWN", "Down"}, {"OBS_KEY_LEFT", "Left"}, {"OBS_KEY_RIGHT", "Right"},
	};
	auto found = named.find(obsKey);
	if (found != named.end())
		return found->second;
	const std::string prefix = "OBS_KEY_";
	if (obsKey.rfind(prefix, 0) == 0) {
		std::string rest = obsKey.substr(prefix.size());
		std::replace(rest.begin(), rest.end(), '_', ' ');
		return rest;
	}
	return obsKey;
}

// same modifier set/order/key-name mapping as comboToLabel() in settings.html, but joined with a bare "+" (no spaces) so the chip stays as compact as possible and never stretches the menu wider than it needs to be.
std::string KeybindLabelFromSettingsJson(const std::string &settingsBody, const char *field)
{
	std::string obj = ExtractJsonObjectField(settingsBody, field);
	if (obj.empty())
		return std::string();
	std::string key = ExtractJsonStringField(obj, "key");
	if (key.empty())
		return std::string();
	std::vector<std::string> parts;
	if (JsonBoolField(obj, "control", false))
		parts.push_back("Ctrl");
	if (JsonBoolField(obj, "alt", false))
		parts.push_back("Alt");
	if (JsonBoolField(obj, "shift", false))
		parts.push_back("Shift");
	if (JsonBoolField(obj, "command", false))
		parts.push_back("Win");
	parts.push_back(KeyLabelFromObsKey(key));
	std::string label;
	for (size_t i = 0; i < parts.size(); i++) {
		if (i)
			label += "+";
		label += parts[i];
	}
	return label;
}

// CEF treats a parent QWidgets close event as a browser-close request even when Qt leaves the parent allocated. Ignore
// that event after hiding the window so the browser stays usable when a hotkey reopens Clips immediately afterward.
class ClipsWindow : public QWidget {
protected:
	void closeEvent(QCloseEvent *event) override
	{
		hide();
		event->ignore();
	}
};

// ui-thread only -- builds the actual window once we know theres no existing clips window to reuse and cef is already confirmed started via EnsureCefReadyBlocking
void CreateClipsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open clips");
		return;
	}

	QWidget *win = new ClipsWindow();
	// Closing Clips only hides it. Keeping its CEF host alive avoids a deferred-delete race when a hotkey reopens it
	// immediately after close, which could otherwise create a blank window or call Qt through a stale widget pointer.
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle("Clips");
	win->resize(1280, 800);
	win->setMinimumSize(850, 620);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	// nullptr cookie manager shares the same cef storage as every other obs-browser dock, keeping streamable sign-in and favorites consistent with the docked clips ui
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/clips-view", nullptr);
	layout->addWidget(browser);
	g_clipsBrowser = browser;

	win->show();
	win->raise();
	win->activateWindow();
	g_clipsWindow = win;
}

// same shape as CreateClipsWindow -- title matches settings.htmls <title> and the /close-window whitelist, size matches the controls_app.html popup so it looks the same either way its opened
void CreateSettingsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open settings");
		return;
	}

	QWidget *win = new QWidget(nullptr);
	win->setAttribute(Qt::WA_DeleteOnClose);
	win->setWindowTitle("ReplayKit Settings");
	win->resize(980, 760);
	// derived from settings.htmls own grid, not guessed: .shell is a fixed 190px sidebar + flexible main column inside 18px of shell padding, .main adds another 10px right padding, and the widest row inside it (.form-row) needs 180px label + 8px gap + a 260px-minimum field (grid-template-columns:180px minmax(260px,1fr)) to avoid wrapping. 190+18+10+180+8+260 = 666px is the point a form row would start fighting for space; 700 gives a small margin. height has no equivalent hard floor (.main scrolls vertically), 500 is just enough to show a few rows without feeling cramped.
	win->setMinimumSize(700, 500);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/settings-view", nullptr);
	layout->addWidget(browser);

	win->show();
	win->raise();
	win->activateWindow();
	g_settingsWindow = win;
}

void ShowSettings()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_settingsWindow) {
		g_settingsWindow->show();
		g_settingsWindow->raise();
		g_settingsWindow->activateWindow();
		return;
	}
	if (g_settingsCheckInFlight)
		return;
	g_settingsCheckInFlight = true;

	// same reasoning as ShowClips -- the docks own settings button opens a window.open() popup this cant see directly, so check for it by title before opening a second one
	std::thread([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=ReplayKit%20Settings", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[focused, cefReady]() {
				g_settingsCheckInFlight = false;
				if (!focused && cefReady)
					CreateSettingsWindow();
			},
			Qt::QueuedConnection);
	}).detach();
}

void ShowClips()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_clipsWindow) {
		if (!g_clipsWindow->isVisible() && g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	if (g_clipsCheckInFlight)
		return;
	g_clipsCheckInFlight = true;

	// checks /focus-window first since the docks own button or a leftover window may already have one open, then runs both blocking http calls off the ui thread and posts back thru queued invokeMethod since qwidget/qcefwidget arent thread-safe
	std::thread([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=Clips", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[focused, cefReady]() {
				g_clipsCheckInFlight = false;
				if (!focused && cefReady)
					CreateClipsWindow();
			},
			Qt::QueuedConnection);
	}).detach();
}

void ToggleClips()
{
	if (g_clipsWindow) {
		if (g_clipsWindow->isVisible()) {
			g_clipsWindow->hide();
			return;
		}
		if (g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	ShowClips();
}

UINT OpenClipsVirtualKey(const std::string &obsKey)
{
	if (obsKey.size() == 9 && obsKey.rfind("OBS_KEY_", 0) == 0) {
		char key = obsKey[8];
		if ((key >= 'A' && key <= 'Z') || (key >= '0' && key <= '9'))
			return (UINT)key;
	}
	if (obsKey.rfind("OBS_KEY_F", 0) == 0) {
		int functionKey = std::atoi(obsKey.c_str() + 9);
		if (functionKey >= 1 && functionKey <= 24)
			return VK_F1 + functionKey - 1;
	}

	static const std::unordered_map<std::string, UINT> keys = {
		{"OBS_KEY_BACKSLASH", VK_OEM_5}, {"OBS_KEY_SLASH", VK_OEM_2}, {"OBS_KEY_SPACE", VK_SPACE},
		{"OBS_KEY_RETURN", VK_RETURN}, {"OBS_KEY_ESCAPE", VK_ESCAPE}, {"OBS_KEY_TAB", VK_TAB},
		{"OBS_KEY_DELETE", VK_DELETE}, {"OBS_KEY_BACKSPACE", VK_BACK}, {"OBS_KEY_UP", VK_UP},
		{"OBS_KEY_DOWN", VK_DOWN}, {"OBS_KEY_LEFT", VK_LEFT}, {"OBS_KEY_RIGHT", VK_RIGHT},
		{"OBS_KEY_MINUS", VK_OEM_MINUS}, {"OBS_KEY_EQUAL", VK_OEM_PLUS}, {"OBS_KEY_BRACKETLEFT", VK_OEM_4},
		{"OBS_KEY_BRACKETRIGHT", VK_OEM_6}, {"OBS_KEY_SEMICOLON", VK_OEM_1}, {"OBS_KEY_APOSTROPHE", VK_OEM_7},
		{"OBS_KEY_COMMA", VK_OEM_COMMA}, {"OBS_KEY_PERIOD", VK_OEM_PERIOD}, {"OBS_KEY_QUOTELEFT", VK_OEM_3},
	};
	auto found = keys.find(obsKey);
	return found == keys.end() ? 0 : found->second;
}

void RegisterOpenClipsHotkey(const std::string &settingsBody)
{
	std::string binding = ExtractJsonObjectField(settingsBody, "openClipsKeybind");
	if (binding.empty() || binding == g_openClipsHotkeyBinding)
		return;
	g_openClipsHotkeyBinding = binding;

	if (g_openClipsHotkeyRegistered) {
		UnregisterHotKey(nullptr, kOpenClipsHotkeyId);
		g_openClipsHotkeyRegistered = false;
	}

	std::string keyName = ExtractJsonStringField(binding, "key");
	if (keyName.empty())
		return;
	UINT key = OpenClipsVirtualKey(keyName);
	if (key == 0) {
		blog(LOG_WARNING, "[replaykit-tray] Open Clips hotkey uses an unsupported key");
		return;
	}
	UINT modifiers = MOD_NOREPEAT;
	if (JsonBoolField(binding, "control", false)) modifiers |= MOD_CONTROL;
	if (JsonBoolField(binding, "alt", false)) modifiers |= MOD_ALT;
	if (JsonBoolField(binding, "shift", false)) modifiers |= MOD_SHIFT;
	if (JsonBoolField(binding, "command", false)) modifiers |= MOD_WIN;
	if (!RegisterHotKey(nullptr, kOpenClipsHotkeyId, modifiers, key)) {
		blog(LOG_WARNING, "[replaykit-tray] Could not register Open Clips hotkey (error=%lu)", GetLastError());
		return;
	}
	g_openClipsHotkeyRegistered = true;
	blog(LOG_INFO, "[replaykit-tray] Open Clips hotkey registered");
}

class OpenClipsHotkeyFilter : public QAbstractNativeEventFilter {
public:
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_HOTKEY || msg->wParam != kOpenClipsHotkeyId)
			return false;
		ToggleClips();
		return true;
	}
};

void LoadOpenClipsHotkey()
{
	if (g_openClipsHotkeyRequestInFlight)
		return;
	g_openClipsHotkeyRequestInFlight = true;
	std::thread([]() {
		std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 3000);
		QMetaObject::invokeMethod(qApp, [settingsBody]() {
			g_openClipsHotkeyRequestInFlight = false;
			RegisterOpenClipsHotkey(settingsBody);
		}, Qt::QueuedConnection);
	}).detach();
}

// posts to the same /share-preview route the dock uses so the projector/audio-monitoring logic stays in one place -- fire and forget, since aboutToShow re-reads the real state next open so a failed toggle just looks unchanged
void ToggleSharePreview(bool checked)
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	std::thread([checked]() {
		std::string json = checked ? "{\"enabled\":true}" : "{\"enabled\":false}";
		std::string body = HttpRequest("POST", "/share-preview", 8767, json.c_str(), 8000);
		bool enabled = JsonBoolField(body, "enabled", !checked);
		QMetaObject::invokeMethod(
			qApp,
			[enabled]() {
				if (g_sharePreviewAction)
					g_sharePreviewAction->setChecked(enabled);
			},
			Qt::QueuedConnection);
	}).detach();
}

// posts to the helpers /restart-obs, a plain close-and-reopen (not the settings pages signed-out "clean" restart) -- confirms first since it force-kills obs rather than stopping gracefully, then fires and forgets since obs is about to die anyway
void RestartObs()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	QMessageBox::StandardButton reply = QMessageBox::question(
		nullptr, QObject::tr("Restart OBS"),
		QObject::tr("This will close and reopen OBS. Any active recording or stream will be stopped. Continue?"),
		QMessageBox::Yes | QMessageBox::No, QMessageBox::No);
	if (reply != QMessageBox::Yes)
		return;
	std::thread([]() { HttpRequest("POST", "/restart-obs", 8767, nullptr, 5000); }).detach();
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
	QMessageBox::StandardButton reply = QMessageBox::question(
		nullptr, QObject::tr("Exit OBS"),
		QObject::tr("This will close OBS. Any active recording or stream will be stopped. Continue?"),
		QMessageBox::Yes | QMessageBox::No, QMessageBox::No);
	if (reply != QMessageBox::Yes)
		return;
	std::thread([]() { HttpRequest("POST", "/exit-obs", 8767, nullptr, 5000); }).detach();
}

// invisible throwaway browser to eat the first-ever cef surfaces one-time cost -- the real clips window paints only a corner and stays white the first time each obs session, and this burns that bad first attempt off-screen instead of chasing the exact cef-internal cause
bool g_cefPrewarmed = false;

void PrewarmCefBrowser()
{
	if (g_cefPrewarmed)
		return;
	g_cefPrewarmed = true;

	std::thread([]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			qApp,
			[cefReady]() {
				if (!cefReady || !g_cef)
					return;

				QWidget *warmWin = new QWidget(nullptr);
				warmWin->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint);
				warmWin->move(-10000, -10000);
				warmWin->resize(64, 64);

				QVBoxLayout *layout = new QVBoxLayout(warmWin);
				layout->setContentsMargins(0, 0, 0, 0);
				QCefWidget *browser = g_cef->create_widget(warmWin, "about:blank", nullptr);
				layout->addWidget(browser);

				warmWin->show();
				g_prewarmWindow = warmWin;
				QTimer::singleShot(3000, warmWin, &QObject::deleteLater);
			},
			Qt::QueuedConnection);
	}).detach();
}

// synchronously deletes our cef widgets before obs_module_unload, mirroring how OBSBasic.cpp deletes extraBrowsers early, to dodge upstream race obsproject/obs-browser#353 where cef can still be mid-teardown when the browser-manager thread is joined -- direct delete not deleteLater becuase a deferred one wouldnt run before applicationShutdown anyway
void CloseCefWidgetsBeforeShutdown()
{
	if (g_clipsWindow)
		delete g_clipsWindow.data();
	if (g_settingsWindow)
		delete g_settingsWindow.data();
	if (g_prewarmWindow)
		delete g_prewarmWindow.data();
}

// blocks right-click activate (obss tray menu allows it like a native context menu) and fast-double-click debounce bypass for every item, including obss own unguarded exit -- scoped to the menu itself, not qApp, since an app-wide filter caused a confirmed 2026-08-10 crash by also catching cef widget events mid-teardown, and confirmed 2026-08-12 that a fast double-click delivers its second click as QEvent::MouseButtonDblClick, not a second press/release, which an earlier version never checked for
class TrayMenuGuard : public QObject {
public:
	explicit TrayMenuGuard(QMenu *menu) : QObject(menu), m_menu(menu) { menu->installEventFilter(this); }

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_menu)
			return false;
		const bool isClick = event->type() == QEvent::MouseButtonPress || event->type() == QEvent::MouseButtonRelease ||
				      event->type() == QEvent::MouseButtonDblClick;
		if (!isClick)
			return false;
		if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
			return true;
		return static_cast<QMouseEvent *>(event)->button() == Qt::RightButton;
	}

private:
	QMenu *m_menu;
};

// obss own isEnabled() on sysTrayRecord/sysTrayReplayBuffer cannot be trusted (confirmed via logging: both reported enabled=0 at click time despite obs_frontend_..._active() proving the underlying feature was genuinely toggleable -- obs only calls setEnabled on sysTrayReplayBuffer from inside ResetOutputs, gated on whether the output handler happened to already have a replay buffer object at that exact moment, never re-enabled later, and sysTrayRecord has no setEnabled call anywhere in obs at all), so these drive the toggle directly through the same stable public entry points obs itself and every obs-websocket-style integration use, instead of routing through the actions own (occasionally-lying) enabled/triggered state.
void ToggleRecording()
{
	bool active = obs_frontend_recording_active();
	blog(LOG_INFO, "[replaykit-tray] ToggleRecording: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_recording_stop() : obs_frontend_recording_start();
}

void ToggleReplayBuffer()
{
	bool active = obs_frontend_replay_buffer_active();
	blog(LOG_INFO, "[replaykit-tray] ToggleReplayBuffer: active=%d -> calling %s", active, active ? "stop" : "start");
	active ? obs_frontend_replay_buffer_stop() : obs_frontend_replay_buffer_start();
}

void PinMenuAboveTaskbar(QMenu *menu);

enum class TrayRowKind { Clips, Recording, ReplayBuffer };

// custom row (name + a darker rounded keybind chip) for record/clipping -- a first attempt at this was reverted because it never aligned with native items no matter the margin (19/9/2/5/15px all tried), and research confirmed why: this menu used to render through windows own native platform menu bridge, which has no real support for QWidgetAction at all. OnFrontendEvent now clears setContextMenu() and pops this same trayMenu manually instead (see the tray->setContextMenu(nullptr) block), so this widget goes through qts own menu layout/paint path like everything else in it -- alignment is a fresh, real question now, not a fight against a bridge that was never going to cooperate. click handling stays debounced across both plausible delivery paths (the widgets own mouseReleaseEvent and the wrapping actions triggered()) since its still not certain in advance which one fires in a popped-not-native menu either.
class TrayActionRow : public QWidget {
public:
	TrayActionRow(TrayRowKind kind, QMenu *menu, QWidget *parent = nullptr) : QWidget(parent), m_kind(kind), m_menu(menu)
	{
		setAttribute(Qt::WA_Hover, true);
		setCursor(Qt::PointingHandCursor);
		setStyleSheet("QWidget:hover { background-color: palette(highlight); }");
		// Minimum (not the QWidget default) means qt treats our sizeHint as a floor it can grow past but never shrink below -- structural insurance against the reported clipping/overlap, instead of only reacting to it after the fact once the chip already got squeezed.
		setSizePolicy(QSizePolicy::Minimum, QSizePolicy::Fixed);

		nameLabel = new QLabel(this);
		chipLabel = new QLabel(this);
		// as tight as still legible -- reads as a small badge, not a second label competing for space.
		chipLabel->setStyleSheet("background-color: rgba(0, 0, 0, 70); border-radius: 3px; padding: 0px 4px; font-size: 9px; color: rgba(255, 255, 255, 160);");
		chipLabel->setVisible(false);

		auto *layout = new QHBoxLayout(this);
		// bracketed against screenshots: 20px measured left of sibling items, 32px measured right of them -- this is the midpoint, not a computed value.
		layout->setContentsMargins(26, 5, 10, 5);
		layout->setSpacing(6);
		// chip sits right next to the name, not pushed to the far right edge -- the stretch goes after both so any leftover space (e.g. qmenu matching this row to a wider sibling item) ends up as trailing empty space instead of a gap between name and chip.
		layout->addWidget(nameLabel);
		layout->addWidget(chipLabel);
		layout->addStretch();
	}

	QLabel *nameLabel;
	QLabel *chipLabel;

	void SetKeybindLabel(const std::string &label)
	{
		chipLabel->setVisible(!label.empty());
		chipLabel->setText(QString::fromStdString(label));
		// this rows sizeHint just changed (chip went from hidden/empty to a real label or back) -- updateGeometry() is qts actual mechanism for "go re-ask for my size", not a manual setMinimumWidth guess; SizePolicy::Minimum from the constructor is what makes sure that re-asked-for size is never shrunk back below.
		updateGeometry();
		if (m_menu) {
			// the reported "first open only, hotkey box overlaps text" bug: qmenu only lays out each actions geometry on first show and caches it after that, so on the very first open the async keybind fetch (this function) lands after that cache is already set and adjustSize() alone does not force a re-measure. a fake resize event is qts own documented way to flip the internal dirty flag that forces that recompute.
			QResizeEvent fakeResize(QSize(1, 1), m_menu->size());
			qApp->sendEvent(m_menu, &fakeResize);
			m_menu->adjustSize();
			PinMenuAboveTaskbar(m_menu);
		}
	}

	void ProxyTrigger()
	{
		if (m_lastTrigger.isValid() && m_lastTrigger.elapsed() < 250)
			return;
		m_lastTrigger.start();
		if (m_kind == TrayRowKind::Clips)
			ShowClips();
		else if (m_kind == TrayRowKind::Recording)
			ToggleRecording();
		else
			ToggleReplayBuffer();
		if (m_menu)
			m_menu->close();
	}

protected:
	void mouseReleaseEvent(QMouseEvent *event) override
	{
		if (event->button() == Qt::LeftButton)
			ProxyTrigger();
		QWidget::mouseReleaseEvent(event);
	}

private:
	TrayRowKind m_kind;
	QMenu *m_menu;
	QElapsedTimer m_lastTrigger;
};

QPointer<TrayActionRow> g_recordRow;
QPointer<TrayActionRow> g_replayBufferRow;
QPointer<TrayActionRow> g_clipsRow;

void RefreshActionRowText()
{
	if (g_recordRow)
		g_recordRow->nameLabel->setText(obs_frontend_recording_active() ? "Stop Recording" : "Start Recording");
	if (g_replayBufferRow)
		g_replayBufferRow->nameLabel->setText(obs_frontend_replay_buffer_active() ? "Stop Clipping" : "Start Clipping");
}

// obs builds the tray menu once and never rebuilds it, so this refreshes the share-preview checkbox and the record/clipping rows keybind chip right before each show instead of polling on a timer nobody is watching
void RefreshDynamicMenuState()
{
	RefreshActionRowText();
	if (g_clipsRow || g_recordRow || g_replayBufferRow) {
		std::thread([]() {
			std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 500);
			std::string clipsLabel = KeybindLabelFromSettingsJson(settingsBody, "openClipsKeybind");
			std::string clipLabel = KeybindLabelFromSettingsJson(settingsBody, "clipKeybind");
			std::string recordingLabel = KeybindLabelFromSettingsJson(settingsBody, "recordingKeybind");
			QMetaObject::invokeMethod(
				qApp,
				[clipsLabel, clipLabel, recordingLabel]() {
					if (g_clipsRow)
						g_clipsRow->SetKeybindLabel(clipsLabel);
					if (g_replayBufferRow)
						g_replayBufferRow->SetKeybindLabel(clipLabel);
					if (g_recordRow)
						g_recordRow->SetKeybindLabel(recordingLabel);
				},
				Qt::QueuedConnection);
		}).detach();
	}
	if (!g_sharePreviewAction)
		return;
	std::thread([]() {
		std::string getBody = HttpRequest("GET", "/share-preview", 8767, nullptr, 500);
		bool available = JsonBoolField(getBody, "available", false);
		bool enabled = JsonBoolField(getBody, "enabled", false);
		QMetaObject::invokeMethod(
			qApp,
			[available, enabled]() {
				if (!g_sharePreviewAction)
					return;
				g_sharePreviewAction->setEnabled(available);
				g_sharePreviewAction->setChecked(enabled);
			},
			Qt::QueuedConnection);
	}).detach();
}

// only pulls the menu up when it would actually overlap the taskbar (y), or back onto the screen when it would run off the right edge (x) -- this used to pin the bottom edge to the taskbar top unconditionally, which was wrong for a tray icon sitting in the hidden-icons flyout (a separate window that floats well above the taskbar): the menu snapped down to the taskbar instead of staying near the flyout it was actually opened from. checking against the menus own natural popup() position instead of always recomputing from the taskbar makes it adapt to wherever the icon actually is. skips the move() entirely when already at the target position -- re-positioning an already-visible native popup is a plausible cause of a reported "clicking outside the menu doesnt close it" bug (moving a shown popups hwnd can desync qts click-outside-to-dismiss tracking on windows), so this only touches geometry when it actually needs correcting.
void PinMenuAboveTaskbar(QMenu *menu)
{
	QScreen *screen = QGuiApplication::screenAt(menu->pos());
	if (!screen)
		return;
	QRect avail = screen->availableGeometry();
	int menuHeight = menu->sizeHint().height();
	int naturalY = menu->pos().y();
	int targetY = (naturalY + menuHeight > avail.bottom() + 1) ? qMax(avail.top(), avail.bottom() + 1 - menuHeight) : naturalY;
	int menuWidth = menu->sizeHint().width();
	int targetX = qMin(menu->pos().x(), qMax(avail.left(), avail.right() + 1 - menuWidth));
	if (menu->pos().x() == targetX && menu->pos().y() == targetY)
		return;
	menu->move(targetX, targetY);
}

// obs marks every real projector window with windowHandle()->setProperty("isOBSProjectorWindow", true) -- see OBSProjector.cpp, which does this specifically so obss own code (SetDisplayAffinity) can recognize one reliably. thats a qt object property, invisible to plain win32 window enumeration, so the replaykit helper (a separate powershell process with no qt/obs-object access, only raw EnumWindows/GetClassName-style calls) has no way to read it directly -- this plugin does, since it runs inside obss own qt process. publishing the current set of hwnds carrying that property to a file lets the helper check the SAME authoritative signal obs uses internally instead of only inferring "looks like a projector" from window class + ownership heuristics, which turned out to have real false-positive risk (many of obss own dialogs -- NameDialog, the Scripts window, the auto-config wizard, etc. -- are independently-owned top-level windows too, just not projectors).
QString ProjectorHandoffDir()
{
	return qEnvironmentVariable("TEMP") + "/ReplayKit/scratch";
}

QString ProjectorHandoffPath()
{
	return ProjectorHandoffDir() + "/obsreplaykit_projector_windows.txt";
}

QString MainWindowHandoffPath()
{
	return ProjectorHandoffDir() + "/obsreplaykit_main_window.txt";
}

// obs_frontend_get_main_window() is the only language-neutral way to identify obss real main window -- its title is a localized template ("<version> - profile: ... - scenes: ...") with no fixed substring a non-english build still renders, which is what broke the helpers old title-matching close. published once, not on the projector timer, since the handle never changes for the life of the process.
void PublishMainWindow()
{
	QWidget *mainWindow = (QWidget *)obs_frontend_get_main_window();
	if (!mainWindow)
		return;

	QDir().mkpath(ProjectorHandoffDir());
	QSaveFile file(MainWindowHandoffPath());
	if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
		return;
	QTextStream out(&file);
	out << QString::number((quintptr)mainWindow->winId());
	file.commit();
}

QTimer *g_projectorPublishTimer = nullptr;

// QSaveFile writes to a temp file and atomically renames it into place on commit(), so a reader on the other process never sees a half-written file -- plain QFile writing in place could get caught mid-write by the helpers poll.
void PublishProjectorWindows()
{
	QStringList hwnds;
	for (QWindow *w : QGuiApplication::topLevelWindows()) {
		if (w && w->property("isOBSProjectorWindow").toBool())
			hwnds << QString::number((quintptr)w->winId());
	}

	QDir().mkpath(ProjectorHandoffDir());
	QSaveFile file(ProjectorHandoffPath());
	if (!file.open(QIODevice::WriteOnly | QIODevice::Text))
		return;
	QTextStream out(&file);
	for (const QString &h : hwnds)
		out << h << '\n';
	file.commit();
}

void OnFrontendEvent(enum obs_frontend_event event, void *)
{
	if (event == OBS_FRONTEND_EVENT_EXIT) {
		if (g_projectorPublishTimer)
			g_projectorPublishTimer->stop();
		if (g_openClipsHotkeyTimer)
			g_openClipsHotkeyTimer->stop();
		CloseCefWidgetsBeforeShutdown();
		return;
	}

	if (event != OBS_FRONTEND_EVENT_FINISHED_LOADING)
		return;

	PrewarmCefBrowser();
	PublishMainWindow();
	g_openClipsHotkeyTimer = new QTimer(qApp);
	QObject::connect(g_openClipsHotkeyTimer, &QTimer::timeout, qApp, []() { LoadOpenClipsHotkey(); });
	g_openClipsHotkeyTimer->start(1000);
	LoadOpenClipsHotkey();

	// 250ms matches the replaykit helpers own poll cadence when its waiting for a projector to appear -- no benefit publishing faster than the one consumer of this file actually checks it.
	g_projectorPublishTimer = new QTimer(qApp);
	QObject::connect(g_projectorPublishTimer, &QTimer::timeout, qApp, []() { PublishProjectorWindows(); });
	g_projectorPublishTimer->start(250);
	PublishProjectorWindows();

	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	if (!tray)
		return;
	QMenu *trayMenu = tray->contextMenu();
	if (!trayMenu)
		return;

	// obs wires this same trayMenu to the tray icon via setContextMenu(), which on windows hands rendering off to a native platform menu bridge that has no support for QWidgetAction at all (confirmed via research, and by five alignment attempts on a QWidgetAction row never converging) -- clearing that association and popping the same menu manually on a right-click keeps every existing action/signal/submenu intact but renders through qts own (non-native) menu painting, which is what actually supports a custom widget row correctly. obss own activated->IconActivated connection (left-click show/hide) is untouched; this only adds a second listener that acts on Context specifically.
	tray->setContextMenu(nullptr);
	QObject::connect(tray, &QSystemTrayIcon::activated, trayMenu, [trayMenu](QSystemTrayIcon::ActivationReason reason) {
		if (reason == QSystemTrayIcon::Context)
			trayMenu->popup(QCursor::pos());
	});

	new TrayMenuGuard(trayMenu);

	// obs builds this menu once in SystemTrayInit() and never rebuilds the top level (only the projector submenus contents refresh per click), so anything hidden below stays hidden without needing to be redone on every open
	// View Clips uses the same custom row as recording/clipping so its current ReplayKit binding is visible in the tray.
	auto *clipsRow = new TrayActionRow(TrayRowKind::Clips, trayMenu);
	auto *clipsRowAction = new QWidgetAction(trayMenu);
	clipsRowAction->setDefaultWidget(clipsRow);
	QObject::connect(clipsRowAction, &QAction::triggered, trayMenu, [clipsRow]() {
		if (clipsRow)
			clipsRow->ProxyTrigger();
	});
	clipsRow->nameLabel->setText(QObject::tr("View Clips"));
	g_clipsRow = clipsRow;

	QAction *sharePreview = new QAction(QObject::tr("Share Preview"), trayMenu);
	sharePreview->setCheckable(true);
	QObject::connect(sharePreview, &QAction::triggered, trayMenu, [](bool checked) { ToggleSharePreview(checked); });
	g_sharePreviewAction = sharePreview;

	QAction *customSettings = new QAction(QObject::tr("ReplayKit Settings"), trayMenu);
	QObject::connect(customSettings, &QAction::triggered, trayMenu, []() { ShowSettings(); });

	QObject::connect(trayMenu, &QMenu::aboutToShow, trayMenu, [trayMenu]() {
		g_menuShownTimer.start();
		RefreshDynamicMenuState();
		// belt-and-suspenders re-hide on top of the one-time setVisible(false) below -- free today since obss updateSysTrayProjectorMenu() doesnt touch these actions visibility, but it runs every click so worth guarding anyway
		if (g_previewProjectorAction) g_previewProjectorAction->setVisible(false);
		if (g_programProjectorAction) g_programProjectorAction->setVisible(false);

		// confirmed 2026-08-16 this alone didnt stick since qts native platform code can finish positioning the menu after aboutToShow returns and silently override an early move() -- kept it anyway (free if it does nothing) and queued a second attempt after native show finishes as the standard workaround. bumped 10ms to 50ms to give qts own positioning more headroom to finish first -- see the comment on PinMenuAboveTaskbar for why an unnecessary post-show move is suspected in a "clicking outside doesnt close the menu" report.
		PinMenuAboveTaskbar(trayMenu);
		QTimer::singleShot(50, trayMenu, [trayMenu]() { PinMenuAboveTaskbar(trayMenu); });
	});

	// lands right after "hide", before obss first separator, so it reads as part of the window-visibility group instead of mixed into streaming/recording actions
	QList<QAction *> actions = trayMenu->actions();
	QAction *before = actions.size() > 1 ? actions.at(1) : nullptr;
	trayMenu->insertAction(before, clipsRowAction);
	trayMenu->insertAction(before, sharePreview);
	trayMenu->insertAction(before, customSettings);

	// obs localizes every one of these labels, so matching them by text (the original approach) silently found none of the three on non-english obs -- exit stays last regardless of language since qt tr() only changes the rendered string, not the action-insertion order SystemTrayInit runs in, and the projector entries are the only submenu-bearing actions in this menu, always preview before program for the same reason. setVisible(false) not removeAction(): a first attempt used removeAction() and shipped a suspected obs freeze on the first right-click; setVisible(false) on menuAction() is the qt-documented-correct way to hide a submenu entry without detaching it from trayMenu or disturbing obss own updateSysTrayProjectorMenu() rebuilds.
	QList<QAction *> trayActions = trayMenu->actions();
	QAction *exitAction = nullptr;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (!(*it)->isSeparator()) {
			exitAction = *it;
			break;
		}
	}
	QList<QAction *> projectorSubmenuActions;
	for (QAction *action : trayActions) {
		if (action != exitAction && action->menu())
			projectorSubmenuActions << action;
	}
	if (projectorSubmenuActions.size() >= 1) {
		g_previewProjectorAction = projectorSubmenuActions.at(0);
		g_previewProjectorAction->setVisible(false);
	}
	if (projectorSubmenuActions.size() >= 2) {
		g_programProjectorAction = projectorSubmenuActions.at(1);
		g_programProjectorAction->setVisible(false);
	}

	// obs always builds {Stream, Record, ReplayBuffer, VirtualCam} as one separator-delimited group immediately before Exit (OBSBasic::SystemTrayInit adds them in this fixed order every time), so walking backward from Exit to the next separator finds Record/ReplayBuffer by position -- locale-independent, unlike the text-matching that silently found nothing on non-english obs (see the projector-matching note above). size()==4 is a sanity check, not a guess: if some future obs version changes this layout, skip the customization entirely rather than misattribute the wrong action to record/clipping.
	QList<QAction *> streamGroup;
	for (auto it = trayActions.crbegin(); it != trayActions.crend(); ++it) {
		if (*it == exitAction)
			continue;
		if ((*it)->isSeparator()) {
			if (!streamGroup.isEmpty())
				break;
			continue;
		}
		streamGroup.prepend(*it);
	}
	blog(LOG_INFO, "[replaykit-tray] stream group structural lookup found %d actions (expected 4: Stream, Record, ReplayBuffer, VirtualCam)",
	     (int)streamGroup.size());
	if (streamGroup.size() == 4) {
		g_nativeRecordAction = streamGroup.at(1);
		g_nativeReplayBufferAction = streamGroup.at(2);
		blog(LOG_INFO, "[replaykit-tray] record action: text=\"%s\" enabled=%d", g_nativeRecordAction->text().toUtf8().constData(),
		     g_nativeRecordAction->isEnabled());
		blog(LOG_INFO, "[replaykit-tray] replay-buffer action: text=\"%s\" enabled=%d",
		     g_nativeReplayBufferAction->text().toUtf8().constData(), g_nativeReplayBufferAction->isEnabled());

		// hidden, not removed (matches g_previewProjectorAction/g_programProjectorAction above) -- TrayActionRow proxies clicks straight to ToggleRecording/ToggleReplayBuffer (the direct obs_frontend_..._start/stop calls), so the native actions own isEnabled()/triggered() (confirmed untrustworthy earlier) never come into play at all.
		auto *recordRow = new TrayActionRow(TrayRowKind::Recording, trayMenu);
		auto *recordRowAction = new QWidgetAction(trayMenu);
		recordRowAction->setDefaultWidget(recordRow);
		QObject::connect(recordRowAction, &QAction::triggered, trayMenu, [recordRow]() {
			if (recordRow)
				recordRow->ProxyTrigger();
		});
		trayMenu->insertAction(g_nativeRecordAction, recordRowAction);
		g_nativeRecordAction->setVisible(false);
		g_recordRow = recordRow;

		auto *replayBufferRow = new TrayActionRow(TrayRowKind::ReplayBuffer, trayMenu);
		auto *replayBufferRowAction = new QWidgetAction(trayMenu);
		replayBufferRowAction->setDefaultWidget(replayBufferRow);
		QObject::connect(replayBufferRowAction, &QAction::triggered, trayMenu, [replayBufferRow]() {
			if (replayBufferRow)
				replayBufferRow->ProxyTrigger();
		});
		trayMenu->insertAction(g_nativeReplayBufferAction, replayBufferRowAction);
		g_nativeReplayBufferAction->setVisible(false);
		g_replayBufferRow = replayBufferRow;

		RefreshActionRowText();
	}

	// obs wires exit straight to close() with no confirmation, an easy accidental target at the cursors landing spot -- disconnect(receiver=nullptr) drops obss own listener (qts documented way to do so) so we can reconnect our confirm-first version instead of leaving two competing handlers
	if (exitAction) {
		QObject::disconnect(exitAction, &QAction::triggered, nullptr, nullptr);
		QObject::connect(exitAction, &QAction::triggered, trayMenu, []() { ConfirmedExit(); });
	}
	QAction *restartObs = new QAction(QObject::tr("Restart OBS"), trayMenu);
	QObject::connect(restartObs, &QAction::triggered, trayMenu, []() { RestartObs(); });
	QAction *viewCrashLogs = new QAction(QObject::tr("View Crash Logs"), trayMenu);
	QObject::connect(viewCrashLogs, &QAction::triggered, trayMenu, []() { OpenCrashLogsFolder(); });
	trayMenu->insertSeparator(exitAction);
	trayMenu->insertAction(exitAction, restartObs);
	trayMenu->insertAction(exitAction, viewCrashLogs);
}

} // namespace

bool obs_module_load(void)
{
	g_openClipsHotkeyFilter = new OpenClipsHotkeyFilter();
	qApp->installNativeEventFilter(g_openClipsHotkeyFilter);
	obs_frontend_add_event_callback(OnFrontendEvent, nullptr);
	return true;
}

void obs_module_unload(void)
{
	if (g_openClipsHotkeyTimer)
		g_openClipsHotkeyTimer->stop();
	if (g_openClipsHotkeyRegistered)
		UnregisterHotKey(nullptr, kOpenClipsHotkeyId);
	if (g_openClipsHotkeyFilter) {
		qApp->removeNativeEventFilter(g_openClipsHotkeyFilter);
		delete g_openClipsHotkeyFilter;
		g_openClipsHotkeyFilter = nullptr;
	}
}
