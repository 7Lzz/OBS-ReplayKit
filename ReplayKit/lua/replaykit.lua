-- replaykit.lua single obs-facing entrypoint for obs replaykit. obs only shows this script in tools -> scripts. feature scripts remain split into focused files and are loaded here in isolated environments so their script_load/update/save/unload callbacks do not collide globally.

local obs = obslua
local ffi = require("ffi")

-- obs hands every path to lua as utf-8, but the io library of luajit and the ...A functions of windows read a path in the ansi code page, so a windows user name with a letter outside it (josé, дмитрий) left every module unloaded and the helper never started; a plain ascii path keeps the plain functions, any other goes thru the wide functions of windows
pcall(ffi.cdef, [[
    int MultiByteToWideChar(unsigned int CodePage, unsigned long dwFlags, const char *lpMultiByteStr, int cbMultiByte, wchar_t *lpWideCharStr, int cchWideChar);
    void *CreateFileW(const wchar_t *lpFileName, unsigned long dwDesiredAccess, unsigned long dwShareMode, void *lpSecurityAttributes, unsigned long dwCreationDisposition, unsigned long dwFlagsAndAttributes, void *hTemplateFile);
    int ReadFile(void *hFile, void *lpBuffer, unsigned long nNumberOfBytesToRead, unsigned long *lpNumberOfBytesRead, void *lpOverlapped);
    int WriteFile(void *hFile, const char *lpBuffer, unsigned long nNumberOfBytesToWrite, unsigned long *lpNumberOfBytesWritten, void *lpOverlapped);
    int GetFileSizeEx(void *hFile, int64_t *lpFileSize);
    int DeleteFileW(const wchar_t *lpFileName);
]])
pcall(ffi.cdef, [[
    typedef struct {
        unsigned long cb;
        wchar_t *lpReserved;
        wchar_t *lpDesktop;
        wchar_t *lpTitle;
        unsigned long dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        unsigned short wShowWindow, cbReserved2;
        void *lpReserved2;
        void *hStdInput, *hStdOutput, *hStdError;
    } rk_startupinfo_w;
    typedef struct { void *hProcess; void *hThread; unsigned long dwProcessId; unsigned long dwThreadId; } rk_process_information;
    int CreateProcessW(const wchar_t *lpApplicationName, wchar_t *lpCommandLine, void *lpProcessAttributes, void *lpThreadAttributes, int bInheritHandles, unsigned long dwCreationFlags, void *lpEnvironment, const wchar_t *lpCurrentDirectory, rk_startupinfo_w *lpStartupInfo, rk_process_information *lpProcessInformation);
]])
-- the modules declare these two as well, so they get blocks of their own that may fail without taking the others along
pcall(ffi.cdef, [[ int CloseHandle(void *hObject); ]])
pcall(ffi.cdef, [[ unsigned long GetLastError(void); ]])

local CP_UTF8 = 65001
local INVALID_HANDLE = ffi.cast("void *", -1)

local function is_ascii(text)
    return not text:find("[\128-\255]")
end

-- a nul-terminated utf-16 copy of a utf-8 string, for the wide functions of windows
local function wide(text)
    local count = ffi.C.MultiByteToWideChar(CP_UTF8, 0, text, #text, nil, 0)
    if count <= 0 and #text > 0 then return nil end
    local buffer = ffi.new("wchar_t[?]", count + 1)
    if count > 0 then ffi.C.MultiByteToWideChar(CP_UTF8, 0, text, #text, buffer, count) end
    buffer[count] = 0
    return buffer
end

local function wide_read(path)
    local name = wide(path)
    if not name then return nil end
    -- GENERIC_READ, open to readers, writers and deleters the way fopen is, OPEN_EXISTING
    local handle = ffi.C.CreateFileW(name, 0x80000000, 7, nil, 3, 0x80, nil)
    if handle == INVALID_HANDLE then return nil end
    local size = ffi.new("int64_t[1]")
    local text = nil
    if ffi.C.GetFileSizeEx(handle, size) ~= 0 then
        local total = tonumber(size[0])
        local buffer = ffi.new("char[?]", total + 1)
        local got = ffi.new("unsigned long[1]")
        local done = 0
        while done < total and ffi.C.ReadFile(handle, buffer + done, total - done, got, nil) ~= 0 and got[0] > 0 do
            done = done + tonumber(got[0])
        end
        text = ffi.string(buffer, done)
    end
    ffi.C.CloseHandle(handle)
    return text
end

local function wide_write(path, text)
    local name = wide(path)
    if not name then return false end
    -- GENERIC_WRITE, open to readers, CREATE_ALWAYS
    local handle = ffi.C.CreateFileW(name, 0x40000000, 1, nil, 2, 0x80, nil)
    if handle == INVALID_HANDLE then return false end
    local wrote = ffi.new("unsigned long[1]")
    local ok = #text == 0 or (ffi.C.WriteFile(handle, text, #text, wrote, nil) ~= 0 and tonumber(wrote[0]) == #text)
    ffi.C.CloseHandle(handle)
    return ok
end

-- what the modules do with a file: read it whole or up to a size, or write it and close it, which is when the text reaches the disk
local function wide_open(path, mode)
    mode = mode or "r"
    if mode:find("[wa]") then
        local parts = {}
        if mode:find("a") then parts[1] = wide_read(path) or "" end
        local file = {}
        function file:write(...)
            for i = 1, select("#", ...) do parts[#parts + 1] = tostring((select(i, ...))) end
            return self
        end
        function file:flush() return self end
        function file:close()
            local ok = wide_write(path, table.concat(parts))
            parts = {}
            if ok then return true end
            return nil, path .. ": could not be written"
        end
        return file
    end
    local text = wide_read(path)
    if not text then return nil, path .. ": No such file or directory" end
    local position = 1
    local file = {}
    function file:read(format)
        format = format or "*l"
        if type(format) == "number" then
            if position > #text then return nil end
            local chunk = text:sub(position, position + format - 1)
            position = position + #chunk
            return chunk
        end
        if format == "*a" or format == "a" then
            local rest = text:sub(position)
            position = #text + 1
            return rest
        end
        if position > #text then return nil end
        local line_end = text:find("\n", position, true) or (#text + 1)
        local line = text:sub(position, line_end - 1):gsub("\r$", "")
        position = line_end + 1
        return line
    end
    function file:close() return true end
    return file
end

local function utf8_open(path, mode)
    if type(path) ~= "string" or is_ascii(path) then return io.open(path, mode) end
    return wide_open(path, mode)
end

local function utf8_loadfile(path, ...)
    if type(path) ~= "string" or is_ascii(path) then return loadfile(path, ...) end
    local text = wide_read(path)
    if not text then return nil, "cannot open " .. path end
    return load(text, "@" .. path, ...)
end

local function utf8_remove(path)
    if type(path) ~= "string" or is_ascii(path) then return os.remove(path) end
    local name = wide(path)
    if name and ffi.C.DeleteFileW(name) ~= 0 then return true end
    return nil, path .. ": could not be removed"
end

-- starts a process from a utf-8 command line with its window hidden from the first tick (STARTF_USESHOWWINDOW and SW_HIDE, which keeps powershell from flashing a console); false and the windows error when it cannot
local function create_process(cmdline, flags)
    local command = wide(cmdline)
    if not command then return false, 0 end
    local si = ffi.new("rk_startupinfo_w")
    si.cb = ffi.sizeof("rk_startupinfo_w")
    si.dwFlags = 0x1
    si.wShowWindow = 0
    local pi = ffi.new("rk_process_information")
    if ffi.C.CreateProcessW(nil, command, nil, nil, 0, flags, nil, nil, si, pi) == 0 then
        return false, tonumber(ffi.C.GetLastError())
    end
    ffi.C.CloseHandle(pi.hProcess)
    ffi.C.CloseHandle(pi.hThread)
    return true
end

local SCRIPT_ROOT = (script_path() or ""):gsub("\\", "/")
if SCRIPT_ROOT ~= "" and SCRIPT_ROOT:sub(-1) ~= "/" then
    SCRIPT_ROOT = SCRIPT_ROOT .. "/"
end

local MODULES = {
    { id = "elevation",     path = "obs_elevation/auto_elevate_obs.lua" },
    { id = "capture",       path = "capture/auto_capture_switch.lua" },
    { id = "replay_start",  path = "replay_buffer/auto_start_replay_buffer.lua" },
    { id = "replay_sound",  path = "replay_buffer/replay_buffer_save_beep.lua" },
    { id = "recording_hotkey", path = "recording/recording_hotkey.lua" },
    { id = "helper",        path = "helper/helper_bootstrap.lua" },
    { id = "discord_projector", path = "virtual_camera/auto_start_virtual_camera.lua" },
    { id = "popup_shield",  path = "popup_shield/popup_capture_shield.lua" },
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

-- the values shared/ owns (the helper address and the folder of the saved state), written by scripts/sync_shared.ps1 on every build; without them no module can find its settings or the helper, so they are checked once here
local SHARED = (function()
    local chunk = utf8_loadfile(SCRIPT_ROOT .. "shared_values.lua")
    if not chunk then return nil end
    local ok, values = pcall(chunk)
    if ok and type(values) == "table" and type(values.host) == "string" and type(values.port) == "number" and type(values.origin) == "string" and type(values.state_folder) == "string" and type(values.old_places) == "table" then return values end
    return nil
end)()
if not SHARED then log_error("shared_values.lua is missing, so the settings and the helper cannot be found") end

local REPLAYKIT_ROOT = SCRIPT_ROOT .. "../"

local function file_exists(path)
    local file = utf8_open(path, "rb")
    if not file then return false end
    file:close()
    return true
end

-- where a saved state file is right now, by the rule of shared/StateLayout.cs Resolve: the state folder, or one of the older places its table gives that file (old_places of the shared values) only while the file is still there and has not been moved yet
local function state_file(name)
    -- without the shared values only the old place is known, and a module that reads nothing there keeps its own defaults
    if not SHARED then return REPLAYKIT_ROOT .. name end
    local current = REPLAYKIT_ROOT .. SHARED.state_folder .. "/" .. name
    if file_exists(current) then return current end
    for _, place in ipairs(SHARED.old_places[name] or {}) do
        local legacy = REPLAYKIT_ROOT .. place .. name
        if file_exists(legacy) then return legacy end
    end
    return current
end

local function load_chunk(path, env)
    if setfenv then
        local chunk, err = utf8_loadfile(path)
        if not chunk then return nil, err end
        setfenv(chunk, env)
        return chunk
    end
    return utf8_loadfile(path, "bt", env)
end

local function load_feature(spec)
    local full_path = SCRIPT_ROOT .. spec.path
    local module_dir = dirname(full_path)
    local env = {
        obslua = obslua,
        script_path = function()
            return module_dir
        end,
        replaykit_shared = SHARED,
        replaykit_state_file = state_file,
        -- the io, loadfile and os.remove of the module take utf-8 paths like everything obs gives it
        io = setmetatable({ open = utf8_open }, { __index = io }),
        os = setmetatable({ remove = utf8_remove }, { __index = os }),
        loadfile = utf8_loadfile,
        replaykit_wide = wide,
        replaykit_create_process = create_process,
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
    local file = utf8_open(path, "rb")
    if not file then return nil end
    local text = file:read("*a")
    file:close()
    return text
end

local function read_runtime_settings_text()
    return read_text(state_file("replaykit_settings.json"))
end

local function read_disable_obs_close_warning()
    local text = read_runtime_settings_text()
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
