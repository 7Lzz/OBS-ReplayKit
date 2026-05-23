-- start the obs virtual camera + configure audio routing for the vcam-into-discord workflow. two seperate paths: voice -> physical mic -> discord native mic input (so discords echo cancellation can do its job); game audio -> 'Desktop Audio (excl. Discord)' -> obs monitoring -> vb-cable -> discords second mic input. mic source deliberately doesnt go thru obs monitoring -- that would loop discord audio bleed back to friends via cable output. discord-side: set camera = obs virtual camera, input device = your physical mic (not the cable loopback).

obs = obslua

-- obs monitoring-type enum: 0 = monitor off (output to mixer only) 1 = monitor only (mute in mixer, send to monitoring device) 2 = monitor and output (send to both mixer and monitoring device)
local OBS_MONITORING_TYPE_NONE              = 0
local OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT = 2

local MIC_SOURCE_NAME           = "Audio Input Capture"
local DESKTOP_AUDIO_SOURCE_NAME = "Desktop Audio (excl. Discord)"
local DISCORD_AUDIO_SOURCE_NAME = "Discord Audio (record only)"
local MONITOR_WAIT_MS           = 500
local MONITOR_WAIT_LIMIT        = 30

-- discord runs many processes (a main discord.exe plus a seperate discordsystemhelper.exe, plus electron renderer/utility processes). we still patch the exclude list to cover the named variants -- mainly so the discord audio (record only) source can record friend voices properly even though were not streaming them. the streaming path doesnt depend on this exclude list working perfectly anymore.
local DISCORD_PROCESS_NAMES = {
    "Discord.exe",
    "DiscordSystemHelper.exe",
    "DiscordCanary.exe",
    "DiscordPTB.exe",
    "DiscordDevelopment.exe",
}

-- monitoring-type setter (idempotent, logs on transition)

local function _set_monitoring(source_name, target, friendly_target_name, log_missing, log_unchanged)
    if log_missing == nil then log_missing = true end
    if log_unchanged == nil then log_unchanged = true end
    local src = obs.obs_get_source_by_name(source_name)
    if src == nil then
        if log_missing then
            print(string.format("[AutoVirtCam] no '%s' source found - skipping monitoring change", source_name))
        end
        return false
    end
    local current = obs.obs_source_get_monitoring_type(src)
    if current ~= target then
        obs.obs_source_set_monitoring_type(src, target)
        print(string.format("[AutoVirtCam] %s monitoring %d -> %d (%s)",
            source_name, current, target, friendly_target_name))
    else
        if log_unchanged then
            print(string.format("[AutoVirtCam] %s monitoring already %d (%s)",
                source_name, target, friendly_target_name))
        end
    end
    obs.obs_source_release(src)
    return true
end

-- do not route the mic thru monitoring to cable input. that creates an open-mic feedback loop: discord plays friends voices thru the users speakers, the mic picks up the bleed, and routing it to cable input echoes their voices straight back at them thru the streams "mic" channel. the mic still feeds obss normal mixer (for recording / stream output to twitch/youtube), it just doesnt go to discord via cable output. user-voice-to-friends should travel thru discords native mic input (with discords echo cancellation enabled), not thru obss monitoring bus.
local function ensure_mic_NOT_to_cable()
    _set_monitoring(MIC_SOURCE_NAME,
        OBS_MONITORING_TYPE_NONE, "Monitor Off")
end

local function ensure_desktop_audio_to_cable()
    _set_monitoring(DESKTOP_AUDIO_SOURCE_NAME,
        OBS_MONITORING_TYPE_MONITOR_AND_OUTPUT, "Monitor and Output")
end

local function ensure_desktop_audio_not_monitored(reason, log_missing, log_unchanged)
    return _set_monitoring(DESKTOP_AUDIO_SOURCE_NAME,
        OBS_MONITORING_TYPE_NONE, reason or "Monitor Off", log_missing, log_unchanged)
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
local configure_desktop_audio_when_monitor_ready
local monitor_wait_attempts = 0
local monitor_failure_logged = false

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
    if obs.obs_frontend_virtualcam_active() then
        print("[AutoVirtCam] virtual camera already active")
        return
    end
    obs.obs_frontend_start_virtualcam()
    obs.timer_add(verify_vcam_started, 1500)
end

configure_desktop_audio_when_monitor_ready = function()
    local status = rawget(_G, "ReplayKitMonitorPicker")

    if type(status) == "table" and status.ready then
        obs.timer_remove(configure_desktop_audio_when_monitor_ready)
        print(string.format("[AutoVirtCam] OBS monitoring device confirmed: %s", tostring(status.device_name or "OBS Stream Audio")))
        ensure_desktop_audio_to_cable()
        return
    end

    if type(status) == "table" and status.failed then
        local disabled = ensure_desktop_audio_not_monitored("Monitor Off - OBS Stream Audio unavailable", false, false)
        if not monitor_failure_logged then
            print("[AutoVirtCam] OBS Stream Audio unavailable - keeping desktop audio monitoring off to avoid double audio")
            monitor_failure_logged = true
        end
        monitor_wait_attempts = monitor_wait_attempts + 1
        if disabled or monitor_wait_attempts >= MONITOR_WAIT_LIMIT then
            obs.timer_remove(configure_desktop_audio_when_monitor_ready)
        end
        return
    end

    ensure_desktop_audio_not_monitored("Monitor Off until OBS Stream Audio is confirmed", false, false)
    monitor_wait_attempts = monitor_wait_attempts + 1
    if monitor_wait_attempts >= MONITOR_WAIT_LIMIT then
        obs.timer_remove(configure_desktop_audio_when_monitor_ready)
        ensure_desktop_audio_not_monitored("Monitor Off - OBS Stream Audio not confirmed")
        print("[AutoVirtCam] OBS Stream Audio was not confirmed - keeping desktop audio monitoring off to avoid double audio")
    end
end

-- lifecycle

function script_description()
    return [[<b>Auto Start Virtual Camera</b><br>
On OBS launch:<br>
&nbsp;&nbsp;1. Routes <i>mic</i> + <i>Game Capture</i> audio to the
monitoring bus (-&gt; OBS Stream Audio -&gt; Discord mic).<br>
&nbsp;&nbsp;2. Routes <i>Desktop Audio (excl. Discord)</i> OFF the
monitoring bus to eliminate Discord-voice leak through Process Audio
Capture exclude-mode bugs.<br>
&nbsp;&nbsp;3. Starts OBS Virtual Camera.<br><br>
Pair with Discord: Camera = <code>OBS Virtual Camera</code>,
Mic = <code>OBS Stream Audio Loopback</code>. Friends see your
OBS scene as your webcam and hear mic + game audio.<br><br>
<b>Important:</b> for friends to hear your game audio, the
<i>Game Capture</i> source must be hooking the game (its preview shows
the game). If only <i>Display Capture</i> is showing the game, friends
will only hear your mic.<br><br>
Discord <b>DISABLE</b>: Noise Suppression / Krisp / Echo Cancellation /
Automatic Gain Control.]]
end

function script_load(_settings)
    print("[AutoVirtCam] loaded")
    -- force mic off monitoring -- routing mic to cable creates a feedback echo loop (discord plays friends voices on speakers -> mic captures bleed -> back into cable -> back to discord -> friends hear themselves). user voice should travel via discords native mic input with its built-in echo cancellation.
    ensure_mic_NOT_to_cable()
    -- fail closed until the companion monitor picker confirms that OBS monitoring is pointed at OBS Stream Audio. Otherwise Monitor and Output can play desktop audio through the users speakers twice.
    ensure_desktop_audio_not_monitored("Monitor Off until OBS Stream Audio is confirmed")
    monitor_wait_attempts = 0
    monitor_failure_logged = false
    obs.timer_remove(configure_desktop_audio_when_monitor_ready)
    obs.timer_add(configure_desktop_audio_when_monitor_ready, MONITOR_WAIT_MS)
    configure_desktop_audio_when_monitor_ready()
    -- cover every discord build variant in the exclude list so the record-only source captures discord audio properly for local recordings.
    ensure_discord_processes_covered(DESKTOP_AUDIO_SOURCE_NAME, true)
    ensure_discord_processes_covered(DISCORD_AUDIO_SOURCE_NAME, true)
    -- defer the virtual-camera start a bit so obss frontend is fully up.
    print("[AutoVirtCam] starting virtual camera in 2s")
    obs.timer_add(start_vcam, 2000)
end

function script_unload()
    obs.timer_remove(start_vcam)
    obs.timer_remove(verify_vcam_started)
    obs.timer_remove(configure_desktop_audio_when_monitor_ready)
    if obs.obs_frontend_virtualcam_active() then
        obs.obs_frontend_stop_virtualcam()
        print("[AutoVirtCam] stopped virtual camera at OBS shutdown")
    end
end
