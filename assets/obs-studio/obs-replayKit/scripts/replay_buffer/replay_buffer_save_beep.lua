local obs = obslua
local ffi = require("ffi")
local winmm = ffi.load("Winmm")

ffi.cdef[[
    bool PlaySoundA(const char *pszSound, void *hmod, unsigned int fdwSound);
    int mciSendStringA(const char *lpszCommand, char *lpszReturnString, unsigned int cchReturn, void *hwndCallback);
]]

local DEFAULT_SOUND_BASENAME = "replay_buffer_saved"
local MCI_ALIAS = "obs_replay_buffer_saved_alert"
local SUPPORTED_EXTS = {
    ".wav", ".mp3", ".m4a", ".aac", ".wma", ".mid", ".midi"
}
local MCI_MPEG_EXTS = {
    [".mp3"] = true,
    [".m4a"] = true,
    [".aac"] = true,
    [".wma"] = true,
}

local SND_ASYNC = 0x00000001
local SND_NODEFAULT = 0x00000002
local SND_FILENAME = 0x00020000

local active_audio_file = ""

local function log(level, message)
    obs.script_log(level, "[ReplayBufferSound] " .. tostring(message))
end

local function join_path(dir, name)
    if dir:sub(-1) == "\\" or dir:sub(-1) == "/" then
        return dir .. name
    end
    return dir .. "\\" .. name
end

local function file_exists(path)
    local f = io.open(path, "rb")
    if not f then return false end
    f:close()
    return true
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

local function find_default_sound()
    for _, ext in ipairs(SUPPORTED_EXTS) do
        local candidate = join_path(script_path(), DEFAULT_SOUND_BASENAME .. ext)
        if file_exists(candidate) then
            return candidate
        end
    end
    return ""
end

local function resolve_sound(path)
    path = tostring(path or "")
    if path ~= "" then
        if not is_supported(path) then
            log(obs.LOG_WARNING, "Unsupported audio type: " .. path)
            return find_default_sound()
        end
        if file_exists(path) then return path end
        log(obs.LOG_WARNING, "Configured audio file is missing: " .. path)
    end
    return find_default_sound()
end

local function mci_quote(path)
    return '"' .. tostring(path or ""):gsub('"', "") .. '"'
end

local function mci(command)
    return tonumber(winmm.mciSendStringA(command, nil, 0, nil)) or 0
end

local function play_mci(path)
    local ext = extname(path):lower()
    mci("close " .. MCI_ALIAS)

    local open_cmd
    if MCI_MPEG_EXTS[ext] then
        open_cmd = "open " .. mci_quote(path) .. " type mpegvideo alias " .. MCI_ALIAS
    else
        open_cmd = "open " .. mci_quote(path) .. " alias " .. MCI_ALIAS
    end

    local err = mci(open_cmd)
    if err ~= 0 then
        log(obs.LOG_WARNING, "Could not open audio file with MCI, error " .. tostring(err) .. ": " .. path)
        return
    end

    err = mci("play " .. MCI_ALIAS .. " from 0")
    if err ~= 0 then
        log(obs.LOG_WARNING, "Could not play audio file with MCI, error " .. tostring(err) .. ": " .. path)
        mci("close " .. MCI_ALIAS)
    end
end

local function play_sound(path)
    if path == "" then return end
    local ext = extname(path):lower()
    if ext == ".wav" then
        winmm.PlaySoundA(path, nil, SND_FILENAME + SND_ASYNC + SND_NODEFAULT)
    else
        play_mci(path)
    end
end

local function on_event(event)
    if event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED then
        play_sound(active_audio_file)
    end
end

function script_description()
    return "Plays a local audio alert after OBS saves a replay-buffer clip."
end

function script_properties()
    local props = obs.obs_properties_create()
    obs.obs_properties_add_path(
        props,
        "audio_file",
        "Replay saved sound",
        obs.OBS_PATH_FILE,
        "Audio Files (*.wav *.mp3 *.m4a *.aac *.wma *.mid *.midi);;All Files (*.*)",
        script_path()
    )
    return props
end

function script_update(settings)
    active_audio_file = resolve_sound(obs.obs_data_get_string(settings, "audio_file"))
    if active_audio_file == "" then
        log(obs.LOG_WARNING, "No replay-buffer sound file found. Add replay_buffer_saved.wav/mp3/etc. beside this script or set a custom file.")
    end
end

function script_load(settings)
    script_update(settings)
    obs.obs_frontend_add_event_callback(on_event)
end

function script_unload()
    obs.obs_frontend_remove_event_callback(on_event)
    mci("close " .. MCI_ALIAS)
    winmm.PlaySoundA(nil, nil, 0)
end
