obs = obslua
ffi = require("ffi")

local cfg = {
    game_source    = "Game Capture",
    display_source = "Display Capture",
    window_source  = "Window Capture",
    scene_name     = "",
    check_interval = 1000,
    verbose        = false,
}

local MAX_SETTINGS_BYTES = 65536
local DEFAULT_CAPTURE_MODE = "hybrid_auto"
local MIN_DESKTOP_SWITCH_GRACE_MS = 50
local MAX_DESKTOP_SWITCH_GRACE_MS = 5000

local IGNORE_LIST = {
    ["obs64.exe"]                   = true,
    ["obs32.exe"]                   = true,
    ["obs.exe"]                     = true,
    ["explorer.exe"]                = true,
    ["searchhost.exe"]              = true,
    ["searchapp.exe"]               = true,
    ["shellexperiencehost.exe"]     = true,
    ["startmenuexperiencehost.exe"] = true,
    ["applicationframehost.exe"]    = true,
    ["video.ui.exe"]                = true,
    ["systemsettings.exe"]          = true,
    ["textinputhost.exe"]           = true,
    ["lockapp.exe"]                 = true,
    ["logonui.exe"]                 = true,
    ["dwm.exe"]                     = true,
    ["taskmgr.exe"]                 = true,
    ["snippingtool.exe"]            = true,
    ["screenclippinghost.exe"]      = true,
    -- robloxs player/client hooks are unreliable for this workflow; keep it on Display Capture.
    ["robloxplayer.exe"]            = true,
    ["robloxplayerbeta.exe"]        = true,
    ["robloxplayerlauncher.exe"]    = true,
    ["robloxstudiobeta.exe"]        = true,
    ["windows10universal.exe"]      = true,
}

local FORCED_GAME_BLOCKED = {
    ["obs64.exe"]                   = true,
    ["obs32.exe"]                   = true,
    ["obs.exe"]                     = true,
    ["explorer.exe"]                = true,
    ["searchhost.exe"]              = true,
    ["searchapp.exe"]               = true,
    ["shellexperiencehost.exe"]     = true,
    ["startmenuexperiencehost.exe"] = true,
    ["applicationframehost.exe"]    = true,
    ["video.ui.exe"]                = true,
    ["systemsettings.exe"]          = true,
    ["textinputhost.exe"]           = true,
    ["lockapp.exe"]                 = true,
    ["logonui.exe"]                 = true,
    ["dwm.exe"]                     = true,
    ["taskmgr.exe"]                 = true,
    ["snippingtool.exe"]            = true,
    ["screenclippinghost.exe"]      = true,
}

local GRAPHICS_APIS = {
    ["d3d9.dll"]     = true,
    ["d3d10.dll"]    = true,
    ["d3d11.dll"]    = true,
    ["d3d12.dll"]    = true,
    ["opengl32.dll"] = true,
    ["vulkan-1.dll"] = true,
}

local hook_blocked             = {}
local window_fallback          = {}
local graphics_api_seen        = {}
local HOOK_CHECK_INITIAL_DELAY = 2000
local HOOK_CHECK_INTERVAL      = 1000
local HOOK_CHECK_MAX_ATTEMPTS  = 10
local DEFAULT_DESKTOP_SWITCH_GRACE_MS = 1000
local WINDOW_STALL_TICKS       = 3
-- a capture that stays dead (the display asleep, the target in a state that cannot be captured) is retried less and less often, up to this many ticks apart, so it neither floods the log nor keeps tearing the capture down
local WINDOW_STALL_MAX_TICKS   = 300
local pending_exe              = nil
local pending_attempts         = 0
local hook_check_scheduled     = false
local desktop_switch_scheduled = false
local desktop_switch_grace_ms  = DEFAULT_DESKTOP_SWITCH_GRACE_MS
local last_window_token        = nil
local window_stall_count       = 0
local window_stall_limit       = WINDOW_STALL_TICKS
local sticky_window_hwnd       = nil
local sticky_window_exe        = nil

ffi.cdef[[
    typedef void*          HWND;
    typedef void*          HANDLE;
    typedef void*          HMODULE;
    typedef void*          HMONITOR;
    typedef unsigned long  DWORD;
    typedef long           LONG;
    typedef int            BOOL;

    typedef struct { LONG left; LONG top; LONG right; LONG bottom; } RECT;

    typedef struct {
        DWORD   cbSize;
        RECT    rcMonitor;
        RECT    rcWork;
        DWORD   dwFlags;
    } MONITORINFO;

    typedef struct { MONITORINFO info; char device[32]; } CAPTURE_MONITOR_INFO;
    typedef struct {
        DWORD cb;
        char DeviceName[32];
        char DeviceString[128];
        DWORD StateFlags;
        char DeviceID[128];
        char DeviceKey[128];
    } CAPTURE_DISPLAY_DEVICE;

    HWND     GetForegroundWindow();
    BOOL     IsWindow(HWND hWnd);
    BOOL     IsIconic(HWND hWnd);
    BOOL     GetWindowRect(HWND hWnd, RECT* lpRect);
    LONG     GetWindowLongA(HWND hWnd, int nIndex);
    HMONITOR MonitorFromWindow(HWND hwnd, DWORD dwFlags);
    BOOL     GetMonitorInfoA(HMONITOR hMonitor, MONITORINFO* lpmi);
    BOOL     EnumDisplayDevicesA(const char* device, DWORD index, CAPTURE_DISPLAY_DEVICE* info, DWORD flags);
    DWORD    GetWindowThreadProcessId(HWND hWnd, DWORD* lpdwProcessId);
    DWORD    GetCurrentProcessId();
    HANDLE   OpenProcess(DWORD dwDesiredAccess, BOOL bInheritHandle, DWORD dwProcessId);
    BOOL     CloseHandle(HANDLE hObject);
    BOOL     QueryFullProcessImageNameA(HANDLE hProcess, DWORD dwFlags, char* lpExeName, DWORD* lpdwSize);
    int      GetWindowTextLengthA(HWND hWnd);
    int      GetWindowTextA(HWND hWnd, char* lpString, int nMaxCount);
    int      GetClassNameA(HWND hWnd, char* lpClassName, int nMaxCount);
    HWND     GetDesktopWindow();
    HWND     GetShellWindow();
    BOOL     IsWindowVisible(HWND hWnd);
    BOOL     EnumProcessModules(HANDLE hProcess, HMODULE* lphModule, DWORD cb, DWORD* lpcbNeeded);
    DWORD    GetModuleFileNameExA(HANDLE hProcess, HMODULE hModule, char* lpFilename, DWORD nSize);
]]

local psapi = ffi.load("psapi")

local GWL_STYLE                 = -16
local WS_CAPTION                = 0x00C00000
local MONITOR_DEFAULTTONEAREST  = 0x00000002
local PROCESS_QUERY_INFORMATION = 0x0400
local PROCESS_VM_READ           = 0x0010

local function log(msg)
    if cfg.verbose then print("[AutoSwitch] " .. msg) end
end

local function normalize_path(path)
    return (path or ""):gsub("\\", "/")
end

local function parent_dir(path)
    path = normalize_path(path):gsub("/+$", "")
    return path:match("^(.*)/[^/]+$") or path
end

local function join_path(a, b)
    a = normalize_path(a):gsub("/+$", "")
    b = normalize_path(b):gsub("^/+", "")
    if a == "" then return b end
    return a .. "/" .. b
end

local function read_text(path)
    local file = io.open(path, "rb")
    if not file then return nil end
    local text = file:read(MAX_SETTINGS_BYTES + 1)
    file:close()
    if text and #text > MAX_SETTINGS_BYTES then return nil end
    if text then text = text:gsub("^\239\187\191", "") end
    return text
end

-- the settings file sits in the replaykit folder, one level above scripts; an install from before that move keeps it in scripts until the helper has moved it
local function runtime_settings_path()
    local scripts_dir = parent_dir(script_path())
    local current = join_path(parent_dir(scripts_dir), "replaykit_settings.json")
    local file = io.open(current, "rb")
    if file then file:close() return current end
    return join_path(scripts_dir, "replaykit_settings.json")
end

local function valid_capture_mode(mode)
    return mode == "hybrid_auto" or mode == "desktop" or mode == "game_auto" or mode == "game_window"
end

local function valid_exe_name(exe)
    return exe ~= nil and exe:match('^[^\\/:*?"<>|]+%.exe$') ~= nil
end

local function read_capture_policy()
    local policy = { mode = DEFAULT_CAPTURE_MODE, auto_games = {}, keep_focused = false, switch_grace_ms = DEFAULT_DESKTOP_SWITCH_GRACE_MS }
    local text = read_text(runtime_settings_path())
    if not text or text == "" then return policy end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then return policy end
    local mode = obs.obs_data_get_string(data, "screenshareCaptureMode")
    if valid_capture_mode(mode) then
        policy.mode = mode
    end
    policy.keep_focused = obs.obs_data_get_bool(data, "screenshareAutoGameKeepFocused")

    -- ui stores this in seconds (decimals down to 0.05s); grace period itself runs in ms
    local delay_seconds = obs.obs_data_get_double(data, "screenshareSwitchDelaySeconds")
    if delay_seconds > 0 then
        local grace_ms = math.floor(delay_seconds * 1000 + 0.5)
        if grace_ms >= MIN_DESKTOP_SWITCH_GRACE_MS and grace_ms <= MAX_DESKTOP_SWITCH_GRACE_MS then
            policy.switch_grace_ms = grace_ms
        end
    end

    local overrides = obs.obs_data_get_array(data, "screenshareGameOverrides")
    if overrides ~= nil then
        local count = math.min(obs.obs_data_array_count(overrides), 32)
        for i = 0, count - 1 do
            local item = obs.obs_data_array_item(overrides, i)
            if item ~= nil then
                local exe = obs.obs_data_get_string(item, "exeName")
                if exe ~= nil then
                    local exe_lower = exe:lower()
                    if valid_exe_name(exe_lower) and not FORCED_GAME_BLOCKED[exe_lower] then
                        policy.auto_games[exe_lower] = true
                    end
                end
                obs.obs_data_release(item)
            end
        end
        obs.obs_data_array_release(overrides)
    end

    obs.obs_data_release(data)
    return policy
end

local function read_capture_mode()
    return read_capture_policy().mode
end

local function open_process(hwnd)
    local pid = ffi.new("unsigned long[1]", 0)
    ffi.C.GetWindowThreadProcessId(hwnd, pid)
    if pid[0] == 0 then return nil end
    local handle = ffi.C.OpenProcess(
        bit.bor(PROCESS_QUERY_INFORMATION, PROCESS_VM_READ), false, pid[0])
    return (handle ~= nil) and handle or nil
end

local function get_exe_from_hwnd(hwnd)
    local handle = open_process(hwnd)
    if handle == nil then return nil end
    local buf  = ffi.new("char[512]")
    local size = ffi.new("unsigned long[1]", 512)
    local ok   = ffi.C.QueryFullProcessImageNameA(handle, 0, buf, size)
    ffi.C.CloseHandle(handle)
    if ok == 0 then return nil end
    return ffi.string(buf, size[0]):match("([^\\]+)$"):lower()
end

local is_valid_foreground_window

-- a title read on a window of this process is sent to the obs ui thread and waits for it, and the ui thread can be waiting for the lock of this script (obs froze that way when a recording started), so windows of obs itself are never asked, they are not games anyway
local function get_window_text(hwnd)
    local owner = ffi.new("unsigned long[1]", 0)
    ffi.C.GetWindowThreadProcessId(hwnd, owner)
    if owner[0] == 0 or owner[0] == ffi.C.GetCurrentProcessId() then return "" end
    local len = ffi.C.GetWindowTextLengthA(hwnd)
    if len <= 0 then return "" end
    if len > 512 then len = 512 end
    local buf = ffi.new("char[?]", len + 1)
    local copied = ffi.C.GetWindowTextA(hwnd, buf, len + 1)
    if copied <= 0 then return "" end
    return ffi.string(buf, copied)
end

local function reset_window_stall()
    window_stall_count = 0
    window_stall_limit = WINDOW_STALL_TICKS
end

local function get_window_class(hwnd)
    local buf = ffi.new("char[256]")
    local copied = ffi.C.GetClassNameA(hwnd, buf, 256)
    if copied <= 0 then return "" end
    return ffi.string(buf, copied)
end

local function escape_window_token_part(value)
    value = tostring(value or "")
    value = value:gsub("#", "#22")
    value = value:gsub(":", "#3A")
    return value
end

local function window_token_from_hwnd(hwnd)
    if not is_valid_foreground_window(hwnd) then return nil end
    local title = get_window_text(hwnd)
    local class_name = get_window_class(hwnd)
    local exe = get_exe_from_hwnd(hwnd)
    if title == "" or class_name == "" or exe == nil or exe == "" then return nil end
    return string.format("%s:%s:%s",
        escape_window_token_part(title),
        escape_window_token_part(class_name),
        escape_window_token_part(exe))
end

local function update_window_capture_source(hwnd)
    local token = window_token_from_hwnd(hwnd)
    if token == nil then return false end

    local source = obs.obs_get_source_by_name(cfg.window_source)
    if source == nil then return false end
    -- the memo alone is not enough: a settings save or the user can point the source at another window, so what it captures now is read back before a push is skipped
    if token == last_window_token then
        local current = obs.obs_source_get_settings(source)
        local window = obs.obs_data_get_string(current, "window")
        obs.obs_data_release(current)
        if window == token then
            obs.obs_source_release(source)
            return true
        end
    end

    local settings = obs.obs_data_create()
    obs.obs_data_set_string(settings, "window", token)
    obs.obs_data_set_int(settings, "method", 2)
    obs.obs_data_set_int(settings, "priority", 0)
    obs.obs_data_set_bool(settings, "cursor", true)
    obs.obs_data_set_bool(settings, "client_area", true)
    obs.obs_data_set_bool(settings, "compatibility", false)
    obs.obs_data_set_bool(settings, "capture_audio", false)
    obs.obs_source_update(source, settings)
    obs.obs_data_release(settings)
    obs.obs_source_release(source)

    last_window_token = token
    return true
end

local function get_exe_name(handle)
    local buf  = ffi.new("char[512]")
    local size = ffi.new("unsigned long[1]", 512)
    local ok   = ffi.C.QueryFullProcessImageNameA(handle, 0, buf, size)
    if ok == 0 then return nil end
    return ffi.string(buf, size[0]):match("([^\\]+)$")
end

local function has_graphics_api(handle)
    local needed = ffi.new("DWORD[1]", 0)
    psapi.EnumProcessModules(handle, nil, 0, needed)
    if needed[0] == 0 then return false end

    local count   = math.floor(needed[0] / ffi.sizeof("HMODULE")) + 64
    local modules = ffi.new("HMODULE[?]", count)
    local ok      = psapi.EnumProcessModules(handle, modules, needed[0], needed)
    if ok == 0 then return false end

    local mod_count = math.floor(needed[0] / ffi.sizeof("HMODULE"))
    local name_buf  = ffi.new("char[512]")

    for i = 0, mod_count - 1 do
        local len = psapi.GetModuleFileNameExA(handle, modules[i], name_buf, 512)
        if len > 0 then
            local mod_name = ffi.string(name_buf, len):match("([^\\]+)$"):lower()
            if GRAPHICS_APIS[mod_name] then
                log("Graphics API found: " .. mod_name)
                return true
            end
        end
    end
    return false
end

local function game_capture_is_live()
    local scenes = obs.obs_frontend_get_scenes()
    if scenes == nil then return false end
    local live = false
    for _, scene_source in ipairs(scenes) do
        local scene = obs.obs_scene_from_source(scene_source)
        if scene then
            local items = obs.obs_scene_enum_items(scene)
            if items then
                for _, item in ipairs(items) do
                    local src  = obs.obs_sceneitem_get_source(item)
                    local name = obs.obs_source_get_name(src)
                    if name == cfg.game_source then
                        if obs.obs_source_get_width(src) > 0 and
                           obs.obs_source_get_height(src) > 0 then
                            live = true
                        end
                    end
                end
                obs.sceneitem_list_release(items)
            end
        end
    end
    obs.source_list_release(scenes)
    return live
end

local function window_capture_is_live()
    local source = obs.obs_get_source_by_name(cfg.window_source)
    if source == nil then return false end
    local live = obs.obs_source_get_width(source) > 0 and
                 obs.obs_source_get_height(source) > 0
    obs.obs_source_release(source)
    return live
end

local function set_visible_in_scene(scene_source, source_name, visible)
    local scene = obs.obs_scene_from_source(scene_source)
    if scene == nil then return end
    local items = obs.obs_scene_enum_items(scene)
    if items == nil then return end
    for _, item in ipairs(items) do
        local src  = obs.obs_sceneitem_get_source(item)
        local name = obs.obs_source_get_name(src)
        if name == source_name then
            obs.obs_sceneitem_set_visible(item, visible)
        end
    end
    obs.sceneitem_list_release(items)
end

local function apply_switch(state, hwnd)
    if state == "window" and not update_window_capture_source(hwnd) then
        log("Window Capture source could not be updated; falling back to desktop")
        state = "desktop"
    end

    local scenes = obs.obs_frontend_get_scenes()
    if scenes == nil then return end
    for _, scene_source in ipairs(scenes) do
        local name = obs.obs_source_get_name(scene_source)
        if cfg.scene_name == "" or cfg.scene_name == name then
            set_visible_in_scene(scene_source, cfg.game_source, state == "game")
            set_visible_in_scene(scene_source, cfg.window_source, state == "window")
            set_visible_in_scene(scene_source, cfg.display_source, state == "desktop")
        end
    end
    obs.source_list_release(scenes)
end

local hook_check_callback
local desktop_switch_callback

local function schedule_hook_check(delay_ms)
    if hook_check_scheduled then
        obs.timer_remove(hook_check_callback)
    end
    hook_check_scheduled = true
    obs.timer_add(hook_check_callback, delay_ms)
end

local function cancel_hook_check()
    if hook_check_scheduled then
        obs.timer_remove(hook_check_callback)
        hook_check_scheduled = false
    end
    pending_exe      = nil
    pending_attempts = 0
end

local function cancel_desktop_switch()
    if desktop_switch_scheduled then
        obs.timer_remove(desktop_switch_callback)
        desktop_switch_scheduled = false
    end
end

local function schedule_desktop_switch()
    if desktop_switch_scheduled then return end
    desktop_switch_scheduled = true
    obs.timer_add(desktop_switch_callback, desktop_switch_grace_ms)
end

local last_state = nil

-- named function so obs.timer_remove can actualy find and kill it
function hook_check_callback()
    -- always remove self first -- this is a one-shot
    obs.timer_remove(hook_check_callback)
    hook_check_scheduled = false

    local checked_exe = pending_exe

    -- if theres no exe we were checking, nothing to do
    if checked_exe == nil then return end

    -- verify the same game is still in the foreground so a tabbed-out game never gets marked blocked. a transient popup can steal focus exactly when this timer fires; abandoning here would leave last_state stuck on game with the hook never verified, so defer the check instead. a real tab-out cancels it thru cancel_hook_check on the next state switch.
    local current_hwnd = ffi.C.GetForegroundWindow()
    local current_exe = get_exe_from_hwnd(current_hwnd)
    if current_exe ~= checked_exe then
        log("Hook check deferred — " .. checked_exe .. " is not foreground right now")
        schedule_hook_check(HOOK_CHECK_INTERVAL)
        return
    end

    if game_capture_is_live() then
        log("Hook confirmed live for " .. checked_exe)
        pending_exe      = nil
        pending_attempts = 0
        return
    end

    pending_attempts = pending_attempts + 1
    if pending_attempts < HOOK_CHECK_MAX_ATTEMPTS then
        log(string.format("Waiting for Game Capture hook for %s (%d/%d)",
            checked_exe, pending_attempts, HOOK_CHECK_MAX_ATTEMPTS))
        schedule_hook_check(HOOK_CHECK_INTERVAL)
        return
    end

    -- still 0x0 and still in the foreground — genuinely hook-blocked
    print(string.format(
        "[AutoSwitch] Game Capture got no frame from '%s' after %d checks — falling back to Window Capture",
        checked_exe, HOOK_CHECK_MAX_ATTEMPTS))
    hook_blocked[checked_exe] = true
    window_fallback[checked_exe] = true
    pending_exe              = nil
    pending_attempts         = 0
    last_state               = "window"
    apply_switch("window", current_hwnd)
end

is_valid_foreground_window = function(hwnd)
    if hwnd == nil then return false end
    if ffi.C.IsWindow(hwnd) == 0 then return false end
    if ffi.C.IsWindowVisible(hwnd) == 0 then return false end
    if ffi.C.IsIconic(hwnd) ~= 0 then return false end
    if hwnd == ffi.C.GetDesktopWindow() then return false end
    if hwnd == ffi.C.GetShellWindow()   then return false end
    return true
end

local function clear_sticky_window()
    sticky_window_hwnd = nil
    sticky_window_exe  = nil
end

local function auto_game_match(hwnd, auto_games)
    if not is_valid_foreground_window(hwnd) then return false end
    if auto_games == nil then return false end

    local exe = get_exe_from_hwnd(hwnd)
    if exe == nil then return false end
    if FORCED_GAME_BLOCKED[exe] then return false end
    if auto_games[exe] then return true, exe end
    return false
end

local function is_forced_game_window(hwnd, auto_games)
    local matched, exe = auto_game_match(hwnd, auto_games)
    if matched then
        log("Auto Game List matched: " .. exe)
        return true, exe
    end
    return false
end

local function sticky_auto_game_window(policy, foreground_hwnd)
    -- tracked regardless of the toggle so turning keep_focused on mid-session has an immediate target instead of only arming once the game is foregrounded again
    local foreground_match, foreground_exe = auto_game_match(foreground_hwnd, policy.auto_games)
    if foreground_match then
        sticky_window_hwnd = foreground_hwnd
        sticky_window_exe  = foreground_exe
    end

    if not policy.keep_focused then
        return false
    end
    if foreground_match then
        return true, foreground_exe, foreground_hwnd
    end

    local sticky_match, sticky_exe = auto_game_match(sticky_window_hwnd, policy.auto_games)
    if sticky_match then
        return true, sticky_exe, sticky_window_hwnd
    end

    clear_sticky_window()
    return false
end

local function is_window_fallback(hwnd)
    if not is_valid_foreground_window(hwnd) then return false end
    local exe = get_exe_from_hwnd(hwnd)
    if exe == nil then return false end
    if window_fallback[exe] then
        log("Window fallback: " .. exe)
        return true, exe
    end
    return false
end

local function is_exclusive_fullscreen(hwnd)
    if not is_valid_foreground_window(hwnd) then return false end

    local handle = open_process(hwnd)
    if handle == nil then return false end

    local exe = get_exe_name(handle)
    if exe == nil then ffi.C.CloseHandle(handle) return false end

    local exe_lower = exe:lower()

    if IGNORE_LIST[exe_lower] then
        log("Ignored: " .. exe)
        ffi.C.CloseHandle(handle)
        return false
    end

    if hook_blocked[exe_lower] then
        log("Hook-blocked: " .. exe)
        ffi.C.CloseHandle(handle)
        return false
    end

    local wrect = ffi.new("RECT")
    if ffi.C.GetWindowRect(hwnd, wrect) == 0 then
        ffi.C.CloseHandle(handle)
        return false
    end

    local hmon  = ffi.C.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    local minfo = ffi.new("MONITORINFO")
    minfo.cbSize = ffi.sizeof("MONITORINFO")
    if ffi.C.GetMonitorInfoA(hmon, minfo) == 0 then
        ffi.C.CloseHandle(handle)
        return false
    end

    local mw = minfo.rcMonitor.right  - minfo.rcMonitor.left
    local mh = minfo.rcMonitor.bottom - minfo.rcMonitor.top
    local ww = wrect.right  - wrect.left
    local wh = wrect.bottom - wrect.top

    if ww ~= mw or wh ~= mh then
        log(string.format("%s: %dx%d vs monitor %dx%d — not fullscreen", exe, ww, wh, mw, mh))
        ffi.C.CloseHandle(handle)
        return false
    end

    local style       = ffi.C.GetWindowLongA(hwnd, GWL_STYLE)
    local has_caption = bit.band(style, WS_CAPTION) ~= 0
    if has_caption then
        log(exe .. " is borderless windowed fullscreen")
    end

    if not graphics_api_seen[exe_lower] and not has_graphics_api(handle) then
        log(exe .. " has no graphics API — not a game")
        ffi.C.CloseHandle(handle)
        return false
    end
    graphics_api_seen[exe_lower] = true

    ffi.C.CloseHandle(handle)
    return true, exe_lower
end

local function detect_capture_state(hwnd, policy)
    local sticky, sticky_exe, sticky_hwnd = sticky_auto_game_window(policy, hwnd)
    if sticky then return "window", sticky_exe, sticky_hwnd end

    local forced, forced_exe = is_forced_game_window(hwnd, policy.auto_games)
    if forced then return "window", forced_exe, hwnd end
    local fallback, fallback_exe = is_window_fallback(hwnd)
    if fallback then return "window", fallback_exe, hwnd end
    local fullscreen, fullscreen_exe = is_exclusive_fullscreen(hwnd)
    if fullscreen then return "game", fullscreen_exe, hwnd end
    return "desktop", nil
end

function desktop_switch_callback()
    obs.timer_remove(desktop_switch_callback)
    desktop_switch_scheduled = false

    local policy = read_capture_policy()
    if policy.mode ~= "hybrid_auto" then return end

    local hwnd = ffi.C.GetForegroundWindow()
    local state = detect_capture_state(hwnd, policy)
    if state ~= "desktop" then
        log("Desktop switch cancelled — capture target returned before grace elapsed")
        return
    end

    cancel_hook_check()
    last_state = "desktop"
    apply_switch("desktop")
    print(string.format("[AutoSwitch] → DESKTOP MODE [%s]", cfg.display_source))
end

local monitor_check_at = 0
local monitor_warning = nil
-- the display the capture is meant to show, kept on the source beside monitor_id and set from the Display setting of replaykit: "primary" follows whichever display windows calls primary, anything else is one display; monitor_id itself only says what it shows right now
local WANTED_MONITOR_KEY = "replaykit_wanted_monitor_id"
-- what this script last put in monitor_id, so a display picked in the properties of the source is told apart from a fallback of this script
local last_applied_monitor = nil
-- a switch this script makes has to hold for this many checks in a row: while a game changes its mode or a monitor wakes up, a display drops out and windows hands the primary to another one for a moment, and following that is how the capture ended up on the second display
local pending_monitor = nil
local pending_checks = 0
local PENDING_CHECK_LIMIT = 2

local function primary_monitor_ids()
    local monitor = ffi.C.MonitorFromWindow(nil, 1)
    local info = ffi.new("CAPTURE_MONITOR_INFO")
    info.info.cbSize = ffi.sizeof(info)
    if ffi.C.GetMonitorInfoA(monitor, ffi.cast("MONITORINFO*", info)) == 0 then return nil, nil end
    local adapter = ffi.string(info.device)
    local device = ffi.new("CAPTURE_DISPLAY_DEVICE")
    device.cb = ffi.sizeof(device)
    local id = nil
    if ffi.C.EnumDisplayDevicesA(adapter, 0, device, 1) ~= 0 then id = ffi.string(device.DeviceID) end
    return id, adapter
end

local function repair_display_capture()
    local now = obs.os_gettime_ns()
    if now < monitor_check_at then return end
    local source = obs.obs_get_source_by_name(cfg.display_source)
    if source == nil then monitor_check_at = now + 1000000000; return end
    monitor_check_at = now + 5 * 1000000000
    local settings = obs.obs_source_get_settings(source)
    -- Live source properties run OBS's modified callbacks and reset display capture.
    local properties = obs.obs_get_source_properties(obs.obs_source_get_id(source))
    local ok, err = pcall(function()
        if settings == nil or properties == nil then return end
        local property = obs.obs_properties_get(properties, "monitor_id")
        if property == nil then return end
        local selected = obs.obs_data_get_string(settings, "monitor_id") or ""
        local choices = {}
        for i = 0, obs.obs_property_list_item_count(property) - 1 do
            local value = obs.obs_property_list_item_string(property, i)
            if value ~= nil and value ~= "" and not obs.obs_property_list_item_disabled(property, i) then
                choices[#choices + 1] = value
            end
        end
        local function find(value)
            if value == nil or value == "" then return nil end
            for _, choice in ipairs(choices) do
                if choice:lower() == value:lower() then return choice end
            end
            return nil
        end
        local function primary_choice()
            local id, adapter = primary_monitor_ids()
            return find(id) or find(adapter)
        end

        local function resolve(wanted_id)
            if wanted_id == "" or wanted_id == "primary" then return primary_choice() end
            return find(wanted_id)
        end

        local wanted = obs.obs_data_get_string(settings, WANTED_MONITOR_KEY) or ""
        local wanted_changed = false
        if wanted == "" then
            -- the installer sets up the primary display, and a source from before this key may hold a fallback this script saved over it
            wanted = "primary"
            wanted_changed = true
        elseif last_applied_monitor ~= nil and selected ~= last_applied_monitor and find(selected) and resolve(wanted) ~= find(selected) then
            -- someone picked a display in the properties of the source, and that is the one to keep from now on
            wanted = find(selected)
            wanted_changed = true
        end

        local desired = resolve(wanted)
        -- a wanted display that is gone leaves the capture where it is, or on the primary when that is gone too, and it goes back by itself once the display is back
        local target = desired or find(selected) or primary_choice()
        if target == nil then
            if monitor_warning ~= selected then
                monitor_warning = selected
                obs.script_log(obs.LOG_WARNING, "[AutoSwitch] Display Capture has no matching active monitor. Choose a display in its Properties.")
            end
            return
        end
        monitor_warning = nil

        local switching = find(selected) ~= target
        if not switching then
            pending_monitor = nil
            pending_checks = 0
        elseif last_applied_monitor ~= nil then
            -- the first check after obs starts switches at once, every later switch waits to see the change hold
            if pending_monitor == target then pending_checks = pending_checks + 1 else pending_monitor = target; pending_checks = 1 end
            if pending_checks < PENDING_CHECK_LIMIT then
                switching = false
                target = selected
            else
                pending_monitor = nil
                pending_checks = 0
            end
        end

        if wanted_changed then obs.obs_data_set_string(settings, WANTED_MONITOR_KEY, wanted) end
        if switching or wanted_changed then
            if switching then obs.obs_data_set_string(settings, "monitor_id", target) end
            obs.obs_source_update(source, settings)
            if switching then
                obs.script_log(obs.LOG_INFO, target == desired
                    and "[AutoSwitch] Display Capture is on its chosen display."
                    or "[AutoSwitch] Display Capture's chosen display is missing, showing another one until it is back.")
            end
        end
        last_applied_monitor = target
    end)
    if properties ~= nil then obs.obs_properties_destroy(properties) end
    if settings ~= nil then obs.obs_data_release(settings) end
    obs.obs_source_release(source)
    if not ok then obs.script_log(obs.LOG_WARNING, "[AutoSwitch] Monitor selection check failed: " .. tostring(err)) end
end

local function check_fullscreen()
    repair_display_capture()
    local policy = read_capture_policy()
    -- picked up live so the slider takes effect without a script reload
    desktop_switch_grace_ms = policy.switch_grace_ms

    if policy.mode ~= "hybrid_auto" then
        cancel_hook_check()
        cancel_desktop_switch()
        last_state = nil
        last_window_token = nil
        reset_window_stall()
        clear_sticky_window()
        return
    end

    local hwnd       = ffi.C.GetForegroundWindow()
    local state, exe, target_hwnd = detect_capture_state(hwnd, policy)

    if state == "window" then
        cancel_hook_check()
        cancel_desktop_switch()
        if last_state ~= "window" or update_window_capture_source(target_hwnd) then
            if last_state ~= "window" then
                print(string.format("[AutoSwitch] %s → WINDOW MODE [%s]",
                    exe or "unknown", cfg.window_source))
                reset_window_stall()
            end
            last_state = "window"
            apply_switch("window", target_hwnd)
        end
        -- wgc sessions can die silently when the target flips display mode right as capture starts. the token memo then blocks every re-push, so the source sits at 0x0 until something recreates the session -- thats the stuck-until-alt-tab bug. after a few dead ticks clear the memo and re-push, which forces the capture to reinit.
        if last_state == "window" then
            if window_capture_is_live() then
                reset_window_stall()
            else
                window_stall_count = window_stall_count + 1
                if window_stall_count >= window_stall_limit then
                    window_stall_count = 0
                    last_window_token = nil
                    -- written down on the first try and each time the wait grows; a capture that stays dead for hours would fill the log otherwise
                    local grown = math.min(window_stall_limit * 2, WINDOW_STALL_MAX_TICKS)
                    if window_stall_limit == WINDOW_STALL_TICKS or grown ~= window_stall_limit then
                        print(string.format("[AutoSwitch] Window Capture stalled at 0x0 — reinitializing capture (next try in %d s if it stays dead)",
                            math.floor(grown * cfg.check_interval / 1000 + 0.5)))
                    end
                    window_stall_limit = grown
                    update_window_capture_source(target_hwnd)
                end
            end
        end
        return
    end

    if state == "game" then
        cancel_desktop_switch()
        if last_state ~= "game" then
            last_state = "game"
            apply_switch("game", target_hwnd)
            pending_exe      = exe
            pending_attempts = 0
            schedule_hook_check(HOOK_CHECK_INITIAL_DELAY)
            print(string.format("[AutoSwitch] %s → GAME MODE [%s] (verifying hook...)",
                exe or "unknown", cfg.game_source))
        end
        return
    end

    if last_state ~= nil and last_state ~= "desktop" then
        schedule_desktop_switch()
        return
    end

    if last_state ~= "desktop" then
        last_state = "desktop"
        apply_switch("desktop")
        print(string.format("[AutoSwitch] → DESKTOP MODE [%s]", cfg.display_source))
    end
end

function script_description()
    return [[<b>Universal Fullscreen Auto-Switch</b><br><br>
Detects exclusive fullscreen games via graphics API (D3D/Vulkan/OpenGL).<br>
Automatically falls back to Window Capture if Game Capture hook is blocked (e.g. anti-cheat).<br>
Game Capture stays active through brief focus changes to avoid hook churn.]]
end

function script_properties()
    local props = obs.obs_properties_create()
    obs.obs_properties_add_text(props, "game_source",    "Game Capture source name",       obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(props, "display_source", "Display Capture source name",     obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(props, "window_source",  "Window Capture source name",      obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(props, "scene_name",     "Scene to apply to (blank = all)", obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_int (props, "check_interval", "Check interval (ms)", 500, 5000, 250)
    obs.obs_properties_add_bool(props, "verbose",        "Verbose logging")
    return props
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "game_source",    "Game Capture")
    obs.obs_data_set_default_string(settings, "display_source", "Display Capture")
    obs.obs_data_set_default_string(settings, "window_source",  "Window Capture")
    obs.obs_data_set_default_string(settings, "scene_name",     "")
    obs.obs_data_set_default_int   (settings, "check_interval", 1000)
    obs.obs_data_set_default_bool  (settings, "verbose",        false)
end

function script_update(settings)
    obs.timer_remove(check_fullscreen)
    obs.timer_remove(hook_check_callback)
    obs.timer_remove(desktop_switch_callback)
    hook_check_scheduled = false
    desktop_switch_scheduled = false
    pending_exe          = nil
    pending_attempts     = 0
    cfg.game_source    = obs.obs_data_get_string(settings, "game_source")
    cfg.display_source = obs.obs_data_get_string(settings, "display_source")
    cfg.window_source  = obs.obs_data_get_string(settings, "window_source")
    cfg.scene_name     = obs.obs_data_get_string(settings, "scene_name")
    cfg.check_interval = math.max(obs.obs_data_get_int(settings, "check_interval"), 1000)
    cfg.verbose        = obs.obs_data_get_bool  (settings, "verbose")
    last_state  = nil
    last_window_token = nil
    reset_window_stall()
    clear_sticky_window()
    hook_blocked = {}
    window_fallback = {}
    graphics_api_seen = {}
    monitor_check_at = 0
    monitor_warning = nil
    desktop_switch_grace_ms = DEFAULT_DESKTOP_SWITCH_GRACE_MS
    repair_display_capture()
    obs.timer_add(check_fullscreen, cfg.check_interval)
    print("[AutoSwitch] Active — dynamic hook detection enabled")
end

function script_load(settings)   script_update(settings) end
function script_unload()
    obs.timer_remove(check_fullscreen)
    obs.timer_remove(hook_check_callback)
    obs.timer_remove(desktop_switch_callback)
    print("[AutoSwitch] Unloaded.")
end
