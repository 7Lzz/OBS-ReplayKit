obs = obslua
ffi = require("ffi")

local advapi32 = ffi.load("Advapi32")
local kernel32 = ffi.load("Kernel32")
local shell32  = ffi.load("Shell32")
local user32   = ffi.load("User32")

ffi.cdef[[
    typedef void*          HANDLE;
    typedef void*          HWND;
    typedef unsigned long  DWORD;
    typedef unsigned long  ULONG;
    typedef int            BOOL;
    typedef unsigned int   UINT;
    typedef int            TOKEN_INFORMATION_CLASS;

    HANDLE GetCurrentProcess();
    DWORD  GetCurrentProcessId();
    const char* GetCommandLineA();
    DWORD  GetModuleFileNameA(void* hModule, char* lpFilename, DWORD nSize);
    BOOL   CloseHandle(HANDLE hObject);

    HWND GetForegroundWindow();

    BOOL OpenProcessToken(HANDLE ProcessHandle, DWORD DesiredAccess, HANDLE* TokenHandle);
    BOOL GetTokenInformation(
        HANDLE                  TokenHandle,
        TOKEN_INFORMATION_CLASS TokenInformationClass,
        void*                   TokenInformation,
        DWORD                   TokenInformationLength,
        DWORD*                  ReturnLength
    );

    typedef struct {
        DWORD       cbSize;
        ULONG       fMask;
        HWND        hwnd;
        const char* lpVerb;
        const char* lpFile;
        const char* lpParameters;
        const char* lpDirectory;
        int         nShow;
        void*       hInstApp;
        void*       lpIDList;
        const char* lpClass;
        void*       hkeyClass;
        DWORD       dwHotKey;
        HANDLE      hIcon;
        HANDLE      hProcess;
    } SHELLEXECUTEINFOA;

    BOOL ShellExecuteExA(SHELLEXECUTEINFOA* pExecInfo);
]]

local TOKEN_QUERY             = 0x0008
local TokenElevation          = 20
local SW_HIDE                 = 0
local SEE_MASK_NOCLOSEPROCESS = 0x00000040

local DEFAULT_OBS_EXE = "C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe"
local REQUIRED_OBS_FLAG = "--disable-direct-composition-video-overlays"

-- same cap the other settings readers use; a json bigger than this is corrupt
local MAX_SETTINGS_BYTES = 65536

local function settings_json_path()
    local dir = (script_path() or ""):gsub("\\", "/"):gsub("/+$", "")
    local parent = dir:match("^(.*)/[^/]+$") or dir
    return parent .. "/replaykit_settings.json"
end

-- run-as-admin is opt-in thru the replaykit settings ui (general tab). a missing file or key reads as off, so a fresh or corrupt settings file never elevates on its own.
local function run_as_admin_enabled()
    local f = io.open(settings_json_path(), "rb")
    if not f then return false end
    local text = f:read(MAX_SETTINGS_BYTES + 1)
    f:close()
    if not text or #text > MAX_SETTINGS_BYTES then return false end
    text = text:gsub("^\239\187\191", "")
    local data = obs.obs_data_create_from_json(text)
    if data == nil then return false end
    local enabled = obs.obs_data_get_bool(data, "runObsAsAdmin")
    obs.obs_data_release(data)
    return enabled
end

local function get_current_obs_exe()
    local buf = ffi.new("char[?]", 32768)
    local len = kernel32.GetModuleFileNameA(nil, buf, 32768)
    if len > 0 and len < 32768 then
        return ffi.string(buf, len)
    end
    return DEFAULT_OBS_EXE
end

local OBS_EXE = get_current_obs_exe()

local function get_helper_path()
    -- script_path() returns the current script directory with a trailing slash.
    local dir = script_path()
    dir = dir:gsub("/", "\\")
    return dir .. "hidden_relauncher.vbs"
end

local function is_elevated()
    local proc  = kernel32.GetCurrentProcess()
    local token = ffi.new("void*[1]")
    if advapi32.OpenProcessToken(proc, TOKEN_QUERY, token) == 0 then return false end

    local elevation = ffi.new("unsigned long[1]", 0)
    local ret_len   = ffi.new("unsigned long[1]", 0)
    local ok = advapi32.GetTokenInformation(
        token[0], TokenElevation,
        elevation, ffi.sizeof("unsigned long"), ret_len
    )
    kernel32.CloseHandle(token[0])
    if ok == 0 then return false end
    return elevation[0] ~= 0
end

local function has_required_obs_flag()
    local cmd = kernel32.GetCommandLineA()
    if cmd == nil then return false end
    return ffi.string(cmd):lower():find(REQUIRED_OBS_FLAG, 1, true) ~= nil
end

-- relaunch obs elevated via a uac prompt on wscript.exe running hidden_relauncher.vbs. used to try a hidden, highest-privilege scheduled task first so this ran silently -- removed becuase that task was installed unconditionally on every apply regardless of whether run-as-admin was even on, and a hidden auto-elevating task is exactly the shape av heuristics flag as a persistence mechanism.
local function launch_helper_uac(helper_path, obs_pid)
    local params = string.format(
        '"%s" "%s" %u',
        helper_path, OBS_EXE, obs_pid
    )

    local info = ffi.new("SHELLEXECUTEINFOA")
    ffi.fill(info, ffi.sizeof("SHELLEXECUTEINFOA"), 0)

    info.cbSize       = ffi.sizeof("SHELLEXECUTEINFOA")
    info.fMask        = SEE_MASK_NOCLOSEPROCESS
    info.hwnd         = user32.GetForegroundWindow()
    info.lpVerb       = "runas"
    info.lpFile       = "wscript.exe"
    info.lpParameters = params
    info.nShow        = SW_HIDE

    local ok = shell32.ShellExecuteExA(info)

    if ok == 0 then
        print("[ElevateOBS] ShellExecuteEx failed - UAC denied or cancelled")
        return false
    end

    if info.hProcess ~= nil then
        kernel32.CloseHandle(info.hProcess)
    end

    print("[ElevateOBS] UAC fallback path - helper launched")
    return true
end

function script_load(settings)
    if not run_as_admin_enabled() then
        print("[ElevateOBS] Run-as-admin is disabled in ReplayKit settings - skipping elevation.")
        return
    end

    local elevated = is_elevated()
    local has_flag = has_required_obs_flag()

    if elevated and has_flag then
        print("[ElevateOBS] Already elevated with ReplayKit OBS flags - nothing to do.")
        return
    end

    local helper_path = get_helper_path()
    print("[ElevateOBS] Looking for helper at: " .. helper_path)

    local f = io.open(helper_path, "r")
    if not f then
        print("[ElevateOBS] ERROR: Helper script not found at: " .. helper_path)
        print("[ElevateOBS] Place hidden_relauncher.vbs beside this Lua script.")
        return
    end
    f:close()

    local obs_pid = tonumber(kernel32.GetCurrentProcessId()) or 0

    if not elevated then
        print("[ElevateOBS] Not elevated - relaunching via UAC...")
    else
        print("[ElevateOBS] Missing required OBS launch flag - relaunching with ReplayKit flags...")
    end
    launch_helper_uac(helper_path, obs_pid)
end

function script_description()
    return [[<b>Auto-Elevate OBS to Administrator</b><br><br>
Launches an elevated hidden WSH helper from the same folder that:<br>
1. Closes the current OBS instance from outside<br>
2. Deletes the <b>safe_mode</b> crash flag<br>
3. Relaunches OBS already elevated<br><br>
- No "OBS already running" popup<br>
- No safe mode / crash popup<br><br>
<b>Requires:</b> <code>hidden_relauncher.vbs</code> beside this .lua file.<br><br>
Only runs when <b>Run OBS and ReplayKit as administrator</b> is enabled in
ReplayKit Settings (General tab). Default is off.]]
end
