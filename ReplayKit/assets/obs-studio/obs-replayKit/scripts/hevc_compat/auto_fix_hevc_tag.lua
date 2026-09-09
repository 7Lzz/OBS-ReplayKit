-- fixes hevc clips obs muxed with the hev1 container tag instead of hvc1 -- ios/avfoundation (and so discords iphone app) wont decode hev1, coming up blank video with audio still fine -- a lossless retag done by fix_hevc_tag.ps1 as a detached process, run on every replay-buffer save, every finished recording, and a first-run startup sweep for whatever was already on disk.

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

local function log(level, message)
    obs.script_log(level, "[HevcTagFix] " .. tostring(message))
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

local function file_exists(path)
    local f = io.open(path, "rb")
    if not f then return false end
    f:close()
    return true
end

local function worker_script_path()
    return join_path(script_path(), "fix_hevc_tag.ps1")
end

local function run_worker(argLine)
    local script = worker_script_path()
    if not file_exists(script) then
        log(obs.LOG_WARNING, "missing worker script: " .. script)
        return
    end
    local cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " ..
        win_quote(script) .. argLine
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_WARNING, "could not start fix worker. Win32 error: " .. tostring(err))
    end
end

local function fix_one(path)
    if not path or path == "" then return end
    run_worker(" -Path " .. win_quote(path))
end

local function run_startup_sweep()
    run_worker("")
end

local function on_event(event)
    if event == obs.OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED then
        fix_one(obs.obs_frontend_get_last_replay())
    elseif event == obs.OBS_FRONTEND_EVENT_RECORDING_STOPPED then
        fix_one(obs.obs_frontend_get_last_recording())
    end
end

function script_load(settings)
    obs.obs_frontend_add_event_callback(on_event)
    run_startup_sweep()
end

function script_unload()
    obs.obs_frontend_remove_event_callback(on_event)
end
