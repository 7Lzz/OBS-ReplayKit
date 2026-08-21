-- Discord output startup. default path is an OBS Windowed Projector that Discord can select as a window share. OBS Virtual Camera is legacy/manual only and is not started unless replaykit_settings.json explicitly sets discord_output_mode="virtual_camera_legacy".

obs = obslua

-- obs monitoring-type enum: 0 = monitor off (output to mixer only) 1 = monitor only (mute in mixer, send to monitoring device) 2 = monitor and output (send to both mixer and monitoring device)
local OBS_MONITORING_TYPE_NONE              = 0
local OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT = 2

local MIC_SOURCE_NAME           = "Audio Input Capture"
local DESKTOP_AUDIO_SOURCE_NAME = "Desktop Audio (excl. Discord)"
local DISCORD_AUDIO_SOURCE_NAME = "Discord Audio (record only)"
local MODE_PROJECTOR            = "projector"
local MODE_SHARE_BRIDGE         = "share_bridge"
local MODE_VCAM_LEGACY          = "virtual_camera_legacy"
local DEFAULT_DISCORD_OUTPUT_MODE = MODE_PROJECTOR
local FAIL_CLOSED_OUTPUT_MODE   = MODE_PROJECTOR
local MAX_SETTINGS_BYTES        = 65536
local SETTINGS_SYNC_INTERVAL_MS = 500
local active_discord_output_mode = DEFAULT_DISCORD_OUTPUT_MODE
local active_projector_enabled  = false
local have_discord_output_settings = false
local last_discord_output_signature = ""
local stop_vcam_if_active

-- discord runs many processes (a main discord.exe plus a seperate discordsystemhelper.exe, plus electron renderer/utility processes). we still patch the exclude list to cover the named variants -- mainly so the discord audio (record only) source can record friend voices properly even though were not streaming them. the streaming path doesnt depend on this exclude list working perfectly anymore.
local DISCORD_PROCESS_NAMES = {
    "Discord.exe",
    "DiscordSystemHelper.exe",
    "DiscordCanary.exe",
    "DiscordPTB.exe",
    "DiscordDevelopment.exe",
}

-- replaykit_settings.json is the persisted source of truth for the Discord output mode -- read it before changing output or monitoring state so OBS startup matches the users last choice.

local function normalize_path(path)
    return tostring(path or ""):gsub("\\", "/")
end

local function parent_dir(path)
    path = normalize_path(path):gsub("/+$", "")
    return path:match("^(.*)/[^/]+$") or ""
end

local function join_path(a, b)
    a = normalize_path(a):gsub("/+$", "")
    b = normalize_path(b):gsub("^/+", "")
    if a == "" then return b end
    return a .. "/" .. b
end

local function read_text(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local text = f:read(MAX_SETTINGS_BYTES + 1)
    f:close()
    if text and #text > MAX_SETTINGS_BYTES then return nil end
    return text
end

local function strip_utf8_bom(text)
    if text and text:sub(1, 3) == "\239\187\191" then
        return text:sub(4)
    end
    return text
end

local function runtime_settings_paths()
    local scriptDir = parent_dir(script_path())
    local parentDir = parent_dir(scriptDir)
    return {
        join_path(scriptDir, "replaykit_settings.json"),
        join_path(parentDir, "replaykit_settings.json"),
        join_path(join_path(parentDir, "scripts"), "replaykit_settings.json"),
    }
end

local function read_runtime_settings_text()
    for _, path in ipairs(runtime_settings_paths()) do
        local text = strip_utf8_bom(read_text(path))
        if text and text ~= "" then return text end
    end
    return nil
end

local function _read_bool(data, key, default)
    if not obs.obs_data_has_user_value(data, key) then return default end
    return obs.obs_data_get_bool(data, key)
end

local function read_discord_output_settings()
    local text = read_runtime_settings_text()
    if not text or text == "" then
        print("[DiscordProjector] WARN: replaykit_settings.json missing; disabling Discord share monitoring")
        return FAIL_CLOSED_OUTPUT_MODE, false, false
    end

    local data = obs.obs_data_create_from_json(text)
    if data == nil then
        print("[DiscordProjector] WARN: replaykit_settings.json invalid; disabling Discord share monitoring")
        return FAIL_CLOSED_OUTPUT_MODE, false, false
    end

    local mode = DEFAULT_DISCORD_OUTPUT_MODE
    if obs.obs_data_has_user_value(data, "discord_output_mode") then
        mode = obs.obs_data_get_string(data, "discord_output_mode")
    elseif obs.obs_data_has_user_value(data, "shareMode") then
        local legacy = obs.obs_data_get_string(data, "shareMode")
        if legacy == MODE_PROJECTOR or legacy == MODE_SHARE_BRIDGE then
            mode = MODE_PROJECTOR
        else
            print(string.format("[DiscordProjector] WARN: shareMode=%s ignored; using OBS projector mode", tostring(legacy)))
        end
    end
    local screenshare_enabled = _read_bool(data, "discord_screenshare_enabled", true)
    local projector_enabled = screenshare_enabled and _read_bool(data, "discord_projector_enabled", true)
    obs.obs_data_release(data)

    if mode == MODE_SHARE_BRIDGE then
        print("[DiscordProjector] WARN: share_bridge is disabled; using OBS projector mode")
        mode = MODE_PROJECTOR
    elseif mode ~= MODE_PROJECTOR then
        print(string.format("[DiscordProjector] WARN: discord_output_mode=%s ignored; using OBS projector mode", tostring(mode)))
        mode = MODE_PROJECTOR
    end

    return mode, projector_enabled, true
end

local function discord_output_uses_legacy_vcam()
    return active_discord_output_mode == MODE_VCAM_LEGACY
end

-- monitoring-type setter (idempotent, logs on transition)

local function _set_monitoring(source_name, target, friendly_target_name, log_missing, log_unchanged, ensure_unmuted, refresh_when_unchanged)
    if log_missing == nil then log_missing = true end
    if log_unchanged == nil then log_unchanged = true end
    if refresh_when_unchanged == nil then refresh_when_unchanged = true end
    local src = obs.obs_get_source_by_name(source_name)
    if src == nil then
        if log_missing then
            print(string.format("[AutoVirtCam] no '%s' source found - skipping monitoring change", source_name))
        end
        return false
    end
    local current = obs.obs_source_get_monitoring_type(src)
    local changed = current ~= target
    if changed then
        obs.obs_source_set_monitoring_type(src, target)
        print(string.format("[AutoVirtCam] %s monitoring %d -> %d (%s)",
            source_name, current, target, friendly_target_name))
    else
        if log_unchanged then
            print(string.format("[AutoVirtCam] %s monitoring already %d (%s)",
            source_name, target, friendly_target_name))
        end
    end
    if ensure_unmuted then
        local muted = obs.obs_source_muted(src)
        if muted then
            print(string.format("[AutoVirtCam] %s unmuted while syncing monitoring", source_name))
        end
        -- OBS 32.1 can leave the mixer monitor icon stale without a source state signal.
        if changed or refresh_when_unchanged or muted then
            obs.obs_source_set_muted(src, false)
        end
    end
    obs.obs_source_release(src)
    return true
end

-- mic monitoring stays off; the desktop source is monitored only while the projector preview is enabled so Discord can capture OBS Stream Audio.
local function ensure_mic_NOT_to_cable()
    _set_monitoring(MIC_SOURCE_NAME,
        OBS_MONITORING_TYPE_NONE, "Monitor Off")
end

local function ensure_desktop_audio_not_monitored(reason, log_missing, log_unchanged, refresh_when_unchanged)
    return _set_monitoring(DESKTOP_AUDIO_SOURCE_NAME,
        OBS_MONITORING_TYPE_NONE, reason or "Monitor Off", log_missing, log_unchanged, true, refresh_when_unchanged)
end

local function ensure_desktop_audio_projector_monitoring(log_missing, log_unchanged, refresh_when_unchanged)
    return _set_monitoring(DESKTOP_AUDIO_SOURCE_NAME,
        OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT, "Monitor and Output - Projector audio", log_missing, log_unchanged, true, refresh_when_unchanged)
end

local function ensure_desktop_audio_monitoring_for_current_mode(log_missing, log_unchanged, refresh_when_unchanged)
    if active_discord_output_mode == MODE_PROJECTOR and active_projector_enabled then
        return ensure_desktop_audio_projector_monitoring(log_missing, log_unchanged, refresh_when_unchanged)
    end
    return ensure_desktop_audio_not_monitored("Monitor Off - Share Preview disabled", log_missing, log_unchanged, refresh_when_unchanged)
end

local function discord_output_signature(mode, enabled)
    return tostring(mode or "") .. "|" .. tostring(enabled == true)
end

local function apply_discord_output_settings(reason, force)
    local mode, enabled, settings_ok = read_discord_output_settings()
    if not settings_ok and have_discord_output_settings then
        print(string.format("[DiscordProjector] WARN: settings sync (%s) could not read settings; keeping last mode=%s enabled=%s",
            reason or "poll", tostring(active_discord_output_mode), tostring(active_projector_enabled)))
        ensure_desktop_audio_monitoring_for_current_mode(false, false, false)
        return
    end
    have_discord_output_settings = settings_ok
    local signature = discord_output_signature(mode, enabled)
    if not force and signature == last_discord_output_signature then
        active_discord_output_mode = mode
        active_projector_enabled = enabled
        ensure_desktop_audio_monitoring_for_current_mode(false, false, false)
        return
    end
    last_discord_output_signature = signature
    active_discord_output_mode = mode
    active_projector_enabled = enabled
    print(string.format("[DiscordProjector] settings sync (%s): mode=%s enabled=%s",
        reason or "poll", tostring(mode), tostring(enabled)))
    ensure_desktop_audio_monitoring_for_current_mode()
    if not discord_output_uses_legacy_vcam() then
        stop_vcam_if_active("Discord share mode")
    end
end

local function poll_discord_output_settings()
    apply_discord_output_settings("settings poll", false)
end

-- process-audio-capture exec-list patching (best-effort for record source)

local function _get_exec_set(settings)
    local set = {}
    local arr = obs.obs_data_get_array(settings, "executable_list")
    if arr == nil then return set end
    local n = obs.obs_data_array_count(arr)
    for i = 0, n - 1 do
        local item = obs.obs_data_array_item(arr, i)
        local v    = obs.obs_data_get_string(item, "value")
        if v and v ~= "" then set[v:lower()] = v end
        obs.obs_data_release(item)
    end
    obs.obs_data_array_release(arr)
    return set
end

local function _set_exec_list(settings, names)
    local arr = obs.obs_data_array_create()
    for _, name in ipairs(names) do
        local item = obs.obs_data_create()
        obs.obs_data_set_string(item, "value", name)
        obs.obs_data_array_push_back(arr, item)
        obs.obs_data_release(item)
    end
    obs.obs_data_set_array(settings, "executable_list", arr)
    obs.obs_data_array_release(arr)
end

local function ensure_discord_processes_covered(source_name, preserve_other)
    if preserve_other == nil then preserve_other = true end
    local src = obs.obs_get_source_by_name(source_name)
    if src == nil then
        print(string.format("[AutoVirtCam] no '%s' source - skipping exec-list patch", source_name))
        return
    end
    local settings = obs.obs_source_get_settings(src)
    local existing = _get_exec_set(settings)

    local out, seen, added = {}, {}, {}
    if preserve_other then
        for lower_k, original in pairs(existing) do
            if not seen[lower_k] then
                table.insert(out, original)
                seen[lower_k] = true
            end
        end
    end
    for _, dn in ipairs(DISCORD_PROCESS_NAMES) do
        local k = dn:lower()
        if not seen[k] then
            table.insert(out, dn); seen[k] = true; table.insert(added, dn)
        end
    end

    if #added == 0 then
        print(string.format("[AutoVirtCam] '%s' exec-list already covers all Discord variants", source_name))
    else
        _set_exec_list(settings, out)
        obs.obs_source_update(src, settings)
        print(string.format("[AutoVirtCam] '%s' exec-list += %s", source_name, table.concat(added, ", ")))
    end
    obs.obs_data_release(settings)
    obs.obs_source_release(src)
end

-- virtual camera start (with a properly-cleaning verification timer)

-- forward-declare so the verification timer can remove itself by name.
local verify_vcam_started

verify_vcam_started = function()
    obs.timer_remove(verify_vcam_started)
    if obs.obs_frontend_virtualcam_active() then
        print("[AutoVirtCam] virtual camera started OK")
    else
        print("[AutoVirtCam] WARN: virtual camera failed to start - check Tools -> Virtual Camera config")
    end
end

local function start_vcam()
    obs.timer_remove(start_vcam)
    active_discord_output_mode, active_projector_enabled, have_discord_output_settings = read_discord_output_settings()
    if not discord_output_uses_legacy_vcam() then
        if obs.obs_frontend_virtualcam_active() then
            obs.obs_frontend_stop_virtualcam()
            print("[DiscordProjector] stopped OBS Virtual Camera (Discord projector mode)")
        else
            print("[DiscordProjector] Skipping OBS Virtual Camera for Discord output")
        end
        return
    end
    if obs.obs_frontend_virtualcam_active() then
        print("[DiscordProjector] virtual camera already active (legacy)")
        return
    end
    print("[DiscordProjector] WARN: starting OBS Virtual Camera only because virtual_camera_legacy is explicitly selected")
    obs.obs_frontend_start_virtualcam()
    obs.timer_add(verify_vcam_started, 1500)
end

stop_vcam_if_active = function(reason)
    obs.timer_remove(start_vcam)
    obs.timer_remove(verify_vcam_started)
    if obs.obs_frontend_virtualcam_active() then
        obs.obs_frontend_stop_virtualcam()
        print(string.format("[AutoVirtCam] stopped virtual camera (%s)", reason or "not needed"))
    else
        print(string.format("[AutoVirtCam] virtual camera remains off (%s)", reason or "not needed"))
    end
end

-- lifecycle

function script_description()
    return [[<b>Discord Share Ready Projector</b><br>
On OBS launch:<br>
&nbsp;&nbsp;1. Reads the saved ReplayKit Discord output mode.<br>
&nbsp;&nbsp;2. Keeps OBS Virtual Camera stopped and lets the ReplayKit helper
open and move an OBS Windowed Projector for Discord Go Live.<br>
&nbsp;&nbsp;3. Polls the saved Share Preview state for audio monitoring sync.<br><br>
Pair with Discord: share the window named
<code>OBS ReplayKit Discord Share (Projector - Desktop Audio)</code> through application screenshare.<br>
The OBS projector window is renamed, moved, and hidden from the taskbar by ReplayKit.<br><br>
<b>Important:</b> for friends to hear your game audio, the
<i>Game Capture</i> source must be hooking the game (its preview shows
the game). If only <i>Display Capture</i> is showing the game, friends
will only hear your mic.<br><br>
Discord <b>DISABLE</b>: Noise Suppression / Krisp / Echo Cancellation /
Automatic Gain Control.]]
end

function script_load(_settings)
    print("[DiscordProjector] loaded")
    active_discord_output_mode, active_projector_enabled, have_discord_output_settings = read_discord_output_settings()
    last_discord_output_signature = discord_output_signature(active_discord_output_mode, active_projector_enabled)
    print("[DiscordProjector] discord_output_mode: " .. active_discord_output_mode)
    if active_discord_output_mode == MODE_PROJECTOR and active_projector_enabled then
        print("[DiscordProjector] Discord share preview enabled")
    end
    -- match OBS startup monitoring to the saved Share Preview state.
    ensure_mic_NOT_to_cable()
    ensure_desktop_audio_monitoring_for_current_mode()
    -- cover every discord build variant in the exclude list so the record-only source captures discord audio properly for local recordings.
    ensure_discord_processes_covered(DESKTOP_AUDIO_SOURCE_NAME, true)
    ensure_discord_processes_covered(DISCORD_AUDIO_SOURCE_NAME, true)
    if discord_output_uses_legacy_vcam() then
        -- defer the virtual-camera start a bit so obss frontend is fully up.
        print("[DiscordProjector] starting legacy virtual camera in 2s")
        obs.timer_add(start_vcam, 2000)
    else
        stop_vcam_if_active("Discord projector mode")
    end
    obs.timer_add(poll_discord_output_settings, SETTINGS_SYNC_INTERVAL_MS)
end

function script_unload()
    obs.timer_remove(start_vcam)
    obs.timer_remove(verify_vcam_started)
    obs.timer_remove(poll_discord_output_settings)
    if obs.obs_frontend_virtualcam_active() then
        obs.obs_frontend_stop_virtualcam()
        print("[AutoVirtCam] stopped virtual camera at OBS shutdown")
    end
end
