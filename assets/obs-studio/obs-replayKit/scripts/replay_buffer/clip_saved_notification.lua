local obs = obslua
local ffi = require("ffi")

ffi.cdef[[
    typedef void*         HANDLE;
    typedef unsigned long DWORD;
    typedef int           BOOL;
    typedef char*         LPSTR;
    typedef const char*   LPCSTR;

    typedef struct {
        DWORD cb;
        LPSTR lpReserved;
        LPSTR lpDesktop;
        LPSTR lpTitle;
        DWORD dwX;
        DWORD dwY;
        DWORD dwXSize;
        DWORD dwYSize;
        DWORD dwXCountChars;
        DWORD dwYCountChars;
        DWORD dwFillAttribute;
        DWORD dwFlags;
        unsigned short wShowWindow;
        unsigned short cbReserved2;
        void* lpReserved2;
        HANDLE hStdInput;
        HANDLE hStdOutput;
        HANDLE hStdError;
    } STARTUPINFOA;

    typedef struct {
        HANDLE hProcess;
        HANDLE hThread;
        DWORD dwProcessId;
        DWORD dwThreadId;
    } PROCESS_INFORMATION;

    BOOL CreateProcessA(
        LPCSTR lpApplicationName,
        LPSTR lpCommandLine,
        void* lpProcessAttributes,
        void* lpThreadAttributes,
        BOOL bInheritHandles,
        DWORD dwCreationFlags,
        void* lpEnvironment,
        LPCSTR lpCurrentDirectory,
        STARTUPINFOA* lpStartupInfo,
        PROCESS_INFORMATION* lpProcessInformation
    );
    BOOL CloseHandle(HANDLE hObject);
    DWORD GetLastError(void);
]]

local kernel32 = ffi.load("kernel32")
local CREATE_NO_WINDOW     = 0x08000000
local STARTF_USESHOWWINDOW = 0x00000001
local SW_HIDE              = 0

local cfg = {
    clip_enabled = true,
    recording_enabled = true,
    seconds = 90,
    duration_ms = 3200,
}

local allowed_popup_kinds = {
    clip = true,
    ["recording-started"] = true,
    ["recording-stopped"] = true,
}

local function log(level, message)
    obs.script_log(level, "[ClipPopup] " .. tostring(message))
end

local function clamp_int(value, min_value, max_value, fallback)
    value = tonumber(value) or fallback
    value = math.floor(value)
    if value < min_value then return min_value end
    if value > max_value then return max_value end
    return value
end

local function win_quote(arg)
    arg = tostring(arg or "")
    if arg ~= "" and not arg:find('[%s"]') then return arg end
    local out = '"'
    local slashes = 0
    for i = 1, #arg do
        local c = arg:sub(i, i)
        if c == "\\" then
            slashes = slashes + 1
        elseif c == '"' then
            out = out .. string.rep("\\", slashes * 2 + 1) .. '"'
            slashes = 0
        else
            out = out .. string.rep("\\", slashes) .. c
            slashes = 0
        end
    end
    return out .. string.rep("\\", slashes * 2) .. '"'
end

local function spawn_hidden(cmdline)
    local si = ffi.new("STARTUPINFOA")
    si.cb = ffi.sizeof("STARTUPINFOA")
    si.dwFlags = STARTF_USESHOWWINDOW
    si.wShowWindow = SW_HIDE

    local pi = ffi.new("PROCESS_INFORMATION")
    local buf = ffi.new("char[?]", #cmdline + 1)
    ffi.copy(buf, cmdline)
    local ok = kernel32.CreateProcessA(
        nil, buf,
        nil, nil,
        0,
        CREATE_NO_WINDOW,
        nil, nil,
        si, pi
    )
    if ok == 0 then
        return false, tonumber(kernel32.GetLastError())
    end
    kernel32.CloseHandle(pi.hProcess)
    kernel32.CloseHandle(pi.hThread)
    return true
end

local function join_path(dir, name)
    if dir:sub(-1) == "\\" or dir:sub(-1) == "/" then
        return dir .. name
    end
    return dir .. "\\" .. name
end

local function parent_dir(path)
    path = tostring(path or ""):gsub("[/\\]+$", "")
    return path:match("^(.*[/\\])") or ""
end

local function file_exists(path)
    local f = io.open(path, "rb")
    if not f then return false end
    f:close()
    return true
end

local function read_text(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local text = f:read("*a")
    f:close()
    return text
end

local function popup_script_path()
    return join_path(script_path(), "clip_saved_notification.ps1")
end

local function runtime_settings_path()
    return join_path(parent_dir(script_path()), "replaykit_settings.json")
end

local function load_runtime_settings()
    local text = read_text(runtime_settings_path())
    if not text or text == "" then return end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then return end

    if obs.obs_data_has_user_value(data, "clipNotificationEnabled") then
        cfg.clip_enabled = obs.obs_data_get_bool(data, "clipNotificationEnabled")
    end
    if obs.obs_data_has_user_value(data, "recordingNotificationEnabled") then
        cfg.recording_enabled = obs.obs_data_get_bool(data, "recordingNotificationEnabled")
    end
    if obs.obs_data_has_user_value(data, "clipNotificationSeconds") then
        cfg.seconds = clamp_int(
            obs.obs_data_get_int(data, "clipNotificationSeconds"),
            1, 600, cfg.seconds
        )
    end
    obs.obs_data_release(data)
end

local function show_popup(kind)
    load_runtime_settings()

    kind = tostring(kind or "")
    if not allowed_popup_kinds[kind] then
        log(obs.LOG_WARNING, "Refusing unknown popup kind: " .. kind)
        return
    end
    if kind == "clip" and not cfg.clip_enabled then return end
    if kind ~= "clip" and not cfg.recording_enabled then return end

    local script = popup_script_path()
    if not file_exists(script) then
        log(obs.LOG_WARNING, "Missing popup script: " .. script)
        return
    end

    local cmd = "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File " ..
        win_quote(script) ..
        " -Kind " .. win_quote(kind) ..
        " -Seconds " .. tostring(cfg.seconds) ..
        " -DurationMs " .. tostring(cfg.duration_ms)
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_WARNING, "Could not show clip popup. Win32 error: " .. tostring(err))
    end
end

local function on_event(event)
    if event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED then
        show_popup("clip")
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STARTED then
        show_popup("recording-started")
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then
        show_popup("recording-stopped")
    end
end

function script_defaults(settings)
    obs.obs_data_set_default_bool(settings, "clip_notification_enabled", true)
    obs.obs_data_set_default_bool(settings, "recording_notification_enabled", true)
    obs.obs_data_set_default_int(settings, "clip_notification_seconds", 90)
end

function script_update(settings)
    cfg.clip_enabled = obs.obs_data_get_bool(settings, "clip_notification_enabled")
    cfg.recording_enabled = obs.obs_data_get_bool(settings, "recording_notification_enabled")
    cfg.seconds = clamp_int(
        obs.obs_data_get_int(settings, "clip_notification_seconds"),
        1, 600, 90
    )
    load_runtime_settings()
end

function script_load(settings)
    script_update(settings)
    obs.obs_frontend_add_event_callback(on_event)
end

function script_unload()
    obs.obs_frontend_remove_event_callback(on_event)
end
