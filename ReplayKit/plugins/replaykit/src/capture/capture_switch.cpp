// shows the capture that fits what is in front, in every scene: game capture for a fullscreen game, window capture for a listed game or one game capture cannot hook, the game out of focus card while a listed game waits behind other windows, display capture otherwise; and keeps display capture on its chosen display
#include "prelude.hpp"
#include "capture/capture_switch.hpp"

#include <QJsonArray>
#include <QRegularExpression>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <iterator>
#include <psapi.h>
#include <set>
#include <vector>

#include "core/json_lite.hpp"
#include "core/shared_values.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

enum class State { None, Desktop, Game, Window, Away };

struct Policy {
	bool known = false;
	bool hybrid = false;
	std::set<std::string> autoGames;
	bool keepFocused = false;
	bool focusCard = false;
	int graceMs = 0;
};

constexpr int kCheckMs = 1000;
constexpr int kHookFirstCheckMs = 2000;
constexpr int kHookCheckMs = 1000;
constexpr int kHookChecks = 10;
constexpr int kDefaultGraceMs = 1000;
constexpr int kMinGraceMs = 50;
constexpr int kMaxGraceMs = 5000;
constexpr int kWindowStallTicks = 3;
// a capture that stays dead (the display asleep, the target in a state that cannot be captured) is retried less and less often, up to this many ticks apart, so it neither floods the log nor keeps tearing the capture down
constexpr int kWindowStallMaxTicks = 300;
constexpr int kMonitorCheckMs = 5000;
// a display switch this module makes has to hold for this many checks in a row: while a game changes its mode or a monitor wakes up, a display drops out and windows hands the primary to another one for a moment, and following that is how the capture ended up on the second display
constexpr int kMonitorChecksToSwitch = 2;
// the display the capture is meant to show, kept on the source beside monitor_id and set from the Display setting of replaykit: "primary" follows whichever display windows calls primary, anything else is one display; monitor_id itself only says what it shows right now
constexpr char kWantedMonitorKey[] = "replaykit_wanted_monitor_id";

// windows game capture has nothing to do with; roblox is here since its hooks are unreliable for this workflow, which keeps it on display capture unless it is listed
const std::set<std::string> kIgnored = {"obs64.exe", "obs32.exe", "obs.exe", "explorer.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe",
					"startmenuexperiencehost.exe", "applicationframehost.exe", "video.ui.exe", "systemsettings.exe", "textinputhost.exe",
					"lockapp.exe", "logonui.exe", "dwm.exe", "taskmgr.exe", "snippingtool.exe", "screenclippinghost.exe", "robloxplayer.exe",
					"robloxplayerbeta.exe", "robloxplayerlauncher.exe", "robloxstudiobeta.exe", "windows10universal.exe"};
const std::set<std::string> kNeverListed = {"obs64.exe", "obs32.exe", "obs.exe", "explorer.exe", "searchhost.exe", "searchapp.exe", "shellexperiencehost.exe",
					    "startmenuexperiencehost.exe", "applicationframehost.exe", "video.ui.exe", "systemsettings.exe", "textinputhost.exe",
					    "lockapp.exe", "logonui.exe", "dwm.exe", "taskmgr.exe", "snippingtool.exe", "screenclippinghost.exe"};
const std::set<std::string> kGraphicsApis = {"d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "opengl32.dll", "vulkan-1.dll"};

Policy g_policy;
QPointer<QTimer> g_checkTimer, g_hookTimer, g_desktopTimer;
std::set<std::string> g_hookBlocked, g_windowFallback, g_graphicsSeen;
std::string g_pendingExe;
int g_pendingAttempts = 0;
State g_lastState = State::None;
std::string g_lastWindowToken;
int g_stallCount = 0;
int g_stallLimit = kWindowStallTicks;
HWND g_stickyHwnd = nullptr;
// the window of the listed game last in front, which the card stands in for until it is closed; tracked whatever keep focused says
HWND g_focusGameHwnd = nullptr;
// whether the card may be showing, so leaving hybrid mode hides it once; true at start since obs saves a card that was showing when it closed
bool g_cardMaybeShown = true;
ULONGLONG g_monitorCheckAt = 0;
std::string g_monitorWarning;
bool g_monitorWarned = false;
std::string g_lastAppliedMonitor;
bool g_appliedMonitor = false;
std::string g_pendingMonitor;
int g_pendingMonitorChecks = 0;

std::string Lower(std::string text)
{
	std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return char(std::tolower(c)); });
	return text;
}

std::string Utf8(const wchar_t *text, int length)
{
	return QString::fromWCharArray(text, length).toStdString();
}

bool ValidWindow(HWND hwnd)
{
	return hwnd && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd) && hwnd != GetDesktopWindow() && hwnd != GetShellWindow();
}

HANDLE OpenWindowProcess(HWND hwnd)
{
	DWORD pid = 0;
	GetWindowThreadProcessId(hwnd, &pid);
	return pid ? OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, FALSE, pid) : nullptr;
}

std::string ExeOf(HANDLE process)
{
	wchar_t path[1024];
	DWORD size = DWORD(std::size(path));
	if (!QueryFullProcessImageNameW(process, 0, path, &size))
		return {};
	const std::string full = Utf8(path, int(size));
	const size_t slash = full.find_last_of('\\');
	return Lower(slash == std::string::npos ? full : full.substr(slash + 1));
}

std::string ExeOfWindow(HWND hwnd)
{
	HANDLE process = OpenWindowProcess(hwnd);
	if (!process)
		return {};
	const std::string exe = ExeOf(process);
	CloseHandle(process);
	return exe;
}

bool HasGraphicsApi(HANDLE process)
{
	DWORD needed = 0;
	EnumProcessModules(process, nullptr, 0, &needed);
	if (!needed)
		return false;
	std::vector<HMODULE> modules(needed / sizeof(HMODULE) + 64);
	if (!EnumProcessModules(process, modules.data(), DWORD(modules.size() * sizeof(HMODULE)), &needed))
		return false;
	const size_t count = std::min<size_t>(needed / sizeof(HMODULE), modules.size());
	wchar_t name[MAX_PATH];
	for (size_t i = 0; i < count; ++i) {
		const DWORD length = GetModuleBaseNameW(process, modules[i], name, MAX_PATH);
		if (length && kGraphicsApis.count(Lower(Utf8(name, int(length)))))
			return true;
	}
	return false;
}

// a title read on a window of this process is a call into the obs ui thread, and windows of obs are not games anyway
std::string WindowTitle(HWND hwnd)
{
	DWORD owner = 0;
	GetWindowThreadProcessId(hwnd, &owner);
	if (!owner || owner == GetCurrentProcessId())
		return {};
	wchar_t title[513];
	const int length = GetWindowTextW(hwnd, title, int(std::size(title)));
	return length > 0 ? Utf8(title, length) : std::string();
}

std::string WindowClass(HWND hwnd)
{
	wchar_t name[256];
	const int length = GetClassNameW(hwnd, name, int(std::size(name)));
	return length > 0 ? Utf8(name, length) : std::string();
}

// the way window capture of obs writes its window setting: title, class and exe, with # and : escaped
std::string TokenPart(std::string value)
{
	std::string out;
	for (const char c : value) {
		if (c == '#')
			out += "#22";
		else if (c == ':')
			out += "#3A";
		else
			out += c;
	}
	return out;
}

std::string WindowToken(HWND hwnd)
{
	if (!ValidWindow(hwnd))
		return {};
	const std::string title = WindowTitle(hwnd), windowClass = WindowClass(hwnd), exe = ExeOfWindow(hwnd);
	if (title.empty() || windowClass.empty() || exe.empty())
		return {};
	return TokenPart(title) + ":" + TokenPart(windowClass) + ":" + TokenPart(exe);
}

void ResetStall()
{
	g_stallCount = 0;
	g_stallLimit = kWindowStallTicks;
}

bool PointWindowCapture(HWND hwnd)
{
	const std::string token = WindowToken(hwnd);
	if (token.empty())
		return false;
	obs_source_t *source = obs_get_source_by_name(kWindowCaptureSourceName);
	if (!source)
		return false;
	// the memo alone is not enough: a settings save or the user can point the source at another window, so what it captures now is read back before a push is skipped
	if (token == g_lastWindowToken) {
		obs_data_t *current = obs_source_get_settings(source);
		const bool same = token == obs_data_get_string(current, "window");
		obs_data_release(current);
		if (same) {
			obs_source_release(source);
			return true;
		}
	}
	obs_data_t *settings = obs_data_create();
	obs_data_set_string(settings, "window", token.c_str());
	obs_data_set_int(settings, "method", 2);
	obs_data_set_int(settings, "priority", 0);
	obs_data_set_bool(settings, "client_area", true);
	obs_data_set_bool(settings, "compatibility", false);
	obs_data_set_bool(settings, "capture_audio", false);
	obs_source_update(source, settings);
	obs_data_release(settings);
	obs_source_release(source);
	g_lastWindowToken = token;
	return true;
}

template <typename Visit> void ForEachSceneItem(Visit visit)
{
	obs_frontend_source_list scenes = {};
	obs_frontend_get_scenes(&scenes);
	for (size_t i = 0; i < scenes.sources.num; ++i) {
		obs_scene_t *scene = obs_scene_from_source(scenes.sources.array[i]);
		if (!scene)
			continue;
		obs_scene_enum_items(
			scene,
			[](obs_scene_t *, obs_sceneitem_t *item, void *data) {
				(*static_cast<Visit *>(data))(item);
				return true;
			},
			&visit);
	}
	obs_frontend_source_list_free(&scenes);
}

bool GameCaptureLive()
{
	bool live = false;
	ForEachSceneItem([&live](obs_sceneitem_t *item) {
		obs_source_t *source = obs_sceneitem_get_source(item);
		if (std::strcmp(obs_source_get_name(source), kGameCaptureSourceName) == 0 && obs_source_get_width(source) > 0 && obs_source_get_height(source) > 0)
			live = true;
	});
	return live;
}

bool WindowCaptureLive()
{
	obs_source_t *source = obs_get_source_by_name(kWindowCaptureSourceName);
	if (!source)
		return false;
	const bool live = obs_source_get_width(source) > 0 && obs_source_get_height(source) > 0;
	obs_source_release(source);
	return live;
}

void SetShown(const std::vector<std::pair<const char *, bool>> &shown, bool visible)
{
	ForEachSceneItem([&shown, visible](obs_sceneitem_t *item) {
		const char *name = obs_source_get_name(obs_sceneitem_get_source(item));
		for (const auto &[source, wanted] : shown)
			if (wanted == visible && std::strcmp(name, source) == 0)
				obs_sceneitem_set_visible(item, visible);
	});
}

void ApplySwitch(State state, HWND hwnd = nullptr)
{
	if (state == State::Window && !PointWindowCapture(hwnd))
		state = State::Desktop;
	const std::vector<std::pair<const char *, bool>> shown = {{kGameCaptureSourceName, state == State::Game},
								  {kWindowCaptureSourceName, state == State::Window},
								  {kDisplayCaptureSourceName, state == State::Desktop},
								  {kFocusCardSourceName, state == State::Away}};
	// what shows now is shown before the rest is hidden, so no frame of the program is empty in between, which the card would also keep as its frozen game picture
	SetShown(shown, true);
	SetShown(shown, false);
	g_cardMaybeShown = state == State::Away;
}

void HideFocusCard()
{
	if (!g_cardMaybeShown)
		return;
	SetShown({{kFocusCardSourceName, false}}, false);
	g_cardMaybeShown = false;
}

void CancelHookCheck()
{
	if (g_hookTimer)
		g_hookTimer->stop();
	g_pendingExe.clear();
	g_pendingAttempts = 0;
}

void CancelDesktopSwitch()
{
	if (g_desktopTimer)
		g_desktopTimer->stop();
}

bool ListedGame(HWND hwnd, std::string *exe = nullptr)
{
	if (!ValidWindow(hwnd))
		return false;
	const std::string name = ExeOfWindow(hwnd);
	if (name.empty() || kNeverListed.count(name) || !g_policy.autoGames.count(name))
		return false;
	if (exe)
		*exe = name;
	return true;
}

// a remembered listed game whose window is still open but not in front, which a minimized game is too; a closed window or a game taken off the list forgets it, and without the card in obs the desktop shows as it always did
bool AwayFromListedGame(HWND foreground, std::string &exe)
{
	if (!g_focusGameHwnd)
		return false;
	if (!IsWindow(g_focusGameHwnd)) {
		g_focusGameHwnd = nullptr;
		return false;
	}
	exe = ExeOfWindow(g_focusGameHwnd);
	if (exe.empty() || !g_policy.autoGames.count(exe)) {
		g_focusGameHwnd = nullptr;
		return false;
	}
	if (g_focusGameHwnd == foreground)
		return false;
	obs_source_t *card = obs_get_source_by_name(kFocusCardSourceName);
	if (!card)
		return false;
	obs_source_release(card);
	return true;
}

bool FullscreenGame(HWND hwnd, std::string &exe)
{
	if (!ValidWindow(hwnd))
		return false;
	HANDLE process = OpenWindowProcess(hwnd);
	if (!process)
		return false;
	exe = ExeOf(process);
	bool game = false;
	RECT window{};
	MONITORINFO monitor{sizeof(MONITORINFO)};
	if (!exe.empty() && !kIgnored.count(exe) && !g_hookBlocked.count(exe) && GetWindowRect(hwnd, &window) &&
	    GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), &monitor) && window.right - window.left == monitor.rcMonitor.right - monitor.rcMonitor.left &&
	    window.bottom - window.top == monitor.rcMonitor.bottom - monitor.rcMonitor.top) {
		game = g_graphicsSeen.count(exe) || HasGraphicsApi(process);
		if (game)
			g_graphicsSeen.insert(exe);
	}
	CloseHandle(process);
	return game;
}

State Detect(HWND hwnd, std::string &exe, HWND &target)
{
	target = hwnd;
	std::string listed;
	const bool foregroundListed = ListedGame(hwnd, &listed);
	if (foregroundListed) {
		g_focusGameHwnd = hwnd;
		// tracked whatever keep focused says, so turning it on mid session has a target at once
		g_stickyHwnd = hwnd;
	}
	if (g_policy.keepFocused) {
		if (foregroundListed) {
			exe = listed;
			return State::Window;
		}
		if (ListedGame(g_stickyHwnd, &exe)) {
			target = g_stickyHwnd;
			return State::Window;
		}
		g_stickyHwnd = nullptr;
	}
	if (foregroundListed) {
		exe = listed;
		return State::Window;
	}
	if (ValidWindow(hwnd)) {
		const std::string name = ExeOfWindow(hwnd);
		if (!name.empty() && g_windowFallback.count(name)) {
			exe = name;
			return State::Window;
		}
	}
	if (FullscreenGame(hwnd, exe))
		return State::Game;
	if (g_policy.focusCard && AwayFromListedGame(hwnd, exe))
		return State::Away;
	exe.clear();
	return State::Desktop;
}

void Announce(State state, const std::string &exe)
{
	if (state == State::Away)
		blog(LOG_INFO, "[replaykit] capture: %s out of focus, showing '%s'", exe.empty() ? "unknown" : exe.c_str(), kFocusCardSourceName);
	else
		blog(LOG_INFO, "[replaykit] capture: desktop, showing '%s'", kDisplayCaptureSourceName);
}

void HookCheck()
{
	if (g_pendingExe.empty())
		return;
	// the same game has to be in front, so a game left behind is never marked blocked; a popup that has the focus right then only defers the check, and a real switch cancels it
	const HWND foreground = GetForegroundWindow();
	if (ExeOfWindow(foreground) != g_pendingExe) {
		g_hookTimer->start(kHookCheckMs);
		return;
	}
	if (GameCaptureLive()) {
		g_pendingExe.clear();
		g_pendingAttempts = 0;
		return;
	}
	if (++g_pendingAttempts < kHookChecks) {
		g_hookTimer->start(kHookCheckMs);
		return;
	}
	blog(LOG_INFO, "[replaykit] capture: game capture got no frame from '%s' after %d checks, falling back to window capture", g_pendingExe.c_str(), kHookChecks);
	g_hookBlocked.insert(g_pendingExe);
	g_windowFallback.insert(g_pendingExe);
	g_pendingExe.clear();
	g_pendingAttempts = 0;
	g_lastState = State::Window;
	ApplySwitch(State::Window, foreground);
}

void DesktopSwitch()
{
	if (!g_policy.known || !g_policy.hybrid)
		return;
	std::string exe;
	HWND target = nullptr;
	const State state = Detect(GetForegroundWindow(), exe, target);
	// the capture target came back before the delay ran out
	if (state != State::Desktop && state != State::Away)
		return;
	CancelHookCheck();
	g_lastState = state;
	ApplySwitch(state);
	Announce(state, exe);
}

struct PrimaryMonitor {
	std::string id, adapter;
};

PrimaryMonitor PrimaryMonitorIds()
{
	PrimaryMonitor primary;
	MONITORINFOEXW info{};
	info.cbSize = sizeof(info);
	if (!GetMonitorInfoW(MonitorFromWindow(nullptr, MONITOR_DEFAULTTOPRIMARY), &info))
		return primary;
	primary.adapter = Utf8(info.szDevice, int(wcslen(info.szDevice)));
	DISPLAY_DEVICEW device{};
	device.cb = sizeof(device);
	if (EnumDisplayDevicesW(info.szDevice, 0, &device, EDD_GET_DEVICE_INTERFACE_NAME))
		primary.id = Utf8(device.DeviceID, int(wcslen(device.DeviceID)));
	return primary;
}

void RepairDisplayCapture()
{
	const ULONGLONG now = GetTickCount64();
	if (now < g_monitorCheckAt)
		return;
	obs_source_t *source = obs_get_source_by_name(kDisplayCaptureSourceName);
	if (!source) {
		g_monitorCheckAt = now + 1000;
		return;
	}
	g_monitorCheckAt = now + kMonitorCheckMs;
	obs_data_t *settings = obs_source_get_settings(source);
	// the properties of the kind, since those of the live source run the modified callbacks of obs and reset display capture
	obs_properties_t *properties = obs_get_source_properties(obs_source_get_id(source));
	obs_property_t *property = properties ? obs_properties_get(properties, "monitor_id") : nullptr;
	if (settings && property) {
		std::vector<std::string> choices;
		for (size_t i = 0; i < obs_property_list_item_count(property); ++i) {
			const char *value = obs_property_list_item_string(property, i);
			if (value && *value && !obs_property_list_item_disabled(property, i))
				choices.emplace_back(value);
		}
		auto find = [&choices](const std::string &value) -> std::string {
			if (value.empty())
				return {};
			for (const std::string &choice : choices)
				if (Lower(choice) == Lower(value))
					return choice;
			return {};
		};
		auto primaryChoice = [&find]() {
			const PrimaryMonitor primary = PrimaryMonitorIds();
			const std::string byId = find(primary.id);
			return byId.empty() ? find(primary.adapter) : byId;
		};
		auto resolve = [&](const std::string &wanted) { return wanted.empty() || wanted == "primary" ? primaryChoice() : find(wanted); };

		const std::string selected = obs_data_get_string(settings, "monitor_id");
		std::string wanted = obs_data_get_string(settings, kWantedMonitorKey);
		bool wantedChanged = false;
		if (wanted.empty()) {
			// the installer sets up the primary display, and a source from before this key may hold a fallback saved over it
			wanted = "primary";
			wantedChanged = true;
		} else if (g_appliedMonitor && selected != g_lastAppliedMonitor && !find(selected).empty() && resolve(wanted) != find(selected)) {
			// someone picked a display in the properties of the source, and that is the one to keep from now on
			wanted = find(selected);
			wantedChanged = true;
		}
		const std::string desired = resolve(wanted);
		// a wanted display that is gone leaves the capture where it is, or on the primary when that is gone too, and it goes back by itself once the display is back
		std::string target = !desired.empty() ? desired : !find(selected).empty() ? find(selected) : primaryChoice();
		if (target.empty()) {
			if (!g_monitorWarned || g_monitorWarning != selected) {
				g_monitorWarned = true;
				g_monitorWarning = selected;
				blog(LOG_WARNING, "[replaykit] capture: Display Capture has no matching active monitor, choose a display in its properties");
			}
		} else {
			g_monitorWarned = false;
			bool switching = find(selected) != target;
			if (!switching) {
				g_pendingMonitor.clear();
				g_pendingMonitorChecks = 0;
			} else if (g_appliedMonitor) {
				// the first check after obs starts switches at once, every later switch waits to see the change hold
				g_pendingMonitorChecks = g_pendingMonitor == target ? g_pendingMonitorChecks + 1 : 1;
				g_pendingMonitor = target;
				if (g_pendingMonitorChecks < kMonitorChecksToSwitch) {
					switching = false;
					target = selected;
				} else {
					g_pendingMonitor.clear();
					g_pendingMonitorChecks = 0;
				}
			}
			if (wantedChanged)
				obs_data_set_string(settings, kWantedMonitorKey, wanted.c_str());
			if (switching || wantedChanged) {
				if (switching)
					obs_data_set_string(settings, "monitor_id", target.c_str());
				obs_source_update(source, settings);
				if (switching)
					blog(LOG_INFO, target == desired ? "[replaykit] capture: Display Capture is on its chosen display"
									 : "[replaykit] capture: the chosen display of Display Capture is missing, showing another one until it is back");
			}
			g_lastAppliedMonitor = target;
			g_appliedMonitor = true;
		}
	}
	if (properties)
		obs_properties_destroy(properties);
	obs_data_release(settings);
	obs_source_release(source);
}

void ResetSwitching()
{
	CancelHookCheck();
	CancelDesktopSwitch();
	g_lastState = State::None;
	g_lastWindowToken.clear();
	ResetStall();
	g_stickyHwnd = nullptr;
	g_focusGameHwnd = nullptr;
}

void Check()
{
	RepairDisplayCapture();
	if (!g_policy.known)
		return;
	if (!g_policy.hybrid) {
		ResetSwitching();
		HideFocusCard();
		return;
	}
	std::string exe;
	HWND target = nullptr;
	const State state = Detect(GetForegroundWindow(), exe, target);

	if (state == State::Window) {
		CancelHookCheck();
		CancelDesktopSwitch();
		if (g_lastState != State::Window || PointWindowCapture(target)) {
			if (g_lastState != State::Window) {
				blog(LOG_INFO, "[replaykit] capture: %s, showing '%s'", exe.empty() ? "unknown" : exe.c_str(), kWindowCaptureSourceName);
				ResetStall();
			}
			g_lastState = State::Window;
			ApplySwitch(State::Window, target);
		}
		// a window capture session can die silently when the target changes its display mode right as it starts, and the memo then blocks every push, so the source sits at 0x0 until something makes it again; a few dead checks clear the memo and push it once more
		if (g_lastState == State::Window) {
			if (WindowCaptureLive()) {
				ResetStall();
			} else if (++g_stallCount >= g_stallLimit) {
				g_stallCount = 0;
				g_lastWindowToken.clear();
				// written down on the first try and each time the wait grows; a capture that stays dead for hours would fill the log otherwise
				const int grown = (std::min)(g_stallLimit * 2, kWindowStallMaxTicks);
				if (g_stallLimit == kWindowStallTicks || grown != g_stallLimit)
					blog(LOG_INFO, "[replaykit] capture: window capture stalled at 0x0, starting it again (next try in %d s if it stays dead)", grown * kCheckMs / 1000);
				g_stallLimit = grown;
				PointWindowCapture(target);
			}
		}
		return;
	}
	if (state == State::Game) {
		CancelDesktopSwitch();
		if (g_lastState != State::Game) {
			g_lastState = State::Game;
			ApplySwitch(State::Game, target);
			g_pendingExe = exe;
			g_pendingAttempts = 0;
			g_hookTimer->start(kHookFirstCheckMs);
			blog(LOG_INFO, "[replaykit] capture: %s, showing '%s' (waiting for its hook)", exe.c_str(), kGameCaptureSourceName);
		}
		return;
	}
	// leaving a game for the desktop or the card waits out the delay, so a quick alt tab or a popup does not flip the capture; the card gives way to the desktop at once when the game is closed
	if (g_lastState == State::Game || g_lastState == State::Window) {
		if (!g_desktopTimer->isActive())
			g_desktopTimer->start(g_policy.graceMs);
		return;
	}
	if (g_lastState != state) {
		CancelDesktopSwitch();
		g_lastState = state;
		ApplySwitch(state);
		Announce(state, exe);
	}
}

QTimer *SingleShot(void (*run)())
{
	QTimer *timer = new QTimer(g_callbacks);
	timer->setSingleShot(true);
	QObject::connect(timer, &QTimer::timeout, g_callbacks, run);
	return timer;
}

} // namespace

void CaptureSwitchStart()
{
	if (g_checkTimer || !g_callbacks)
		return;
	g_hookTimer = SingleShot(HookCheck);
	g_desktopTimer = SingleShot(DesktopSwitch);
	g_checkTimer = new QTimer(g_callbacks);
	QObject::connect(g_checkTimer, &QTimer::timeout, g_callbacks, Check);
	g_checkTimer->start(kCheckMs);
	RepairDisplayCapture();
}

void CaptureSwitchApplySettings(const std::string &settingsBody)
{
	const QJsonValue value = JsonBodyObject(settingsBody).value(QStringLiteral("settings"));
	if (!value.isObject())
		return;
	const QJsonObject settings = value.toObject();
	static const QStringList modes = {QStringLiteral("hybrid_auto"), QStringLiteral("desktop"), QStringLiteral("game_auto"), QStringLiteral("game_window")};
	const QString mode = settings.value(QStringLiteral("screenshareCaptureMode")).toString();
	const QJsonValue keepFocused = settings.value(QStringLiteral("screenshareAutoGameKeepFocused"));
	const QJsonValue focusCard = settings.value(QStringLiteral("screenshareAutoGameOutOfFocus"));
	if (!modes.contains(mode) || !keepFocused.isBool() || !focusCard.isBool())
		return;
	Policy policy;
	policy.known = true;
	policy.hybrid = mode == QLatin1String("hybrid_auto");
	policy.focusCard = focusCard.toBool();
	// the card stands in for the game whenever it is not in front, so keeping the game shown out of focus only applies with the card off
	policy.keepFocused = keepFocused.toBool() && !policy.focusCard;
	// the page stores the delay in seconds, down to 0.05
	const int graceMs = int(std::lround(settings.value(QStringLiteral("screenshareSwitchDelaySeconds")).toDouble() * 1000.0));
	policy.graceMs = graceMs >= kMinGraceMs && graceMs <= kMaxGraceMs ? graceMs : kDefaultGraceMs;
	static const QRegularExpression exeName(QStringLiteral("^[^\\\\/:*?\"<>|]+\\.exe$"));
	const QJsonArray overrides = settings.value(QStringLiteral("screenshareGameOverrides")).toArray();
	for (int i = 0; i < std::min<int>(overrides.size(), 32); ++i) {
		const std::string exe = Lower(overrides.at(i).toObject().value(QStringLiteral("exeName")).toString().toStdString());
		if (exeName.match(QString::fromStdString(exe)).hasMatch() && !kNeverListed.count(exe) && exe != Lower(QString::fromWCharArray(kShareWindowExeName).toStdString()))
			policy.autoGames.insert(exe);
	}
	g_policy = policy;
}

void CaptureSwitchStop()
{
	if (g_checkTimer)
		g_checkTimer->stop();
	if (g_hookTimer)
		g_hookTimer->stop();
	if (g_desktopTimer)
		g_desktopTimer->stop();
}

} // namespace rk
