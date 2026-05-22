-- streamable_upload.lua low-lag obs entrypoint for streamable uploads. obs should not host clip browsing, video preview streaming, or upload-state polling on its lua/ui thread. this script only starts the external local helper and forwards hotkey/menu upload requests to it. the helper serves the obs dock ui on 127.0.0.1 from a seperate powershell helper process.

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

local HTTP_PORT = 8767
-- global streamable helper logging switch. keep false for production. flip this one constant to true when debugging lua/helper/upload/compress logs.
local STREAMABLE_LOGGING_ENABLED = false
local hotkey_id = obs.OBS_INVALID_HOTKEY_ID

local cfg = {
    clip_dir = "",
}

local function log(level, msg)
    if not STREAMABLE_LOGGING_ENABLED then return end
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

local function json_escape(s)
    return '"' .. tostring(s or "")
        :gsub("\\", "\\\\")
        :gsub('"', '\\"')
        :gsub("\r", "\\r")
        :gsub("\n", "\\n")
        :gsub("\t", "\\t") .. '"'
end

local function helper_config_path()
    return script_dir() .. "helper_config.json"
end

local function helper_path()
    return script_dir() .. "local_helper_server.ps1"
end

local function ps_helper_path()
    return script_dir() .. "upload_worker.ps1"
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
    f:write("  \"psHelper\": " .. json_escape(ps_helper_path()) .. ",\n")
    f:write("  \"loggingEnabled\": " .. tostring(STREAMABLE_LOGGING_ENABLED) .. "\n")
    f:write("}\n")
    f:close()
    return true
end

-- set to true once weve successfully launched the helper this session. re-spawn attempts are skipped while its true (the os port-bind would just fail anyway). stays false if the launch errored so the next helper_request() retries after a failed helper launch.
local helper_started = false

local function start_helper()
    if helper_started then return true end

    local helper = helper_path()
    if not file_exists(helper) then
        log(obs.LOG_ERROR, "Missing helper: " .. helper)
        return false
    end
    if not file_exists(ps_helper_path()) then
        log(obs.LOG_ERROR, "Missing upload helper: " .. ps_helper_path())
        return false
    end
    if not write_helper_config() then return false end

    -- prefer the branded launcher (obsreplaykit.exe) when its been installed next to the helper script. it hosts windows powershell in-process via system.management.automation, so the helper appears in task manager as "obs replaykit" with the obs icon instead of a generic "windows powershell" entry. falls back to spawning powershell.exe directly when the launcher is absent (e.g. fresh clone before apply, or pre-launcher install).
    local launcher = script_dir() .. "OBSReplayKit.exe"
    local cmd
    if file_exists(launcher) then
        cmd = win_quote(launcher) ..
            ' -ConfigPath ' .. win_quote(helper_config_path())
    else
        cmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File ' ..
            win_quote(helper) .. ' -ConfigPath ' .. win_quote(helper_config_path())
    end
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Could not start Streamable helper. Win32 error: " .. tostring(err))
        return false
    end
    helper_started = true
    return true
end

local function helper_request(path)
    start_helper()
    local url = "http://127.0.0.1:" .. HTTP_PORT .. path
    local cmd = "curl.exe -s -S --max-time 2 -X POST " .. win_quote(url)
    local ok, err = spawn_hidden(cmd)
    if not ok then
        log(obs.LOG_ERROR, "Could not contact Streamable helper. Win32 error: " .. tostring(err))
        return false
    end
    return true
end

local function open_url(path)
    if not start_helper() then return false end
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
        log(obs.LOG_INFO, "Sent latest-clip upload request to the external Streamable helper.")
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
        log(obs.LOG_INFO, "Streamable helper launched outside OBS on http://127.0.0.1:" .. HTTP_PORT)
    end
end

function script_save(settings)
    local arr = obs.obs_hotkey_save(hotkey_id)
    obs.obs_data_set_array(settings, "hotkey", arr)
    obs.obs_data_array_release(arr)
end

function script_unload()
    spawn_hidden("curl.exe -s --max-time 1 -X POST " ..
        win_quote("http://127.0.0.1:" .. HTTP_PORT .. "/shutdown"))
end
