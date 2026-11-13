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
    DWORD  GetModuleFileNameW(void* hModule, wchar_t* lpFilename, DWORD nSize);
    BOOL   TerminateProcess(HANDLE hProcess, UINT uExitCode);
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
        DWORD          cbSize;
        ULONG          fMask;
        HWND           hwnd;
        const wchar_t* lpVerb;
        const wchar_t* lpFile;
        const wchar_t* lpParameters;
        const wchar_t* lpDirectory;
        int            nShow;
        void*          hInstApp;
        void*          lpIDList;
        const wchar_t* lpClass;
        void*          hkeyClass;
        DWORD          dwHotKey;
        HANDLE         hIcon;
        HANDLE         hProcess;
    } SHELLEXECUTEINFOW;

    BOOL ShellExecuteExW(SHELLEXECUTEINFOW* pExecInfo);

    int    WideCharToMultiByte(UINT CodePage, DWORD dwFlags, const wchar_t* lpWideCharStr, int cchWideChar, char* lpMultiByteStr, int cbMultiByte, const char* lpDefaultChar, BOOL* lpUsedDefaultChar);
]]

local TOKEN_QUERY        = 0x0008
local TokenElevation     = 20
local SW_SHOWNORMAL      = 1
local CP_UTF8            = 65001

-- same cap the other settings readers use; a json bigger than this is corrupt
local MAX_SETTINGS_BYTES = 65536

local function settings_json_path()
    return replaykit_state_file("replaykit_settings.json")
end

-- run-as-admin is opt-in thru the replaykit settings ui (windows tab). a missing file or key reads as off, so a fresh or corrupt settings file never elevates on its own.
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

-- every launch of obs by replaykit carries the same switches (shared/ObsLaunch.cs), so an obs without all of them was started some other way
local function has_obs_launch_args(obs_args)
    local cmd = kernel32.GetCommandLineA()
    if cmd == nil then return false end
    local line = ffi.string(cmd):lower()
    for switch in obs_args:lower():gmatch("%S+") do
        if not line:find(switch, 1, true) then return false end
    end
    return true
end

local function utf8_of(wide_text)
    local count = kernel32.WideCharToMultiByte(CP_UTF8, 0, wide_text, -1, nil, 0, nil, nil)
    if count <= 1 then return "" end
    local buffer = ffi.new("char[?]", count)
    kernel32.WideCharToMultiByte(CP_UTF8, 0, wide_text, -1, buffer, count, nil, nil)
    return ffi.string(buffer, count - 1)
end

-- the helper beside the scripts, which has the mode that starts obs again (ObsProcess.RunRelauncher)
local function helper_exe()
    local scripts = script_path():gsub("/", "\\"):match("^(.*\\)[^\\]+\\?$")
    return scripts and (scripts .. "helper\\OBSReplayKit.exe")
end

-- starts the relaunch mode of the helper with administrator rights through the uac prompt and stops this obs at once; the relauncher waits until this process is gone, clears its crash marker and starts obs with the replaykit switches, and obs takes over its administrator rights. obs is not started from here, becuase it refuses a second instance and reaches that check before this copy is gone. a declined prompt leaves this obs running as it is
local function relaunch_elevated()
    local exe = ffi.new("wchar_t[32768]")
    local length = kernel32.GetModuleFileNameW(nil, exe, 32768)
    if length == 0 or length >= 32768 then
        print("[ElevateOBS] ERROR: the path of obs could not be read")
        return false
    end
    local helper = helper_exe()
    local probe = helper and io.open(helper, "rb")
    if not probe then
        print("[ElevateOBS] ERROR: the ReplayKit helper is missing, so OBS cannot be started again: " .. tostring(helper))
        return false
    end
    probe:close()

    -- a windows path cannot hold a quote, so plain quotes are enough around it
    local file = replaykit_wide(helper)
    local dir = replaykit_wide(helper:match("^(.*)\\[^\\]+$"))
    local verb = replaykit_wide("runas")
    local params = replaykit_wide(string.format('--relaunch-obs -ObsPath "%s" -ObsPid %d', utf8_of(exe), tonumber(kernel32.GetCurrentProcessId())))

    local info = ffi.new("SHELLEXECUTEINFOW")
    info.cbSize       = ffi.sizeof("SHELLEXECUTEINFOW")
    info.hwnd         = user32.GetForegroundWindow()
    info.lpVerb       = verb
    info.lpFile       = file
    info.lpParameters = params
    info.lpDirectory  = dir
    info.nShow        = SW_SHOWNORMAL

    if shell32.ShellExecuteExW(info) == 0 then
        print("[ElevateOBS] ShellExecuteEx failed - UAC denied or cancelled")
        return false
    end

    print("[ElevateOBS] elevated relauncher started - stopping this copy")
    kernel32.TerminateProcess(kernel32.GetCurrentProcess(), 0)
    return true
end

function script_load(settings)
    if not run_as_admin_enabled() then
        print("[ElevateOBS] Run-as-admin is disabled in ReplayKit settings - skipping elevation.")
        return
    end

    local obs_args = replaykit_shared and replaykit_shared.obs_args
    if type(obs_args) ~= "string" or obs_args == "" then
        print("[ElevateOBS] ERROR: shared_values.lua has no OBS launch arguments - skipping elevation.")
        return
    end

    local elevated = is_elevated()
    if elevated and has_obs_launch_args(obs_args) then
        print("[ElevateOBS] Already elevated with ReplayKit OBS flags - nothing to do.")
        return
    end

    if not elevated then
        print("[ElevateOBS] Not elevated - relaunching via UAC...")
    else
        print("[ElevateOBS] Missing required OBS launch flag - relaunching with ReplayKit flags...")
    end
    relaunch_elevated()
end

function script_description()
    return [[<b>Auto-Elevate OBS to Administrator</b><br><br>
Starts OBS again with administrator rights through the Windows UAC prompt:<br>
1. Starts the ReplayKit helper elevated in its relaunch mode<br>
2. Stops the running copy<br>
3. The helper waits until that copy is gone, deletes its crash marker and
starts OBS with the ReplayKit launch flags, so there is no "OBS already
running" popup<br><br>
Only runs when <b>Run OBS and ReplayKit as administrator</b> is enabled in
ReplayKit Settings (Windows tab). Default is off.]]
end
