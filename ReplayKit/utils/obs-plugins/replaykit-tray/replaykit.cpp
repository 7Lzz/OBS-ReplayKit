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
#include <QStringList>
#include <QWidget>
#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QLabel>
#include <QPushButton>
#include <QDockWidget>
#include <QMainWindow>
#include <QWidgetAction>
#include <QStyle>
#include <QPointer>
#include <QTimer>
#include <QElapsedTimer>
#include <QEvent>
#include <QCloseEvent>
#include <QResizeEvent>
#include <QMouseEvent>
#include <QKeyEvent>
#include <QPaintEvent>
#include <QDialog>
#include <QFont>
#include <QFile>
#include <QMessageBox>
#include <QDesktopServices>
#include <QUrl>
#include <QDir>
#include <QGuiApplication>
#include <QIcon>
#include <QImage>
#include <QPixmap>
#include <QPainter>
#include <QPainterPath>
#include <QtMath>
#include <QSvgRenderer>
#include <QScreen>
#include <QWindow>
#include <QCursor>
#include <QSettings>
#include <QMoveEvent>

#include "browser-panel.hpp"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <dwmapi.h>
#include <string>
#include <cstring>
#include <cstdlib>
#include <cstdio>
#include <thread>
#include <future>
#include <functional>
#include <atomic>
#include <mutex>
#include <chrono>
#include <algorithm>
#include <unordered_map>
#include <vector>

#include <windows.h>
#include <shlobj.h>   // SHGetPropertyStoreForWindow
#include <shobjidl.h> // ITaskbarList

OBS_DECLARE_MODULE()

namespace {

QObject *g_callbacks = nullptr;
std::mutex g_workerMutex;
std::vector<std::future<void>> g_workers;
bool g_workersStopping = false;
std::mutex g_cefMutex;

void RunAsync(std::function<void()> work)
{
	std::lock_guard<std::mutex> lock(g_workerMutex);
	if (g_workersStopping)
		return;
	for (auto it = g_workers.begin(); it != g_workers.end();) {
		if (it->wait_for(std::chrono::seconds(0)) == std::future_status::ready) {
			try { it->get(); } catch (...) { blog(LOG_WARNING, "ReplayKit background task failed"); }
			it = g_workers.erase(it);
		} else {
			++it;
		}
	}
	g_workers.emplace_back(std::async(std::launch::async, std::move(work)));
}

void StopWorkers()
{
	std::vector<std::future<void>> workers;
	{
		std::lock_guard<std::mutex> lock(g_workerMutex);
		g_workersStopping = true;
		workers.swap(g_workers);
	}
	for (auto &worker : workers) {
		try { worker.get(); } catch (...) { blog(LOG_WARNING, "ReplayKit background task failed"); }
	}
}

QCef *g_cef = nullptr;
bool g_cefInitTried = false;
QPointer<QWidget> g_clipsWindow;
QPointer<QCefWidget> g_clipsBrowser;
bool g_clipsFullscreenActive = false;
QPointer<QWidget> g_settingsWindow;
QPointer<QWidget> g_setupWindow;
QPointer<QWidget> g_prewarmWindow;

// app-icon state for our own windows -- declared up here because the popup builders reset them on a fresh
// WA_DeleteOnClose window and sit above the icon code. The rest of the icon statics live with RefreshAppIcon.
HICON g_ownedIconClips = nullptr;
HICON g_ownedIconSettings = nullptr;
HICON g_ownedIconSetup = nullptr;
int g_taggedClips = -1;
int g_taggedSettings = -1;
int g_taggedSetup = -1;
constexpr int kOpenClipsHotkeyId = 0x524B;
bool g_openClipsHotkeyRegistered = false;
QAbstractNativeEventFilter *g_openClipsHotkeyFilter = nullptr;
QPointer<QTimer> g_openClipsHotkeyTimer;
std::string g_openClipsHotkeyBinding;
bool g_openClipsHotkeyRequestInFlight = false;

// close-to-tray: when true, the OBS window's X hides to tray instead of quitting. polled from /settings on
// the same 1s timer as the open-clips hotkey. real quits/restarts (tray Exit, restart routes) send ALLOWCLOSE
// over the ipc pipe just before posting WM_CLOSE so the filter lets those through untouched.
bool g_closeToTray = true;
QPointer<QWidget> g_mainWindow;
QPointer<QObject> g_mainWindowCloseFilter;

// ipc pipe: this plugin is the server, the helper connects as a client and reconnects on its own -- replaces the
// old scratch-file handoff. the gui thread fills g_mainWinValue / g_projectorCsv; the pipe thread forwards those
// to the helper and applies inbound OPENCLIPS / ALLOWCLOSE / CONFIRM <kind> (see PipeDispatchLine for the full list).
constexpr const wchar_t *kIpcPipeName = L"\\\\.\\pipe\\OBSReplayKitIpc";
std::thread g_pipeThread;
std::atomic<bool> g_pipeStop{false};
std::atomic<quintptr> g_mainWinValue{0};
std::atomic<unsigned long long> g_allowCloseUntilMs{0};
std::atomic<bool> g_pipeSendAllowCloseAck{false};
std::mutex g_projectorCsvMutex;
std::string g_projectorCsv;
bool g_projectorCsvReady = false;
// GetTickCount64() of the last PublishProjectorWindows run -- the pipe thread stops forwarding PROJECTORS if this
// goes stale (obs gui thread stalled), so the helper ages its snapshot out and falls back, same as the old file mtime.
unsigned long long g_projectorCsvAtMs = 0;
HMODULE g_replayKitModule = nullptr;
HANDLE g_nativeCrashLog = INVALID_HANDLE_VALUE;
PVOID g_nativeCrashHandler = nullptr;
volatile LONG g_nativeCrashWriting = 0;
// ui-thread only flag guarding against a second click opening a redundant window while the first ones background check is still talking to the helper
bool g_clipsCheckInFlight = false;
bool g_settingsCheckInFlight = false;
bool g_setupCheckInFlight = false;

// share previews checked state is re-read from the helper every time the menu opens, since obs only builds this menu once
QPointer<QAction> g_sharePreviewAction;
// last /share-preview "available" -- false means the cable + hidden projector were never set up, so a click offers to install instead of toggling
bool g_sharePreviewAvailable = false;


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
	std::lock_guard<std::mutex> lock(g_cefMutex);
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

// remembers the OBS main window + Clips window position/size across sessions, since obs's own geometry save is
// unreliable here (force-kill restarts skip it, and .NET's window heuristics can grab the wrong window). one ini
// in the replaykit config dir; Qt's saveGeometry/restoreGeometry handle dpi, maximized state, and multi-monitor,
// and restoreGeometry refuses a rect that would land fully off-screen.
QString WindowStateIniPath()
{
	QString dir = qEnvironmentVariable("APPDATA") + "/obs-studio/obs-replayKit";
	QDir().mkpath(dir);
	return dir + "/window_state.ini";
}

void SaveWindowGeometry(const QString &key, QWidget *w)
{
	if (!w)
		return;
	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	s.setValue(key, w->saveGeometry());
}

void SaveClipsWindowGeometry(QWidget *window = nullptr)
{
	QWidget *target = window ? window : g_clipsWindow.data();
	if (!target || g_clipsFullscreenActive)
		return;
	SaveWindowGeometry("clipsWindow", target);
}

void RestoreWindowGeometry(const QString &key, QWidget *w)
{
	if (!w)
		return;
	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	QByteArray geo = s.value(key).toByteArray();
	if (!geo.isEmpty())
		w->restoreGeometry(geo);
}

QPointer<QTimer> g_clipsGeoSaveTimer;
QPointer<QTimer> g_obsGeoSaveTimer;
QPointer<QTimer> g_geoPollTimer;
QByteArray g_lastPolledObsGeo;
QByteArray g_lastPolledClipsGeo;

// coalesce the flood of move/resize events into one write a little after motion stops.
void ScheduleClipsGeometrySave()
{
	if (!g_clipsWindow)
		return;
	if (!g_clipsGeoSaveTimer) {
		g_clipsGeoSaveTimer = new QTimer(g_callbacks);
		g_clipsGeoSaveTimer->setSingleShot(true);
		QObject::connect(g_clipsGeoSaveTimer, &QTimer::timeout, g_callbacks,
			 []() { SaveClipsWindowGeometry(); });
	}
	g_clipsGeoSaveTimer->start(400);
}

void ScheduleObsMainGeometrySave()
{
	if (!g_mainWindow)
		return;
	if (!g_obsGeoSaveTimer) {
		g_obsGeoSaveTimer = new QTimer(g_callbacks);
		g_obsGeoSaveTimer->setSingleShot(true);
		QObject::connect(g_obsGeoSaveTimer, &QTimer::timeout, g_callbacks,
				 []() { SaveWindowGeometry("obsMainWindow", g_mainWindow); });
	}
	g_obsGeoSaveTimer->start(600);
}

// belt-and-suspenders for a forced close. taskkill /F, a crash, or an OS shutdown can end obs without firing the
// close/hide handlers or letting the debounced savers above flush, losing whatever move happened since the last
// event. this slow poll writes the current geometry every few seconds so a hard kill loses at most one interval of
// position. change-checked against an in-memory copy, and skips hidden/minimized windows so it never clobbers a
// good saved position with a tray-hidden or minimized rect.
void PollWindowGeometry()
{
	QByteArray obsGeo, clipsGeo;
	if (g_mainWindow && g_mainWindow->isVisible() && !g_mainWindow->isMinimized())
		obsGeo = g_mainWindow->saveGeometry();
	if (g_clipsWindow && !g_clipsFullscreenActive && g_clipsWindow->isVisible() && !g_clipsWindow->isMinimized())
		clipsGeo = g_clipsWindow->saveGeometry();

	const bool obsChanged = !obsGeo.isEmpty() && obsGeo != g_lastPolledObsGeo;
	const bool clipsChanged = !clipsGeo.isEmpty() && clipsGeo != g_lastPolledClipsGeo;
	if (!obsChanged && !clipsChanged)
		return;

	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	if (obsChanged) {
		s.setValue("obsMainWindow", obsGeo);
		g_lastPolledObsGeo = obsGeo;
	}
	if (clipsChanged) {
		s.setValue("clipsWindow", clipsGeo);
		g_lastPolledClipsGeo = clipsGeo;
	}
}

void StartWindowGeometryPoll()
{
	if (g_geoPollTimer)
		return;
	g_geoPollTimer = new QTimer(g_callbacks);
	QObject::connect(g_geoPollTimer, &QTimer::timeout, g_callbacks, PollWindowGeometry);
	g_geoPollTimer->start(5000);
}

// CEF treats a parent QWidgets close event as a browser-close request even when Qt leaves the parent allocated. Ignore
// that event after hiding the window so the browser stays usable when a hotkey reopens Clips immediately afterward.
class ClipsWindow : public QWidget {
protected:
	void closeEvent(QCloseEvent *event) override
	{
		SaveClipsWindowGeometry(this);
		hide();
		event->ignore();
	}
	// minimize is treated exactly like close -- hide the window instead of leaving a taskbar stub, and drop the WindowMinimized bit so the next ShowClips/ToggleClips brings it back at its normal (or maximized) size rather than still-minimized; deferred one tick since clearing the state mid WindowStateChange fights the window managers own minimize animation.
	void changeEvent(QEvent *event) override
	{
		QWidget::changeEvent(event);
		if (event->type() == QEvent::WindowStateChange && isMinimized()) {
			QTimer::singleShot(0, this, [this]() {
				hide();
				setWindowState(windowState() & ~Qt::WindowMinimized);
			});
		}
	}
	void moveEvent(QMoveEvent *event) override
	{
		QWidget::moveEvent(event);
		ScheduleClipsGeometrySave();
	}
	void resizeEvent(QResizeEvent *event) override
	{
		QWidget::resizeEvent(event);
		ScheduleClipsGeometrySave();
	}
};

// settings + setup host a cef browser the same way clips does, so they share its close rule -- hide, never destroy. destroying a cef-hosting qwidget mid-session trips the obs-browser#353 teardown race: the freed child browser keeps being asked to realise a native window every frame (CreateWindowEx "the parameter is incorrect", on repeat) and pegs the ui thread until obs is force-killed. the real delete is CloseCefWidgetsBeforeShutdown at module unload, the one point cef teardown is safe.
class CefHostWindow : public QWidget {
protected:
	void closeEvent(QCloseEvent *event) override
	{
		hide();
		event->ignore();
	}
};

// -- notification bell -------------------------------------------------------
// the bell rides in the title bar of the three windows we own (obs main, Clips, ReplayKit Settings). windows gives no
// way to add a real caption button, so each one is a tiny frameless always-on-top widget parked over the caption strip
// just left of the system minimize button, kept in step with its target by an event filter on move/resize/show. the
// unread count comes from the helper; clicking opens the panel window.

int g_notifyUnread = 0;
bool g_notifyPollInFlight = false;
// the dock --grey5 / --grey3 pair the clips sort trigger paints with, resolved from the active appearance theme by the
// helper and refreshed on the same /settings poll the tray rows use. stock-theme values until that first answer lands.
QColor g_notifySurface = QColor(0x2F, 0x32, 0x3C);
QColor g_notifyBorder = QColor(0x46, 0x4B, 0x59);
QColor g_notifyTipBg = QColor(0x13, 0x14, 0x1A);
QColor g_notifyTipFg = QColor(0xFF, 0xFF, 0xFF);
// resolved theme colours from the same /settings menuColors, for the confirm dialog. stock values until the first poll.
// panel/field/fieldHover/borderStrong/danger/muted mirror the dock .btn tokens so the dialog matches the rest of the ui.
QColor g_menuAccent = QColor(0x28, 0x4C, 0xB8);
QColor g_menuAccentLight = QColor(0x47, 0x6B, 0xD7);
QColor g_menuOnAccent = QColor(0xFF, 0xFF, 0xFF);
QColor g_menuPanel = QColor(0x27, 0x2A, 0x33);
QColor g_menuField = QColor(0x3C, 0x40, 0x4D);
QColor g_menuFieldHover = QColor(0x46, 0x4B, 0x59);
QColor g_menuBorderStrong = QColor(0x5B, 0x62, 0x73);
QColor g_menuDanger = QColor(0xE3, 0x3B, 0x57);
QColor g_menuMuted = QColor(0x96, 0x96, 0x96);
QPointer<QWidget> g_notifyPanel;
QPointer<QCefWidget> g_notifyBrowser;
// cef never gives qt the activation the panel would need to hear WindowDeactivate, so dismissal is driven off the real
// foreground window instead. the timer only runs while the panel is up.
QTimer *g_notifyDismissTimer = nullptr;
QElapsedTimer g_notifyShownAt;
bool g_notifyMouseWasDown = false;
bool g_notifyIgnoreOpeningMouseUntilRelease = false;
class NotifyButton;
// the button the open panel hangs off, so a window move can drag the panel along with it
QPointer<NotifyButton> g_notifyPanelAnchor;
static void ShowNotificationPanel(NotifyButton *anchor);
static void PositionNotifyPanel();

// notifications bell, embedded so the caption glyph needs no runtime asset -- fill is forced white here becuase QSvgRenderer has no css context for the source currentColor; painter opacity carries the idle-vs-hover dim
static const char kBellSvg[] =
	R"SVG(<svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg"><path fill-rule="evenodd" clip-rule="evenodd" d="M12.0303 1.17969C13.0589 1.17981 13.9714 1.73512 14.4414 2.62207C16.9695 3.59821 18.7694 6.05313 18.7695 8.91992V11.8105C18.7696 12.2705 18.9905 13.0803 19.2305 13.4902L20.3701 15.3906C20.7999 16.1106 20.8799 16.9806 20.5899 17.7705C20.2998 18.5604 19.6698 19.16 18.8799 19.4199C17.8056 19.7813 16.7003 20.0534 15.5791 20.2383C15.0846 21.7314 13.6779 22.8105 12.0195 22.8105C11.0297 22.8104 10.07 22.4099 9.37012 21.71C8.95526 21.2951 8.64619 20.7889 8.4629 20.2393C7.33941 20.0544 6.23057 19.7816 5.1504 19.4199C4.31045 19.13 3.66969 18.5404 3.38966 17.7705C3.09969 17.0006 3.2003 16.1506 3.66016 15.3906L4.80958 13.4805C5.04953 13.0805 5.26944 12.2806 5.26954 11.8105V8.91992C5.26966 6.04341 7.08241 3.58069 9.62403 2.61133C10.096 1.73724 11.0064 1.17969 12.0303 1.17969ZM13.7783 20.459C13.1936 20.5059 12.6064 20.5303 12.0195 20.5303C11.4329 20.5303 10.8464 20.5059 10.2617 20.459C10.3144 20.5254 10.3695 20.5901 10.4297 20.6504C10.8496 21.0703 11.4297 21.3104 12.0195 21.3105C12.731 21.3105 13.3656 20.9766 13.7783 20.459ZM12.7676 3.72363C12.2707 3.66407 11.7845 3.66172 11.3125 3.71875C8.75472 4.0651 6.76968 6.26075 6.76954 8.91992V11.8105C6.76946 12.5405 6.46956 13.6201 6.09962 14.25L4.9502 16.1602C4.73025 16.53 4.66992 16.92 4.79981 17.25C4.9198 17.59 5.21995 17.8502 5.62989 17.9902C9.80986 19.3902 14.2399 19.3902 18.4199 17.9902C18.7799 17.8702 19.0604 17.6002 19.1904 17.2402C19.3204 16.8803 19.2899 16.4901 19.0899 16.1602L17.9404 14.25C17.5604 13.6 17.2695 12.5298 17.2695 11.7998V8.91992C17.2694 6.27388 15.3132 4.08642 12.7676 3.72363Z" fill="#ffffff"/></svg>)SVG";

// chevron beside the bell -- same glyph and flip-on-open behaviour as the clips sort trigger
static const char kChevronSvg[] =
	R"SVG(<svg width="24" height="24" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><path d="M7.41 8.59 12 13.17l4.59-4.58L18 10l-6 6-6-6z" fill="#ffffff"/></svg>)SVG";

// pulls an integer field out of the small known-shape helper responses, same reasoning as JsonBoolField
int JsonIntField(const std::string &body, const char *field, int defaultValue)
{
	std::string needle = std::string("\"") + field + "\":";
	size_t at = body.find(needle);
	if (at == std::string::npos)
		return defaultValue;
	at += needle.size();
	while (at < body.size() && (body[at] == ' ' || body[at] == '\t'))
		at++;
	bool negative = at < body.size() && body[at] == '-';
	if (negative)
		at++;
	if (at >= body.size() || body[at] < '0' || body[at] > '9')
		return defaultValue;
	int value = 0;
	while (at < body.size() && body[at] >= '0' && body[at] <= '9') {
		value = value * 10 + (body[at] - '0');
		at++;
	}
	return negative ? -value : value;
}

// native twin of the shared _tooltip partial (#rk-tip). the bell is a title-bar window with no page behind it, so the
// css cannot be reused -- every number here mirrors that file and has to move with it. click-through, never focused.
class NotifyTip : public QWidget {
public:
	NotifyTip() : QWidget(nullptr)
	{
		setWindowFlags(Qt::ToolTip | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground);
		setAttribute(Qt::WA_ShowWithoutActivating);
		setAttribute(Qt::WA_TransparentForMouseEvents);
		// deliberately NOT setFont(): obs installs an app-wide qss, and a stylesheet font-size overrides a widget font,
		// which is what made this tip render larger than every other one. TipFont() is used directly for metrics and
		// painting instead. segoe ui also ships no 500 weight, so chromium renders the partials font-weight:500 as
		// regular -- Normal here is what actually matches on screen, where Medium rounded up to semibold.
		m_anim = new QTimer(this);
		m_anim->setInterval(16);
		connect(m_anim, &QTimer::timeout, this, [this]() {
			qreal elapsed = m_since.isValid() ? m_since.elapsed() : kPop;
			m_t = qMin(qreal(1.0), elapsed / qreal(kPop));
			setWindowOpacity(qMin(qreal(1.0), elapsed / qreal(kFade)));
			if (m_t >= 1.0)
				m_anim->stop();
			update();
		});
	}

	// anchor is the control in global coords. mirrors positionBulkTip: 8px below it, arrow on the controls centre
	// clamped 10px inside the tip, and the whole box kept 6px clear of the screen edge.
	void ShowFor(const QString &text, const QRect &anchorGlobal)
	{
		m_text = text;
		QFontMetrics fm(TipFont());
		int w = fm.horizontalAdvance(text) + kPadX * 2;
		int bodyH = fm.height() + kPadY * 2;
		setFixedSize(w + kShadow * 2, bodyH + kArrow + kShadow * 2);

		int centre = anchorGlobal.center().x();
		int left = centre - w / 2;
		// the arrow lives above the body, so the widget starts kArrow higher than the css tips top edge
		int top = anchorGlobal.bottom() + 1 + kGap - kArrow;
		if (QScreen *screen = QGuiApplication::screenAt(anchorGlobal.center())) {
			QRect avail = screen->availableGeometry();
			left = qBound(avail.left() + kEdge, left, avail.right() - w - kEdge);
			top = qMin(top, avail.bottom() - (bodyH + kArrow) - kEdge);
		}
		m_arrowX = qBound(qreal(10.0), qreal(centre - left), qreal(w - 10.0));
		move(left - kShadow, top - kShadow);

		m_t = 0.0;
		m_since.start();
		setWindowOpacity(0.0);
		show();
		raise();
		m_anim->start();
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.setRenderHint(QPainter::Antialiasing, true);

		// grow-in: translateY(3px) scale(.96) -> none, about top center, on an ease-out that stands in for ease_pop
		qreal e = 1.0 - qPow(1.0 - m_t, 3.0);
		qreal scale = 0.96 + 0.04 * e;
		qreal dy = 3.0 * (1.0 - e);
		QPointF origin(kShadow + (width() - kShadow * 2) / 2.0, qreal(kShadow));
		p.translate(origin);
		p.scale(scale, scale);
		p.translate(-origin);
		p.translate(0, dy);

		QRectF body(kShadow, kShadow + kArrow, width() - kShadow * 2, height() - kShadow * 2 - kArrow);
		QPainterPath shape;
		shape.addRoundedRect(body, kRadius, kRadius);
		QPainterPath arrow;
		qreal ax = kShadow + m_arrowX;
		arrow.moveTo(ax - kArrow, body.top());
		arrow.lineTo(ax, body.top() - kArrow);
		arrow.lineTo(ax + kArrow, body.top());
		arrow.closeSubpath();
		shape = shape.united(arrow);

		// box-shadow 0 3px 10px rgba(0,0,0,.42), stacked as thin rings -- a QGraphicsEffect on a translucent tool
		// window does not composite reliably, and at this size the rings are indistinguishable from a real blur
		p.setPen(Qt::NoPen);
		for (int i = kShadow; i >= 1; --i) {
			QColor ring(0, 0, 0);
			ring.setAlphaF(0.42 * 0.085 * (1.0 - qreal(i - 1) / kShadow));
			QPainterPath blurred;
			blurred.addRoundedRect(body.translated(0, 3).adjusted(-i, -i, i, i), kRadius + i, kRadius + i);
			p.setBrush(ring);
			p.drawPath(blurred);
		}

		// mirrors #rk-tip in _tooltip.html: 88% opaque ground so it reads as slightly see-through
		QColor tipFill = g_notifyTipBg;
		tipFill.setAlphaF(0.88);
		p.setBrush(tipFill);
		p.drawPath(shape);
		p.setPen(g_notifyTipFg);
		p.setFont(TipFont());
		p.drawText(body, Qt::AlignCenter, m_text);
	}

private:
	static const int kPadX = 8;
	static const int kPadY = 5;
	static const int kRadius = 3;
	static const int kArrow = 5;
	static const int kGap = 8;
	static const int kEdge = 6;
	static const int kShadow = 10;
	static const int kPop = 130;   // --anim_press
	static const int kFade = 100;  // opacity 100ms linear
	// mirrors #rk-tip in _tooltip.html: 11px, weight 500 which segoe ui resolves to regular
	static QFont TipFont()
	{
		QFont f("Segoe UI");
		f.setPixelSize(11);
		f.setWeight(QFont::Normal);
		return f;
	}

	QString m_text;
	qreal m_arrowX = 0.0;
	qreal m_t = 1.0;
	QTimer *m_anim = nullptr;
	QElapsedTimer m_since;
};

NotifyTip *g_notifyTip = nullptr;

class NotifyButton : public QWidget {
public:
	// mirrors .clip-sort-trigger exactly: 6px pad, 16px glyph, 3px gap, 12px chevron, 6px pad, 6px corner radius.
	// kDividerZone is the only thing past the pill -- the hairline to the system minimize button and its breathing room.
	static const int kPillWidth = 43;
	static const int kDividerZone = 8;
	static const int kWidth = kPillWidth + kDividerZone;
	static const int kHeight = 28;
	static const int kRadius = 6;

	explicit NotifyButton(QWidget *target) : QWidget(target), m_target(target)
	{
		// owned by the target rather than free-floating: an owned top-level always sits directly above its owner --
		// including over the owners caption, which is the whole point -- and never over unrelated apps.
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground);
		setAttribute(Qt::WA_ShowWithoutActivating);
		setFixedSize(kWidth, kHeight);
		setCursor(Qt::PointingHandCursor);
	}

	QWidget *target() const { return m_target; }
	// each bell owns its panel: a parentless Qt::Tool is auto-hidden whenever the application deactivates, and one
	// shared panel cannot be owned by all three windows at once, so there is one per bell kept alive after first use
	QPointer<QWidget> m_panel;
	QPointer<QCefWidget> m_browser;

protected:
	void enterEvent(QEnterEvent *event) override
	{
		QWidget::enterEvent(event);
		m_hover = true;
		StartHoverFade();
		ShowTip();
	}
	void leaveEvent(QEvent *event) override
	{
		QWidget::leaveEvent(event);
		m_hover = false;
		StartHoverFade();
		HideTip();
	}

	void ShowTip()
	{
		// suppressed while our own panel is open, the same rule showBulkTip applies to the sort trigger
		if (m_panel && m_panel->isVisible())
			return;
		if (!g_notifyTip)
			g_notifyTip = new NotifyTip();
		QRect pillGlobal(mapToGlobal(QPoint(0, 0)), QSize(kPillWidth, height()));
		g_notifyTip->ShowFor("Notifications", pillGlobal);
	}
	static void HideTip()
	{
		if (g_notifyTip)
			g_notifyTip->hide();
	}

	// css does this for the sort trigger; here it is a plain 90ms ramp driven off one timer so the fill matches
	void StartHoverFade()
	{
		if (!m_fadeTimer) {
			m_fadeTimer = new QTimer(this);
			m_fadeTimer->setInterval(16);
			connect(m_fadeTimer, &QTimer::timeout, this, [this]() {
				const qreal step = 16.0 / 90.0;
				qreal target = m_hover ? 1.0 : 0.0;
				if (m_hoverFade < target)
					m_hoverFade = qMin(target, m_hoverFade + step);
				else
					m_hoverFade = qMax(target, m_hoverFade - step);
				if (qFuzzyCompare(m_hoverFade + 1.0, target + 1.0))
					m_fadeTimer->stop();
				update();
			});
		}
		m_fadeTimer->start();
	}
	void mousePressEvent(QMouseEvent *event) override
	{
		if (event->button() == Qt::LeftButton && event->position().x() < kPillWidth) {
			HideTip();
			ShowNotificationPanel(this);
		}
	}
	// windows eats the first click on a window whose owner is not active, spending it on activation instead of
	// delivering it -- that is the click that sometimes did nothing. answering MA_NOACTIVATE keeps the press coming
	// through without this tool window taking activation at all.
	bool nativeEvent(const QByteArray &, void *message, qintptr *result) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_MOUSEACTIVATE)
			return false;
		*result = MA_NOACTIVATE;
		return true;
	}
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.setRenderHint(QPainter::Antialiasing, true);
		const bool open = m_panel && m_panel->isVisible();
		const QRectF pill(0.5, 0.5, kPillWidth - 1.0, height() - 1.0);

		// WA_TranslucentBackground makes this a layered window, and those hit-test on alpha: a fully transparent pixel
		// drops the click through to the caption underneath, so at rest only the glyphs own pixels were clickable.
		// one imperceptible pass gives the pill a hit area at all times. the divider zone is deliberately left out so
		// it still drags the window like the caption it sits on.
		p.fillRect(QRectF(0, 0, kPillWidth, height()), QColor(0, 0, 0, 1));

		// surface states copied from .clip-sort-trigger: nothing at rest, the menu surface on hover, and on open the
		// same surface plus a border whose bottom edge is left off so the panel below reads as one shape.
		if (open) {
			QPainterPath tab;
			tab.moveTo(pill.left(), pill.bottom());
			tab.lineTo(pill.left(), pill.top() + kRadius);
			tab.quadTo(pill.left(), pill.top(), pill.left() + kRadius, pill.top());
			tab.lineTo(pill.right() - kRadius, pill.top());
			tab.quadTo(pill.right(), pill.top(), pill.right(), pill.top() + kRadius);
			tab.lineTo(pill.right(), pill.bottom());
			p.setPen(Qt::NoPen);
			p.setBrush(g_notifySurface);
			p.drawPath(tab);
			p.setPen(QPen(g_notifyBorder, 1.0));
			p.setBrush(Qt::NoBrush);
			p.drawPath(tab);
		} else if (m_hoverFade > 0.001) {
			// 90ms linear ramp, the same transition .clip-sort-trigger animates its background-color over
			p.setPen(Qt::NoPen);
			QColor fill = g_notifySurface;
			fill.setAlphaF(m_hoverFade);
			p.setBrush(fill);
			p.drawRoundedRect(pill, kRadius, kRadius);
		}

		// 6px pad then the 16px bell, then a 3px gap and the 12px chevron -- the sort triggers own run of boxes
		const qreal midY = height() / 2.0;
		static QSvgRenderer bellSvg{QByteArray::fromRawData(kBellSvg, qsizetype(sizeof(kBellSvg) - 1))};
		if (bellSvg.isValid()) {
			p.save();
			p.setOpacity(m_hover || open ? 1.0 : 0.8);
			bellSvg.render(&p, QRectF(6.0, midY - 8.0, 16.0, 16.0));
			p.restore();
		}
		static QSvgRenderer chevronSvg{QByteArray::fromRawData(kChevronSvg, qsizetype(sizeof(kChevronSvg) - 1))};
		if (chevronSvg.isValid()) {
			QRectF box(25.0, midY - 6.0, 12.0, 12.0);
			p.save();
			p.setOpacity(0.65);
			if (open) {
				// flips on open, same as .clip-sort-wrap.is-open .clip-sort-chevron
				p.translate(box.center());
				p.rotate(180.0);
				p.translate(-box.center());
			}
			chevronSvg.render(&p, box);
			p.restore();
		}

		// hairline between us and the system minimize button, same idea as the menu separators in clips -- without it
		// the bell reads as part of the window buttons rather than as our own control
		p.setPen(QPen(QColor(255, 255, 255, 38), 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawLine(QPointF(width() - 0.5, 6.0), QPointF(width() - 0.5, height() - 6.0));

		if (g_notifyUnread > 0) {
			QString label = g_notifyUnread > 9 ? QString("9+") : QString::number(g_notifyUnread);
			QFont f = font();
			f.setPixelSize(8);
			f.setBold(true);
			p.setFont(f);
			qreal badgeW = label.size() > 1 ? 15.0 : 12.0;
			QRectF badge(19.0, 1.0, badgeW, 12.0);
			p.setPen(Qt::NoPen);
			p.setBrush(QColor(227, 59, 87));
			p.drawRoundedRect(badge, 6, 6);
			p.setPen(QColor(255, 255, 255));
			p.drawText(badge, Qt::AlignCenter, label);
		}
	}

private:
	QWidget *m_target = nullptr;
	bool m_hover = false;
	qreal m_hoverFade = 0.0;
	QTimer *m_fadeTimer = nullptr;
};

QList<QPointer<NotifyButton>> g_notifyButtons;

// parks the button inside the caption, immediately left of the system buttons. DWMWA_CAPTION_BUTTON_BOUNDS is the only
// reliable source for where those start -- their width changes with dpi, the windows version, and whether the window
// has a maximize box at all. hidden whenever the target is not a normal visible window, so it can never float alone.
static void PositionNotifyButton(NotifyButton *btn)
{
	QWidget *target = btn ? btn->target() : nullptr;
	if (!target)
		return;
	if (!target->isVisible() || target->isMinimized()) {
		btn->hide();
		return;
	}
	HWND hwnd = (HWND)target->winId();
	RECT frame = {};
	if (!GetWindowRect(hwnd, &frame)) {
		btn->hide();
		return;
	}
	RECT buttons = {};
	if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, &buttons, sizeof(buttons))) ||
	    buttons.right <= buttons.left) {
		btn->hide();
		return;
	}
	// bounds come back window-relative, including the invisible resize border
	int captionTop = frame.top + buttons.top;
	int captionHeight = buttons.bottom - buttons.top;
	if (captionHeight < NotifyButton::kHeight) {
		btn->hide();
		return;
	}
	int x = frame.left + buttons.left - NotifyButton::kWidth - 2;
	int y = captionTop + (captionHeight - NotifyButton::kHeight) / 2;
	if (x < frame.left) {
		btn->hide();
		return;
	}
	// moving the window out from under a press loses it, so hold still while the button is being clicked
	if (btn->rect().contains(btn->mapFromGlobal(QCursor::pos())) && (GetAsyncKeyState(VK_LBUTTON) & 0x8000))
		return;
	if (btn->pos() != QPoint(x, y))
		btn->move(x, y);
	if (!btn->isVisible())
		btn->show();
	if (g_notifyPanel && g_notifyPanelAnchor == btn)
		PositionNotifyPanel();
}

// Flush under the bell and right-aligned to it, with a one-pixel overlap that hides the shared border exactly like the
// Clips sort menu. frameGeometry is already in screen coordinates for this top-level tool button; mapToGlobal mixes
// parent/client coordinates and is what left the panel visibly detached on some caption configurations.
static void PositionNotifyPanel()
{
	QWidget *panel = g_notifyPanel;
	NotifyButton *anchor = g_notifyPanelAnchor;
	if (!panel || !anchor || !anchor->isVisible())
		return;
	// right edge flush with the pill, not the divider zone, and a pixel of overlap so the triggers open bottom edge
	// covers the panels top border and the seam disappears -- what .clip-sort-menu does with top:calc(100% - 1px)
	QRect anchorRect = anchor->frameGeometry();
	int x = anchorRect.left() + NotifyButton::kPillWidth - panel->width();
	int y = anchorRect.bottom();
	QScreen *screen = QGuiApplication::screenAt(anchorRect.center());
	if (!screen)
		screen = QGuiApplication::primaryScreen();
	if (screen) {
		QRect avail = screen->availableGeometry();
		x = qMax(avail.left() + 4, qMin(x, avail.right() - panel->width() - 4));
		y = qMax(avail.top() + 4, qMin(y, avail.bottom() - panel->height() - 4));
	}
	panel->move(x, y);
}

static void PositionAllNotifyButtons()
{
	for (auto &btn : g_notifyButtons)
		if (btn)
			PositionNotifyButton(btn.data());
}

// keeps one button glued to its window. every one of these events can move the caption, and the safety-net timer alone
// lagged visibly while dragging.
class NotifyAnchorFilter : public QObject {
public:
	NotifyAnchorFilter(QObject *parent, NotifyButton *btn) : QObject(parent), m_btn(btn) {}

protected:
	bool eventFilter(QObject *, QEvent *event) override
	{
		switch (event->type()) {
		case QEvent::Move:
		case QEvent::Resize:
		case QEvent::Show:
		case QEvent::WindowStateChange:
		case QEvent::WindowActivate:
			if (m_btn)
				PositionNotifyButton(m_btn.data());
			break;
		case QEvent::Hide:
		case QEvent::Close:
			if (m_btn)
				m_btn->hide();
			break;
		default:
			break;
		}
		return false;
	}

private:
	QPointer<NotifyButton> m_btn;
};

void AttachNotifyButton(QWidget *target)
{
	if (!target)
		return;
	for (auto &existing : g_notifyButtons)
		if (existing && existing->target() == target)
			return;
	NotifyButton *btn = new NotifyButton(target);
	target->installEventFilter(new NotifyAnchorFilter(target, btn));
	g_notifyButtons.append(QPointer<NotifyButton>(btn));
	PositionNotifyButton(btn);
}

void RefreshNotifyBadges()
{
	for (auto &btn : g_notifyButtons)
		if (btn)
			btn->update();
}

// no dwm attribute exposes the icon/title rect the way DWMWA_CAPTION_BUTTON_BOUNDS exposes the system buttons, so the
// text width is measured instead: lfCaptionFont from the non-client metrics is the exact font windows draws the
// title in, and windowTitle() is the same string qt already keeps the native caption in sync with
static QFont CaptionTitleFont()
{
	NONCLIENTMETRICSW ncm = {};
	ncm.cbSize = sizeof(ncm);
	QFont fallback("Segoe UI");
	fallback.setPixelSize(12);
	if (!SystemParametersInfoW(SPI_GETNONCLIENTMETRICS, sizeof(ncm), &ncm, 0))
		return fallback;
	QFont f(QString::fromWCharArray(ncm.lfCaptionFont.lfFaceName));
	f.setPixelSize(ncm.lfCaptionFont.lfHeight != 0 ? qAbs(ncm.lfCaptionFont.lfHeight) : 12);
	f.setBold(ncm.lfCaptionFont.lfWeight >= FW_BOLD);
	return f;
}

// same darker rounded chip as the tray menus own keybind badge (identical rgba fill/ink), and the divider next to it
// is the same hairline NotifyButton draws next to the system minimize button
class TitleKeybindBadge : public QWidget {
public:
	static const int kHeight = 20;
	static const int kChipRadius = 3;
	static const int kChipPadX = 5;
	static const int kChipGap = 24;
	static const int kLabelChipGap = 5;
	// bigger than plain breathing room needs to be -- also covers CaptionTitleFonts measured title width running a bit short of the real rendered title on the main obs window
	static const int kDividerGapBefore = 18;
	static const int kDividerGapAfter = 8;
	static const int kEdgeMargin = 6;
	// NotifyButton's own hairline is 16px (its 28px height minus a 6px inset top and bottom) -- kept as the same
	// absolute length here too, just centered in this widgets own shorter height instead of re-deriving from it
	static const int kDividerLength = 16;
	static const int kIconLeftMargin = 10;
	static const int kIconTextGap = 8;

	explicit TitleKeybindBadge(QWidget *target) : QWidget(target), m_target(target)
	{
		setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		setAttribute(Qt::WA_TranslucentBackground);
		setAttribute(Qt::WA_ShowWithoutActivating);
		// click-through like the notification tip -- this only ever displays keybinds, so a press here should drag
		// the caption underneath instead of getting eaten by this widget
		setAttribute(Qt::WA_TransparentForMouseEvents);
		setFixedHeight(kHeight);
	}

	QWidget *target() const { return m_target; }
	bool HasLabels() const { return !m_recordLabel.isEmpty() || !m_clipLabel.isEmpty(); }

	void SetLabels(const QString &recordLabel, const QString &clipLabel)
	{
		if (recordLabel == m_recordLabel && clipLabel == m_clipLabel)
			return;
		m_recordLabel = recordLabel;
		m_clipLabel = clipLabel;
		update();
	}

	// non-empty keybinds only -- a cleared keybind drops its whole name+chip pair instead of leaving an orphaned label
	QList<QPair<QString, QString>> Entries() const
	{
		QList<QPair<QString, QString>> entries;
		if (!m_clipLabel.isEmpty())
			entries.append({QStringLiteral("Clip"), m_clipLabel});
		if (!m_recordLabel.isEmpty())
			entries.append({QStringLiteral("Recording"), m_recordLabel});
		return entries;
	}

	// divider plus its own margins, then each pairs name label and its measured chip width
	int ContentWidth() const
	{
		QFontMetrics fm(ChipFont());
		QFontMetrics nameFm(NameFont());
		auto entries = Entries();
		int w = kDividerGapBefore + 1 + kDividerGapAfter;
		for (int i = 0; i < entries.size(); i++) {
			if (i > 0)
				w += kChipGap;
			w += nameFm.horizontalAdvance(entries[i].first) + kLabelChipGap;
			w += fm.horizontalAdvance(entries[i].second) + kChipPadX * 2;
		}
		return w;
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.setRenderHint(QPainter::Antialiasing, true);
		QFont chipFont = ChipFont();
		QFont nameFont = NameFont();
		QFontMetrics fm(chipFont);
		QFontMetrics nameFm(nameFont);
		qreal x = kDividerGapBefore;
		// same hairline NotifyButton draws next to the system minimize button -- the 0.5 offset is what it uses to
		// land a 1px pen on a single pixel column; without it antialiasing splits the stroke across two columns,
		// which is what made this read as thicker and paler than the source instead of a crisp match
		qreal dividerTop = (height() - kDividerLength) / 2.0;
		p.setPen(QPen(QColor(255, 255, 255, 38), 1.0));
		p.setBrush(Qt::NoBrush);
		p.drawLine(QPointF(x - 0.5, dividerTop), QPointF(x - 0.5, dividerTop + kDividerLength));
		x += 1 + kDividerGapAfter;
		auto entries = Entries();
		for (int i = 0; i < entries.size(); i++) {
			if (i > 0)
				x += kChipGap;
			// bold white name (Recording/Clipping) same as the tray rows own nameLabel, paired tight against its chip
			p.setFont(nameFont);
			p.setPen(QColor(255, 255, 255));
			qreal nameW = nameFm.horizontalAdvance(entries[i].first);
			p.drawText(QRectF(x, 0.0, nameW, height()), Qt::AlignVCenter | Qt::AlignLeft, entries[i].first);
			x += nameW + kLabelChipGap;

			p.setFont(chipFont);
			qreal chipW = fm.horizontalAdvance(entries[i].second) + kChipPadX * 2;
			QRectF chip(x, 0.0, chipW, height());
			p.setPen(Qt::NoPen);
			// same rgba(0,0,0,70)/rgba(255,255,255,160) the tray rows own chipLabel uses, so both keybind chips match
			p.setBrush(QColor(0, 0, 0, 70));
			p.drawRoundedRect(chip, kChipRadius, kChipRadius);
			p.setPen(QColor(255, 255, 255, 160));
			p.drawText(chip, Qt::AlignCenter, entries[i].second);
			x += chipW;
		}
	}

private:
	static QFont ChipFont()
	{
		QFont f("Segoe UI");
		f.setPixelSize(10);
		return f;
	}
	static QFont NameFont()
	{
		QFont f("Segoe UI");
		f.setPixelSize(11);
		f.setBold(true);
		return f;
	}
	QWidget *m_target = nullptr;
	QString m_recordLabel;
	QString m_clipLabel;
};

QList<QPointer<TitleKeybindBadge>> g_titleKeybindBadges;
QString g_titleRecordLabel;
QString g_titleClipLabel;

// mirrors PositionNotifyButton but anchored off the left edge instead of DWMWA_CAPTION_BUTTON_BOUNDS, since nothing
// analogous exists for the icon/title side -- hides itself whenever the native title (dynamic, sometimes very long
// on the main obs window) would leave no real room before the bell or the system buttons, rather than overlapping
static void PositionTitleKeybindBadge(TitleKeybindBadge *badge)
{
	QWidget *target = badge ? badge->target() : nullptr;
	if (!target)
		return;
	if (!target->isVisible() || target->isMinimized() || !badge->HasLabels()) {
		badge->hide();
		return;
	}
	HWND hwnd = (HWND)target->winId();
	RECT frame = {};
	if (!GetWindowRect(hwnd, &frame)) {
		badge->hide();
		return;
	}
	RECT buttons = {};
	if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, &buttons, sizeof(buttons))) ||
	    buttons.right <= buttons.left) {
		badge->hide();
		return;
	}
	int captionTop = frame.top + buttons.top;
	int captionHeight = buttons.bottom - buttons.top;
	if (captionHeight < TitleKeybindBadge::kHeight) {
		badge->hide();
		return;
	}

	// stop short of whichever comes first, our own bell or the system buttons, so a long obs title (profile + scene
	// collection can run long) just drops the badge instead of drawing over either one
	int rightLimit = frame.left + buttons.left;
	for (auto &btn : g_notifyButtons) {
		if (btn && btn->target() == target && btn->isVisible())
			rightLimit = qMin(rightLimit, btn->x());
	}
	rightLimit -= TitleKeybindBadge::kEdgeMargin;

	int textLeft = frame.left + TitleKeybindBadge::kIconLeftMargin + GetSystemMetrics(SM_CXSMICON) +
		       TitleKeybindBadge::kIconTextGap;
	int textWidth = QFontMetrics(CaptionTitleFont()).horizontalAdvance(target->windowTitle());
	int badgeLeft = textLeft + textWidth;
	int badgeWidth = badge->ContentWidth();
	if (badgeLeft + badgeWidth > rightLimit) {
		badge->hide();
		return;
	}
	int y = captionTop + (captionHeight - TitleKeybindBadge::kHeight) / 2;
	if (badge->width() != badgeWidth)
		badge->setFixedWidth(badgeWidth);
	if (badge->pos() != QPoint(badgeLeft, y))
		badge->move(badgeLeft, y);
	if (!badge->isVisible())
		badge->show();
}

// same event set as NotifyAnchorFilter, plus WindowTitleChange -- this badges x position depends on the titles own
// measured width, so a title edit (profile switch, recording state) has to reflow it and not just a move or resize
class TitleKeybindAnchorFilter : public QObject {
public:
	TitleKeybindAnchorFilter(QObject *parent, TitleKeybindBadge *badge) : QObject(parent), m_badge(badge) {}

protected:
	bool eventFilter(QObject *, QEvent *event) override
	{
		switch (event->type()) {
		case QEvent::Move:
		case QEvent::Resize:
		case QEvent::Show:
		case QEvent::WindowStateChange:
		case QEvent::WindowActivate:
		case QEvent::WindowTitleChange:
			if (m_badge)
				PositionTitleKeybindBadge(m_badge.data());
			break;
		case QEvent::Hide:
		case QEvent::Close:
			if (m_badge)
				m_badge->hide();
			break;
		default:
			break;
		}
		return false;
	}

private:
	QPointer<TitleKeybindBadge> m_badge;
};

void AttachTitleKeybindBadge(QWidget *target)
{
	if (!target)
		return;
	for (auto &existing : g_titleKeybindBadges)
		if (existing && existing->target() == target)
			return;
	TitleKeybindBadge *badge = new TitleKeybindBadge(target);
	target->installEventFilter(new TitleKeybindAnchorFilter(target, badge));
	g_titleKeybindBadges.append(QPointer<TitleKeybindBadge>(badge));
	badge->SetLabels(g_titleRecordLabel, g_titleClipLabel);
	PositionTitleKeybindBadge(badge);
}

static void PositionAllTitleKeybindBadges()
{
	for (auto &badge : g_titleKeybindBadges)
		if (badge)
			PositionTitleKeybindBadge(badge.data());
}

// same /settings body the hotkey + theme poll already fetches, so this costs no extra request -- recording and
// clipping are global hotkeys regardless of which window has focus, so every title bar shows the same pair
void RefreshTitleKeybindBadges(const std::string &settingsBody)
{
	QString recordLabel = QString::fromStdString(KeybindLabelFromSettingsJson(settingsBody, "recordingKeybind"));
	QString clipLabel = QString::fromStdString(KeybindLabelFromSettingsJson(settingsBody, "clipKeybind"));
	if (recordLabel == g_titleRecordLabel && clipLabel == g_titleClipLabel)
		return;
	g_titleRecordLabel = recordLabel;
	g_titleClipLabel = clipLabel;
	for (auto &badge : g_titleKeybindBadges) {
		if (!badge)
			continue;
		badge->SetLabels(recordLabel, clipLabel);
		PositionTitleKeybindBadge(badge.data());
	}
}

// the bells follow the appearance theme: --grey5 / --grey3 come back on the same /settings body the hotkey poll
// already fetches, so this costs no extra request and lands within a second of a theme change.
void ApplyNotifyThemeColors(const std::string &settingsBody)
{
	std::string menuColors = ExtractJsonObjectField(settingsBody, "menuColors");
	if (menuColors.empty())
		return;
	QColor surface(QString::fromStdString(ExtractJsonStringField(menuColors, "surface")));
	QColor border(QString::fromStdString(ExtractJsonStringField(menuColors, "border")));
	QColor tip(QString::fromStdString(ExtractJsonStringField(menuColors, "tip")));
	QColor tipInk(QString::fromStdString(ExtractJsonStringField(menuColors, "text")));
	QColor accent(QString::fromStdString(ExtractJsonStringField(menuColors, "accent")));
	QColor accentLight(QString::fromStdString(ExtractJsonStringField(menuColors, "accentLight")));
	QColor onAccent(QString::fromStdString(ExtractJsonStringField(menuColors, "onAccent")));
	QColor mPanel(QString::fromStdString(ExtractJsonStringField(menuColors, "panel")));
	QColor mField(QString::fromStdString(ExtractJsonStringField(menuColors, "field")));
	QColor mFieldHover(QString::fromStdString(ExtractJsonStringField(menuColors, "fieldHover")));
	QColor mBorderStrong(QString::fromStdString(ExtractJsonStringField(menuColors, "borderStrong")));
	QColor mDanger(QString::fromStdString(ExtractJsonStringField(menuColors, "danger")));
	QColor mMuted(QString::fromStdString(ExtractJsonStringField(menuColors, "muted")));
	if (accent.isValid()) g_menuAccent = accent;
	if (accentLight.isValid()) g_menuAccentLight = accentLight;
	if (onAccent.isValid()) g_menuOnAccent = onAccent;
	if (mPanel.isValid()) g_menuPanel = mPanel;
	if (mField.isValid()) g_menuField = mField;
	if (mFieldHover.isValid()) g_menuFieldHover = mFieldHover;
	if (mBorderStrong.isValid()) g_menuBorderStrong = mBorderStrong;
	if (mDanger.isValid()) g_menuDanger = mDanger;
	if (mMuted.isValid()) g_menuMuted = mMuted;
	bool changed = false;
	if (tip.isValid() && tip != g_notifyTipBg) {
		g_notifyTipBg = tip;
		changed = true;
	}
	if (tipInk.isValid() && tipInk != g_notifyTipFg) {
		g_notifyTipFg = tipInk;
		changed = true;
	}
	if (surface.isValid() && surface != g_notifySurface) {
		g_notifySurface = surface;
		changed = true;
	}
	if (border.isValid() && border != g_notifyBorder) {
		g_notifyBorder = border;
		changed = true;
	}
	if (!changed)
		return;
	if (g_notifyPanel) {
		QPalette pal = g_notifyPanel->palette();
		pal.setColor(QPalette::Window, g_notifySurface);
		g_notifyPanel->setPalette(pal);
	}
	RefreshNotifyBadges();
}

// the helper owns the list, so the count is polled rather than pushed -- one small request every few seconds, skipped
// while one is already in flight so a slow helper cannot stack them up.
void PollNotificationCount()
{
	if (g_notifyPollInFlight)
		return;
	g_notifyPollInFlight = true;
	RunAsync([]() {
		std::string body = HttpRequest("GET", "/notifications", 8767, nullptr, 2500);
		int unread = JsonIntField(body, "unread", -1);
		QMetaObject::invokeMethod(
			g_callbacks,
			[unread]() {
				g_notifyPollInFlight = false;
				if (unread < 0)
					return; // helper down or answering something else: keep the last known count
				if (unread == g_notifyUnread)
					return;
				g_notifyUnread = unread;
				RefreshNotifyBadges();
			},
			Qt::QueuedConnection);
	});
}

// window drags run inside a modal move loop that never gets back to qts move/resize events until it ends, which is why
// the button and panel lagged behind the window. WM_WINDOWPOSCHANGED is delivered throughout that loop, so tracking it
// natively is what makes them look attached.
class NotifyTrackFilter : public QAbstractNativeEventFilter {
public:
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg)
			return false;
		if (msg->message != WM_WINDOWPOSCHANGED && msg->message != WM_MOVE && msg->message != WM_SIZE)
			return false;
		for (auto &btn : g_notifyButtons) {
			if (!btn || !btn->target())
				continue;
			if ((HWND)btn->target()->winId() != msg->hwnd)
				continue;
			PositionNotifyButton(btn.data());
			break;
		}
		for (auto &badge : g_titleKeybindBadges) {
			if (!badge || !badge->target())
				continue;
			if ((HWND)badge->target()->winId() != msg->hwnd)
				continue;
			PositionTitleKeybindBadge(badge.data());
			break;
		}
		return false;
	}
};

// the panel is a frameless cef window pinned under the bell that opened it. Qt::Tool rather than Qt::Popup, becuase a
// popup grabs the mouse and the page inside would never receive a click.
static void HideNotificationPanel()
{
	if (g_notifyDismissTimer)
		g_notifyDismissTimer->stop();
	if (g_notifyPanel)
		g_notifyPanel->hide();
	if (g_notifyPanelAnchor)
		g_notifyPanelAnchor->update();
	PollNotificationCount();
}

// the panel is built once and then shown/hidden, never destroyed: a fresh QCefWidget per open repaints white before the
// page lands, which is the flash. same reason the Clips window only hides on close.
static void ShowNotificationPanel(NotifyButton *anchor)
{
	if (!g_cef || !anchor)
		return;
	if (g_notifyPanel && g_notifyPanel->isVisible()) {
		// second click on the bell closes it again, same as any menu
		HideNotificationPanel();
		return;
	}

	g_notifyPanel = anchor->m_panel;
	g_notifyBrowser = anchor->m_browser;
	if (!g_notifyPanel) {
		// Tool windows still use global coordinates when owned. Ownership keeps the panel above the OBS window
		// when the caption bell is raised, and prevents Qt from hiding an unrelated parentless tool.
		QWidget *panel = new QWidget(anchor->target());
		panel->setWindowFlags(Qt::Tool | Qt::FramelessWindowHint | Qt::NoDropShadowWindowHint);
		panel->setWindowTitle("ReplayKit Notifications");
		panel->resize(360, 430);
		// paints under cef before its first frame, so the gap is the panel colour rather than white
		panel->setAutoFillBackground(true);
		QPalette pal = panel->palette();
		pal.setColor(QPalette::Window, g_notifySurface);
		panel->setPalette(pal);

		QVBoxLayout *layout = new QVBoxLayout(panel);
		layout->setContentsMargins(0, 0, 0, 0);
		QCefWidget *browser = g_cef->create_widget(panel, "http://127.0.0.1:8767/notifications-view", nullptr);
		layout->addWidget(browser);
		g_notifyBrowser = browser;
		g_notifyPanel = panel;
		anchor->m_panel = panel;
		anchor->m_browser = browser;
	}

	// dismissal watches for a real click landing outside the panel rather than for activation: cef keeps focus in its
	// own hwnd, so the panel is often never the foreground window and any activation-based test hides it instantly.
	// same GetAsyncKeyState polling the resize grip uses, edge-triggered so a held button fires once.
	if (!g_notifyDismissTimer) {
		g_notifyDismissTimer = new QTimer(g_callbacks);
		QObject::connect(g_notifyDismissTimer, &QTimer::timeout, g_callbacks, []() {
			if (!g_notifyPanel || !g_notifyPanel->isVisible())
				return;
			bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
			// The bell opens this panel from mousePressEvent. Do not reinterpret that same press as an outside click,
			// even when the user holds it longer than one timer interval; wait until it is physically released first.
			if (g_notifyIgnoreOpeningMouseUntilRelease) {
				g_notifyMouseWasDown = down;
				if (!down) g_notifyIgnoreOpeningMouseUntilRelease = false;
				return;
			}
			bool pressed = down && !g_notifyMouseWasDown;
			g_notifyMouseWasDown = down;
			if (!pressed)
				return;
			// the press that opened the panel can still be in flight on the first tick
			if (g_notifyShownAt.isValid() && g_notifyShownAt.elapsed() < 150)
				return;
			POINT pt;
			if (!GetCursorPos(&pt))
				return;
			RECT panelRect;
			if (GetWindowRect((HWND)g_notifyPanel->winId(), &panelRect) && PtInRect(&panelRect, pt))
				return;
			// a click on the bell itself is the buttons own toggle, not a dismiss
			RECT bellRect;
			if (g_notifyPanelAnchor && GetWindowRect((HWND)g_notifyPanelAnchor->winId(), &bellRect) &&
			    PtInRect(&bellRect, pt))
				return;
			HideNotificationPanel();
		});
	}

	// only one at a time, so shut whichever other bell left one open
	for (auto &other : g_notifyButtons)
		if (other && other != anchor && other->m_panel)
			other->m_panel->hide();

	g_notifyPanelAnchor = anchor;
	PositionNotifyPanel();
	g_notifyPanel->show();
	// Showing can cause Qt to apply an initial platform placement; anchor once more afterward to keep the seam exact.
	PositionNotifyPanel();
	g_notifyPanel->raise();
	g_notifyPanel->activateWindow();
	// The trigger must sit above its menu, just like the sort trigger's higher z-index hides the shared border.
	anchor->raise();
	anchor->update();
	anchor->update();
	g_notifyShownAt.start();
	g_notifyMouseWasDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
	g_notifyIgnoreOpeningMouseUntilRelease = true;
	g_notifyDismissTimer->start(60);
	// the page stays loaded between opens, so it has to re-read the list and replay its drop-open itself
	if (g_notifyBrowser)
		g_notifyBrowser->executeJavaScript("window.__replaykitShowNotifications && window.__replaykitShowNotifications();");
}

// clear a leftover WindowMinimized bit before showing so a Clips window that was minimized then hidden comes back at its real size instead of a taskbar stub; keeps WindowMaximized intact.
static void UnminimizeClips()
{
	if (g_clipsWindow)
		g_clipsWindow->setWindowState(g_clipsWindow->windowState() & ~Qt::WindowMinimized);
}

// force a taskbar button on our own top-level windows. they normally get one for free, but RetagTaskbarButton rewrites
// their PKEY_AppUserModel_ID (Windows then rebuilds the button) and a stray owner would suppress it -- WS_EX_APPWINDOW
// makes the button unconditional. set while hidden, before show(), so it is honoured on the first paint.
static void ForceTaskbarButton(QWidget *w)
{
	if (!w)
		return;
	HWND hwnd = (HWND)w->winId(); // realises the native window
	if (!hwnd)
		return;
	LONG_PTR ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
	SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex | WS_EX_APPWINDOW) & ~WS_EX_TOOLWINDOW);
}

void RefreshAppIcon(); // defined below -- re-pushes the app icon to obs + our own windows
static void ApplyAuxWindowIcon(QWidget *w, HICON *owned, int *tagged, const wchar_t *aumid); // defined below

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
	g_clipsWindow = win;

	// restore the remembered position/size before the first show so it doesnt flash at the default spot.
	RestoreWindowGeometry("clipsWindow", win);

	ForceTaskbarButton(win);
	win->show();
	// retag BEFORE taking focus -- RefreshAppIcon rebuilds taskbar buttons across our windows (ITaskbarList DeleteTab/AddTab) and that drops the foreground state, so activating first left clips open but unfocused and pushed settings behind
	RefreshAppIcon(); // give the fresh window the current app icon + taskbar retag
	win->raise();
	win->activateWindow();
	AttachNotifyButton(win);
	AttachTitleKeybindBadge(win);
}

// same shape as CreateClipsWindow -- title matches settings.htmls <title> and the /close-window whitelist, size matches the controls_app.html popup so it looks the same either way its opened
void CreateSettingsWindow()
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open settings");
		return;
	}

	QWidget *win = new CefHostWindow();
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle("ReplayKit Settings");
	win->resize(980, 760);
	// derived from settings.htmls own grid, not guessed: .shell is a fixed 190px sidebar + flexible main column inside 18px of shell padding, .main adds another 10px right padding, and the widest row inside it (.form-row) needs 180px label + 8px gap + a 260px-minimum field (grid-template-columns:180px minmax(260px,1fr)) to avoid wrapping. 190+18+10+180+8+260 = 666px is the point a form row would start fighting for space; 700 gives a small margin. height has no equivalent hard floor (.main scrolls vertically), 500 is just enough to show a few rows without feeling cramped.
	win->setMinimumSize(700, 500);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/settings-view", nullptr);
	layout->addWidget(browser);

	ForceTaskbarButton(win);
	win->show();
	g_settingsWindow = win;
	RefreshAppIcon(); // give the new window the current app icon + taskbar retag
	// focus last, for the same reason as CreateClipsWindow
	win->raise();
	win->activateWindow();
	AttachNotifyButton(win);
	AttachTitleKeybindBadge(win);
	// re-apply a couple times: winId()/the cef child arent fully realised on the first pass, and a slow RKICON over
	// the pipe can land after this. cheap -- the tag guard skips the taskbar rebuild on the repeats.
	QTimer::singleShot(500, g_callbacks, []() { if (g_settingsWindow) ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow"); });
	QTimer::singleShot(1600, g_callbacks, []() { if (g_settingsWindow) ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow"); });
}

// our browser dock replaces obs's native "Controls" dock (Start Streaming / Recording / ...). place ours in the
// native dock's slot, hide the native one, and force ours to the plain title "Controls". runs on a slow forever-timer
// because obs re-titles the dock after the browser loads and re-shows the native dock from its stored state. NEVER
// close() an obs dock -- OBSDock::closeEvent pops the "Closing Dockable Window" box; toggleViewAction / setVisible do
// not. the persistent config rename is PatchBrowserDockTitleInUserIni().
void ApplyControlsDockTweaks()
{
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	auto *main = qobject_cast<QMainWindow *>(mw);
	QDockWidget *nativeDock = mw->findChild<QDockWidget *>("controlsDock");

	// obs names an extra-browser dock "<title>_extraBrowser" (confirmed in the obs log), not by uuid.
	QDockWidget *ourDock = nullptr;
	for (QDockWidget *dock : mw->findChildren<QDockWidget *>()) {
		QString id = dock->objectName();
		id.remove('-');
		if (id.toLower() == QLatin1String("a59ce0ef5d6f4a4f91d9c7c3c1d4e2b0") ||
		    dock->objectName().endsWith(QLatin1String("_extraBrowser")) ||
		    dock->windowTitle() == QLatin1String("Custom Controls") ||
		    dock->windowTitle() == QLatin1String("Controls")) {
			ourDock = dock;
			break;
		}
	}

	// safety net: the bundled DockState already puts our dock in the bottom row (assets/obs-studio/user.ini), but if
	// it landed somewhere else (old install, obs floated it) drop it into the bottom row next to the mixer, once per
	// session. anchored to mixerDock, not the native Controls dock -- that one is hidden below and unreliable.
	static bool placed = false;
	if (main && ourDock && !placed) {
		if (ourDock->isFloating() || main->dockWidgetArea(ourDock) != Qt::BottomDockWidgetArea) {
			ourDock->setFloating(false);
			QDockWidget *anchor = mw->findChild<QDockWidget *>("mixerDock");
			if (!anchor || !anchor->isVisible())
				anchor = mw->findChild<QDockWidget *>("sourcesDock");
			if (anchor && anchor->isVisible())
				main->splitDockWidget(anchor, ourDock, Qt::Horizontal);
			else
				main->addDockWidget(Qt::BottomDockWidgetArea, ourDock);
			ourDock->show();
			ourDock->raise();
			int w = (nativeDock && nativeDock->width() > 60) ? nativeDock->width() : 300;
			main->resizeDocks({ ourDock }, { w }, Qt::Horizontal);
		}
		placed = true;
	}

	// hide the native Controls dock (the bundled DockState also hides it; this covers old installs + any re-show)
	if (nativeDock && nativeDock->isVisible()) {
		if (QAction *a = nativeDock->toggleViewAction(); a && a->isChecked())
			a->setChecked(false); // hides it + unchecks the Docks-menu entry, no close popup
		else
			nativeDock->setVisible(false);
	}

	// force the plain title
	if (ourDock && ourDock->windowTitle() != QLatin1String("Controls")) {
		ourDock->setWindowTitle(QStringLiteral("Controls"));
		if (QWidget *tb = ourDock->titleBarWidget()) {
			for (QLabel *lbl : tb->findChildren<QLabel *>())
				if (lbl->text().contains(QLatin1String("Custom Controls")))
					lbl->setText(QStringLiteral("Controls"));
		}
	}
}

// the visible title bar is handled live by ApplyControlsDockTweaks; this makes the rename stick in obs's own config
// (the "Custom Browser Docks" dialog + the dock name obs creates it with next launch). called from the EXIT event,
// which fires AFTER OBSBasic::closeEvent has already written user.ini, so this edit lands last and survives.
void PatchBrowserDockTitleInUserIni()
{
	QString path = qEnvironmentVariable("APPDATA") + "/obs-studio/user.ini";
	QFile f(path);
	if (!f.open(QIODevice::ReadOnly | QIODevice::Text))
		return;
	QString text = QString::fromUtf8(f.readAll());
	f.close();
	// only the ExtraBrowserDocks line, only when it is our uuid's entry, only the exact stale title token.
	QStringList lines = text.split('\n');
	bool changed = false;
	for (QString &line : lines) {
		if (!line.startsWith(QLatin1String("ExtraBrowserDocks=")))
			continue;
		if (!line.contains(QLatin1String("a59ce0ef5d6f4a4f91d9c7c3c1d4e2b0")))
			break;
		QString before = line;
		line.replace(QLatin1String("\"title\": \"Custom Controls\""), QLatin1String("\"title\": \"Controls\""));
		line.replace(QLatin1String("\"title\":\"Custom Controls\""), QLatin1String("\"title\":\"Controls\""));
		changed = (line != before);
		break;
	}
	if (!changed)
		return;
	QFile w(path);
	if (w.open(QIODevice::WriteOnly | QIODevice::Text)) {
		w.write(lines.join('\n').toUtf8());
		w.close();
		blog(LOG_INFO, "[replaykit-tray] renamed the browser dock to 'Controls' in user.ini");
	}
}

QTimer *g_dockTweakTimer = nullptr;

// triggers OBS's own Settings dialog for the merged controls dock. the action is named "action_Settings" in
// window-basic-main.ui (locale-independent); fall back to the auto-connected private slot if the name ever changes.
void OpenObsSettings()
{
	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	if (QAction *act = mw->findChild<QAction *>("action_Settings")) {
		act->trigger();
		return;
	}
	QMetaObject::invokeMethod(mw, "on_action_Settings_triggered", Qt::QueuedConnection);
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
	RunAsync([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=ReplayKit%20Settings", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[focused, cefReady]() {
				g_settingsCheckInFlight = false;
				if (!focused && cefReady)
					CreateSettingsWindow();
			},
			Qt::QueuedConnection);
	});
}

// twin of CreateSettingsWindow for the one-time first-run wizard. It gets the same native icon path so CEF's
// favicon cannot leave Setup visually different from Clips and Settings.
void CreateSetupWindow(bool quiet)
{
	if (!g_cef) {
		blog(LOG_WARNING, "[replaykit-tray] obs-browser is unavailable; cannot open the setup wizard");
		return;
	}

	QWidget *win = new CefHostWindow();
	win->setAttribute(Qt::WA_DeleteOnClose, false);
	win->setWindowTitle("ReplayKit Setup");
	win->resize(820, 640);
	win->setMinimumSize(640, 520);

	QVBoxLayout *layout = new QVBoxLayout(win);
	layout->setContentsMargins(0, 0, 0, 0);
	QCefWidget *browser = g_cef->create_widget(win, "http://127.0.0.1:8767/setup-view", nullptr);
	layout->addWidget(browser);

	ForceTaskbarButton(win);
	win->show();
	if (!quiet) {
		win->raise();
		win->activateWindow();
	}
	g_setupWindow = win;
	RefreshAppIcon();
	QTimer::singleShot(500, g_callbacks, []() { if (g_setupWindow) ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow"); });
	QTimer::singleShot(1600, g_callbacks, []() { if (g_setupWindow) ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow"); });
}

// quiet = the helper opened this by itself on a pending first run, so show it without pulling focus off obs as it starts up. a deliberate open (tray row, dock button) still raises and activates.
void ShowSetup(bool quiet)
{
	if (g_setupWindow) {
		g_setupWindow->show();
		if (!quiet) {
			g_setupWindow->raise();
			g_setupWindow->activateWindow();
		}
		return;
	}
	if (g_setupCheckInFlight)
		return;
	g_setupCheckInFlight = true;

	// nothing else ever opens this title, so no /focus-window pre-check like ShowSettings needs.
	RunAsync([quiet]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[cefReady, quiet]() {
				g_setupCheckInFlight = false;
				if (cefReady)
					CreateSetupWindow(quiet);
			},
			Qt::QueuedConnection);
	});
}

void ShowClips()
{
	if (g_menuShownTimer.isValid() && g_menuShownTimer.elapsed() < kMenuClickDebounceMs)
		return;
	if (g_clipsWindow) {
		if (!g_clipsWindow->isVisible() && g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		UnminimizeClips();
		g_clipsWindow->show();
		g_clipsWindow->raise();
		g_clipsWindow->activateWindow();
		return;
	}
	if (g_clipsCheckInFlight)
		return;
	g_clipsCheckInFlight = true;

	// checks /focus-window first since the docks own button or a leftover window may already have one open, then runs both blocking http calls off the ui thread and posts back thru queued invokeMethod since qwidget/qcefwidget arent thread-safe
	RunAsync([]() {
		bool focused = HttpFocusWindowSucceeded("/focus-window?title=Clips", 8767, 800);
		bool cefReady = focused || EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
			[focused, cefReady]() {
				g_clipsCheckInFlight = false;
				if (!focused && cefReady)
					CreateClipsWindow();
			},
			Qt::QueuedConnection);
	});
}

bool IsReplayKitFault(DWORD code)
{
	return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_ARRAY_BOUNDS_EXCEEDED ||
		code == EXCEPTION_ILLEGAL_INSTRUCTION || code == EXCEPTION_IN_PAGE_ERROR ||
		code == EXCEPTION_NONCONTINUABLE_EXCEPTION || code == EXCEPTION_STACK_OVERFLOW;
}

LONG CALLBACK RecordReplayKitException(EXCEPTION_POINTERS *exception)
{
	if (!exception || !exception->ExceptionRecord || !g_replayKitModule || !IsReplayKitFault(exception->ExceptionRecord->ExceptionCode))
		return EXCEPTION_CONTINUE_SEARCH;
	MEMORY_BASIC_INFORMATION memory = {};
	if (!VirtualQuery(exception->ExceptionRecord->ExceptionAddress, &memory, sizeof(memory)) || memory.AllocationBase != g_replayKitModule)
		return EXCEPTION_CONTINUE_SEARCH;
	if (InterlockedCompareExchange(&g_nativeCrashWriting, 1, 0) != 0)
		return EXCEPTION_CONTINUE_SEARCH;
	if (g_nativeCrashLog != INVALID_HANDLE_VALUE) {
		char line[256];
		int length = snprintf(line, sizeof(line),
			"{\"kind\":\"first_chance_replaykit_fault\",\"code\":\"0x%08lX\",\"address\":\"%p\",\"threadId\":%lu}\r\n",
			(unsigned long)exception->ExceptionRecord->ExceptionCode, exception->ExceptionRecord->ExceptionAddress,
			(unsigned long)GetCurrentThreadId());
		if (length > 0) {
			DWORD written = 0;
			WriteFile(g_nativeCrashLog, line, (DWORD)length, &written, nullptr);
		}
	}
	InterlockedExchange(&g_nativeCrashWriting, 0);
	return EXCEPTION_CONTINUE_SEARCH;
}

void StartReplayKitCrashReporter()
{
	wchar_t appData[MAX_PATH] = {};
	if (!GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH))
		return;
	std::wstring obsDirectory = std::wstring(appData) + L"\\obs-studio";
	std::wstring crashesDirectory = obsDirectory + L"\\crashes";
	std::wstring directory = crashesDirectory + L"\\replaykit";
	CreateDirectoryW(obsDirectory.c_str(), nullptr);
	CreateDirectoryW(crashesDirectory.c_str(), nullptr);
	if (!CreateDirectoryW(directory.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
		return;
	std::wstring path = directory + L"\\replaykit-native.jsonl";
	g_nativeCrashLog = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
		nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
	GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&StartReplayKitCrashReporter, &g_replayKitModule);
	g_nativeCrashHandler = AddVectoredExceptionHandler(1, RecordReplayKitException);
	if (g_nativeCrashLog == INVALID_HANDLE_VALUE || !g_nativeCrashHandler)
		blog(LOG_WARNING, "[replaykit] crash reporter could not start");
	else
		blog(LOG_INFO, "[replaykit] crash reporter active: %ls", path.c_str());
}

void StopReplayKitCrashReporter()
{
	if (g_nativeCrashHandler) {
		RemoveVectoredExceptionHandler(g_nativeCrashHandler);
		g_nativeCrashHandler = nullptr;
	}
	if (g_nativeCrashLog != INVALID_HANDLE_VALUE) {
		CloseHandle(g_nativeCrashLog);
		g_nativeCrashLog = INVALID_HANDLE_VALUE;
	}
}

void ToggleClips()
{
	if (g_clipsWindow) {
		if (g_clipsWindow->isVisible()) {
			SaveClipsWindowGeometry();
			g_clipsWindow->hide();
			return;
		}
		if (g_clipsBrowser)
			g_clipsBrowser->executeJavaScript("window.__replaykitResetClips && window.__replaykitResetClips();");
		UnminimizeClips();
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
	RunAsync([]() {
		std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 3000);
		QMetaObject::invokeMethod(g_callbacks, [settingsBody]() {
			g_openClipsHotkeyRequestInFlight = false;
			g_closeToTray = JsonBoolField(settingsBody, "closeToTray", true);
			RegisterOpenClipsHotkey(settingsBody);
			ApplyNotifyThemeColors(settingsBody);
			RefreshTitleKeybindBadges(settingsBody);
		}, Qt::QueuedConnection);
	});
}

// a themed confirm the dock cannot draw itself. it must dim EVERYTHING obs renders -- the preview and the cef docks
// are native child hwnds, so a plain child widget painted over them just shows black. this is a top-level frameless
// translucent window sized over obs's client area; dwm composites the dim over the native surfaces. the card child
// is opaque so its text still gets cleartype. modal via QDialog::exec(). gui thread only.
class RKConfirmDialog : public QDialog {
public:
	RKConfirmDialog(QWidget *host, const QString &title, const QString &body, const QString &okText, bool danger)
		: QDialog(host), m_host(host)
	{
		setWindowFlags(Qt::Dialog | Qt::FramelessWindowHint);
		setAttribute(Qt::WA_TranslucentBackground, true);
		setAttribute(Qt::WA_DeleteOnClose, false);
		setModal(true);
		{
			QFont f = font();
			f.setFamily("Segoe UI");
			f.setPixelSize(13);
			setFont(f);
		}
		if (host)
			host->installEventFilter(this);
		syncToHost();

		const QColor panel = g_menuPanel, brd = g_notifyBorder, ink = g_notifyTipFg, muted = g_menuMuted;
		const QColor btnBg = g_menuField, btnBgHover = g_menuFieldHover, btnBorderHover = g_menuBorderStrong;
		const QColor okBg = danger ? g_menuDanger : g_menuAccent;
		const QColor okBg2 = danger ? g_menuDanger.lighter(115) : g_menuAccentLight;
		const QColor okInk = danger ? QColor(0xFF, 0xFF, 0xFF) : g_menuOnAccent;

		// one .arg() per marker so %10/%11 can never be misread as %1 + "0"
		QString qss = QString(
			"#rkCard { background:%1; border:1px solid %2; border-radius:6px; }"
			"#rkTitle { color:%3; font-size:15px; font-weight:700; }"
			"#rkBody { color:%4; font-size:13px; }"
			"#rkCard QPushButton { min-width:84px; min-height:30px; padding:0 16px; border-radius:4px;"
			" background:%5; border:1px solid %5; color:%3; }"
			"#rkCard QPushButton:hover { background:%6; border-color:%7; }"
			"#rkCard QPushButton:pressed { background:%8; }"
			"#rkCard QPushButton:focus { border-color:%7; }"
			"#rkOk { background:%9; border-color:%9; color:%10; }"
			"#rkOk:hover { background:%11; border-color:%11; }"
			"#rkOk:pressed { background:%9; }");
		qss = qss.arg(panel.name()).arg(brd.name()).arg(ink.name()).arg(muted.name())
			  .arg(btnBg.name()).arg(btnBgHover.name()).arg(btnBorderHover.name())
			  .arg(btnBg.darker(112).name()).arg(okBg.name()).arg(okInk.name()).arg(okBg2.name());
		setStyleSheet(qss);

		auto *root = new QVBoxLayout(this);
		root->setContentsMargins(0, 0, 0, 0);
		root->addStretch();
		auto *mid = new QHBoxLayout();
		mid->addStretch();

		auto *card = new QWidget(this);
		card->setObjectName("rkCard");
		card->setAttribute(Qt::WA_StyledBackground, true);
		card->setMinimumWidth(330);
		card->setMaximumWidth(400);
		auto *cv = new QVBoxLayout(card);
		cv->setContentsMargins(20, 18, 20, 16);
		cv->setSpacing(12);

		auto *t = new QLabel(title, card);
		t->setObjectName("rkTitle");
		t->setWordWrap(true);
		auto *b = new QLabel(body, card);
		b->setObjectName("rkBody");
		b->setWordWrap(true);
		cv->addWidget(t);
		cv->addWidget(b);

		auto *btnRow = new QHBoxLayout();
		btnRow->setSpacing(8);
		btnRow->addStretch();
		auto *cancel = new QPushButton(QObject::tr("Cancel"), card);
		auto *ok = new QPushButton(okText, card);
		ok->setObjectName("rkOk");
		ok->setCursor(Qt::PointingHandCursor);
		cancel->setCursor(Qt::PointingHandCursor);
		ok->setDefault(true);
		btnRow->addWidget(cancel);
		btnRow->addWidget(ok);
		cv->addLayout(btnRow);

		mid->addWidget(card);
		mid->addStretch();
		root->addLayout(mid);
		root->addStretch();

		connect(ok, &QPushButton::clicked, this, &QDialog::accept);
		connect(cancel, &QPushButton::clicked, this, &QDialog::reject);
	}

protected:
	void paintEvent(QPaintEvent *) override
	{
		QPainter p(this);
		p.fillRect(rect(), QColor(0, 0, 0, 140));
	}
	void keyPressEvent(QKeyEvent *e) override
	{
		if (e->key() == Qt::Key_Return || e->key() == Qt::Key_Enter)
			accept();
		else
			QDialog::keyPressEvent(e); // QDialog maps Esc -> reject
	}
	void mousePressEvent(QMouseEvent *e) override
	{
		if (!childAt(e->pos()))
			reject();
	}
	bool eventFilter(QObject *o, QEvent *e) override
	{
		if (o == m_host) {
			if (e->type() == QEvent::Move || e->type() == QEvent::Resize)
				syncToHost();
			else if (e->type() == QEvent::WindowStateChange && m_host && m_host->isMinimized())
				reject();
			else if (e->type() == QEvent::Hide)
				reject();
		}
		return QDialog::eventFilter(o, e);
	}

private:
	void syncToHost()
	{
		if (!m_host)
			return;
		setGeometry(QRect(m_host->mapToGlobal(QPoint(0, 0)), m_host->size()));
	}

	QWidget *m_host = nullptr;
};

static bool g_confirmOpen = false;

// only one themed RKConfirmDialog at a time -- a repeat trigger while one is up is ignored. (the plain QMessageBox
// lifecycle confirms use a different rule: replace the one on screen, see ShowLifecycleConfirm.)
struct ConfirmGuard {
	bool ok;
	ConfirmGuard() : ok(!g_confirmOpen) { if (ok) g_confirmOpen = true; }
	~ConfirmGuard() { if (ok) g_confirmOpen = false; }
};

// draws RKConfirmDialog over obs and blocks until the user answers. gui thread only. a second call while one is
// already up is ignored (returns false) instead of stacking another dialog.
bool ShowReplayKitConfirm(const QString &title, const QString &body, const QString &okText, bool danger)
{
	ConfirmGuard guard;
	if (!guard.ok)
		return false;

	QWidget *mw = g_mainWindow ? g_mainWindow.data() : (QWidget *)obs_frontend_get_main_window();
	if (!mw) {
		return QMessageBox::question(nullptr, title, body, QMessageBox::Yes | QMessageBox::No,
					    QMessageBox::No) == QMessageBox::Yes;
	}
	// obs may be minimized to the tray -- surface the window so the sheet is actually seen before it acts.
	if (mw->isMinimized())
		mw->setWindowState(mw->windowState() & ~Qt::WindowMinimized);
	if (!mw->isVisible())
		mw->showNormal();
	mw->raise();
	mw->activateWindow();

	RKConfirmDialog dlg(mw, title, body, okText, danger);
	dlg.show();
	dlg.raise();
	dlg.activateWindow();
	return dlg.exec() == QDialog::Accepted;
}

// one confirm request from the dock (via the ipc CONFIRM verb) or the tray. runs the matching action on ok.
// ShowReplayKitConfirm owns the no-stacking guard, so a repeat while one is open just returns false here.
void HandleConfirmRequest(const std::string &kind)
{
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
	RunAsync([route]() { HttpRequest("POST", route.c_str(), 8767, nullptr, 8000); });
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

// generic obs-lifecycle confirm (Exit / Restart). plain QMessageBox on purpose -- it should read like obs's own
// dialogs, not the in-obs themed sheet. non-modal + single-instance: pressing the tray item again drops the box
// that is showing and pops a fresh one instead of stacking or being ignored.
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

// invisible throwaway browser to eat the first-ever cef surfaces one-time cost -- the real clips window paints only a corner and stays white the first time each obs session, and this burns that bad first attempt off-screen instead of chasing the exact cef-internal cause
bool g_cefPrewarmed = false;

void PrewarmCefBrowser()
{
	if (g_cefPrewarmed)
		return;
	g_cefPrewarmed = true;

	RunAsync([]() {
		bool cefReady = EnsureCefReadyBlocking();
		QMetaObject::invokeMethod(
			g_callbacks,
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
	});
}

// synchronously deletes our cef widgets before obs_module_unload, mirroring how OBSBasic.cpp deletes extraBrowsers early, to dodge upstream race obsproject/obs-browser#353 where cef can still be mid-teardown when the browser-manager thread is joined -- direct delete not deleteLater becuase a deferred one wouldnt run before applicationShutdown anyway
void CloseCefWidgetsBeforeShutdown()
{
	if (g_notifyDismissTimer)
		g_notifyDismissTimer->stop();
	g_notifyPanelAnchor = nullptr;
	g_notifyPanel = nullptr;
	g_notifyBrowser = nullptr;
	for (auto &button : g_notifyButtons) {
		if (button && button->m_panel)
			delete button->m_panel.data();
	}
	if (g_setupWindow)
		delete g_setupWindow.data();
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
		// object name, not a type selector -- this class lives in an anon namespace with no Q_OBJECT, so qss sees it as plain "QWidget"; the id keeps the rules off the child labels
		setObjectName("rkTrayActionRow");
		// Minimum (not the QWidget default) means qt treats our sizeHint as a floor it can grow past but never shrink below -- structural insurance against the reported clipping/overlap, instead of only reacting to it after the fact once the chip already got squeezed.
		setSizePolicy(QSizePolicy::Minimum, QSizePolicy::Fixed);

		nameLabel = new QLabel(this);
		nameLabel->setObjectName("rkTrayRowName");
		chipLabel = new QLabel(this);
		ApplyThemeColors(QString(), QString(), QString(), QString());
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

	void SetAction(QWidgetAction *a) { m_action = a; }
	QWidgetAction *action() const { return m_action; }

	// store the resolved theme colours and paint the rest state -- everything is a DIRECT stylesheet on the row / the label (no descendant selectors, no dynamic properties, no QMenu::hovered, which reports the wrong action for a QWidgetAction row so the highlight sticks on the plain item next to it); the row's own HoverEnter/HoverLeave, from WA_Hover, the same events that power :hover, drive SetHovered()
	void ApplyThemeColors(const QString &accent, const QString &accentLight, const QString &onAccent, const QString &text)
	{
		Q_UNUSED(accent);
		m_fill = accentLight.isEmpty() ? QStringLiteral("palette(highlight)") : accentLight;
		m_inkActive = onAccent.isEmpty() ? QStringLiteral("palette(highlighted-text)") : onAccent;
		// hardcoded white, not palette(window-text): obs themes via qss not qpalette, so the palette role is often qt-default black -- and this is only the value for the sub-100ms before the first /settings fetch lands anyway
		m_inkRest = text.isEmpty() ? QStringLiteral("#FFFFFF") : text;
		SetHovered(m_hovered);
	}

	void SetHovered(bool on)
	{
		m_hovered = on;
		setStyleSheet(on ? QStringLiteral("#rkTrayActionRow { border-radius: 4px; background-color: %1; border: 1px solid rgba(255, 255, 255, 70); }").arg(m_fill)
				 : QStringLiteral("#rkTrayActionRow { border-radius: 4px; border: 1px solid transparent; }"));
		nameLabel->setStyleSheet(QStringLiteral("color: %1;").arg(on ? m_inkActive : m_inkRest));
	}

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

	// HoverEnter/HoverLeave come from WA_Hover and pair up reliably (QEvent::Enter/Leave did not -- Leave never arrived, so an earlier version stuck dark); on enter we also make this the menu's active action so the plain item the mouse came from drops its native selection highlight instead of staying stuck lit
	bool event(QEvent *e) override
	{
		if (e->type() == QEvent::HoverEnter) {
			SetHovered(true);
			if (m_menu && m_action)
				m_menu->setActiveAction(m_action);
		} else if (e->type() == QEvent::HoverLeave) {
			SetHovered(false);
		}
		return QWidget::event(e);
	}

private:
	TrayRowKind m_kind;
	QMenu *m_menu;
	QElapsedTimer m_lastTrigger;
	QWidgetAction *m_action = nullptr;
	bool m_hovered = false;
	QString m_fill;
	QString m_inkRest;
	QString m_inkActive;
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
		RunAsync([]() {
			std::string settingsBody = HttpRequest("GET", "/settings", 8767, nullptr, 500);
			std::string clipsLabel = KeybindLabelFromSettingsJson(settingsBody, "openClipsKeybind");
			std::string clipLabel = KeybindLabelFromSettingsJson(settingsBody, "clipKeybind");
			std::string recordingLabel = KeybindLabelFromSettingsJson(settingsBody, "recordingKeybind");
			std::string menuColors = ExtractJsonObjectField(settingsBody, "menuColors");
			std::string text = ExtractJsonStringField(menuColors, "text");
			std::string accent = ExtractJsonStringField(menuColors, "accent");
			std::string accentLight = ExtractJsonStringField(menuColors, "accentLight");
			std::string onAccent = ExtractJsonStringField(menuColors, "onAccent");
			QMetaObject::invokeMethod(
				g_callbacks,
				[clipsLabel, clipLabel, recordingLabel, text, accent, accentLight, onAccent]() {
					const QString t = QString::fromStdString(text);
					const QString a = QString::fromStdString(accent);
					const QString al = QString::fromStdString(accentLight);
					const QString oa = QString::fromStdString(onAccent);
					if (g_clipsRow) {
						g_clipsRow->ApplyThemeColors(a, al, oa, t);
						g_clipsRow->SetKeybindLabel(clipsLabel);
					}
					if (g_replayBufferRow) {
						g_replayBufferRow->ApplyThemeColors(a, al, oa, t);
						g_replayBufferRow->SetKeybindLabel(clipLabel);
					}
					if (g_recordRow) {
						g_recordRow->ApplyThemeColors(a, al, oa, t);
						g_recordRow->SetKeybindLabel(recordingLabel);
					}
				},
				Qt::QueuedConnection);
		});
	}
	if (!g_sharePreviewAction)
		return;
	RunAsync([]() {
		std::string getBody = HttpRequest("GET", "/share-preview", 8767, nullptr, 500);
		bool available = JsonBoolField(getBody, "available", false);
		bool enabled = JsonBoolField(getBody, "enabled", false);
		QMetaObject::invokeMethod(
			g_callbacks,
			[available, enabled]() {
				if (!g_sharePreviewAction)
					return;
				g_sharePreviewAvailable = available;
				// stays enabled when unavailable so the click can offer to set Share Preview up
				g_sharePreviewAction->setEnabled(true);
				g_sharePreviewAction->setChecked(available && enabled);
			},
			Qt::QueuedConnection);
	});
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

// obs_frontend_get_main_window() is the only language-neutral way to identify obss real main window -- its title is a localized template ("<version> - profile: ... - scenes: ...") with no fixed substring a non-english build still renders, which is what broke the helpers old title-matching close. captured once, not on the projector timer, since the handle never changes for the life of the process; the pipe thread sends it to the helper on each (re)connect.
void PublishMainWindow()
{
	QWidget *mainWindow = (QWidget *)obs_frontend_get_main_window();
	if (!mainWindow)
		return;
	g_mainWinValue.store((quintptr)mainWindow->winId());
}

// -- live app-icon swap (helper Appearance tab) -- SETICON <path> over the ipc pipe. covers obss title bar,
// taskbar button and system-tray icon; "-" restores what obs shipped with (captured on first use). SETICONDOT
// toggles a red recording dot overlaid while a recording / replay buffer is running.
//
// win11 taskbar note: qt6 setWindowIcon (WM_SETICON at 16/32px) updates the title bar + alt-tab but NOT the
// win11 taskbar button -- win11 only picks up an icon change when the WM_SETICON payload is large, and it also
// reads the window CLASS icon. so we push one 256px HICON to every WM_SETICON slot AND the class, and re-assert
// on Show/WindowStateChange (restoring from the tray rebuilds the taskbar button).
static bool g_appIconDefaultsCaptured = false;
static QIcon g_appIconDefaultMain;
static QIcon g_appIconDefaultTray;
static HICON g_classOrigBig = nullptr;   // obs's own class icons, captured once
static HICON g_classOrigSmall = nullptr;
static HICON g_ownedIcon = nullptr;      // the single 256px HICON we push to every slot + the class; freed on the next change
static QString g_appIconPath;            // "" / "-" == default; last value the helper sent
static QString g_rkIconPath;             // RKICON <path> -- the replaykit-branded .ico for our own windows when appIcon is default
static bool g_recordingDotEnabled = true; // helper Appearance toggle (SETICONDOT)
static int g_taskbarTaggedCustom = -1;   // -1 unknown / 0 default / 1 custom -- last state we retagged the obs taskbar button for
// g_ownedIconClips / g_ownedIconSettings / g_taggedClips / g_taggedSettings are declared near the top of the file

// QIcon -> HICON at a given px size (works for .ico and .png). caller owns the result (DestroyIcon).
static HICON HIconFromQIcon(const QIcon &icon, int size)
{
	if (icon.isNull() || size <= 0)
		return nullptr;
	QImage img = icon.pixmap(size, size).toImage().convertToFormat(QImage::Format_ARGB32_Premultiplied);
	if (img.isNull())
		return nullptr;

	BITMAPV5HEADER bi = {};
	bi.bV5Size = sizeof(BITMAPV5HEADER);
	bi.bV5Width = img.width();
	bi.bV5Height = -img.height(); // top-down
	bi.bV5Planes = 1;
	bi.bV5BitCount = 32;
	bi.bV5Compression = BI_BITFIELDS;
	bi.bV5RedMask = 0x00FF0000;
	bi.bV5GreenMask = 0x0000FF00;
	bi.bV5BlueMask = 0x000000FF;
	bi.bV5AlphaMask = 0xFF000000;

	HDC hdc = GetDC(nullptr);
	void *bits = nullptr;
	HBITMAP color = CreateDIBSection(hdc, (BITMAPINFO *)&bi, DIB_RGB_COLORS, &bits, nullptr, 0);
	ReleaseDC(nullptr, hdc);
	if (!color)
		return nullptr;
	for (int y = 0; y < img.height(); ++y)
		memcpy((quint8 *)bits + (size_t)y * img.width() * 4, img.constScanLine(y), (size_t)img.width() * 4);

	// all-zero AND mask -- the 32bpp alpha channel carries transparency; 1bpp rows are WORD-aligned.
	std::vector<quint8> maskBits((size_t)(((img.width() + 15) / 16) * 2) * img.height(), 0);
	HBITMAP mask = CreateBitmap(img.width(), img.height(), 1, 1, maskBits.data());
	ICONINFO ii = {};
	ii.fIcon = TRUE;
	ii.hbmColor = color;
	ii.hbmMask = mask;
	HICON hIcon = CreateIconIndirect(&ii);
	DeleteObject(color);
	DeleteObject(mask);
	return hIcon;
}

// overlays the recording indicator -- geometry matched to obs's own tray_active.png: a pure-red filled circle in
// the bottom-left, 37.5% of the icon, ~5% inset from the left and bottom edges, no ring.
static QIcon ComposeRecordingDot(const QIcon &base)
{
	if (base.isNull())
		return base;
	QIcon out;
	for (int sz : {16, 20, 24, 32, 40, 48, 64, 128, 256}) {
		QPixmap pm = base.pixmap(sz, sz);
		if (pm.isNull())
			continue;
		qreal s = pm.width();
		qreal d = s * 0.375;
		QRectF dot(s * 0.047, s - s * 0.051 - d, d, d);
		QPainter p(&pm);
		p.setRenderHint(QPainter::Antialiasing, true);
		p.setPen(Qt::NoPen);
		p.setBrush(QColor(255, 0, 0));
		p.drawEllipse(dot);
		p.end();
		out.addPixmap(pm);
	}
	return out.isNull() ? base : out;
}

// Win11 resolves a running window's taskbar-button icon through obs64.exe / its start-menu shortcut and can ignore
// WM_SETICON + the class icon. An explicit AppUserModelID makes it consult the icon we set. We only update that
// property; removing and re-adding the taskbar tab made every icon choice visibly flash.
static void RetagTaskbarButton(HWND hwnd, bool force, const wchar_t *aumid)
{
	// PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, pid 5 -- inlined to skip the propsys.lib link.
	static const PROPERTYKEY kAumidKey = {
		{0x9F4C2855, 0x9F79, 0x4B39, {0xA8, 0xD0, 0xE1, 0xD4, 0x2D, 0xE1, 0xD5, 0xF3}}, 5};

	IPropertyStore *store = nullptr;
	if (SUCCEEDED(SHGetPropertyStoreForWindow(hwnd, IID_PPV_ARGS(&store))) && store) {
		PROPVARIANT pv;
		PropVariantInit(&pv); // vt stays VT_EMPTY -> SetValue clears the property
		if (force && aumid) {
			size_t n = (wcslen(aumid) + 1) * sizeof(wchar_t);
			pv.pwszVal = (LPWSTR)CoTaskMemAlloc(n);
			if (pv.pwszVal) {
				memcpy(pv.pwszVal, aumid, n);
				pv.vt = VT_LPWSTR;
			}
		}
		store->SetValue(kAumidKey, pv);
		store->Commit();
		PropVariantClear(&pv);
		store->Release();
	}

}

// our own top-level windows (Clips, ReplayKit Settings) -- keep OBS's native icon by default, or use the selected
// custom icon. Every custom-icon window shares the main OBS taskbar identity so Windows groups them together.
static void ApplyAuxWindowIcon(QWidget *w, HICON *owned, int *tagged, const wchar_t *aumid)
{
	if (!w)
		return;
	bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");
	// Prefer OBS's loaded icon over the bundled fallback; the fallback's old low-colour frames look distorted in a
	// Windows caption at 16px.
	QIcon ic;
	if (custom)
		ic = QIcon(g_appIconPath);
	if (ic.isNull())
		ic = g_appIconDefaultMain;
	if (ic.isNull())
		ic = QGuiApplication::windowIcon(); // obs's own app icon -- always valid once obs is up
	if (ic.isNull() && !g_rkIconPath.isEmpty())
		ic = QIcon(g_rkIconPath);
	if (ic.isNull())
		return;

	w->setWindowIcon(ic);
	HWND hwnd = (HWND)w->winId();

	// Only a user-selected icon needs its own taskbar identity. The OBS default keeps OBS's native title-bar and
	// taskbar resolution path, which avoids scaling a 256px fallback image into the caption slot.
	bool forced = custom;
	if (forced) {
		HICON ni = HIconFromQIcon(ic, 256);
		if (ni) {
			SendMessage(hwnd, WM_SETICON, ICON_BIG, (LPARAM)ni);
			SendMessage(hwnd, WM_SETICON, ICON_SMALL, (LPARAM)ni);
			SendMessage(hwnd, WM_SETICON, ICON_SMALL2, (LPARAM)ni);
			HICON old = *owned;
			*owned = ni;
			if (old && old != ni)
				DestroyIcon(old);
		}
	} else if (*owned) {
		DestroyIcon(*owned);
		*owned = nullptr;
	}

    if (*tagged != (forced ? 1 : 0)) {
        RetagTaskbarButton(hwnd, forced, forced ? L"ReplayKit.OBSCustomIcon" : nullptr);
        *tagged = forced ? 1 : 0;
    }
}

// snapshot obs's own window + class + tray icons before we touch anything. safe to call repeatedly.
static void CaptureAppIconDefaults()
{
	if (g_appIconDefaultsCaptured)
		return;
	QWidget *mw = (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return; // main window not up yet -- try again later
	g_appIconDefaultMain = mw->windowIcon();
	HWND hwnd = (HWND)mw->winId();
	g_classOrigBig = (HICON)GetClassLongPtr(hwnd, GCLP_HICON);
	g_classOrigSmall = (HICON)GetClassLongPtr(hwnd, GCLP_HICONSM);
	if (QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray())
		g_appIconDefaultTray = tray->icon();
	g_appIconDefaultsCaptured = true;
}

// the single place that pushes the current icon (custom-or-default, plus the recording dot when active) to every surface.
void RefreshAppIcon()
{
	CaptureAppIconDefaults();

	bool custom = !(g_appIconPath.isEmpty() || g_appIconPath == "-");

	// nothing was ever swapped and nothing is requested -- leave obs's own icon untouched. (a branded rk icon for
	// our own windows still counts as something to do.)
	if (!custom && !g_appIconDefaultsCaptured && g_rkIconPath.isEmpty())
		return;

	QWidget *mw = (QWidget *)obs_frontend_get_main_window();
	QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
	bool recording = obs_frontend_recording_active() || obs_frontend_replay_buffer_active();

	QIcon baseMain = custom ? QIcon(g_appIconPath) : g_appIconDefaultMain;
	QIcon baseTray = custom ? QIcon(g_appIconPath) : g_appIconDefaultTray;
	if (custom && baseMain.isNull()) {
		blog(LOG_WARNING, "[replaykit] SETICON: could not load '%s'", g_appIconPath.toUtf8().constData());
		return;
	}

	// the dot only rides a user-chosen icon -- on "default" obs manages its own recording indicator.
	bool dot = recording && custom && g_recordingDotEnabled;
	QIcon effMain = dot ? ComposeRecordingDot(baseMain) : baseMain;
	QIcon effTray = dot ? ComposeRecordingDot(baseTray) : baseTray;

	if (mw)
		mw->setWindowIcon(effMain);
	if (tray)
		tray->setIcon(effTray);
	// Windows can use Qt's process-wide icon for an existing taskbar button. Always restore it too; otherwise a
	// prior preset remains cached there after the user returns to Default.
	if (!effMain.isNull())
		QGuiApplication::setWindowIcon(effMain);

	// win11 taskbar: needs a large WM_SETICON payload + the window class icon. push one 256px HICON to every slot,
	// or restore obs's own class icons (qt's setWindowIcon above already put the default back on the WM_SETICON slots).
	if (mw) {
		HWND hwnd = (HWND)mw->winId();
		if (custom) {
			HICON ni = HIconFromQIcon(effMain, 256);
			if (ni) {
				SetClassLongPtr(hwnd, GCLP_HICON, (LONG_PTR)ni);
				SetClassLongPtr(hwnd, GCLP_HICONSM, (LONG_PTR)ni);
				SendMessage(hwnd, WM_SETICON, ICON_BIG, (LPARAM)ni);
				SendMessage(hwnd, WM_SETICON, ICON_SMALL, (LPARAM)ni);
				SendMessage(hwnd, WM_SETICON, ICON_SMALL2, (LPARAM)ni);
				HICON old = g_ownedIcon;
				g_ownedIcon = ni;
				if (old && old != ni)
					DestroyIcon(old);
			}
		} else {
			SetClassLongPtr(hwnd, GCLP_HICON, (LONG_PTR)g_classOrigBig);
			SetClassLongPtr(hwnd, GCLP_HICONSM, (LONG_PTR)g_classOrigSmall);
			if (g_ownedIcon) {
				DestroyIcon(g_ownedIcon);
				g_ownedIcon = nullptr;
			}
		}

		// Only update the per-window identity when its mode changes. RetagTaskbarButton deliberately avoids a
		// DeleteTab/AddTab cycle, so this does not flash the button.
		if (g_taskbarTaggedCustom != (custom ? 1 : 0)) {
			RetagTaskbarButton(hwnd, custom, L"ReplayKit.OBSCustomIcon");
			g_taskbarTaggedCustom = custom ? 1 : 0;
		}
	}

	// Our own windows follow OBS's native default or the selected custom icon. Custom-icon windows share the main
	// OBS custom AUMID so they form one taskbar group instead of separate buttons.
	ApplyAuxWindowIcon(g_clipsWindow, &g_ownedIconClips, &g_taggedClips, L"ReplayKit.ClipsWindow");
	ApplyAuxWindowIcon(g_settingsWindow, &g_ownedIconSettings, &g_taggedSettings, L"ReplayKit.SettingsWindow");
	ApplyAuxWindowIcon(g_setupWindow, &g_ownedIconSetup, &g_taggedSetup, L"ReplayKit.SetupWindow");

	blog(LOG_INFO, "[replaykit] icon refresh: custom=%d recording=%d", custom ? 1 : 0, recording ? 1 : 0);
}

void ApplyAppIcon(const QString &path)
{
	g_appIconPath = path;
	CaptureAppIconDefaults();
	RefreshAppIcon();
}

// turns the OBS main window's close (X) into hide-to-tray while g_closeToTray is on. narrowly scoped to the
// one QWidget (an app-wide filter caused a confirmed 2026-08-10 crash catching cef events mid-teardown). an
// ALLOWCLOSE over the ipc pipe from the helpers restart/exit routes, right before they post WM_CLOSE, opens a
// 60s window where the next close passes through so real quits + replaykit restarts still save geometry.
class MainWindowCloseFilter : public QObject {
public:
	explicit MainWindowCloseFilter(QWidget *mw) : QObject(mw), m_mw(mw) {}

protected:
	bool eventFilter(QObject *watched, QEvent *event) override
	{
		if (watched != m_mw)
			return false;

		const QEvent::Type type = event->type();
		if (type == QEvent::Move || type == QEvent::Resize) {
			ScheduleObsMainGeometrySave();
			return false; // observe only, never consume
		}
		if (type == QEvent::Show || type == QEvent::WindowStateChange) {
			// restoring from the tray rebuilds the taskbar button -- put our icon back on it
			RefreshAppIcon();
			return false;
		}
		if (type != QEvent::Close || !g_closeToTray)
			return false;

		if (GetTickCount64() < g_allowCloseUntilMs.load())
			return false; // a real restart/exit is in progress -- let obs close and save geometry

		QSystemTrayIcon *tray = (QSystemTrayIcon *)obs_frontend_get_system_tray();
		if (!tray || !tray->isVisible())
			return false; // nothing to minimize into -- fall back to a normal close

		// EXIT wont fire on a hide-to-tray, so capture position now.
		SaveWindowGeometry("obsMainWindow", m_mw);
		event->ignore();
		if (m_mw)
			m_mw->hide();
		return true;
	}

private:
	QWidget *m_mw;
};

void InstallMainWindowCloseFilter()
{
	if (g_mainWindowCloseFilter)
		return;
	QWidget *mw = (QWidget *)obs_frontend_get_main_window();
	if (!mw)
		return;
	// drop any ALLOWCLOSE window carried over from a previous session's restart so it can never leak into this one.
	g_allowCloseUntilMs.store(0);
	g_mainWindow = mw;
	auto *filter = new MainWindowCloseFilter(mw);
	mw->installEventFilter(filter);
	g_mainWindowCloseFilter = filter;

	// grab obs's own icons now, while the main window is up and untouched, so a restore/aux-window path never
	// finds them null.
	CaptureAppIconDefaults();

	// put the window back where it was last session. once here (obs has finished its own layout) and again shortly
	// after, since qts platform code can still be repositioning the main window right after FINISHED_LOADING.
	RestoreWindowGeometry("obsMainWindow", mw);
	QTimer::singleShot(400, g_callbacks, []() {
		RestoreWindowGeometry("obsMainWindow", g_mainWindow);
		// capture a baseline even if the user never moves the window this session.
		ScheduleObsMainGeometrySave();
	});

	// slow safety-net save so a forced close cannot lose the last position -- see PollWindowGeometry.
	StartWindowGeometryPoll();
}

QTimer *g_projectorPublishTimer = nullptr;
QTimer *g_notifyTimer = nullptr;

// obs marks every real projector window with windowHandle()->setProperty("isOBSProjectorWindow", true) -- see OBSProjector.cpp, which does this specifically so obss own code (SetDisplayAffinity) can recognize one reliably. thats a qt object property, invisible to plain win32 enumeration, so the helper (a separate process with no qt/obs-object access) cant read it directly -- this plugin can, since it runs inside obss own qt process. this refreshes the hwnd list the pipe thread streams to the helper so it can check the SAME authoritative signal obs uses internally instead of inferring "looks like a projector" from window class + ownership heuristics, which had real false-positive risk (NameDialog, the Scripts window, the auto-config wizard are independently-owned top-level windows too). must run on the gui thread for topLevelWindows()/winId().
void PublishProjectorWindows()
{
	QStringList hwnds;
	for (QWindow *w : QGuiApplication::topLevelWindows()) {
		if (w && w->property("isOBSProjectorWindow").toBool())
			hwnds << QString::number((quintptr)w->winId());
	}
	std::string csv = hwnds.join(',').toStdString();
	{
		std::lock_guard<std::mutex> lock(g_projectorCsvMutex);
		g_projectorCsv = csv;
		g_projectorCsvReady = true;
		g_projectorCsvAtMs = GetTickCount64();
	}
}

// writes one newline-terminated line to the connected helper. false on any write failure so the caller drops the
// connection and waits for a reconnect.
bool PipeWriteLine(HANDLE pipe, const std::string &line)
{
	std::string framed = line;
	framed.push_back('\n');
	const char *p = framed.data();
	size_t left = framed.size();
	while (left > 0) {
		DWORD written = 0;
		if (!WriteFile(pipe, p, (DWORD)left, &written, nullptr) || written == 0)
			return false;
		p += written;
		left -= written;
	}
	return true;
}

void PipeDispatchLine(const std::string &line)
{
	if (line == "OPENCLIPS") {
		QMetaObject::invokeMethod(g_callbacks, []() { ShowClips(); }, Qt::QueuedConnection);
	} else if (line == "OPENSETTINGS") {
		QMetaObject::invokeMethod(g_callbacks, []() { ShowSettings(); }, Qt::QueuedConnection);
	} else if (line == "OPENOBSSETTINGS") {
		QMetaObject::invokeMethod(g_callbacks, []() { OpenObsSettings(); }, Qt::QueuedConnection);
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
			g_callbacks, [on]() { g_recordingDotEnabled = on; RefreshAppIcon(); }, Qt::QueuedConnection);
	} else if (line.rfind("RKICON ", 0) == 0) {
		QString p = QString::fromUtf8(line.substr(7).c_str());
		QMetaObject::invokeMethod(
			g_callbacks, [p]() { g_rkIconPath = p; RefreshAppIcon(); }, Qt::QueuedConnection);
	} else if (line.rfind("CLIPSFULLSCREEN ", 0) == 0) {
		bool active = line.substr(16) == "1";
		QMetaObject::invokeMethod(g_callbacks, [active]() {
			g_clipsFullscreenActive = active;
			if (!active) ScheduleClipsGeometrySave();
		}, Qt::QueuedConnection);
	} else if (line.rfind("CONFIRM ", 0) == 0) {
		std::string kind = line.substr(8);
		QMetaObject::invokeMethod(
			g_callbacks, [kind]() { HandleConfirmRequest(kind); }, Qt::QueuedConnection);
	} else if (line == "ALLOWCLOSE") {
		// a real restart/exit is coming -- let the next WM_CLOSE through the close-to-tray filter for 60s, and
		// ack so the helper knows the filter saw it before it posts the close.
		g_allowCloseUntilMs.store(GetTickCount64() + 60000);
		g_pipeSendAllowCloseAck.store(true);
	}
}

// serves one connected helper until it disconnects or the plugin shuts down. non-blocking: PeekNamedPipe drains
// inbound, small WriteFiles push outbound, a 40ms sleep paces the loop. MAINWIN goes once per connection,
// PROJECTORS about every 250ms (the cadence the old file write used).
void PipeServeClient(HANDLE pipe)
{
	bool mainWinSent = false;
	auto lastProjectors = std::chrono::steady_clock::now() - std::chrono::milliseconds(250);
	std::string inbound;

	while (!g_pipeStop.load()) {
		quintptr mw = g_mainWinValue.load();
		if (!mainWinSent && mw != 0) {
			if (!PipeWriteLine(pipe, "MAINWIN " + std::to_string((unsigned long long)mw)))
				return;
			mainWinSent = true;
		}
		if (g_pipeSendAllowCloseAck.exchange(false)) {
			if (!PipeWriteLine(pipe, "ALLOWCLOSE_ACK"))
				return;
		}
		auto now = std::chrono::steady_clock::now();
		if (now - lastProjectors >= std::chrono::milliseconds(250)) {
			lastProjectors = now;
			std::string csv;
			bool fresh;
			{
				std::lock_guard<std::mutex> lock(g_projectorCsvMutex);
				csv = g_projectorCsv;
				fresh = g_projectorCsvReady && GetTickCount64() - g_projectorCsvAtMs <= 2000;
			}
			if (fresh && !PipeWriteLine(pipe, "PROJECTORS " + csv))
				return;
		}

		DWORD avail = 0;
		if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &avail, nullptr))
			return; // broken pipe
		while (avail > 0) {
			char buf[1024];
			DWORD want = avail < sizeof(buf) ? avail : (DWORD)sizeof(buf);
			DWORD got = 0;
			if (!ReadFile(pipe, buf, want, &got, nullptr) || got == 0)
				return;
			inbound.append(buf, got);
			if (inbound.size() > 64 * 1024)
				return;
			avail -= got;
		}
		size_t nl;
		while ((nl = inbound.find('\n')) != std::string::npos) {
			std::string one = inbound.substr(0, nl);
			if (!one.empty() && one.back() == '\r')
				one.pop_back();
			inbound.erase(0, nl + 1);
			PipeDispatchLine(one);
		}

		std::this_thread::sleep_for(std::chrono::milliseconds(40));
	}
}

// the pipe server: one instance, recreated after every client disconnect so a helper hot-swap just reconnects.
void PipeServerThread()
{
	while (!g_pipeStop.load()) {
		HANDLE pipe = CreateNamedPipeW(kIpcPipeName, PIPE_ACCESS_DUPLEX,
					       PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 64 * 1024, 64 * 1024,
					       0, nullptr);
		if (pipe == INVALID_HANDLE_VALUE) {
			std::this_thread::sleep_for(std::chrono::milliseconds(500));
			continue;
		}

		BOOL connected = ConnectNamedPipe(pipe, nullptr);
		if (!connected && GetLastError() == ERROR_PIPE_CONNECTED)
			connected = TRUE;
		// obs_module_unload pokes the pipe with a throwaway client to break this wait on shutdown.
		if (g_pipeStop.load()) {
			DisconnectNamedPipe(pipe);
			CloseHandle(pipe);
			break;
		}
		if (!connected) {
			CloseHandle(pipe);
			std::this_thread::sleep_for(std::chrono::milliseconds(200));
			continue;
		}

		PipeServeClient(pipe);

		DisconnectNamedPipe(pipe);
		CloseHandle(pipe);
	}
}

void OnFrontendEvent(enum obs_frontend_event event, void *)
{
	if (event == OBS_FRONTEND_EVENT_EXIT) {
		PatchBrowserDockTitleInUserIni(); // fires after obs already wrote user.ini, so this rename sticks
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
		// final geometry capture while the windows are still up and positioned (CloseCefWidgets deletes Clips next).
		if (g_clipsWindow)
			SaveClipsWindowGeometry();
		if (g_mainWindow)
			SaveWindowGeometry("obsMainWindow", g_mainWindow);
		CloseCefWidgetsBeforeShutdown();
		return;
	}

	// recording / replay-buffer state drives the red dot on a custom app icon
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED || event == OBS_FRONTEND_EVENT_RECORDING_STOPPED ||
	    event == OBS_FRONTEND_EVENT_RECORDING_PAUSED || event == OBS_FRONTEND_EVENT_RECORDING_UNPAUSED ||
	    event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED || event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED) {
		QMetaObject::invokeMethod(g_callbacks, []() { RefreshAppIcon(); }, Qt::QueuedConnection);
		return;
	}

	if (event != OBS_FRONTEND_EVENT_FINISHED_LOADING)
		return;

	PrewarmCefBrowser();
	PublishMainWindow();
	InstallMainWindowCloseFilter();
	// the browser dock is built well after FINISHED_LOADING (cef init) and obs keeps re-showing the native one / re-
	// titling ours, so this runs forever on a slow tick. each call is a cheap findChildren + a couple of string compares.
	ApplyControlsDockTweaks();
	if (!g_dockTweakTimer) {
		g_dockTweakTimer = new QTimer(g_callbacks);
		QObject::connect(g_dockTweakTimer, &QTimer::timeout, g_callbacks, []() { ApplyControlsDockTweaks(); });
		g_dockTweakTimer->start(4000);
	}
	g_openClipsHotkeyTimer = new QTimer(g_callbacks);
	QObject::connect(g_openClipsHotkeyTimer, &QTimer::timeout, g_callbacks, []() { LoadOpenClipsHotkey(); });
	g_openClipsHotkeyTimer->start(1000);
	LoadOpenClipsHotkey();

	// 250ms matches the helpers own poll cadence when its waiting for a projector to appear -- the pipe thread forwards this snapshot to the helper at the same rate.
	g_projectorPublishTimer = new QTimer(g_callbacks);
	QObject::connect(g_projectorPublishTimer, &QTimer::timeout, g_callbacks, []() { PublishProjectorWindows(); });
	g_projectorPublishTimer->start(250);
	PublishProjectorWindows();

	// the bell in obss own title bar. the Clips and Settings windows get theirs as they are created.
	AttachNotifyButton(g_mainWindow);
	AttachTitleKeybindBadge(g_mainWindow);
	qApp->installNativeEventFilter(new NotifyTrackFilter());
	// unread count, plus a slow re-position pass so a caption move no qt event covered (dpi change, a snap) still settles
	g_notifyTimer = new QTimer(g_callbacks);
	QObject::connect(g_notifyTimer, &QTimer::timeout, g_callbacks, []() {
		PollNotificationCount();
		PositionAllNotifyButtons();
		PositionAllTitleKeybindBadges();
	});
	g_notifyTimer->start(3000);
	PollNotificationCount();

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
	clipsRow->SetAction(clipsRowAction);
	QObject::connect(clipsRowAction, &QAction::triggered, trayMenu, [clipsRow]() {
		if (clipsRow)
			clipsRow->ProxyTrigger();
	});
	clipsRow->nameLabel->setText(QObject::tr("View Clips"));
	g_clipsRow = clipsRow;

	QAction *sharePreview = new QAction(QObject::tr("Share Preview"), trayMenu);
	sharePreview->setCheckable(true);
	QObject::connect(sharePreview, &QAction::triggered, trayMenu, [](bool checked) {
		// not set up yet -- the checkbox flip is meaningless, so undo it and offer the install instead
		if (!g_sharePreviewAvailable) {
			if (g_sharePreviewAction)
				g_sharePreviewAction->setChecked(false);
			PromptInstallSharePreview();
			return;
		}
		ToggleSharePreview(checked);
	});
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

	// the rows drive their own hover look off HoverEnter/HoverLeave; this only guarantees they end up un-hovered once the menu closes, in case a HoverLeave was missed on the way out
	QObject::connect(trayMenu, &QMenu::aboutToHide, trayMenu, []() {
		if (g_clipsRow) g_clipsRow->SetHovered(false);
		if (g_recordRow) g_recordRow->SetHovered(false);
		if (g_replayBufferRow) g_replayBufferRow->SetHovered(false);
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
		recordRow->SetAction(recordRowAction);
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
		replayBufferRow->SetAction(replayBufferRowAction);
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
	g_callbacks = new QObject();
	g_workersStopping = false;
	g_pipeStop.store(false);
	StartReplayKitCrashReporter();
	g_openClipsHotkeyFilter = new OpenClipsHotkeyFilter();
	qApp->installNativeEventFilter(g_openClipsHotkeyFilter);
	obs_frontend_add_event_callback(OnFrontendEvent, nullptr);
	g_pipeThread = std::thread(PipeServerThread);
	return true;
}

void obs_module_unload(void)
{
	obs_frontend_remove_event_callback(OnFrontendEvent, nullptr);
	StopReplayKitCrashReporter();
	g_pipeStop.store(true);
	if (g_pipeThread.joinable()) {
		// Cover a connect/write that starts just after the stop flag was set.
		while (WaitForSingleObject(g_pipeThread.native_handle(), 50) == WAIT_TIMEOUT)
			CancelSynchronousIo(g_pipeThread.native_handle());
		g_pipeThread.join();
	}
	StopWorkers();
	// Destroy queued callbacks before their code is unloaded with this module.
	delete g_callbacks;
	g_callbacks = nullptr;
	g_projectorPublishTimer = nullptr;
	if (g_openClipsHotkeyTimer)
		g_openClipsHotkeyTimer->stop();
	if (g_openClipsHotkeyRegistered)
		UnregisterHotKey(nullptr, kOpenClipsHotkeyId);
	if (g_openClipsHotkeyFilter) {
		qApp->removeNativeEventFilter(g_openClipsHotkeyFilter);
		delete g_openClipsHotkeyFilter;
		g_openClipsHotkeyFilter = nullptr;
	}
	// the filter is parented to the main window, so it is already gone if the window was destroyed first; guard with the QPointer and only detach when both still exist.
	if (g_mainWindow && g_mainWindowCloseFilter)
		g_mainWindow->removeEventFilter(g_mainWindowCloseFilter);
	delete g_mainWindowCloseFilter.data();
}
