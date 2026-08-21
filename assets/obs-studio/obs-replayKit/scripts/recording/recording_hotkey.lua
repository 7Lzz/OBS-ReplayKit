-- synchronizes ReplayKits saved clip and recording keybinds into OBSs native hotkeys when the saved keys change.

local obs = obslua
local ffi_ok, ffi = pcall(require, "ffi")

local REFRESH_SECONDS = 1.0
local MAX_SETTINGS_BYTES = 65536
local CLIP_HOTKEY_NAMES = { "ReplayBuffer.Save" }
local RECORDING_HOTKEY_NAMES = {
    "OBSBasic.StartRecording",
    "OBSBasic.StopRecording",
}

local last_signature = nil
local refresh_elapsed = 0
local native_warned = false
local missing_warned = {}
local obsffi = nil

local function log(level, msg)
    obs.script_log(level, "[ReplayKit Hotkey Sync] " .. tostring(msg))
end

local function ensure_hotkey_ffi()
    if obsffi ~= nil then return true end
    if not ffi_ok then return false end

    if not _G.__replaykit_hotkey_sync_ffi_defined then
        pcall(function()
            ffi.cdef[[
                typedef struct rk_obs_hotkey rk_obs_hotkey_t;
                typedef size_t rk_obs_hotkey_id;
                typedef bool (*rk_obs_hotkey_enum_func)(void *data, rk_obs_hotkey_id id, rk_obs_hotkey_t *key);
                const char *obs_hotkey_get_name(const rk_obs_hotkey_t *key);
                void obs_enum_hotkeys(rk_obs_hotkey_enum_func func, void *data);
            ]]
        end)
        _G.__replaykit_hotkey_sync_ffi_defined = true
    end

    local ok, lib = pcall(ffi.load, "obs")
    if not ok then ok, lib = pcall(ffi.load, "obs.dll") end
    if not ok then return false end

    obsffi = lib
    return true
end

local function normalize_path(path)
    return (path or ""):gsub("\\", "/")
end

local function parent_dir(path)
    path = normalize_path(path):gsub("/+$", "")
    return path:match("^(.*)/[^/]+$") or path
end

local function join_path(a, b)
    a = normalize_path(a):gsub("/+$", "")
    b = normalize_path(b):gsub("^/+", "")
    if a == "" then return b end
    return a .. "/" .. b
end

local function read_text(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local text = f:read(MAX_SETTINGS_BYTES + 1)
    f:close()
    if text and #text > MAX_SETTINGS_BYTES then return nil end
    if text then
        text = text:gsub("^\239\187\191", "")
    end
    return text
end

local function runtime_settings_path()
    return join_path(parent_dir(script_path()), "replaykit_settings.json")
end

local function valid_obs_key(key)
    return type(key) == "string"
        and #key >= 9
        and #key <= 56
        and key:match("^OBS_KEY_[A-Z0-9_]+$") ~= nil
end

local function read_combo(data, key_name)
    if not obs.obs_data_has_user_value(data, key_name)
        or type(obs.obs_data_get_obj) ~= "function" then
        return {}
    end

    local combo = {}
    local item = obs.obs_data_get_obj(data, key_name)
    if item ~= nil then
        local key = obs.obs_data_get_string(item, "key")
        if valid_obs_key(key) then
            combo.key = key
            for _, mod in ipairs({ "control", "alt", "shift", "command" }) do
                if obs.obs_data_has_user_value(item, mod) and obs.obs_data_get_bool(item, mod) then
                    combo[mod] = true
                end
            end
        end
        obs.obs_data_release(item)
    end
    return combo
end

local function load_runtime_settings()
    local text = read_text(runtime_settings_path())
    if not text or text == "" then return { clip = {}, recording = {} } end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then return { clip = {}, recording = {} } end

    local settings = {
        clip = read_combo(data, "clipKeybind"),
        recording = read_combo(data, "recordingKeybind"),
    }
    obs.obs_data_release(data)
    return settings
end

local function combo_signature(combo)
    if not combo or not combo.key then return "" end
    local parts = {}
    for _, mod in ipairs({ "control", "alt", "shift", "command" }) do
        if combo[mod] then parts[#parts + 1] = mod end
    end
    parts[#parts + 1] = combo.key
    return table.concat(parts, "+")
end

local function settings_signature(settings)
    return "clip=" .. combo_signature(settings.clip) ..
        "|recording=" .. combo_signature(settings.recording)
end

local function create_binding_array(combo)
    local arr = obs.obs_data_array_create()
    if combo and combo.key then
        local item = obs.obs_data_create()
        for _, mod in ipairs({ "control", "alt", "shift", "command" }) do
            if combo[mod] then obs.obs_data_set_bool(item, mod, true) end
        end
        obs.obs_data_set_string(item, "key", combo.key)
        obs.obs_data_array_push_back(arr, item)
        obs.obs_data_release(item)
    end
    return arr
end

local function matches_hotkey_name(name, targets)
    for _, target in ipairs(targets) do
        if name == target then return true end
    end
    return false
end

local function collect_hotkey_ids(target_names)
    local found = {}
    if not ensure_hotkey_ffi() then return found end

    local cb
    local ok_cast
    ok_cast, cb = pcall(ffi.cast, "rk_obs_hotkey_enum_func", function(data, id, hotkey)
        local name_ptr = obsffi.obs_hotkey_get_name(hotkey)
        if name_ptr ~= nil then
            local name = ffi.string(name_ptr)
            if matches_hotkey_name(name, target_names) then
                found[#found + 1] = tonumber(id)
            end
        end
        return true
    end)
    if not ok_cast then return found end

    local ok = pcall(function()
        obsffi.obs_enum_hotkeys(cb, nil)
    end)
    cb:free()
    if not ok then return {} end
    return found
end

local function load_native_hotkeys(target_names, combo, warn_key, missing_message)
    if type(obs.obs_hotkey_load) ~= "function" then
        if not native_warned then
            native_warned = true
            log(obs.LOG_WARNING, "OBS native hotkey APIs are unavailable; hotkey changes require OBS restart.")
        end
        return
    end

    local found = collect_hotkey_ids(target_names)
    if #found == 0 then
        if not missing_warned[warn_key] then
            missing_warned[warn_key] = true
            log(obs.LOG_WARNING, missing_message)
        end
        return
    end

    local arr = create_binding_array(combo)
    for _, id in ipairs(found) do
        local ok, err = pcall(obs.obs_hotkey_load, id, arr)
        if not ok then
            log(obs.LOG_WARNING, "Could not load OBS hotkey: " .. tostring(err))
        end
    end
    obs.obs_data_array_release(arr)
end

local function refresh_hotkeys(force)
    local settings = load_runtime_settings()
    local signature = settings_signature(settings)
    if not force and signature == last_signature then return end
    last_signature = signature

    load_native_hotkeys(CLIP_HOTKEY_NAMES, settings.clip, "clip", "Could not find OBS native clip hotkey to update.")
    load_native_hotkeys(RECORDING_HOTKEY_NAMES, settings.recording, "recording", "Could not find OBS native recording hotkeys to update.")
    log(obs.LOG_INFO, "Loaded OBS native hotkeys: " .. signature)
end

function script_load(settings)
    refresh_hotkeys(true)
end

function script_update(settings)
    refresh_hotkeys(true)
end

function script_tick(seconds)
    refresh_elapsed = refresh_elapsed + (seconds or 0)
    if refresh_elapsed < REFRESH_SECONDS then return end
    refresh_elapsed = 0
    refresh_hotkeys(false)
end
