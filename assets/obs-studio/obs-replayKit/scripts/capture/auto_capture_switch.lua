obs = obslua
ffi = require("ffi")

local cfg = {
    game_source    = "Game Capture",
    display_source = "Display Capture",
    scene_name     = "",
    check_interval = 1000,
    verbose        = false,
}

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
    -- Roblox's player/client hooks are unreliable for this workflow; keep it on Display Capture.
    ["robloxplayer.exe"]            = true,
    ["robloxplayerbeta.exe"]        = true,
    ["robloxplayerlauncher.exe"]    = true,
    ["robloxstudiobeta.exe"]        = true,
    ["windows10universal.exe"]      = true,
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
local graphics_api_seen        = {}
local HOOK_CHECK_INITIAL_DELAY = 2000
local HOOK_CHECK_INTERVAL      = 1000
local HOOK_CHECK_MAX_ATTEMPTS  = 10
local DESKTOP_SWITCH_GRACE     = 1000
local pending_exe              = nil
local pending_attempts         = 0
local hook_check_scheduled     = false
local desktop_switch_scheduled = false

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

    HWND     GetForegroundWindow();
    BOOL     GetWindowRect(HWND hWnd, RECT* lpRect);
    LONG     GetWindowLongA(HWND hWnd, int nIndex);
    HMONITOR MonitorFromWindow(HWND hwnd, DWORD dwFlags);
    BOOL     GetMonitorInfoA(HMONITOR hMonitor, MONITORINFO* lpmi);
    DWORD    GetWindowThreadProcessId(HWND hWnd, DWORD* lpdwProcessId);
    HANDLE   OpenProcess(DWORD dwDesiredAccess, BOOL bInheritHandle, DWORD dwProcessId);
    BOOL     CloseHandle(HANDLE hObject);
    BOOL     QueryFullProcessImageNameA(HANDLE hProcess, DWORD dwFlags, char* lpExeName, DWORD* lpdwSize);
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

local function apply_switch(game_active)
    local scenes = obs.obs_frontend_get_scenes()
    if scenes == nil then return end
    for _, scene_source in ipairs(scenes) do
        local name = obs.obs_source_get_name(scene_source)
        if cfg.scene_name == "" or cfg.scene_name == name then
            set_visible_in_scene(scene_source, cfg.game_source,    game_active)
            set_visible_in_scene(scene_source, cfg.display_source, not game_active)
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
    obs.timer_add(desktop_switch_callback, DESKTOP_SWITCH_GRACE)
end

-- named function so obs.timer_remove can actualy find and kill it
function hook_check_callback()
    -- always remove self first — this is a one-shot
    obs.timer_remove(hook_check_callback)
    hook_check_scheduled = false

    local checked_exe = pending_exe

    -- if theres no exe we were checking, nothing to do
    if checked_exe == nil then return end

    -- critical: verify the same game is still in the foreground. if the user already tabbed out, do not mark it as blocked.
    local current_exe = get_exe_from_hwnd(ffi.C.GetForegroundWindow())
    if current_exe ~= checked_exe then
        log("Hook check cancelled — " .. checked_exe .. " is no longer foreground")
        pending_exe      = nil
        pending_attempts = 0
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
        "[AutoSwitch] Game Capture got no frame from '%s' after %d checks — falling back to Display Capture",
        checked_exe, HOOK_CHECK_MAX_ATTEMPTS))
    hook_blocked[checked_exe] = true
    pending_exe              = nil
    pending_attempts         = 0
    apply_switch(false)
end

local last_state = nil

local function is_exclusive_fullscreen(hwnd)
    if hwnd == nil then return false end
    if ffi.C.IsWindowVisible(hwnd) == 0 then return false end
    if hwnd == ffi.C.GetDesktopWindow() then return false end
    if hwnd == ffi.C.GetShellWindow()   then return false end

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

function desktop_switch_callback()
    obs.timer_remove(desktop_switch_callback)
    desktop_switch_scheduled = false

    local hwnd = ffi.C.GetForegroundWindow()
    local game_active = is_exclusive_fullscreen(hwnd)
    if game_active then
        log("Desktop switch cancelled — game returned before grace elapsed")
        return
    end

    cancel_hook_check()
    last_state = false
    apply_switch(false)
    print(string.format("[AutoSwitch] → DESKTOP MODE [%s]", cfg.display_source))
end

local function check_fullscreen()
    local hwnd             = ffi.C.GetForegroundWindow()
    local game_active, exe = is_exclusive_fullscreen(hwnd)

    if game_active then
        cancel_desktop_switch()
        if last_state ~= true then
            last_state = true
            apply_switch(true)
            pending_exe      = exe
            pending_attempts = 0
            schedule_hook_check(HOOK_CHECK_INITIAL_DELAY)
            print(string.format("[AutoSwitch] %s → GAME MODE [%s] (verifying hook...)",
                exe or "unknown", cfg.game_source))
        end
        return
    end

    if last_state == true then
        schedule_desktop_switch()
        return
    end

    if last_state ~= false then
        last_state = false
        apply_switch(false)
        print(string.format("[AutoSwitch] → DESKTOP MODE [%s]", cfg.display_source))
    end
end

function script_description()
    return [[<b>Universal Fullscreen Auto-Switch</b><br><br>
Detects exclusive fullscreen games via graphics API (D3D/Vulkan/OpenGL).<br>
Automatically falls back to Display Capture if Game Capture hook is blocked (e.g. anti-cheat).<br>
Game Capture stays active through brief focus changes to avoid hook churn.]]
end

function script_properties()
    local props = obs.obs_properties_create()
    obs.obs_properties_add_text(props, "game_source",    "Game Capture source name",       obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(props, "display_source", "Display Capture source name",     obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_text(props, "scene_name",     "Scene to apply to (blank = all)", obs.OBS_TEXT_DEFAULT)
    obs.obs_properties_add_int (props, "check_interval", "Check interval (ms)", 500, 5000, 250)
    obs.obs_properties_add_bool(props, "verbose",        "Verbose logging")
    return props
end

function script_defaults(settings)
    obs.obs_data_set_default_string(settings, "game_source",    "Game Capture")
    obs.obs_data_set_default_string(settings, "display_source", "Display Capture")
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
    cfg.scene_name     = obs.obs_data_get_string(settings, "scene_name")
    cfg.check_interval = math.max(obs.obs_data_get_int(settings, "check_interval"), 1000)
    cfg.verbose        = obs.obs_data_get_bool  (settings, "verbose")
    last_state  = nil
    hook_blocked = {}
    graphics_api_seen = {}
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
