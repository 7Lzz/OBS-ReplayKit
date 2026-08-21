-- replaykit.lua single obs-facing entrypoint for obs replaykit. obs only shows this script in tools -> scripts. feature scripts remain split into focused files and are loaded here in isolated environments so their script_load/update/save/unload callbacks do not collide globally.

local obs = obslua

local SCRIPT_ROOT = (script_path() or ""):gsub("\\", "/")
if SCRIPT_ROOT ~= "" and SCRIPT_ROOT:sub(-1) ~= "/" then
    SCRIPT_ROOT = SCRIPT_ROOT .. "/"
end

local MODULES = {
    { id = "elevation",     path = "obs_elevation/auto_elevate_obs.lua" },
    { id = "capture",       path = "capture/auto_capture_switch.lua" },
    { id = "replay_start",  path = "replay_buffer/auto_start_replay_buffer.lua" },
    { id = "replay_sound",  path = "replay_buffer/replay_buffer_save_beep.lua" },
    { id = "replay_popup",  path = "replay_buffer/clip_saved_notification.lua" },
    { id = "recording_hotkey", path = "recording/recording_hotkey.lua" },
    { id = "helper",        path = "helper/helper_bootstrap.lua" },
    { id = "discord_projector", path = "virtual_camera/auto_start_virtual_camera.lua" },
    { id = "popup_shield",  path = "popup_shield/popup_capture_shield.lua" },
    { id = "hevc_tag_fix",  path = "hevc_compat/auto_fix_hevc_tag.lua" },
}

local loaded = {}
local close_warning_signature = nil
local CLOSE_WARNING_SYNC_INTERVAL_MS = 1000

local function dirname(path)
    return path:match("^(.*[/\\])") or ""
end

local function log_error(message)
    obs.script_log(obs.LOG_ERROR, "[ReplayKit] " .. tostring(message))
end

local function load_chunk(path, env)
    if setfenv then
        local chunk, err = loadfile(path)
        if not chunk then return nil, err end
        setfenv(chunk, env)
        return chunk
    end
    return loadfile(path, "bt", env)
end

local function load_feature(spec)
    local full_path = SCRIPT_ROOT .. spec.path
    local module_dir = dirname(full_path)
    local env = {
        obslua = obslua,
        script_path = function()
            return module_dir
        end,
    }
    setmetatable(env, { __index = _G })

    local chunk, err = load_chunk(full_path, env)
    if not chunk then
        log_error(spec.id .. ": " .. tostring(err))
        return
    end

    local ok, run_err = pcall(chunk)
    if not ok then
        log_error(spec.id .. ": " .. tostring(run_err))
        return
    end

    loaded[#loaded + 1] = {
        id = spec.id,
        env = env,
    }
end

for _, spec in ipairs(MODULES) do
    load_feature(spec)
end

local function call_feature(feature, callback, ...)
    local fn = rawget(feature.env, callback)
    if type(fn) ~= "function" then return end
    local ok, err = pcall(fn, ...)
    if not ok then
        log_error(feature.id .. "." .. callback .. ": " .. tostring(err))
    end
end

local function call_all(callback, ...)
    for _, feature in ipairs(loaded) do
        call_feature(feature, callback, ...)
    end
end

local function call_all_reverse(callback, ...)
    for i = #loaded, 1, -1 do
        call_feature(loaded[i], callback, ...)
    end
end

local function read_text(path)
    local file = io.open(path, "rb")
    if not file then return nil end
    local text = file:read("*a")
    file:close()
    return text
end

local function read_disable_obs_close_warning()
    local text = read_text(SCRIPT_ROOT .. "replaykit_settings.json")
    if not text or text == "" then return true end
    if not text:find('"disableObsCloseWarning"%s*:') then return true end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then return true end
    local disabled = obs.obs_data_get_bool(data, "disableObsCloseWarning")
    obs.obs_data_release(data)
    return disabled
end

local function set_confirm_on_exit(config, confirm_on_exit)
    if config == nil or type(obs.config_set_bool) ~= "function" then return false end
    obs.config_set_bool(config, "General", "ConfirmOnExit", confirm_on_exit)
    return true
end

local function apply_obs_close_warning_setting(force)
    local confirm_on_exit = not read_disable_obs_close_warning()
    local signature = tostring(confirm_on_exit)
    if not force and close_warning_signature == signature then return end
    close_warning_signature = signature

    local applied = false
    if type(obs.obs_frontend_get_user_config) == "function" then
        applied = set_confirm_on_exit(obs.obs_frontend_get_user_config(), confirm_on_exit) or applied
    end
    if type(obs.obs_frontend_get_app_config) == "function" then
        applied = set_confirm_on_exit(obs.obs_frontend_get_app_config(), confirm_on_exit) or applied
    end
    if not applied and type(obs.obs_frontend_get_global_config) == "function" then
        applied = set_confirm_on_exit(obs.obs_frontend_get_global_config(), confirm_on_exit) or applied
    end
end

local function sync_obs_close_warning_setting()
    apply_obs_close_warning_setting(false)
end

function script_description()
    return [[<b>OBS ReplayKit</b><br><br>
Single OBS-facing script for the ReplayKit setup. It starts clipping
capture, keeps OBS monitoring off, switches capture sources for fullscreen
games, opens and parks the Discord projector, plays the clip-saved sound, handles
OBS elevation, syncs recording hotkeys, and launches the ReplayKit
helper.<br><br>
The implementation stays split into internal feature files so the OBS
Scripts panel stays clean without turning the codebase into one giant file.]]
end

function script_properties()
    return obs.obs_properties_create()
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "game_source", "Game Capture")
    obs.obs_data_set_default_string(settings, "display_source", "Display Capture")
    obs.obs_data_set_default_string(settings, "window_source", "Window Capture")
    obs.obs_data_set_default_string(settings, "scene_name", "")
    obs.obs_data_set_default_int(settings, "check_interval", 500)
    obs.obs_data_set_default_bool(settings, "verbose", false)
    obs.obs_data_set_default_string(settings, "audio_file", "")
    obs.obs_data_set_default_string(settings, "recording_start_audio_file", "")
    obs.obs_data_set_default_string(settings, "recording_stop_audio_file", "")
    obs.obs_data_set_default_string(settings, "clip_dir", "")
    obs.obs_data_set_default_bool(settings, "clip_notification_enabled", true)
    obs.obs_data_set_default_bool(settings, "recording_notification_enabled", true)
    obs.obs_data_set_default_int(settings, "clip_notification_seconds", 90)
    call_all("script_defaults", settings)
end

function script_update(settings)
    apply_obs_close_warning_setting(true)
    call_all("script_update", settings)
end

function script_load(settings)
    apply_obs_close_warning_setting(true)
    obs.timer_add(sync_obs_close_warning_setting, CLOSE_WARNING_SYNC_INTERVAL_MS)
    call_all("script_load", settings)
end

function script_save(settings)
    call_all("script_save", settings)
end

function script_unload()
    obs.timer_remove(sync_obs_close_warning_setting)
    call_all_reverse("script_unload")
end

function script_tick(seconds)
    call_all("script_tick", seconds)
end
