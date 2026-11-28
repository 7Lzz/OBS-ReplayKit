// the replaykit entries of the obs Tools menu and the hotkey that uploads the latest clip
#include "prelude.hpp"
#include "app/tools_actions.hpp"

#include <shellapi.h>

#include "app/clip_buffer.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/locale.hpp"
#include "core/shared_values.hpp"

namespace rk {

namespace {

obs_hotkey_id g_uploadHotkey = OBS_INVALID_HOTKEY_ID;
bool g_menuAdded = false;

void PostToHelper(const char *path)
{
	const std::string route(path);
	RunAsync([route]() {
		if (HttpRequest("POST", route.c_str(), nullptr, 3000).empty())
			blog(LOG_WARNING, "[replaykit] tools: the helper did not answer %s", route.c_str());
	});
}

void UploadLatest()
{
	PostToHelper("/upload-latest");
}

void OnUploadHotkey(void *, obs_hotkey_id, obs_hotkey_t *, bool pressed)
{
	if (pressed)
		UploadLatest();
}

void OnSave(obs_data_t *modules, bool saving, void *)
{
	if (g_uploadHotkey == OBS_INVALID_HOTKEY_ID)
		return;
	if (saving) {
		obs_data_t *own = obs_data_create();
		obs_data_array_t *binding = obs_hotkey_save(g_uploadHotkey);
		obs_data_set_array(own, kUploadHotkeyKey, binding);
		obs_data_array_release(binding);
		obs_data_set_obj(modules, kSceneModuleKey, own);
		obs_data_release(own);
		return;
	}
	obs_data_t *own = obs_data_get_obj(modules, kSceneModuleKey);
	obs_data_array_t *binding = own ? obs_data_get_array(own, kUploadHotkeyKey) : nullptr;
	obs_hotkey_load(g_uploadHotkey, binding);
	obs_data_array_release(binding);
	obs_data_release(own);
}

// opens the clips page in the default browser through the shell, the same handler a link click uses
void OpenClipsInBrowser()
{
	const std::wstring url = QString::fromStdString(HelperUrl("/clips-view")).toStdWString();
	if (reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL)) <= 32)
		blog(LOG_WARNING, "[replaykit] tools: the clips page could not be opened in the browser");
}

} // namespace

void ToolsActionsRegister()
{
	if (g_uploadHotkey != OBS_INVALID_HOTKEY_ID)
		return;
	g_uploadHotkey = obs_hotkey_register_frontend("upload_clip_to_streamable", TrMessage("lua.hotkeyUploadLatestClip").toUtf8().constData(), OnUploadHotkey, nullptr);
	obs_frontend_add_save_callback(OnSave, nullptr);
}

void ToolsActionsAddMenu()
{
	if (g_menuAdded)
		return;
	g_menuAdded = true;
	obs_frontend_add_tools_menu_item(TrMessage("lua.toolsSaveClip").toUtf8().constData(), [](void *) { ClipBufferSave(); }, nullptr);
	obs_frontend_add_tools_menu_item(TrMessage("lua.toolsUploadLastClip").toUtf8().constData(), [](void *) { UploadLatest(); }, nullptr);
	obs_frontend_add_tools_menu_item(TrMessage("lua.toolsOpenClips").toUtf8().constData(), [](void *) { OpenClipsInBrowser(); }, nullptr);
	obs_frontend_add_tools_menu_item(TrMessage("lua.toolsOpenClipFolder").toUtf8().constData(), [](void *) { PostToHelper("/open-clip-folder"); }, nullptr);
}

void ToolsActionsUnregister()
{
	obs_frontend_remove_save_callback(OnSave, nullptr);
	if (g_uploadHotkey != OBS_INVALID_HOTKEY_ID) {
		obs_hotkey_unregister(g_uploadHotkey);
		g_uploadHotkey = OBS_INVALID_HOTKEY_ID;
	}
}

} // namespace rk
