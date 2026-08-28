local obs = obslua
local ffi = require("ffi")
local winmm = ffi.load("Winmm")

ffi.cdef[[
    int mciSendStringA(const char *lpszCommand, char *lpszReturnString, unsigned int cchReturn, void *hwndCallback);
]]

local DEFAULT_SOUND_BASENAME = "clipping_saved"
local DEFAULT_RECORDING_START_SOUND_BASENAME = "recording_start_saved"
local DEFAULT_RECORDING_STOP_SOUND_BASENAME = "recording_ended_saved"
local MCI_ALIAS = "obs_clipping_saved_alert"
local SUPPORTED_EXTS = {
    ".wav", ".mp3", ".m4a", ".aac", ".wma", ".mid", ".midi"
}
local MCI_MPEG_EXTS = {
    [".mp3"] = true,
    [".m4a"] = true,
    [".aac"] = true,
    [".wma"] = true,
}

local active_audio_file = ""
local active_recording_start_audio_file = ""
local active_recording_stop_audio_file = ""
local active_clip_sound_volume = 25
local active_recording_sound_volume = 25

local function log(level, message)
    obs.script_log(level, "[ClipSound] " .. tostring(message))
end

local function join_path(dir, name)
    if dir:sub(-1) == "\\" or dir:sub(-1) == "/" then
        return dir .. name
    end
    return dir .. "\\" .. name
end

local function parent_dir(path)
    path = tostring(path or ""):gsub("\\", "/"):gsub("/+$", "")
    return path:match("^(.*)/[^/]+$") or ""
end

local function read_text(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local text = f:read("*a")
    f:close()
    return text
end

local function runtime_settings_path()
    return join_path(parent_dir(script_path()), "replaykit_settings.json")
end

local function file_exists(path)
    local f = io.open(path, "rb")
    if not f then return false end
    f:close()
    return true
end

local function clamp_int(value, min_value, max_value, fallback)
    local n = tonumber(value)
    if n == nil then return fallback end
    n = math.floor(n + 0.5)
    if n < min_value then return min_value end
    if n > max_value then return max_value end
    return n
end

local function extname(path)
    return tostring(path or ""):match("(%.%w+)$") or ""
end

local function is_supported(path)
    local ext = extname(path):lower()
    for _, supported in ipairs(SUPPORTED_EXTS) do
        if ext == supported then return true end
    end
    return false
end

local function find_default_sound(basename)
    for _, ext in ipairs(SUPPORTED_EXTS) do
        local candidate = join_path(script_path(), basename .. ext)
        if file_exists(candidate) then
            return candidate
        end
    end
    return ""
end

local function resolve_sound(path, default_basename)
    path = tostring(path or "")
    if path ~= "" then
        if not is_supported(path) then
            log(obs.LOG_WARNING, "Unsupported audio type: " .. path)
            return find_default_sound(default_basename)
        end
        if file_exists(path) then return path end
        log(obs.LOG_WARNING, "Configured audio file is missing: " .. path)
    end
    return find_default_sound(default_basename)
end

local function mci_quote(path)
    return '"' .. tostring(path or ""):gsub('"', "") .. '"'
end

local function mci(command)
    return tonumber(winmm.mciSendStringA(command, nil, 0, nil)) or 0
end

local function load_runtime_volumes()
    local text = read_text(runtime_settings_path())
    if not text or text == "" then return end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then return end

    if obs.obs_data_has_user_value(data, "clipSoundVolume") then
        active_clip_sound_volume = clamp_int(obs.obs_data_get_int(data, "clipSoundVolume"), 0, 100, active_clip_sound_volume)
    end
    if obs.obs_data_has_user_value(data, "recordingSoundVolume") then
        active_recording_sound_volume = clamp_int(obs.obs_data_get_int(data, "recordingSoundVolume"), 0, 100, active_recording_sound_volume)
    end
    obs.obs_data_release(data)
end

local function play_mci(path, volume_percent)
    local ext = extname(path):lower()
    mci("close " .. MCI_ALIAS)

    local open_cmd
    if MCI_MPEG_EXTS[ext] then
        open_cmd = "open " .. mci_quote(path) .. " type mpegvideo alias " .. MCI_ALIAS
    elseif ext == ".wav" then
        open_cmd = "open " .. mci_quote(path) .. " type waveaudio alias " .. MCI_ALIAS
    else
        open_cmd = "open " .. mci_quote(path) .. " alias " .. MCI_ALIAS
    end

    local err = mci(open_cmd)
    if err ~= 0 then
        log(obs.LOG_WARNING, "Could not open audio file with MCI, error " .. tostring(err) .. ": " .. path)
        return
    end

    local volume = clamp_int(volume_percent, 0, 100, 100) * 10
    local volume_err = mci("setaudio " .. MCI_ALIAS .. " volume to " .. tostring(volume))
    if volume_err ~= 0 then
        log(obs.LOG_WARNING, "Could not set audio volume with MCI, error " .. tostring(volume_err) .. ": " .. path)
    end

    err = mci("play " .. MCI_ALIAS .. " from 0")
    if err ~= 0 then
        log(obs.LOG_WARNING, "Could not play audio file with MCI, error " .. tostring(err) .. ": " .. path)
        mci("close " .. MCI_ALIAS)
    end
end

local function play_sound(path, volume_percent)
    if path == "" then return end
    play_mci(path, volume_percent)
end

local function on_event(event)
    load_runtime_volumes()
    if event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED then
        play_sound(active_audio_file, active_clip_sound_volume)
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then
        play_sound(active_recording_start_audio_file, active_recording_sound_volume)
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then
        play_sound(active_recording_stop_audio_file, active_recording_sound_volume)
    end
end

function script_description()
    return "Plays local audio alerts after OBS saves a clip or recording starts/stops."
end

function script_properties()
    local props = obs.obs_properties_create()
    obs.obs_properties_add_path(
        props,
        "audio_file",
        "Clip saved sound",
        obs.OBS_PATH_FILE,
        "Audio Files (*.wav *.mp3 *.m4a *.aac *.wma *.mid *.midi);;All Files (*.*)",
        script_path()
    )
    obs.obs_properties_add_path(
        props,
        "recording_start_audio_file",
        "Recording started sound",
        obs.OBS_PATH_FILE,
        "Audio Files (*.wav *.mp3 *.m4a *.aac *.wma *.mid *.midi);;All Files (*.*)",
        script_path()
    )
    obs.obs_properties_add_path(
        props,
        "recording_stop_audio_file",
        "Recording stopped sound",
        obs.OBS_PATH_FILE,
        "Audio Files (*.wav *.mp3 *.m4a *.aac *.wma *.mid *.midi);;All Files (*.*)",
        script_path()
    )
    return props
end

function script_update(settings)
    load_runtime_volumes()
    active_audio_file = resolve_sound(obs.obs_data_get_string(settings, "audio_file"), DEFAULT_SOUND_BASENAME)
    active_recording_start_audio_file = resolve_sound(
        obs.obs_data_get_string(settings, "recording_start_audio_file"),
        DEFAULT_RECORDING_START_SOUND_BASENAME
    )
    active_recording_stop_audio_file = resolve_sound(
        obs.obs_data_get_string(settings, "recording_stop_audio_file"),
        DEFAULT_RECORDING_STOP_SOUND_BASENAME
    )
    if active_audio_file == "" then
        log(obs.LOG_WARNING, "No clip sound file found. Add clipping_saved.wav/mp3/etc. beside this script or set a custom file.")
    end
    if active_recording_start_audio_file == "" then
        log(obs.LOG_WARNING, "No recording started sound file found. Add recording_start_saved.wav/mp3/etc. beside this script or set a custom file.")
    end
    if active_recording_stop_audio_file == "" then
        log(obs.LOG_WARNING, "No recording stopped sound file found. Add recording_ended_saved.wav/mp3/etc. beside this script or set a custom file.")
    end
end

function script_load(settings)
    script_update(settings)
    obs.obs_frontend_add_event_callback(on_event)
end

function script_unload()
    obs.obs_frontend_remove_event_callback(on_event)
    mci("close " .. MCI_ALIAS)
end
