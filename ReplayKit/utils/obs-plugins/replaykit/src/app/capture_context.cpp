#include "prelude.hpp"
#include "app/capture_context.hpp"

#include <QJsonDocument>
#include <QJsonArray>
#include <QJsonObject>
#include <QUuid>
#include <algorithm>
#include <graphics/matrix4.h>
#include <graphics/vec3.h>
#include <deque>
#include <mutex>
#include "app/clip_buffer.hpp"
#include "core/state.hpp"
#include "core/win32.hpp"

namespace rk {
namespace {
std::mutex gate;
std::deque<std::string> messages;
QPointer<QTimer> timer;
QString session;
std::atomic<qint64> requestedAt{0};
std::atomic<bool> replayRunning{false}, replayAmbiguous{false};
// off while game recognition is turned off in the settings: no sample walks the scene or reads the windows, and the helper is sent nothing to look up
std::atomic<bool> recognition{true};
HWINEVENTHOOK focusHook = nullptr;
QString lastKind = "unknown";
qint64 clipStartedAt = 0, lastNameAt = 0;
QJsonArray recordingRanges;
qint64 recordingStart = 0;
uint64_t sampleCalls = 0, totalSampleNs = 0, maxSampleNs = 0;
int sourceCount = 0;
QString captureReason = "inactive";

void Queue(const char *verb, const QJsonObject &data)
{
	std::lock_guard<std::mutex> lock(gate);
	if (messages.size() >= 512)
		messages.pop_front();
	messages.push_back(std::string(verb) + " " + QJsonDocument(data).toJson(QJsonDocument::Compact).toStdString());
}

QString ImagePath(DWORD pid)
{
	HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
	if (!process)
		return {};
	wchar_t path[32768];
	DWORD length = 32768;
	const bool read = QueryFullProcessImageNameW(process, 0, path, &length);
	CloseHandle(process);
	return read ? QString::fromWCharArray(path, int(length)) : QString();
}

struct Capture {
	std::string kind, exe, title, windowClass, monitor;
	// the share of the canvas the source covers, and its place in the stack, higher drawn later and so on top
	double coverage = 0;
	int order = 0;
};

// what one walk of the scene tree knows of the level it is in: the captures found so far, the size of the scene or group whose items it walks, and the share of the canvas that scene or group itself takes
struct CollectLevel {
	std::vector<Capture> *captures;
	double width, height, share;
};

// the share of its scene an item covers: the corners of its box, as the scene places it, clipped to the scene
double ItemShare(obs_sceneitem_t *item, double width, double height)
{
	if (width <= 0 || height <= 0)
		return 0;
	matrix4 box;
	obs_sceneitem_get_box_transform(item, &box);
	double left = 1e18, top = 1e18, right = -1e18, bottom = -1e18;
	for (float x : {0.0f, 1.0f})
		for (float y : {0.0f, 1.0f}) {
			vec3 corner, placed;
			vec3_set(&corner, x, y, 0.0f);
			vec3_transform(&placed, &corner, &box);
			left = qMin(left, double(placed.x));
			right = qMax(right, double(placed.x));
			top = qMin(top, double(placed.y));
			bottom = qMax(bottom, double(placed.y));
		}
	const double w = qMax(0.0, qMin(right, width) - qMax(left, 0.0)), h = qMax(0.0, qMin(bottom, height) - qMax(top, 0.0));
	return w * h / (width * height);
}

bool Collect(obs_scene_t *, obs_sceneitem_t *item, void *data)
{
	auto *level = static_cast<CollectLevel *>(data);
	if (!obs_sceneitem_visible(item))
		return true;
	obs_source_t *source = obs_sceneitem_get_source(item);
	if (!source || !obs_source_enabled(source))
		return true;
	const double share = level->share * ItemShare(item, level->width, level->height);
	obs_scene_t *inner = obs_scene_from_source(source);
	if (!inner && obs_sceneitem_is_group(item))
		inner = obs_sceneitem_group_get_scene(item);
	if (inner) {
		CollectLevel nested{level->captures, double(obs_source_get_width(source)), double(obs_source_get_height(source)), share};
		obs_scene_enum_items(inner, Collect, &nested);
		return true;
	}
	const std::string id = obs_source_get_unversioned_id(source);
	if (id != "game_capture" && id != "window_capture" && id != "monitor_capture")
		return true;
	Capture capture;
	capture.kind = "unknown";
	capture.coverage = share;
	capture.order = int(level->captures->size());
	if (obs_source_get_width(source) && obs_source_get_height(source)) {
		if (id == "monitor_capture") {
			obs_data_t *settings = obs_source_get_settings(source);
			capture.monitor = obs_data_get_string(settings, "monitor_id");
			obs_data_release(settings);
			capture.kind = "display";
		} else {
			calldata_t info = {};
			if (proc_handler_call(obs_source_get_proc_handler(source), "get_hooked", &info) && calldata_bool(&info, "hooked")) {
				capture.kind = id == "game_capture" ? "game" : "window";
				capture.exe = calldata_string(&info, "executable");
				capture.title = calldata_string(&info, "title");
				capture.windowClass = calldata_string(&info, "class");
			}
			calldata_free(&info);
		}
	}
	level->captures->push_back(capture);
	return true;
}

bool IsCapturedMonitor(HMONITOR monitor, const std::string &id)
{
	MONITORINFOEXW info = {};
	info.cbSize = sizeof(info);
	if (id.empty() || !monitor || !GetMonitorInfoW(monitor, &info))
		return false;
	DISPLAY_DEVICEW device = {};
	device.cb = sizeof(device);
	if (!EnumDisplayDevicesW(info.szDevice, 0, &device, EDD_GET_DEVICE_INTERFACE_NAME))
		return false;
	return QString::fromStdString(id).compare(QString::fromWCharArray(device.DeviceID), Qt::CaseInsensitive) == 0;
}

bool MatchesMonitor(HWND foreground, const std::string &id)
{
	return IsCapturedMonitor(MonitorFromWindow(foreground, MONITOR_DEFAULTTONULL), id);
}

struct MonitorSearch {
	std::string id;
	HMONITOR found = nullptr;
};

BOOL CALLBACK FindMonitor(HMONITOR monitor, HDC, LPRECT, LPARAM data)
{
	auto *search = reinterpret_cast<MonitorSearch *>(data);
	if (!IsCapturedMonitor(monitor, search->id))
		return TRUE;
	search->found = monitor;
	return FALSE;
}

struct ShownSearch {
	POINT middle;
	HWND found = nullptr;
};

// windows come top first; the first real app window over the middle of the display is what it shows, past this plugins own popups, tool windows, click-through overlays and cloaked windows on other desktops
BOOL CALLBACK FindShown(HWND window, LPARAM data)
{
	auto *search = reinterpret_cast<ShownSearch *>(data);
	RECT rect = {};
	if (!IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, &rect) || !PtInRect(&rect, search->middle))
		return TRUE;
	const LONG_PTR style = GetWindowLongPtrW(window, GWL_EXSTYLE);
	DWORD pid = 0, cloaked = 0;
	GetWindowThreadProcessId(window, &pid);
	if ((style & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) || pid == GetCurrentProcessId() ||
	    (SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked))
		return TRUE;
	search->found = window;
	return FALSE;
}

// the window a display capture shows when the focus is on another display: the top app window over the middle of the captured one
HWND WindowShownOn(const std::string &id)
{
	MonitorSearch monitor{id};
	EnumDisplayMonitors(nullptr, nullptr, FindMonitor, reinterpret_cast<LPARAM>(&monitor));
	MONITORINFO info = {};
	info.cbSize = sizeof(info);
	if (!monitor.found || !GetMonitorInfoW(monitor.found, &info))
		return nullptr;
	ShownSearch search{{(info.rcMonitor.left + info.rcMonitor.right) / 2, (info.rcMonitor.top + info.rcMonitor.bottom) / 2}};
	EnumWindows(FindShown, reinterpret_cast<LPARAM>(&search));
	return search.found;
}

// a window over the whole of its display the way a fullscreen game runs; a maximized window is an ordinary app
bool CoversMonitor(HWND window)
{
	if (IsZoomed(window))
		return false;
	RECT rect = {};
	MONITORINFO info = {};
	info.cbSize = sizeof(info);
	if (!GetWindowRect(window, &rect) || !GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONULL), &info))
		return false;
	return rect.left <= info.rcMonitor.left && rect.top <= info.rcMonitor.top && rect.right >= info.rcMonitor.right && rect.bottom >= info.rcMonitor.bottom;
}

// the title of a java window, the only way minecraft java edition is told apart from other java programs; the title of any other program never leaves the plugin, since it can hold private text such as a browser tab
QString JavaTitle(HWND window, const QString &path)
{
	const QString exe = path.section('\\', -1);
	if (exe.compare(QStringLiteral("javaw.exe"), Qt::CaseInsensitive) != 0 && exe.compare(QStringLiteral("java.exe"), Qt::CaseInsensitive) != 0)
		return {};
	wchar_t title[256] = {};
	GetWindowTextW(window, title, 256);
	return QString::fromWCharArray(title);
}

// a game or window capture shows the focused window when it is hooked to exactly that window
bool TargetFocused(const Capture &capture, HWND foreground, const QString &path)
{
	if (!foreground || path.isEmpty() || (capture.kind != "game" && capture.kind != "window"))
		return false;
	wchar_t title[1024] = {}, windowClass[256] = {};
	GetWindowTextW(foreground, title, 1024);
	GetClassNameW(foreground, windowClass, 256);
	return path.section('\\', -1).compare(QString::fromStdString(capture.exe), Qt::CaseInsensitive) == 0 &&
	       QString::fromWCharArray(windowClass) == QString::fromStdString(capture.windowClass) && QString::fromWCharArray(title) == QString::fromStdString(capture.title);
}

// the capture a clip shows when several are in the scene: of those with a picture, the one on top that covers most of the canvas hides the rest; else the clearly biggest; else the only one showing what is in focus; else none, so a clip is never put down to a guess
const Capture *Choose(const std::vector<Capture> &captures, HWND foreground, const QString &path)
{
	std::vector<const Capture *> live;
	for (const Capture &capture : captures)
		if (capture.kind != "unknown" && capture.coverage > 0.01)
			live.push_back(&capture);
	if (live.size() <= 1)
		return live.empty() ? nullptr : live[0];
	std::sort(live.begin(), live.end(), [](const Capture *a, const Capture *b) { return a->order > b->order; });
	for (const Capture *capture : live)
		if (capture->coverage >= 0.6)
			return capture;
	std::sort(live.begin(), live.end(), [](const Capture *a, const Capture *b) { return a->coverage > b->coverage; });
	if (live[0]->coverage >= live[1]->coverage * 1.5)
		return live[0];
	const Capture *focused = nullptr;
	for (const Capture *capture : live) {
		const bool shows = capture->kind == "display" ? foreground && MatchesMonitor(foreground, capture->monitor) : TargetFocused(*capture, foreground, path);
		if (!shows)
			continue;
		if (focused)
			return nullptr;
		focused = capture;
	}
	return focused;
}

void Sample()
{
	if (!recognition.load()) {
		ClipBufferGameName(QString());
		return;
	}
	const auto began = std::chrono::steady_clock::now();
	const qint64 at = qint64(GetTickCount64());
	QJsonObject data{{"at", at}, {"session", session}, {"kind", "unknown"}};
	if (ClipBufferActive()) {
		if (clipStartedAt == 0)
			clipStartedAt = at;
	} else {
		clipStartedAt = 0;
	}
	const int seconds = ClipBufferNameWindow();
	data["buffer"] = clipStartedAt;
	data["start"] = clipStartedAt > 0 ? qMax(clipStartedAt, at - qint64(seconds) * 1000) : at;
	data["seconds"] = seconds;
	sourceCount = 0;
	captureReason = "inactive";
	if (ClipBufferActive() || (obs_frontend_recording_active() && !obs_frontend_recording_paused())) {
		std::vector<Capture> captures;
		obs_source_t *scene = obs_frontend_get_current_scene();
		if (scene) {
			CollectLevel root{&captures, double(obs_source_get_width(scene)), double(obs_source_get_height(scene)), 1.0};
			if (obs_scene_t *current = obs_scene_from_source(scene))
				obs_scene_enum_items(current, Collect, &root);
			obs_source_release(scene);
		}
		HWND foreground = GetForegroundWindow();
		DWORD pid = 0;
		GetWindowThreadProcessId(foreground, &pid);
		const QString path = ImagePath(pid);
		sourceCount = int(captures.size());
		const Capture *chosen = Choose(captures, foreground, path);
		const bool anyLive = std::any_of(captures.begin(), captures.end(), [](const Capture &c) { return c.kind != "unknown" && c.coverage > 0.01; });
		captureReason = captures.empty() ? "no-visible-capture" : !anyLive ? "capture-not-live" : !chosen ? "multiple-captures" : !foreground || path.isEmpty() ? "foreground-unavailable" : "capture-not-live";
		// a display capture records whatever its display shows, so the window judged is the focused one when it is on that display and else the one on top there
		if (chosen && chosen->kind == "display") {
			const Capture &capture = *chosen;
			const bool focusedThere = foreground && !IsIconic(foreground) && MatchesMonitor(foreground, capture.monitor);
			HWND shown = focusedThere ? foreground : WindowShownOn(capture.monitor);
			DWORD shownPid = 0;
			if (shown)
				GetWindowThreadProcessId(shown, &shownPid);
			const QString shownPath = shownPid ? ImagePath(shownPid) : QString();
			captureReason = !shown ? "display-not-found" : shownPath.isEmpty() ? "shown-window-unreadable" : focusedThere ? "matched" : "matched-unfocused";
			if (!shownPath.isEmpty()) {
				data["kind"] = QStringLiteral("display");
				data["path"] = shownPath;
				data["fullscreen"] = CoversMonitor(shown);
				data["title"] = JavaTitle(shown, shownPath);
			}
		} else if (chosen && foreground && !IsIconic(foreground) && !path.isEmpty()) {
			const Capture &capture = *chosen;
			const bool matched = TargetFocused(capture, foreground, path);
			captureReason = matched ? "matched" : "capture-target-not-focused";
			DWORD currentPid = 0;
			GetWindowThreadProcessId(foreground, &currentPid);
			if (matched && foreground == GetForegroundWindow() && pid == currentPid) {
				data["kind"] = QString::fromStdString(capture.kind);
				data["path"] = path;
				data["title"] = JavaTitle(foreground, path);
			}
		}
	}
	if (clipStartedAt == 0 || at - lastNameAt > 1500)
		ClipBufferGameName(QString());
	// why a sample shows no capture, which the helper keeps with the stretch of the clip it covers
	data["reason"] = captureReason;
	Queue("CAPTURE", data);
	lastKind = data["kind"].toString();
	const uint64_t elapsed = uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - began).count());
	++sampleCalls;
	totalSampleNs += elapsed;
	maxSampleNs = qMax(maxSampleNs, elapsed);
}

void CALLBACK FocusChanged(HWINEVENTHOOK, DWORD, HWND, LONG, LONG, DWORD, DWORD)
{
	if (g_callbacks)
		QMetaObject::invokeMethod(g_callbacks, Sample, Qt::QueuedConnection);
}
}

void CaptureContextStart()
{
	if (timer)
		return;
	session = QUuid::createUuid().toString(QUuid::WithoutBraces);
	replayRunning.store(obs_frontend_replay_buffer_active());
	timer = new QTimer(g_callbacks);
	QObject::connect(timer, &QTimer::timeout, g_callbacks, Sample);
	timer->start(500);
	focusHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, FocusChanged, 0, 0, WINEVENT_OUTOFCONTEXT);
	if (!focusHook)
		blog(LOG_WARNING, "[replaykit] game focus events unavailable; capture sampling remains active");
	Sample();
}

void CaptureContextStop()
{
	clipStartedAt = lastNameAt = 0;
	ClipBufferGameName(QString());
	replayRunning.store(false);
	requestedAt.store(0);
	if (timer)
		timer->stop();
	if (focusHook) {
		UnhookWinEvent(focusHook);
		focusHook = nullptr;
	}
	std::lock_guard<std::mutex> lock(gate);
	messages.clear();
}

void CaptureContextSetRecognition(bool on)
{
	if (recognition.exchange(on) == on)
		return;
	if (on)
		CaptureContextRefresh();
	else
		ClipBufferGameName(QString());
}

void CaptureContextRequested()
{
	if (!replayRunning.load())
		return;
	CaptureContextRefresh();
	ClipBufferPrepareName();
	qint64 empty = 0;
	if (!requestedAt.compare_exchange_strong(empty, qint64(GetTickCount64())))
		replayAmbiguous.store(true);
}

void CaptureContextRefresh()
{
	if (timer && timer->isActive())
		Sample();
}

void CaptureContextResetBuffer()
{
	clipStartedAt = lastNameAt = 0;
	ClipBufferGameName(QString());
}

void CaptureContextName(const QJsonObject &data)
{
	const qint64 at = qint64(data["at"].toDouble()), start = qint64(data["start"].toDouble());
	const qint64 age = qint64(GetTickCount64()) - at;
	const int seconds = data["seconds"].toInt();
	if (age < 0 || age > 1500 || at < lastNameAt || data["session"].toString() != session || clipStartedAt <= 0 ||
		qint64(data["buffer"].toDouble()) != clipStartedAt || seconds != ClipBufferNameWindow() || start < clipStartedAt || start > at || at - start > qint64(seconds) * 1000)
		return;
	lastNameAt = at;
	ClipBufferGameName(data["name"].toString());
}

void CaptureContextReplayFile(const std::string &path)
{
	const qint64 at = requestedAt.exchange(0);
	const bool ambiguous = replayAmbiguous.exchange(false);
	if (!ambiguous && at > 0 && qint64(GetTickCount64()) - at < 120000)
		CaptureContextFile(path, at);
	// a save this plugin did not see pressed (the obs button, a stream deck, another websocket client) or two that overlapped: the helper takes the end of the clip from the moment obs created the file
	else if (!path.empty())
		Queue("CAPTUREFILE", QJsonObject{{"path", QString::fromStdString(path)}, {"end", 0}, {"replay", true}});
}

void CaptureContextFile(const std::string &path, qint64 end)
{
	if (path.empty())
		return;
	Sample();
	Queue("CAPTUREFILE", QJsonObject{{"path", QString::fromStdString(path)}, {"end", end > 0 ? end : qint64(GetTickCount64())}});
}

void CaptureContextBindSave(qint64 id, const QJsonArray &pieces)
{
	Queue("CAPTURESAVE", QJsonObject{{"id", id}, {"session", session}, {"pieces", pieces}});
}

void CaptureContextEvent(enum obs_frontend_event event)
{
	const qint64 now = qint64(GetTickCount64());
	if (event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED || event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED) {
		CaptureContextResetBuffer();
		replayRunning.store(event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED);
		requestedAt.store(0);
		replayAmbiguous.store(false);
	}
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED) {
		recordingRanges = QJsonArray();
		recordingStart = now;
	} else if (event == OBS_FRONTEND_EVENT_RECORDING_UNPAUSED) {
		recordingStart = now;
	} else if (event == OBS_FRONTEND_EVENT_RECORDING_PAUSED || event == OBS_FRONTEND_EVENT_RECORDING_STOPPED) {
		if (recordingStart > 0 && recordingRanges.size() < 4096)
			recordingRanges.append(QJsonObject{{"s", recordingStart}, {"e", now}});
		recordingStart = 0;
		if (event == OBS_FRONTEND_EVENT_RECORDING_STOPPED) {
			char *path = obs_frontend_get_last_recording();
			if (path && *path)
				Queue("CAPTUREFILE", QJsonObject{{"path", QString::fromUtf8(path)}, {"end", now}, {"ranges", recordingRanges}});
			bfree(path);
			recordingRanges = QJsonArray();
		}
	}
}

QJsonObject CaptureContextProbe()
{
	std::lock_guard<std::mutex> lock(gate);
	return QJsonObject{{"running", timer && timer->isActive()}, {"recognition", recognition.load()}, {"focusEvents", focusHook != nullptr}, {"kind", lastKind}, {"queued", int(messages.size())},
		{"sources", sourceCount}, {"reason", captureReason},
		{"samples", qint64(sampleCalls)}, {"averageUs", sampleCalls ? double(totalSampleNs) / double(sampleCalls) / 1000.0 : 0}, {"maximumUs", double(maxSampleNs) / 1000.0}};
}

std::vector<std::string> CaptureContextMessages()
{
	std::lock_guard<std::mutex> lock(gate);
	std::vector<std::string> result(messages.begin(), messages.end());
	messages.clear();
	return result;
}
}
