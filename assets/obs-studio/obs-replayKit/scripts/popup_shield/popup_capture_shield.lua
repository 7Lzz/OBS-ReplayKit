-- keep the replaykit update prompt out of discord style screenshare pickers and out of desktop captures. clips and settings are left visible to capture on purpose. setwindowdisplayaffinity only works from the process that owns the window, which is why this runs as obs lua instead of in the powershell helper: the cef popups are top level windows of this obs process.

obs = obslua
local ffi = require("ffi")

ffi.cdef[[
    typedef void*          HWND;
    typedef unsigned long  DWORD;
    typedef int            BOOL;

    DWORD    GetCurrentProcessId();
    DWORD    GetWindowThreadProcessId(HWND hWnd, DWORD* lpdwProcessId);
    BOOL     IsWindowVisible(HWND hWnd);
    int      GetWindowTextA(HWND hWnd, char* lpString, int nMaxCount);
    HWND     FindWindowExA(HWND hWndParent, HWND hWndChildAfter, const char* lpszClass, const char* lpszWindow);
    BOOL     GetWindowDisplayAffinity(HWND hWnd, DWORD* pdwAffinity);
    BOOL     SetWindowDisplayAffinity(HWND hWnd, DWORD dwAffinity);
]]

-- needs win10 2004+; on older builds the set call fails and the window is left alone
local WDA_EXCLUDEFROMCAPTURE = 0x00000011
local SHIELD_INTERVAL_MS     = 1500
local MAX_TITLE              = 128

-- exact titles of the cef popups replaykit owns. matched exact or as "<title> " prefix, same convention the helpers stylewindow uses, so the discord share projector or a scene projector that happens to contain one of these words never gets shielded.
local SHIELD_TITLES = { "ReplayKit Update" }

-- hwnd -> true, only to keep the log to one line per window
local logged = {}

local function title_matches(title)
    for _, want in ipairs(SHIELD_TITLES) do
        if title == want or title:sub(1, #want + 1) == want .. " " then
            return true
        end
    end
    return false
end

local function get_title(hwnd)
    local buf = ffi.new("char[?]", MAX_TITLE)
    local len = ffi.C.GetWindowTextA(hwnd, buf, MAX_TITLE)
    if len <= 0 then return nil end
    return ffi.string(buf, len)
end

local function shield_window(hwnd, title)
    local cur = ffi.new("DWORD[1]", 0)
    local got = ffi.C.GetWindowDisplayAffinity(hwnd, cur)
    if got ~= 0 and cur[0] == WDA_EXCLUDEFROMCAPTURE then return end

    local key = tonumber(ffi.cast("intptr_t", hwnd))
    if ffi.C.SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) ~= 0 then
        if not logged[key] then
            logged[key] = true
            print("[PopupShield] excluded '" .. title .. "' from capture")
        end
    elseif not logged[key] then
        logged[key] = true
        print("[PopupShield] SetWindowDisplayAffinity failed for '" .. title .. "' (needs Windows 10 2004+)")
    end
end

-- walk top level windows without an enumwindows callback; findwindowex chaining avoids ffi callback slots. pid filter first becuase reading titles of foreign windows can block on hung apps, and the affinity call would fail for them anyway.
function shield_pass()
    local my_pid = ffi.C.GetCurrentProcessId()
    local pid = ffi.new("DWORD[1]", 0)
    local hwnd = ffi.C.FindWindowExA(nil, nil, nil, nil)
    while hwnd ~= nil do
        pid[0] = 0
        ffi.C.GetWindowThreadProcessId(hwnd, pid)
        if pid[0] == my_pid and ffi.C.IsWindowVisible(hwnd) ~= 0 then
            local title = get_title(hwnd)
            if title and title_matches(title) then
                shield_window(hwnd, title)
            end
        end
        hwnd = ffi.C.FindWindowExA(nil, hwnd, nil, nil)
    end
end

function script_load(settings)
    obs.timer_add(shield_pass, SHIELD_INTERVAL_MS)
    print("[PopupShield] Active — ReplayKit update prompt excluded from screenshare pickers")
end

function script_unload()
    obs.timer_remove(shield_pass)
end

function script_description()
    return [[<b>ReplayKit Popup Capture Shield</b><br><br>
Marks the Update popup with <code>WDA_EXCLUDEFROMCAPTURE</code> so it
stays out of Discord share pickers, screenshares and desktop captures.
Clips and Settings are left visible to capture. The Discord share
projector is not affected.]]
end
