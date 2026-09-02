-- helper_bootstrap.lua low-lag obs entrypoint that launches the ReplayKit helper and forwards hotkey/menu requests to it. obs should not host clip browsing, video preview streaming, or upload-state polling on its lua/ui thread, so this script only starts the external helper -- it serves the obs dock ui on 127.0.0.1 from a seperate powershell process.

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
    void Sleep(DWORD dwMilliseconds);
    unsigned long long GetTickCount64(void);

    typedef unsigned long long SOCKET;
    typedef struct WSAData {
        unsigned short wVersion;
        unsigned short wHighVersion;
        char szDescription[257];
        char szSystemStatus[129];
        unsigned short iMaxSockets;
        unsigned short iMaxUdpDg;
        char* lpVendorInfo;
    } WSADATA;
    struct in_addr {
        unsigned long s_addr;
    };
    struct sockaddr {
        unsigned short sa_family;
        char sa_data[14];
    };
    struct sockaddr_in {
        short sin_family;
        unsigned short sin_port;
        struct in_addr sin_addr;
        char sin_zero[8];
    };
    int WSAStartup(unsigned short wVersionRequested, WSADATA* lpWSAData);
    int WSACleanup(void);
    SOCKET socket(int af, int type, int protocol);
    int connect(SOCKET s, const struct sockaddr *name, int namelen);
    int closesocket(SOCKET s);
    unsigned short htons(unsigned short hostshort);
    unsigned long inet_addr(const char *cp);
    int send(SOCKET s, const char *buf, int len, int flags);
    int recv(SOCKET s, char *buf, int len, int flags);
    int setsockopt(SOCKET s, int level, int optname, const char *optval, int optlen);
]]

local kernel32 = ffi.load("kernel32")
local ws2_32 = ffi.load("ws2_32")
local CREATE_NO_WINDOW     = 0x08000000
local CREATE_BREAKAWAY_FROM_JOB = 0x01000000
local DETACHED_PROCESS     = 0x00000008
local STARTF_USESHOWWINDOW = 0x00000001
local SW_HIDE              = 0
local AF_INET              = 2
local SOCK_STREAM          = 1
local IPPROTO_TCP          = 6
local INVALID_SOCKET       = ffi.cast("SOCKET", -1)
local SOL_SOCKET           = 0xFFFF
local SO_RCVTIMEO          = 0x1006

local HTTP_PORT = 8767
local HELPER_START_WAIT_MS = 3000
local HELPER_WATCHDOG_MS = 10000
local HELPER_RESTART_COOLDOWN_MS = 15000
-- global streamable helper logging switch. keep false for production. flip this one constant to true when debugging lua/helper/upload/compress logs.
local REPLAYKIT_LOGGING_ENABLED = false
local hotkey_id = obs.OBS_INVALID_HOTKEY_ID
local helper_watchdog_started = false
local helper_last_restart_ms = 0
local winsock_started = false

local cfg = {
    clip_dir = "",
}

local function log(level, msg)
    if not REPLAYKIT_LOGGING_ENABLED then return end
    obs.script_log(level, msg)
end

local function env(key, default)
    return os.getenv(key) or default
end

local function script_dir()
    local src = debug.getinfo(1, "S").source
    if src:sub(1, 1) == "@" then src = src:sub(2) end
    return (src:match("^(.*[\\/])") or "")
end

local function file_exists(path)
    local f = io.open(path, "rb")
    if not f then return false end
    f:close()
    return true
end

local function now_ms()
    return tonumber(kernel32.GetTickCount64())
end

local function ensure_winsock()
    if winsock_started then return true end
    local data = ffi.new("WSADATA")
    if ws2_32.WSAStartup(0x0202, data) ~= 0 then
        return false
    end
    winsock_started = true
    return true
end

local function helper_port_open()
    if not ensure_winsock() then return false end
    local s = ws2_32.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
    if s == INVALID_SOCKET then return false end
    local addr = ffi.new("struct sockaddr_in")
    addr.sin_family = AF_INET
    addr.sin_port = ws2_32.htons(HTTP_PORT)
    addr.sin_addr.s_addr = ws2_32.inet_addr("127.0.0.1")
    local ok = ws2_32.connect(
        s,
        ffi.cast("const struct sockaddr*", addr),
        ffi.sizeof(addr)
    ) == 0
    ws2_32.closesocket(s)
    return ok
end

-- a raw connect() only proves the helper has bound the port, not that its single request-handling thread can still answer -- thats how a past hang went undetected, with helper_port_open() reporting healthy while the servicing thread was stuck, so this does a real http round trip with a short recv timeout instead.
local function helper_responds_ok(timeout_ms)
    if not ensure_winsock() then return false end
    local s = ws2_32.socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
    if s == INVALID_SOCKET then return false end

    local tmo = ffi.new("DWORD[1]", timeout_ms)
    ws2_32.setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, ffi.cast("const char*", tmo), ffi.sizeof("DWORD"))

    local addr = ffi.new("struct sockaddr_in")
    addr.sin_family = AF_INET
    addr.sin_port = ws2_32.htons(HTTP_PORT)
    addr.sin_addr.s_addr = ws2_32.inet_addr("127.0.0.1")
    if ws2_32.connect(s, ffi.cast("const struct sockaddr*", addr), ffi.sizeof(addr)) ~= 0 then
        ws2_32.closesocket(s)
        return false
    end

    local req = "GET /status HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"
    if ws2_32.send(s, req, #req, 0) <= 0 then
        ws2_32.closesocket(s)
        return false
    end

    local buf = ffi.new("char[?]", 64)
    local n = ws2_32.recv(s, buf, 64, 0)
    ws2_32.closesocket(s)
    if n <= 0 then return false end
    return ffi.string(buf, n):find("HTTP/1.1 200", 1, true) ~= nil
end

local function wait_for_helper_port(timeout_ms)
    local deadline = now_ms() + timeout_ms
    repeat
        if helper_port_open() then return true end
        kernel32.Sleep(100)
    until now_ms() >= deadline
    return helper_port_open()
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
    -- create_no_window alone allows powershell.exe to briefly flash a console window on some windows builds before it self-hides. the documented "zero flash" pattern is to also set startf_useshowwindow + wshowwindow=sw_hide in startupinfo, which tells the os to hide the window at the kernel level from the very first scheduling tick. note: do not combine with detached_process -- that means "no console at all" and breaks powershells startup (its init queries the console handle).
    si.dwFlags     = STARTF_USESHOWWINDOW
    si.wShowWindow = SW_HIDE
    local pi = ffi.new("PROCESS_INFORMATION")
    local buf = ffi.new("char[?]", #cmdline + 1)
    ffi.copy(buf, cmdline)
    local flags = CREATE_NO_WINDOW + CREATE_BREAKAWAY_FROM_JOB + DETACHED_PROCESS
    local ok = kernel32.CreateProcessA(nil, buf, nil, nil, 0, flags, nil, nil, si, pi)
    -- Some restrictive job policies deny breakaway. The compiled helper still
    -- works in that case, so retry with the ordinary launch flags.
    if ok == 0 then
        buf = ffi.new("char[?]", #cmdline + 1)
        ffi.copy(buf, cmdline)
        ok = kernel32.CreateProcessA(nil, buf, nil, nil, 0, CREATE_NO_WINDOW, nil, nil, si, pi)
    end
    if ok == 0 then
        return false, tonumber(kernel32.GetLastError())
    end
    kernel32.CloseHandle(pi.hProcess)
    kernel32.CloseHandle(pi.hThread)
    return true
end

local function json_escape(s)
    return '"' .. tostring(s or "")
        :gsub("\\", "\\\\")
        :gsub('"', '\\"')
        :gsub("\r", "\\r")
        :gsub("\n", "\\n")
        :gsub("\t", "\\t") .. '"'
end

local function read_text(path)
    local file = io.open(path, "rb")
    if not file then return nil end
    local text = file:read("*a")
    file:close()
    return text
end

-- the settings.html "Enable debug logging" toggle writes into replaykit_settings.json (one level up from this script), not into helper_config.json directly -- orred with REPLAYKIT_LOGGING_ENABLED below so a users toggle and a devs hardcoded override both work independently.
local function read_debug_logging_enabled()
    local text = read_text(script_dir() .. "../replaykit_settings.json")
    if not text or text == "" then return false end
    if not text:find('"debugLoggingEnabled"%s*:') then return false end
    local data = obs.obs_data_create_from_json(text)
    if data == nil then return false end
    local enabled = obs.obs_data_get_bool(data, "debugLoggingEnabled")
    obs.obs_data_release(data)
    return enabled
end

local function helper_config_path()
    return script_dir() .. "helper_config.json"
end

local function helper_path()
    return script_dir() .. "OBSReplayKit.exe"
end

local function helper_runtime_dir()
    return script_dir()
end

local function write_helper_config()
    local p = helper_config_path()
    local f = io.open(p, "wb")
    if not f then
        log(obs.LOG_ERROR, "Could not write helper config: " .. p)
        return false
    end
    f:write("{\n")
    f:write("  \"port\": " .. HTTP_PORT .. ",\n")
    f:write("  \"scriptDir\": " .. json_escape(helper_runtime_dir()) .. ",\n")
    f:write("  \"clipDir\": " .. json_escape(cfg.clip_dir) .. ",\n")
    f:write("  \"loggingEnabled\": " .. tostring(REPLAYKIT_LOGGING_ENABLED or read_debug_logging_enabled()) .. "\n")
    f:write("}\n")
    f:close()
    return true
end

-- set to true once weve successfully launched the helper this session. re-spawn attempts are skipped while its true (the os port-bind would just fail anyway). stays false if the launch errored so the next helper_request() retries after a failed helper launch.
local helper_started = false

-- Runs the update startup check in the compiled helper. This keeps the prompt
-- available while OBS is minimized without launching PowerShell.
local update_bootstrap_started = false
local function start_update_bootstrap()
    if update_bootstrap_started then return true end
    local helper = helper_path()
    if not file_exists(helper) then
        log(obs.LOG_INFO, "Helper missing; skipping startup update check: " .. helper)
        return false
    end
    local cmd = win_quote(helper) .. ' --update-startup-check -Port ' .. HTTP_PORT
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Update bootstrap spawn failed. Win32 error: " .. tostring(err))
        return false
    end
    update_bootstrap_started = true
    return true
end

local function start_helper()
    if helper_started then
        if helper_port_open() then return true end
        helper_started = false
    end

    -- the helper is a full compiled c# app (OBSReplayKit.exe, the ReplayKitHelper project) -- it replaced the PowerShell implementation this bootstrap used to spawn (either directly, or in-process via a thin branded launcher exe hosting System.Management.Automation), so there is no PowerShell fallback left to spawn instead; a missing exe here is a hard install error, re-run the ReplayKit setup/apply flow to restore it.
    local helper = helper_path()
    if not file_exists(helper) then
        log(obs.LOG_ERROR, "Missing helper: " .. helper)
        return false
    end
    if not write_helper_config() then return false end

    local cmd = win_quote(helper) .. ' -ConfigPath ' .. win_quote(helper_config_path())
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Could not start ReplayKit helper. Win32 error: " .. tostring(err))
        return false
    end
    if not wait_for_helper_port(HELPER_START_WAIT_MS) then
        helper_started = false
        log(obs.LOG_ERROR, "ReplayKit helper did not bind port " .. HTTP_PORT .. " after launch")
        return false
    end
    helper_started = true
    start_update_bootstrap()
    return true
end

-- a busy-but-alive helper can legitimately take a couple seconds to answer under real load, so that alone isnt "dead" -- requiring a run of failures before acting is what tells the two apart, since a single bad check firing start_helper() lets even a losing redundant instance post /shutdown at the healthy one on its way out, turning "helper was briefly slow" into "helper gets kicked every ~20s".
local helper_consecutive_fails = 0
local HELPER_UNHEALTHY_THRESHOLD = 3

local function helper_watchdog()
    if helper_responds_ok(2000) then
        helper_started = true
        helper_consecutive_fails = 0
        return
    end
    helper_consecutive_fails = helper_consecutive_fails + 1
    if helper_consecutive_fails < HELPER_UNHEALTHY_THRESHOLD then
        return
    end
    local now = now_ms()
    if helper_started or (now - helper_last_restart_ms) >= HELPER_RESTART_COOLDOWN_MS then
        helper_started = false
        helper_last_restart_ms = now
        helper_consecutive_fails = 0
        -- start_helper() just spawns a fresh OBSReplayKit.exe; that processs own startup notices the port is taken, posts /shutdown, and force-kills whatever didnt respond, so this actually replaces a wedged helper rather than leaving a second one competing for the port -- gated behind 3 consecutive failures since a losing redundant instance posts that same /shutdown even when the survivor isnt actually dead.
        start_helper()
    end
end

local function start_helper_watchdog()
    if helper_watchdog_started then return end
    helper_watchdog_started = true
    obs.timer_add(helper_watchdog, HELPER_WATCHDOG_MS)
end

local function helper_request(path)
    if not start_helper() then return false end
    if not wait_for_helper_port(2000) then return false end
    local url = "http://127.0.0.1:" .. HTTP_PORT .. path
    local cmd = "curl.exe -s -S --max-time 2 -X POST " .. win_quote(url)
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Could not contact ReplayKit helper. Win32 error: " .. tostring(err))
        return false
    end
    return true
end

local function open_url(path)
    if not start_helper() then return false end
    wait_for_helper_port(2000)
    local url = "http://127.0.0.1:" .. HTTP_PORT .. path
    local cmd = "rundll32.exe url.dll,FileProtocolHandler " .. win_quote(url)
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Could not open ReplayKit page. Win32 error: " .. tostring(err))
        return false
    end
    return true
end

local function save_replay()
    if obs.obs_frontend_replay_buffer_save then
        obs.obs_frontend_replay_buffer_save()
        log(obs.LOG_INFO, "Saved clip from ReplayKit menu.")
    end
end

local function trigger_latest_upload()
    if helper_request("/upload-latest") then
        log(obs.LOG_INFO, "Sent latest-clip upload request to the external ReplayKit helper.")
    end
end

local function open_clips()
    open_url("/clips-view")
end

local function open_clip_folder()
    helper_request("/open-clip-folder")
end

local function on_hotkey(pressed)
    if pressed then trigger_latest_upload() end
end

local function on_save_replay_menu_clicked()
    save_replay()
end

local function on_upload_menu_clicked()
    trigger_latest_upload()
end

local function on_open_clips_menu_clicked()
    open_clips()
end

local function on_open_clip_folder_menu_clicked()
    open_clip_folder()
end

function script_description()
    return
        "<b>Upload Latest Clip to Streamable</b><br/><br/>" ..
        "Low-lag rewrite: OBS only launches a separate local helper. " ..
        "ReplayKit actions are available in the Custom Controls dock and " ..
        "OBS's Tools menu. The Clips browser opens only when you choose it."
end

function script_properties()
    local p = obs.obs_properties_create()
    obs.obs_properties_add_path(
        p, "clip_dir",
        "Clip folder (blank = ~\\Pictures\\Videos)",
        obs.OBS_PATH_DIRECTORY,
        "", ""
    )
    return p
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "clip_dir", "")
end

function script_update(settings)
    cfg.clip_dir = obs.obs_data_get_string(settings, "clip_dir")
    write_helper_config()
end

function script_load(settings)
    hotkey_id = obs.obs_hotkey_register_frontend(
        "upload_clip_to_streamable",
        "Upload Latest Clip to Streamable",
        on_hotkey
    )
    local arr = obs.obs_data_get_array(settings, "hotkey")
    obs.obs_hotkey_load(hotkey_id, arr)
    obs.obs_data_array_release(arr)

    if obs.obs_frontend_add_tools_menu_item then
        obs.obs_frontend_add_tools_menu_item(
            "ReplayKit: Save Clip",
            on_save_replay_menu_clicked
        )
        obs.obs_frontend_add_tools_menu_item(
            "ReplayKit: Upload Last Clip",
            on_upload_menu_clicked
        )
        obs.obs_frontend_add_tools_menu_item(
            "ReplayKit: Open Clips",
            on_open_clips_menu_clicked
        )
        obs.obs_frontend_add_tools_menu_item(
            "ReplayKit: Open Clip Folder",
            on_open_clip_folder_menu_clicked
        )
    end

    if start_helper() then
        log(obs.LOG_INFO, "ReplayKit helper launched outside OBS on http://127.0.0.1:" .. HTTP_PORT)
    end
    start_helper_watchdog()
end

function script_save(settings)
    local arr = obs.obs_hotkey_save(hotkey_id)
    obs.obs_data_set_array(settings, "hotkey", arr)
    obs.obs_data_array_release(arr)
end

function script_unload()
    if helper_watchdog_started then
        obs.timer_remove(helper_watchdog)
        helper_watchdog_started = false
    end
    spawn_hidden("curl.exe -s --max-time 1 -X POST " ..
        win_quote("http://127.0.0.1:" .. HTTP_PORT .. "/shutdown"))
    if winsock_started then
        ws2_32.WSACleanup()
        winsock_started = false
    end
end
