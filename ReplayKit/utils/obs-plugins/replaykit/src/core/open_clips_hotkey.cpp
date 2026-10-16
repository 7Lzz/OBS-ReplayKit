// global hotkey that opens or closes the clips window
#include "prelude.hpp"
#include "core/open_clips_hotkey.hpp"

#include "core/json_lite.hpp"
#include "windows/clips_window.hpp"

namespace rk {

namespace {

// the saved binding and the one extra binding of the settings page, each its own windows hotkey
constexpr int kOpenClipsHotkeyIds[2] = {0x524B, 0x524C};
bool g_openClipsHotkeyRegistered[2] = {false, false};
QAbstractNativeEventFilter *g_openClipsHotkeyFilter = nullptr;
std::string g_openClipsHotkeyBinding;

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
		if (!msg || msg->message != WM_HOTKEY || (msg->wParam != (WPARAM)kOpenClipsHotkeyIds[0] && msg->wParam != (WPARAM)kOpenClipsHotkeyIds[1]))
			return false;
		ToggleClips();
		return true;
	}
};

void RegisterOneOpenClipsHotkey(int slot, const std::string &binding)
{
	std::string keyName = ExtractJsonStringField(binding, "key");
	if (keyName.empty())
		return;
	UINT key = OpenClipsVirtualKey(keyName);
	if (key == 0) {
		blog(LOG_WARNING, "[replaykit] Open Clips hotkey uses an unsupported key");
		return;
	}
	UINT modifiers = MOD_NOREPEAT;
	if (JsonBoolField(binding, "control", false)) modifiers |= MOD_CONTROL;
	if (JsonBoolField(binding, "alt", false)) modifiers |= MOD_ALT;
	if (JsonBoolField(binding, "shift", false)) modifiers |= MOD_SHIFT;
	if (JsonBoolField(binding, "command", false)) modifiers |= MOD_WIN;
	if (!RegisterHotKey(nullptr, kOpenClipsHotkeyIds[slot], modifiers, key)) {
		blog(LOG_WARNING, "[replaykit] Could not register Open Clips hotkey %d (error=%lu)", slot + 1, GetLastError());
		return;
	}
	g_openClipsHotkeyRegistered[slot] = true;
	blog(LOG_INFO, "[replaykit] Open Clips hotkey %d registered", slot + 1);
}

void UnregisterOpenClipsHotkeys()
{
	for (int slot = 0; slot < 2; slot++) {
		if (g_openClipsHotkeyRegistered[slot])
			UnregisterHotKey(nullptr, kOpenClipsHotkeyIds[slot]);
		g_openClipsHotkeyRegistered[slot] = false;
	}
}

} // namespace

QPointer<QTimer> g_openClipsHotkeyTimer;

void RegisterOpenClipsHotkey(const std::string &settingsBody)
{
	std::string binding = ExtractJsonObjectField(settingsBody, "openClipsKeybind");
	std::string extra = ExtractJsonObjectField(settingsBody, "openClipsKeybindExtra");
	if (binding.empty() && extra.empty())
		return;
	std::string signature = binding + "|" + extra;
	if (signature == g_openClipsHotkeyBinding)
		return;
	g_openClipsHotkeyBinding = signature;

	UnregisterOpenClipsHotkeys();
	RegisterOneOpenClipsHotkey(0, binding);
	// the same combo twice would only fail the second registration
	if (extra != binding)
		RegisterOneOpenClipsHotkey(1, extra);
}

// puts the native filter in place that turns the registered hotkey into a clips toggle
void InstallOpenClipsHotkeyFilter()
{
	g_openClipsHotkeyFilter = new OpenClipsHotkeyFilter();
	qApp->installNativeEventFilter(g_openClipsHotkeyFilter);
}

// takes the hotkey, its poll timer and the native filter down before this module is unloaded
void RemoveOpenClipsHotkey()
{
	if (g_openClipsHotkeyTimer)
		g_openClipsHotkeyTimer->stop();
	UnregisterOpenClipsHotkeys();
	if (g_openClipsHotkeyFilter) {
		qApp->removeNativeEventFilter(g_openClipsHotkeyFilter);
		delete g_openClipsHotkeyFilter;
		g_openClipsHotkeyFilter = nullptr;
	}
}

} // namespace rk
