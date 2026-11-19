// global hotkey that opens or closes the clips window
#include "prelude.hpp"
#include "core/open_clips_hotkey.hpp"

#include <array>

#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "windows/clips_window.hpp"

namespace rk {

namespace {

// the saved binding and the one extra binding of the settings page, each its own windows hotkey
constexpr int kFirstHotkeyId = 0x524B;
struct Binding {
	UINT key = 0;
	UINT modifiers = 0;
	bool operator==(const Binding &other) const { return key == other.key && modifiers == other.modifiers; }
};
std::array<Binding, 4> bindings;
QAbstractNativeEventFilter *g_openClipsHotkeyFilter = nullptr;
std::string g_openClipsHotkeyBinding;
std::vector<std::pair<obs_hotkey_id, obs_data_array_t *>> savedHotkeys;
std::atomic<bool> capturing{false};
unsigned captureGeneration = 0;
// the key of a press that is still held: its auto-repeats are taken as hotkeys and dropped, since with no-repeat they went to the window in front, where an alt combo with no menu behind it sounds the windows default beep
UINT g_openClipsHeldKey = 0;
QPointer<QTimer> g_openClipsReleaseTimer;

void WatchOpenClipsRelease(UINT key)
{
	g_openClipsHeldKey = key;
	if (!g_openClipsReleaseTimer) {
		g_openClipsReleaseTimer = new QTimer(qApp);
		g_openClipsReleaseTimer->setInterval(30);
		QObject::connect(g_openClipsReleaseTimer, &QTimer::timeout, []() {
			if (GetAsyncKeyState((int)g_openClipsHeldKey) & 0x8000)
				return;
			g_openClipsHeldKey = 0;
			g_openClipsReleaseTimer->stop();
		});
	}
	g_openClipsReleaseTimer->start();
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

class OpenClipsHotkeyFilter : public QAbstractNativeEventFilter {
public:
	bool nativeEventFilter(const QByteArray &, void *message, qintptr *) override
	{
		MSG *msg = static_cast<MSG *>(message);
		if (!msg || msg->message != WM_HOTKEY || msg->wParam < kFirstHotkeyId || msg->wParam >= kFirstHotkeyId + bindings.size())
			return false;
		const Binding &binding = bindings[msg->wParam - kFirstHotkeyId];
		if (capturing.load() || binding.key != HIWORD(msg->lParam) || binding.modifiers != LOWORD(msg->lParam) || g_openClipsHeldKey)
			return true;
		ToggleClips();
		WatchOpenClipsRelease(HIWORD(msg->lParam));
		return true;
	}
};

bool ParseBinding(const std::string &json, Binding &binding)
{
	std::string keyName = ExtractJsonStringField(json, "key");
	if (keyName.empty())
		return true;
	binding.key = OpenClipsVirtualKey(keyName);
	if (JsonBoolField(json, "control", false)) binding.modifiers |= MOD_CONTROL;
	if (JsonBoolField(json, "alt", false)) binding.modifiers |= MOD_ALT;
	if (JsonBoolField(json, "shift", false)) binding.modifiers |= MOD_SHIFT;
	if (JsonBoolField(json, "command", false)) binding.modifiers |= MOD_WIN;
	return binding.key != 0;
}

bool InstallBinding(size_t slot, const Binding &binding)
{
	if (RegisterHotKey(nullptr, kFirstHotkeyId + int(slot), binding.modifiers, binding.key))
		return true;
	blog(LOG_WARNING, "[replaykit] could not register Open Clips hotkey (error=%lu)", GetLastError());
	return false;
}

void RemoveBinding(size_t slot)
{
	UnregisterHotKey(nullptr, kFirstHotkeyId + int(slot));
}

void CaptureState(void *, calldata_t *cd)
{
	calldata_set_bool(cd, "active", capturing.load());
}

} // namespace

QPointer<QTimer> g_openClipsHotkeyTimer;

bool RegisterOpenClipsHotkey(const std::string &settingsBody)
{
	if (capturing.load())
		return false;
	std::string binding = ExtractJsonObjectField(settingsBody, "openClipsKeybind");
	std::string extra = ExtractJsonObjectField(settingsBody, "openClipsKeybindExtra");
	if (binding.empty() && extra.empty())
		return false;
	std::string signature = binding + "|" + extra;
	if (signature == g_openClipsHotkeyBinding)
		return true;
	std::array<Binding, 2> wanted{};
	if (!ParseBinding(binding, wanted[0]) || !ParseBinding(extra, wanted[1]))
		return false;
	auto staged = bindings;
	for (const auto &combo : wanted) {
		if (!combo.key || std::find(staged.begin(), staged.end(), combo) != staged.end())
			continue;
		auto slot = std::find(staged.begin(), staged.end(), Binding{});
		if (slot == staged.end() || !InstallBinding(size_t(slot - staged.begin()), combo)) {
			for (size_t i = 0; i < staged.size(); ++i)
				if (staged[i].key && !bindings[i].key)
					RemoveBinding(i);
			return false;
		}
		*slot = combo;
	}
	for (size_t i = 0; i < staged.size(); ++i) {
		if (staged[i].key && std::find(wanted.begin(), wanted.end(), staged[i]) == wanted.end()) {
			RemoveBinding(i);
			staged[i] = {};
		}
	}
	bindings = staged;
	g_openClipsHotkeyBinding = signature;
	return true;
}

bool SetHotkeyCapture(bool active)
{
	const unsigned generation = ++captureGeneration;
	if (!active) {
		if (!capturing.load())
			return true;
		for (const auto &entry : savedHotkeys) {
			obs_hotkey_load(entry.first, entry.second);
			obs_data_array_release(entry.second);
		}
		savedHotkeys.clear();
		for (size_t i = 0; i < bindings.size(); ++i) {
			if (bindings[i].key && !InstallBinding(i, bindings[i])) {
				bindings[i] = {};
				g_openClipsHotkeyBinding.clear();
			}
		}
		capturing.store(false);
		return true;
	}
	if (!g_callbacks || !g_openClipsHotkeyFilter)
		return false;
	if (!capturing.exchange(true)) {
		std::vector<obs_hotkey_id> ids;
		obs_enum_hotkeys([](void *data, obs_hotkey_id id, obs_hotkey_t *hotkey) {
			const char *raw = obs_hotkey_get_name(hotkey);
			if (!raw)
				return true;
			const std::string name(raw);
			if (name == "ReplayBuffer.Save" || name == "OBSBasic.SaveReplayBuffer" || name == "ReplayKit.SaveClip" ||
			    name == "OBSBasic.StartRecording" || name == "OBSBasic.StopRecording" || name == "OBSBasic.Screenshot")
				static_cast<std::vector<obs_hotkey_id> *>(data)->push_back(id);
			return true;
		}, &ids);
		for (const auto id : ids)
			savedHotkeys.emplace_back(id, obs_hotkey_save(id));
		obs_data_array_t *empty = obs_data_array_create();
		for (const auto &entry : savedHotkeys)
			obs_hotkey_load(entry.first, empty);
		obs_data_array_release(empty);
		for (size_t i = 0; i < bindings.size(); ++i)
			if (bindings[i].key)
				RemoveBinding(i);
	}
	QTimer::singleShot(15000, g_callbacks, [generation]() {
		if (generation == captureGeneration)
			SetHotkeyCapture(false);
	});
	return true;
}

// puts the native filter in place that turns the registered hotkey into a clips toggle
void InstallOpenClipsHotkeyFilter()
{
	proc_handler_add(obs_get_proc_handler(), "void replaykit_hotkey_capture_active(out bool active)", CaptureState, nullptr);
	g_openClipsHotkeyFilter = new OpenClipsHotkeyFilter();
	qApp->installNativeEventFilter(g_openClipsHotkeyFilter);
}

// takes the hotkey, its poll timer and the native filter down before this module is unloaded
void RemoveOpenClipsHotkey()
{
	if (g_openClipsHotkeyTimer)
		g_openClipsHotkeyTimer->stop();
	delete g_openClipsReleaseTimer;
	g_openClipsHeldKey = 0;
	SetHotkeyCapture(false);
	for (size_t i = 0; i < bindings.size(); ++i)
		if (bindings[i].key)
			RemoveBinding(i);
	bindings = {};
	g_openClipsHotkeyBinding.clear();
	if (g_openClipsHotkeyFilter) {
		qApp->removeNativeEventFilter(g_openClipsHotkeyFilter);
		delete g_openClipsHotkeyFilter;
		g_openClipsHotkeyFilter = nullptr;
	}
}

} // namespace rk
