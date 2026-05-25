-- Route OBS audio monitoring through the ReplayKit-managed OBS Stream Audio
-- device. If that device is missing or fails WASAPI init, fail closed and tell
-- the user exactly which Monitoring Device to choose.

obs       = obslua
ffi       = require("ffi")
local advapi32 = ffi.load("Advapi32")
local kernel32 = ffi.load("kernel32")

ffi.cdef[[
    typedef int            LONG;
    typedef int            BOOL;
    typedef unsigned long  DWORD;
    typedef wchar_t        WCHAR;
    typedef void*          HKEY;
    typedef void*          HANDLE;

    LONG RegOpenKeyExW(HKEY hKey, const WCHAR* lpSubKey, DWORD ulOptions,
                       DWORD samDesired, HKEY *phkResult);
    LONG RegQueryValueExW(HKEY hKey, const WCHAR* lpValueName, DWORD *lpReserved,
                          DWORD *lpType, void* lpData, DWORD *lpcbData);
    LONG RegEnumKeyExW(HKEY hKey, DWORD dwIndex, WCHAR* lpName, DWORD *lpcchName,
                       DWORD *lpReserved, WCHAR *lpClass, DWORD *lpcchClass,
                       void *lpftLastWriteTime);
    LONG RegCloseKey(HKEY hKey);

    typedef struct _FILETIME {
        DWORD dwLowDateTime;
        DWORD dwHighDateTime;
    } FILETIME;

    typedef struct _WIN32_FIND_DATAW {
        DWORD    dwFileAttributes;
        FILETIME ftCreationTime;
        FILETIME ftLastAccessTime;
        FILETIME ftLastWriteTime;
        DWORD    nFileSizeHigh;
        DWORD    nFileSizeLow;
        DWORD    dwReserved0;
        DWORD    dwReserved1;
        WCHAR    cFileName[260];
        WCHAR    cAlternateFileName[14];
    } WIN32_FIND_DATAW;

    HANDLE FindFirstFileW(const WCHAR* lpFileName, WIN32_FIND_DATAW* lpFindFileData);
    BOOL   FindNextFileW(HANDLE hFindFile, WIN32_FIND_DATAW* lpFindFileData);
    BOOL   FindClose(HANDLE hFindFile);
]]

local INVALID_HANDLE_VALUE     = ffi.cast("HANDLE", -1)
local FILE_ATTRIBUTE_DIRECTORY = 0x10

local HKEY_LOCAL_MACHINE  = ffi.cast("HKEY", 0x80000002)
local KEY_READ            = 0x20019
local DEVICE_STATE_ACTIVE = 0x1
local PKEY_FRIENDLY_NAME  = "{a45c254e-df1c-4efd-8020-67d146a850e0},2"
local RENDER_KEY          = [[SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render]]

local APPDATA          = os.getenv("APPDATA") or ""
local LOG_DIR          = APPDATA .. "\\obs-studio\\logs"
local BLACKLIST_FILE   = APPDATA .. "\\obs-studio\\monitor_picker_blacklist.txt"

local CHECK_DELAY_TICKS = 200 -- ~3.3s at 60fps; enough for wasapi init to log a failure

-- ReplayKit renames the render endpoint to this user-facing name during Apply.
local MONITOR_STATUS = {
    pending = true,
    ready = false,
    failed = false,
    device_name = nil,
    device_id = nil,
    message = "not started",
}
_G.ReplayKitMonitorPicker = MONITOR_STATUS

local function set_monitor_status(kind, message, name, obs_id)
    MONITOR_STATUS.pending = (kind == "pending")
    MONITOR_STATUS.ready = (kind == "ready")
    MONITOR_STATUS.failed = (kind == "failed")
    MONITOR_STATUS.device_name = name
    MONITOR_STATUS.device_id = obs_id
    MONITOR_STATUS.message = message or ""
end

local function w(s)
    local buf = ffi.new("WCHAR[?]", #s + 1)
    for i = 1, #s do buf[i - 1] = s:byte(i) end
    buf[#s] = 0
    return buf
end

local function read_str(key, name)
    local size = ffi.new("DWORD[1]", 1024)
    local typ  = ffi.new("DWORD[1]", 0)
    local buf  = ffi.new("WCHAR[512]")
    if advapi32.RegQueryValueExW(key, w(name), nil, typ, buf, size) ~= 0 then return nil end
    local out, n = {}, math.floor(size[0] / 2)
    for i = 0, n - 1 do
        local c = buf[i]
        if c == 0 then break end
        if c < 128 then out[#out + 1] = string.char(c) end
    end
    return table.concat(out)
end

local function read_dword(key, name)
    local size = ffi.new("DWORD[1]", 4)
    local typ  = ffi.new("DWORD[1]", 0)
    local data = ffi.new("DWORD[1]", 0)
    if advapi32.RegQueryValueExW(key, w(name), nil, typ, data, size) ~= 0 then return nil end
    return data[0]
end

local function enumerate_render_endpoints()
    local results = {}
    local render = ffi.new("HKEY[1]")
    if advapi32.RegOpenKeyExW(HKEY_LOCAL_MACHINE, w(RENDER_KEY), 0, KEY_READ, render) ~= 0 then
        return results
    end
    local idx = 0
    while true do
        local name_buf = ffi.new("WCHAR[256]")
        local name_len = ffi.new("DWORD[1]", 256)
        if advapi32.RegEnumKeyExW(render[0], idx, name_buf, name_len, nil, nil, nil, nil) ~= 0 then break end
        idx = idx + 1
        local guid = {}
        for i = 0, name_len[0] - 1 do
            local c = name_buf[i]
            if c < 128 then guid[#guid + 1] = string.char(c) end
        end
        local guid_str = table.concat(guid)
        local ep_key = ffi.new("HKEY[1]")
        if advapi32.RegOpenKeyExW(render[0], w(guid_str), 0, KEY_READ, ep_key) == 0 then
            local state = read_dword(ep_key[0], "DeviceState") or 0
            advapi32.RegCloseKey(ep_key[0])
            local friendly = guid_str
            local props_key = ffi.new("HKEY[1]")
            if advapi32.RegOpenKeyExW(render[0], w(guid_str .. "\\Properties"), 0, KEY_READ, props_key) == 0 then
                local fn = read_str(props_key[0], PKEY_FRIENDLY_NAME)
                if fn then friendly = fn end
                advapi32.RegCloseKey(props_key[0])
            end
            results[#results + 1] = {
                name   = friendly,
                obs_id = "{0.0.0.00000000}." .. guid_str,
                active = (bit.band(state, DEVICE_STATE_ACTIVE) ~= 0),
            }
        end
    end
    advapi32.RegCloseKey(render[0])
    return results
end

local function enumerate_obs_monitoring_devices()
    local results = {}
    local ok = pcall(function()
        obs.obs_enum_audio_monitoring_devices(function(_param, name, id)
            if name and id and #name > 0 and #id > 0 then
                results[#results + 1] = {
                    name   = name,
                    obs_id = id,
                    active = true,
                }
            end
            return true
        end, nil)
    end)
    if not ok then
        print("[MonitorPicker] OBS audio-monitor device enumeration failed.")
        return {}
    end
    return results
end

local function virtual_priority(name)
    local lower = name:lower()
    if lower:find("surround", 1, true) or lower:find("16ch", 1, true) then
        return 999
    end
    if lower == "obs stream audio" then
        return 0
    end
    if lower:find("obs stream audio", 1, true) and not lower:find("loopback", 1, true) then
        return 10
    end
    if lower:find("cable input", 1, true) then
        return 20
    end
    return 999
end

local function add_virtual_candidates(out, seen, devices, blacklist, source_name)
    local added = 0
    for _, d in ipairs(devices) do
        if d.active and not blacklist[d.obs_id] then
            local rank = virtual_priority(d.name)
            if rank < 999 and not seen[d.obs_id] then
                seen[d.obs_id] = true
                out[#out + 1] = {
                    name = d.name,
                    obs_id = d.obs_id,
                    rank = rank,
                    source = source_name,
                }
                added = added + 1
            end
        end
    end
    return added
end

local function read_blacklist()
    local bad = {}
    local f = io.open(BLACKLIST_FILE, "r")
    if not f then return bad end
    for line in f:lines() do
        line = line:gsub("^%s+", ""):gsub("%s+$", "")
        if #line > 0 then
            local id = line:match("^([^\t]+)")
            if id then bad[id] = true end
        end
    end
    f:close()
    return bad
end

local function add_to_blacklist(obs_id, name)
    local f = io.open(BLACKLIST_FILE, "a")
    if not f then return end
    f:write(obs_id .. "\t" .. (name or "") .. "\n")
    f:close()
end

local function current_log_path()
    -- find the newest *.txt under log_dir via win32 findfirstfilew / findnextfilew. previously this used io.popen(dir ...) which always flashes a cmd.exe console window on windows becuase luas io.popen doesnt honor create_no_window. going thru the kernel directly is zero-flash and faster.
    local pattern = LOG_DIR .. "\\*.txt"
    local find_data = ffi.new("WIN32_FIND_DATAW")
    local handle = kernel32.FindFirstFileW(w(pattern), find_data)
    if handle == INVALID_HANDLE_VALUE then return nil end

    local newest_name = nil
    local newest_hi, newest_lo = 0, 0
    repeat
        if bit.band(find_data.dwFileAttributes, FILE_ATTRIBUTE_DIRECTORY) == 0 then
            local ft = find_data.ftLastWriteTime
            local hi, lo = tonumber(ft.dwHighDateTime), tonumber(ft.dwLowDateTime)
            if newest_name == nil or hi > newest_hi or (hi == newest_hi and lo > newest_lo) then
                newest_hi, newest_lo = hi, lo
                local chars = {}
                for i = 0, 259 do
                    local c = find_data.cFileName[i]
                    if c == 0 then break end
                    if c < 128 then chars[#chars + 1] = string.char(c) end
                end
                newest_name = table.concat(chars)
            end
        end
    until kernel32.FindNextFileW(handle, find_data) == 0
    kernel32.FindClose(handle)

    if not newest_name or #newest_name == 0 then return nil end
    return LOG_DIR .. "\\" .. newest_name
end

local function count_log_lines(path)
    if not path then return 0 end
    local f = io.open(path, "r")
    if not f then return 0 end
    local count = 0
    for _ in f:lines() do count = count + 1 end
    f:close()
    return count
end

local function failure_after_line(path, start_line)
    if not path then return false end
    local f = io.open(path, "r")
    if not f then return false end
    local line_num, found = 0, false
    for line in f:lines() do
        line_num = line_num + 1
        if line_num > start_line and
           line:find("audio_monitor_init_wasapi: Failed to activate device", 1, true) then
            found = true
            break
        end
    end
    f:close()
    return found
end

local state = {
    candidates = {}, current_idx = 0,
    current_name = nil, current_obs_id = nil,
    log_path = nil, log_lines_at_set = 0,
    ticks_remaining = 0, done = false,
}

local function stop_without_change()
    print("[MonitorPicker] OBS Stream Audio was not available.")
    print("[MonitorPicker] Open OBS Settings > Audio > Advanced > Monitoring Device")
    print("[MonitorPicker] and select 'OBS Stream Audio'.")
    set_monitor_status("failed", "OBS Stream Audio was not available")
    state.done = true
end

local function try_next()
    state.current_idx = state.current_idx + 1
    if state.current_idx > #state.candidates then
        stop_without_change()
        return
    end
    local c = state.candidates[state.current_idx]
    state.current_name, state.current_obs_id = c.name, c.obs_id
    set_monitor_status("pending", "trying " .. c.name, c.name, c.obs_id)
    if not obs.obs_set_audio_monitoring_device(c.name, c.obs_id) then
        print(string.format("[MonitorPicker] OBS rejected #%d %s - blacklisting", state.current_idx, c.name))
        add_to_blacklist(c.obs_id, c.name)
        try_next()
        return
    end
    state.log_lines_at_set = count_log_lines(state.log_path)
    state.ticks_remaining  = CHECK_DELAY_TICKS
    print(string.format("[MonitorPicker] try #%d/%d: monitor -> %s (verifying ~3s...)",
        state.current_idx, #state.candidates, c.name))
end

function script_tick(seconds)
    if state.done or state.ticks_remaining <= 0 then return end
    state.ticks_remaining = state.ticks_remaining - 1
    if state.ticks_remaining > 0 then return end
    if failure_after_line(state.log_path, state.log_lines_at_set) then
        print(string.format("[MonitorPicker] '%s' failed WASAPI init - blacklisting", state.current_name))
        add_to_blacklist(state.current_obs_id, state.current_name)
        try_next()
    else
        print(string.format("[MonitorPicker] '%s' is working - clean audio to Discord, no local echo", state.current_name))
        set_monitor_status("ready", "OBS Stream Audio monitoring device is active", state.current_name, state.current_obs_id)
        state.done = true
    end
end

function script_load(_settings)
    set_monitor_status("pending", "selecting OBS Stream Audio")
    state.log_path = current_log_path()
    local blacklist = read_blacklist()

    local candidates = {}
    local seen = {}

    local obs_devices = enumerate_obs_monitoring_devices()
    add_virtual_candidates(candidates, seen, obs_devices, blacklist, "OBS")

    if #candidates == 0 then
        print("[MonitorPicker] OBS did not expose OBS Stream Audio - checking Windows render endpoints.")
        local render_devices = enumerate_render_endpoints()
        local added = add_virtual_candidates(candidates, seen, render_devices, blacklist, "Windows")
        if added > 0 then
            print(string.format("[MonitorPicker] Windows render endpoint fallback found %d candidate(s).", added))
        end
    end

    table.sort(candidates, function(a, b)
        if a.rank == b.rank then return a.name < b.name end
        return a.rank < b.rank
    end)

    state.candidates, state.current_idx, state.done = candidates, 0, false

    if #candidates == 0 then
        stop_without_change()
        return
    end

    print(string.format("[MonitorPicker] %d virtual sink candidate(s):", #candidates))
    for i, c in ipairs(candidates) do
        print(string.format("  %d. (rank %d, %s) %s", i, c.rank, c.source, c.name))
    end
    try_next()
end

function script_description()
    return [[<b>Auto Pick Monitor Device</b><br>
Routes OBS's Audio Monitoring through <b>OBS Stream Audio</b> so Discord
can receive OBS audio cleanly without making you hear desktop audio twice.<br><br>
If the device is missing, open OBS Settings &gt; Audio &gt; Advanced and set
Monitoring Device to <b>OBS Stream Audio</b>.<br><br>
Failed devices are blacklisted in
<code>%APPDATA%\obs-studio\monitor_picker_blacklist.txt</code> so they're
skipped on future launches.]]
end
