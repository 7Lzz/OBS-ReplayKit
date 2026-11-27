# writes the values shared/ owns out for the native plugin, the lua scripts, the controls dock and the setup csproj: the helper address from shared/HelperEndpoint.cs, the replaykit folder and its helper exe from shared/ReplayKitFolder.cs, the folder of the saved state from shared/StateLayout.cs and the plugin zip names from shared/BundleLayout.cs. build.bat runs it before every build, and a file is only rewritten when its text changes so the plugin build can still tell it is up to date
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'build_layout.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Constant([string]$relativePath, [string]$pattern, [string]$what) {
    $path = Join-Path $repoRoot $relativePath
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing $what source: $path" }
    $match = [regex]::Match((Get-Content -LiteralPath $path -Raw), $pattern)
    if (-not $match.Success) { throw "Could not read $what from $path" }
    return $match.Groups[1].Value
}

$helperHost = Read-Constant 'shared\HelperEndpoint.cs' 'const\s+string\s+Host\s*=\s*"([^"]+)"' 'the helper host'
$port = [int](Read-Constant 'shared\HelperEndpoint.cs' 'const\s+int\s+Port\s*=\s*(\d+)\s*;' 'the helper port')
$replaykitFolder = Read-Constant 'shared\ReplayKitFolder.cs' 'const\s+string\s+Name\s*=\s*"([^"]+)"' 'the replaykit folder'
$helperExe = Read-Constant 'shared\ReplayKitFolder.cs' 'const\s+string\s+HelperExe\s*=\s*@"([^"]+)"' 'the helper exe'
$stateFolder = Read-Constant 'shared\StateLayout.cs' 'const\s+string\s+Folder\s*=\s*"([^"]+)"' 'the state folder'
$tempFolder = Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+Folder\s*=\s*"([^"]+)"' 'the temp folder'
$tempScratch = Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+ScratchFolder\s*=\s*"([^"]+)"' 'the scratch folder'
$tempClipBuffer = Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+ClipBufferFolder\s*=\s*"([^"]+)"' 'the clip buffer folder'
$tempLogs = Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+LogsFolder\s*=\s*"([^"]+)"' 'the logs folder'
$shareLog = Read-Constant 'shared\TempLayout.cs' 'const\s+string\s+ShareWindowLogFile\s*=\s*"([^"]+)"' 'the share window log'
if ($shareLog -notmatch '^[a-z0-9][a-z0-9._-]{0,63}$') { throw "Share window log must be one plain file name, got '$shareLog'" }
# the share window names, in a header of their own that the share window includes and shared_values.hpp includes for the plugin, since an update ends a running window whose exe changed and a change elsewhere in shared/ must not rebuild it
$share = [ordered]@{}
foreach ($name in 'ExeName', 'Folder', 'PipeName', 'StateMappingName', 'FrameEventName', 'AudioEventName', 'InstanceMutexName', 'WindowClass') {
    $value = Read-Constant 'shared\ShareWindowLayout.cs' ('const\s+string\s+' + $name + '\s*=\s*"([^"]+)"\s*;') "the share window $name"
    if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') { throw "Share window $name has an unexpected shape: '$value'" }
    $share[$name] = $value
}
$shareProtocol = [int](Read-Constant 'shared\ShareWindowLayout.cs' 'const\s+int\s+ProtocolVersion\s*=\s*(\d+)\s*;' 'the share window protocol')
# the mixer grouping id, in a header of its own in the process host folder, since the host fingerprints only its own folder and must rebuild when the id changes
$audioGroup = [guid](Read-Constant 'shared\AudioGroup.cs' 'const\s+string\s+ObsSessionGroup\s*=\s*"([0-9a-fA-F-]{36})"\s*;' 'the obs audio group')
$groupBytes = $audioGroup.ToByteArray()
$cppAudioGroup = '{{0x{0:x8}, 0x{1:x4}, 0x{2:x4}, {{{3}}}}}' -f [BitConverter]::ToUInt32($groupBytes, 0), [BitConverter]::ToUInt16($groupBytes, 4), [BitConverter]::ToUInt16($groupBytes, 6), (($groupBytes[8..15] | ForEach-Object { '0x{0:x2}' -f $_ }) -join ', ')
$cppShare = ($share.Keys | ForEach-Object { "inline constexpr wchar_t kShareWindow$_[] = L`"$($share[$_])`";" }) -join "`n"
$localeCodes = (Read-Constant 'shared\LanguageMatch.cs' 'const\s+string\s+SupportedCodes\s*=\s*"([^"]+)"' 'the supported languages') -split ','
foreach ($code in $localeCodes) {
    if ($code -cnotmatch '^[a-z]{2,3}-[A-Za-z]{2,4}$') { throw "Unexpected locale code: $code" }
}
$cppLocales = ($localeCodes | ForEach-Object { '"' + $_ + '"' }) -join ', '

# every consumer pastes these into code or into a path, so anything outside the expected shape is refused
if ($helperHost -notmatch '^127\.\d{1,3}\.\d{1,3}\.\d{1,3}$') { throw "Helper host must be a loopback address, got '$helperHost'" }
if ($port -lt 1024 -or $port -gt 65535) { throw "Helper port must be between 1024 and 65535, got $port" }
if ($replaykitFolder -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$') { throw "ReplayKit folder must be one plain folder name, got '$replaykitFolder'" }
if ($helperExe -notmatch '^([A-Za-z0-9][A-Za-z0-9._-]*\\)+[A-Za-z0-9][A-Za-z0-9._-]*\.exe$') { throw "Helper exe must be a plain relative path, got '$helperExe'" }
$cppHelperExe = $helperExe.Replace('\', '\\')
if ($stateFolder -notmatch '^[a-z0-9][a-z0-9-]{0,31}$') { throw "State folder must be one plain folder name, got '$stateFolder'" }
$obsArgs = Read-Constant 'shared\ObsLaunch.cs' 'const\s+string\s+Arguments\s*=\s*"([^"]+)"' 'the obs launch arguments'
if ($obsArgs -notmatch '^(--[a-z0-9-]+(=[A-Za-z0-9-]+)?)( --[a-z0-9-]+(=[A-Za-z0-9=-]+)?)*$') { throw "OBS launch arguments must be plain switches, got '$obsArgs'" }
foreach ($name in $tempFolder, $tempScratch, $tempClipBuffer) { if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,31}$') { throw "Temp folder must be one plain folder name, got '$name'" } }
$origin = "http://${helperHost}:$port"
# the game out of focus card, whose source the plugin registers and adds to the scenes and the capture switch script shows
$focusCardName = Read-Constant 'shared\OverlayScene.cs' 'const\s+string\s+FocusCardSourceName\s*=\s*"([^"]+)"\s*;' 'the focus card source name'
$focusCardKind = Read-Constant 'shared\OverlayScene.cs' 'const\s+string\s+FocusCardSourceKind\s*=\s*"([^"]+)"\s*;' 'the focus card source kind'
if ($focusCardName -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$') { throw "Focus card source name has an unexpected shape: '$focusCardName'" }
if ($focusCardKind -notmatch '^[a-z0-9][a-z0-9_-]{0,63}$') { throw "Focus card source kind has an unexpected shape: '$focusCardKind'" }
# the three captures the capture switch script shows one at a time and the focus card covers
$captures = [ordered]@{}
foreach ($kind in 'Display', 'Game', 'Window') {
    $value = Read-Constant 'shared\OverlayScene.cs' ('const\s+string\s+' + $kind + 'CaptureSourceName\s*=\s*"([^"]+)"\s*;') "the $kind capture source name"
    if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$') { throw "$kind capture source name has an unexpected shape: '$value'" }
    $captures[$kind] = $value
}
# the input overlay sources the plugin hides together and the source kind of the bongo cat it registers
$overlayNames = [ordered]@{}
foreach ($name in 'WasdSourceName', 'MouseSourceName', 'BongoSourceKind') {
    $value = Read-Constant 'shared\OverlayScene.cs' ('const\s+string\s+' + $name + '\s*=\s*"([^"]+)"\s*;') "the $name"
    if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$') { throw "$name has an unexpected shape: '$value'" }
    $overlayNames[$name] = $value
}
# the windows the plugin makes beside obs, with the titles the helper finds them by and their smallest sizes
$windows = [ordered]@{}
foreach ($name in 'Settings', 'Clips', 'Setup') {
    $title = Read-Constant 'shared\WindowLayout.cs' ('const\s+string\s+' + $name + 'Title\s*=\s*"([^"]+)"\s*;') "the $name window title"
    if ($title -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$') { throw "$name window title has an unexpected shape: '$title'" }
    $width = [int](Read-Constant 'shared\WindowLayout.cs' ('const\s+int\s+' + $name + 'MinWidth\s*=\s*(\d+)\s*;') "the $name window width")
    $height = [int](Read-Constant 'shared\WindowLayout.cs' ('const\s+int\s+' + $name + 'MinHeight\s*=\s*(\d+)\s*;') "the $name window height")
    if ($width -lt 200 -or $height -lt 200) { throw "$name window minimum size is too small: ${width}x$height" }
    $windows[$name] = @{ Title = $title; Width = $width; Height = $height }
}
$cppWindows = ($windows.Keys | ForEach-Object { "inline constexpr char k${_}WindowTitle[] = `"$($windows[$_].Title)`";`ninline constexpr int k${_}WindowMinWidth = $($windows[$_].Width);`ninline constexpr int k${_}WindowMinHeight = $($windows[$_].Height);" }) -join "`n"
# what the capture switch script does while the settings file has no word yet on the game out of focus card or on keeping the game in front
$autoGameDefaults = [ordered]@{}
foreach ($name in 'ScreenshareAutoGameOutOfFocus', 'ScreenshareAutoGameKeepFocused') {
    $autoGameDefaults[$name] = (Read-Constant 'shared\SettingDefaults.cs' ('const\s+bool\s+' + $name + '\s*=\s*(true|false)\s*;') "the default of $name").ToLowerInvariant()
}

# the old places of each state file, from the table of shared/StateLayout.cs, so lua and the plugin look where the helper and the installer look
$stateText = Get-Content -LiteralPath (Join-Path $repoRoot 'shared\StateLayout.cs') -Raw
$stateNames = @{}; foreach ($m in [regex]::Matches($stateText, 'const\s+string\s+(\w+File)\s*=\s*"([^"]+)"\s*;')) { $stateNames[$m.Groups[1].Value] = $m.Groups[2].Value }
$stateFolders = @{}; foreach ($m in [regex]::Matches($stateText, 'static\s+readonly\s+string\s+(\w+)\s*=\s*@?"([^"]*)"\s*;')) { $stateFolders[$m.Groups[1].Value] = $m.Groups[2].Value.Replace('\', '/') }
$oldPlaces = [ordered]@{}
foreach ($m in [regex]::Matches($stateText, 'new\s+Entry\s*\{\s*File\s*=\s*(\w+)\s*,\s*OldFolders\s*=\s*new\[\]\s*\{([^}]*)\}')) {
    $file = $stateNames[$m.Groups[1].Value]
    if (-not $file -or $file -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw "StateLayout entry $($m.Groups[1].Value) has no plain file name" }
    $places = @(foreach ($ref in ($m.Groups[2].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if (-not $stateFolders.ContainsKey($ref)) { throw "StateLayout old folder $ref is not a plain literal" }
        $folder = $stateFolders[$ref]
        if ($folder -ne '' -and $folder -notmatch '^[a-z0-9][a-z0-9-]*(/[a-z0-9][a-z0-9-]*)*$') { throw "StateLayout old folder $ref has an unexpected shape: '$folder'" }
        if ($folder -eq '') { '' } else { $folder + '/' }
    })
    if ($places.Count -lt 1 -or $places.Count -gt 3) { throw "StateLayout entry $file needs one to three old folders" }
    $oldPlaces[$file] = $places
}
if ($oldPlaces.Count -lt 1) { throw 'Could not read the entries of shared/StateLayout.cs' }
$luaPlaces = ($oldPlaces.Keys | ForEach-Object { '["' + $_ + '"] = { ' + (($oldPlaces[$_] | ForEach-Object { '"' + $_ + '"' }) -join ', ') + ' }' }) -join ', '
$cppPlaces = ($oldPlaces.Keys | ForEach-Object { $list = @($oldPlaces[$_] | ForEach-Object { '"' + $_ + '"' }); while ($list.Count -lt 3) { $list += 'nullptr' }; "`t{`"" + $_ + '", {' + ($list -join ', ') + '}},' }) -join "`n"
function Write-Generated([string]$relativePath, [string]$text) {
    $path = Join-Path $ReplayKitGenerated $relativePath
    Assert-BuildPath $path
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    if ((Test-Path -LiteralPath $path) -and ([IO.File]::ReadAllText($path) -ceq $text)) { return }
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}

Write-Generated "share_window_values.hpp" @"
// generated by scripts/sync_shared.ps1 from shared/TempLayout.cs and shared/ShareWindowLayout.cs -- edit those files, not this one
#pragma once

namespace rk {

inline constexpr char kTempFolder[] = "$tempFolder";
inline constexpr char kTempLogsFolder[] = "$tempLogs";
inline constexpr wchar_t kShareWindowLogFile[] = L"$shareLog";
$cppShare
inline constexpr unsigned kShareWindowProtocolVersion = $shareProtocol;

} // namespace rk

"@

Write-Generated "obs_audio_group.hpp" @"
// generated by scripts/sync_shared.ps1 from shared/AudioGroup.cs -- edit that file, not this one
#pragma once

#include <guiddef.h>

inline constexpr GUID kObsAudioGroup = $cppAudioGroup;

"@

Write-Generated "replaykit_folder.hpp" @"
// generated by scripts/sync_shared.ps1 from shared/ReplayKitFolder.cs -- edit that file, not this one
#pragma once

inline constexpr wchar_t kReplayKitFolder[] = L"$replaykitFolder";
inline constexpr wchar_t kHelperExe[] = L"$cppHelperExe";

"@

Write-Generated "core\shared_values.hpp" @"
// generated by scripts/sync_shared.ps1 from shared/HelperEndpoint.cs, shared/ReplayKitFolder.cs, shared/StateLayout.cs, shared/TempLayout.cs, shared/OverlayScene.cs and shared/WindowLayout.cs, with the share window names from share_window_values.hpp -- edit those files, not this one
#pragma once

#include "share_window_values.hpp"

namespace rk {

inline constexpr char kHelperHost[] = "$helperHost";
inline constexpr int kHelperPort = $port;
inline constexpr char kReplayKitFolder[] = "$replaykitFolder";
inline constexpr char kStateFolder[] = "$stateFolder";
inline constexpr char kTempScratchFolder[] = "$tempScratch";
inline constexpr char kTempClipBufferFolder[] = "$tempClipBuffer";
inline constexpr const char *kSupportedLocales[] = {$cppLocales};
inline constexpr char kFocusCardSourceName[] = "$focusCardName";
inline constexpr char kFocusCardSourceKind[] = "$focusCardKind";
inline constexpr char kDisplayCaptureSourceName[] = "$($captures['Display'])";
inline constexpr char kGameCaptureSourceName[] = "$($captures['Game'])";
inline constexpr char kWindowCaptureSourceName[] = "$($captures['Window'])";
inline constexpr char kWasdSourceName[] = "$($overlayNames['WasdSourceName'])";
inline constexpr char kMouseSourceName[] = "$($overlayNames['MouseSourceName'])";
inline constexpr char kBongoSourceKind[] = "$($overlayNames['BongoSourceKind'])";
$cppWindows

// the places each state file lived before the state folder, relative to the replaykit folder, newest first
struct StateOldPlaces {
	const char *file;
	const char *places[3];
};
inline constexpr StateOldPlaces kStateOldPlaces[] = {
$cppPlaces
};

} // namespace rk

"@

Write-Generated "shared_values.lua" @"
-- generated by scripts/sync_shared.ps1 from shared/HelperEndpoint.cs, shared/StateLayout.cs, shared/ObsLaunch.cs, shared/OverlayScene.cs, shared/ShareWindowLayout.cs and shared/SettingDefaults.cs -- edit those files, not this one
return { host = "$helperHost", port = $port, origin = "$origin", state_folder = "$stateFolder", obs_args = "$obsArgs", share_window_exe = "$($share['ExeName'])", focus_card_source = "$focusCardName", capture_sources = { display = "$($captures['Display'])", game = "$($captures['Game'])", window = "$($captures['Window'])" }, auto_game_defaults = { out_of_focus = $($autoGameDefaults['ScreenshareAutoGameOutOfFocus']), keep_focused = $($autoGameDefaults['ScreenshareAutoGameKeepFocused']) }, old_places = { $luaPlaces } }

"@

Write-Generated "helper-endpoint.js" @"
// generated by scripts/sync_shared.ps1 from shared/HelperEndpoint.cs -- edit that file, not this one
window.RK_HELPER_ORIGIN = "$origin";

"@

# the plugin zips of the release bundle, for the setup csproj that packs them; the installer reads the same constants
$bundle = [ordered]@{}
foreach ($name in 'BangoCatZip', 'BangoCatZipRoot', 'BangoCatPluginDir', 'PresetsZip', 'PresetsZipRoot', 'InputOverlayZip', 'InputOverlayZipRoot', 'WinCaptureAudioZip', 'WinCaptureAudioDllDir', 'WinCaptureAudioDll', 'WinCaptureAudioDataDir', 'InstallersZip', 'VbCablePack', 'ShaderfilterZip') {
    $value = Read-Constant 'shared\BundleLayout.cs' ('const\s+string\s+' + $name + '\s*=\s*"([^"]+)"\s*;') "the bundle name $name"
    $shape = if ($name -like '*Zip' -or $name -eq 'VbCablePack') { '^[A-Za-z0-9][A-Za-z0-9 ._-]*\.zip$' } elseif ($name -like '*Dll') { '^[A-Za-z0-9][A-Za-z0-9._-]*\.dll$' } else { '^([A-Za-z0-9][A-Za-z0-9 ._-]*/)+$' }
    if ($value -notmatch $shape -or $value -match '(^|/)\.\.?/') { throw "Bundle name $name has an unexpected shape: '$value'" }
    $bundle[$name] = $value.Replace('/', '\')
}
$bundle['BangoCatZipPrefix'] = $bundle['BangoCatZipRoot'] + $bundle['BangoCatPluginDir']
$bundle['ReplayKitFolder'] = $replaykitFolder
$properties = ($bundle.Keys | ForEach-Object { "    <$_>$([Security.SecurityElement]::Escape($bundle[$_]))</$_>" }) -join "`n"
Write-Generated "Bundle.props" @"
<Project>
  <!-- generated by scripts/sync_shared.ps1 from shared/BundleLayout.cs and shared/ReplayKitFolder.cs; edit those files, not this one -->
  <PropertyGroup>
$properties
  </PropertyGroup>
</Project>

"@
Write-Host "    ReplayKit helper $origin, saved state in $replaykitFolder\$stateFolder"
