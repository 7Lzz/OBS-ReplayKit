#include "prelude.hpp"
#include "app/capture_context.hpp"

#include <QJsonDocument>
#include <QJsonArray>
#include <QJsonObject>
#include <QUuid>
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
};

bool Collect(obs_scene_t *, obs_sceneitem_t *item, void *data)
{
	if (!obs_sceneitem_visible(item))
		return true;
	obs_source_t *source = obs_sceneitem_get_source(item);
	if (!source || !obs_source_enabled(source))
		return true;
	if (obs_scene_t *nested = obs_scene_from_source(source)) {
		obs_scene_enum_items(nested, Collect, data);
		return true;
	}
	if (obs_sceneitem_is_group(item)) {
		obs_scene_enum_items(obs_sceneitem_group_get_scene(item), Collect, data);
		return true;
	}
	const std::string id = obs_source_get_unversioned_id(source);
	if (id != "game_capture" && id != "window_capture" && id != "monitor_capture")
		return true;
	Capture capture;
	capture.kind = "unknown";
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
	static_cast<std::vector<Capture> *>(data)->push_back(capture);
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

void Sample()
{
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
			if (obs_scene_t *current = obs_scene_from_source(scene))
				obs_scene_enum_items(current, Collect, &captures);
			obs_source_release(scene);
		}
		HWND foreground = GetForegroundWindow();
		DWORD pid = 0;
		GetWindowThreadProcessId(foreground, &pid);
		const QString path = ImagePath(pid);
		sourceCount = int(captures.size());
		captureReason = captures.empty() ? "no-visible-capture" : captures.size() > 1 ? "multiple-captures" : !foreground || path.isEmpty() ? "foreground-unavailable" : "capture-not-live";
		// a display capture records whatever its display shows, so the window judged is the focused one when it is on that display and else the one on top there
		if (captures.size() == 1 && captures[0].kind == "display") {
			const Capture &capture = captures[0];
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
			}
		} else if (captures.size() == 1 && foreground && !IsIconic(foreground) && !path.isEmpty()) {
			const Capture &capture = captures[0];
			const QString exe = path.section('\\', -1);
			bool matched = false;
			if (capture.kind == "game" || capture.kind == "window") {
				wchar_t title[1024] = {}, windowClass[256] = {};
				GetWindowTextW(foreground, title, 1024);
				GetClassNameW(foreground, windowClass, 256);
				matched = exe.compare(QString::fromStdString(capture.exe), Qt::CaseInsensitive) == 0 &&
					  QString::fromWCharArray(windowClass) == QString::fromStdString(capture.windowClass) &&
					  QString::fromWCharArray(title) == QString::fromStdString(capture.title);
				captureReason = matched ? "matched" : "capture-target-not-focused";
			}
			DWORD currentPid = 0;
			GetWindowThreadProcessId(foreground, &currentPid);
			if (matched && foreground == GetForegroundWindow() && pid == currentPid) {
				data["kind"] = QString::fromStdString(capture.kind);
				data["path"] = path;
			}
		}
	}
	if (clipStartedAt == 0 || at - lastNameAt > 1500)
		ClipBufferGameName(QString());
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
	return QJsonObject{{"running", timer && timer->isActive()}, {"focusEvents", focusHook != nullptr}, {"kind", lastKind}, {"queued", int(messages.size())},
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
