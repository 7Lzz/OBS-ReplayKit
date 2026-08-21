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

-- name of the task scheduler entry the obs replaykit installer registers. keep in sync with task_name in obs_replaykit/scheduled_task.py. if this constant ever changes both sides have to move at once or the no-uac path silently degrades to shellexecuteex (which still works, but the familiar uac popup comes back).
local SCHTASKS_TASK_NAME = "OBSReplayKit-Elevate"

-- handoff file that the lua side fills in with the current obs path + pid right before triggering the scheduled task. wscript cant recieve command-line arguments thru schtasks /run, so this is the only way for the elevated relauncher to know which process to terminate. path matches the one hidden_relauncher.vbs reads.
local HANDOFF_PATH = (os.getenv("TEMP") or "C:\\Windows\\Temp") ..
                     "\\ReplayKit\\scratch\\obsreplaykit_elevate.txt"

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

-- write the runtime obs path + pid into the handoff file the elevated wscript will read. returns true on success, false otherwise; the caller treats a failed write as "fall back to uac flow".
local function write_handoff(obs_pid)
    -- io.open in "w" mode doesnt create missing parent directories, and this can run before anything else has touched %temp%\ReplayKit -- os_mkdir each level explicitly rather than assume its recursive; calling it on an already-existing dir is a harmless no-op.
    local tempRoot = os.getenv("TEMP") or "C:\\Windows\\Temp"
    obs.os_mkdir(tempRoot .. "\\ReplayKit")
    obs.os_mkdir(tempRoot .. "\\ReplayKit\\scratch")
    local f, err = io.open(HANDOFF_PATH, "w")
    if not f then
        print("[ElevateOBS] handoff write failed: " .. tostring(err))
        return false
    end
    f:write(OBS_EXE, "\n", tostring(obs_pid), "\n")
    f:close()
    return true
end

-- try to fire the obsreplaykit-elevate scheduled task. returns true on success. the task is registered with runlevel highestavailable so it runs elevated without a uac prompt; schtasks /run just signals the task scheduler to start it. the /i flag is "ignore the constraint that the task might already be running" -- harmless here becuase the task uses ignorenew multi-instance policy anyway.
local function try_scheduled_task(obs_pid)
    if not write_handoff(obs_pid) then return false end

    -- shellexecuteex with sw_hide so the schtasks console flash never appears. verb "open" (the defualt) avoids triggering uac; only "runas" elevates, and schtasks itself doesnt need elevation to start a task the calling user owns.
    local params = string.format('/Run /TN "%s" /I', SCHTASKS_TASK_NAME)

    local info = ffi.new("SHELLEXECUTEINFOA")
    ffi.fill(info, ffi.sizeof("SHELLEXECUTEINFOA"), 0)
    info.cbSize       = ffi.sizeof("SHELLEXECUTEINFOA")
    info.fMask        = SEE_MASK_NOCLOSEPROCESS
    info.hwnd         = user32.GetForegroundWindow()
    info.lpVerb       = "open"
    info.lpFile       = "schtasks.exe"
    info.lpParameters = params
    info.nShow        = SW_HIDE

    local ok = shell32.ShellExecuteExA(info)
    if ok == 0 then
        -- shellexecuteex returns 0 on any failure; the task may be absent (fresh install pre-apply) or group policy may be blocking task scheduler use.
        print("[ElevateOBS] schtasks /Run ShellExecuteEx failed (task missing? GPO blocked?)")
        return false
    end

    if info.hProcess ~= nil then
        kernel32.CloseHandle(info.hProcess)
    end

    print("[ElevateOBS] scheduled task '" .. SCHTASKS_TASK_NAME ..
          "' triggered (no UAC popup)")
    return true
end

-- last-resort fallback: per-launch uac popup. identical to the pre-scheduled-task implementation -- kept so installs without the task (older bundles, hand-extracted setups, task deleted by user via task scheduler) still elevate successfully, just with the familiar windows based script host uac prompt.
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

    -- preferred path: scheduled task fires the relauncher elevated without a uac popup. apply registers the task once during install; this branch covers every subsequent launch.
    if not elevated then
        print("[ElevateOBS] Not elevated - attempting silent elevation via scheduled task...")
    else
        print("[ElevateOBS] Missing required OBS launch flag - relaunching with ReplayKit flags...")
    end
    if try_scheduled_task(obs_pid) then return end

    -- fall back to the original shellexecuteex flow only if the scheduled task path fails (task missing becuase the user is on a bundle older than the task-installer, or becuase they deleted it from task scheduler manually).
    print("[ElevateOBS] Scheduled-task path unavailable - falling back to UAC flow.")
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
