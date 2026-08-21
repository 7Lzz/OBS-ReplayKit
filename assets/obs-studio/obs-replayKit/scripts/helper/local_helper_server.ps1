# local_helper_server.ps1 entry point for the obs replaykit helper. feature code lives in ./modules.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference   = 'SilentlyContinue'

$script:HelperRoot = $PSScriptRoot
$script:ModuleRoot = Join-Path $script:HelperRoot 'modules'
$script:HelperModules = @(
    '00_state.ps1',
    '10_auth_core.ps1',
    '11_browser_cookies.ps1',
    '20_config.ps1',
    '30_native.ps1',
    '40_clips.ps1',
    '41_trim.ps1',
    '42_compress_overwrite.ps1',
    '50_upload_state.ps1',
    '51_upload.ps1',
    '52_compression.ps1',
    '60_media.ps1',
    '61_obs_websocket.ps1',
    '62_replaykit_settings.ps1',
    '63_update.ps1',
    '64_discord_projector.ps1',
    '65_uninstall.ps1',
    '70_http_response.ps1',
    '71_routes.ps1',
    '80_connection.ps1',
    '90_runtime.ps1'
)

# snapshot of what functions exist before any module loads, so 90_runtime.ps1 can diff against it to find exactly what the 21 modules defined and register that set into the connection pools initialsessionstate, letting pooled runspaces call them without re-dot-sourcing every file per request.
$script:PreloadFunctionNames = (Get-ChildItem Function:\).Name

foreach ($module in $script:HelperModules) {
    $modulePath = Join-Path $script:ModuleRoot $module
    if (-not (Test-Path -LiteralPath $modulePath)) {
        throw "Missing helper module: $modulePath"
    }
    . $modulePath
}
