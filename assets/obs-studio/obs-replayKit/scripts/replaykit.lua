-- replaykit.lua single obs-facing entrypoint for obs replaykit. obs only shows this script in tools -> scripts. feature scripts remain split into focused files and are loaded here in isolated environments so their script_load/update/save/unload callbacks do not collide globally.

local obs = obslua

local SCRIPT_ROOT = (script_path() or ""):gsub("\\", "/")
if SCRIPT_ROOT ~= "" and SCRIPT_ROOT:sub(-1) ~= "/" then
    SCRIPT_ROOT = SCRIPT_ROOT .. "/"
end

local MODULES = {
    { id = "elevation",     path = "obs_elevation/auto_elevate_obs.lua" },
    { id = "audio",         path = "audio/auto_pick_monitor_device.lua" },
    { id = "capture",       path = "capture/auto_capture_switch.lua" },
    { id = "replay_start",  path = "replay_buffer/auto_start_replay_buffer.lua" },
    { id = "replay_sound",  path = "replay_buffer/replay_buffer_save_beep.lua" },
    { id = "replay_popup",  path = "replay_buffer/clip_saved_notification.lua" },
    { id = "streamable",    path = "streamable/streamable_upload.lua" },
    { id = "virtual_cam",   path = "virtual_camera/auto_start_virtual_camera.lua" },
}

local loaded = {}

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

function script_description()
    return [[<b>OBS ReplayKit</b><br><br>
Single OBS-facing script for the ReplayKit setup. It starts the replay
buffer, routes OBS monitoring, switches capture sources for fullscreen
games, starts the virtual camera, plays the replay-saved sound, handles
OBS elevation, and launches the Streamable helper.<br><br>
The implementation stays split into internal feature files so the OBS
Scripts panel stays clean without turning the codebase into one giant file.]]
end

function script_properties()
    local props = obs.obs_properties_create()

    obs.obs_properties_add_text(
        props, "game_source",
        "Game Capture source name",
        obs.OBS_TEXT_DEFAULT
    )
    obs.obs_properties_add_text(
        props, "display_source",
        "Display Capture source name",
        obs.OBS_TEXT_DEFAULT
    )
    obs.obs_properties_add_text(
        props, "scene_name",
        "Scene to apply capture switching to (blank = all)",
        obs.OBS_TEXT_DEFAULT
    )
    obs.obs_properties_add_int(
        props, "check_interval",
        "Capture switch check interval (ms)",
        100, 5000, 100
    )
    obs.obs_properties_add_bool(
        props, "verbose",
        "Verbose capture-switch logging"
    )
    obs.obs_properties_add_path(
        props,
        "audio_file",
        "Replay saved sound",
        obs.OBS_PATH_FILE,
        "Audio Files (*.wav *.mp3 *.m4a *.aac *.wma *.mid *.midi);;All Files (*.*)",
        SCRIPT_ROOT .. "replay_buffer"
    )
    obs.obs_properties_add_path(
        props,
        "clip_dir",
        "Clip folder (blank = ~\\Pictures\\Videos)",
        obs.OBS_PATH_DIRECTORY,
        "",
        ""
    )
    obs.obs_properties_add_bool(
        props,
        "clip_notification_enabled",
        "Show replay saved popup"
    )
    obs.obs_properties_add_int(
        props,
        "clip_notification_seconds",
        "Replay saved popup seconds",
        1, 600, 1
    )

    return props
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "game_source", "Game Capture")
    obs.obs_data_set_default_string(settings, "display_source", "Display Capture")
    obs.obs_data_set_default_string(settings, "scene_name", "")
    obs.obs_data_set_default_int(settings, "check_interval", 500)
    obs.obs_data_set_default_bool(settings, "verbose", false)
    obs.obs_data_set_default_string(settings, "audio_file", "")
    obs.obs_data_set_default_string(settings, "clip_dir", "")
    obs.obs_data_set_default_bool(settings, "clip_notification_enabled", true)
    obs.obs_data_set_default_int(settings, "clip_notification_seconds", 90)
    call_all("script_defaults", settings)
end

function script_update(settings)
    call_all("script_update", settings)
end

function script_load(settings)
    call_all("script_load", settings)
end

function script_save(settings)
    call_all("script_save", settings)
end

function script_unload()
    call_all_reverse("script_unload")
end

function script_tick(seconds)
    call_all("script_tick", seconds)
end
