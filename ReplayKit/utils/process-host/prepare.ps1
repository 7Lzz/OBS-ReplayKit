param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
$encoding = [Text.UTF8Encoding]::new($false)
function ReplaceSource([string]$relative, [string]$before, [string]$after) {
    $before = $before.Replace("`r`n", "`n")
    $after = $after.Replace("`r`n", "`n")
    $path = Join-Path $Source $relative
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if ($text.Contains($after)) { return }
    if (-not $text.Contains($before)) { throw "Unsupported source layout: $relative" }
    [IO.File]::WriteAllText($path, $text.Replace($before, $after), $encoding)
}
ReplaceSource 'cmake/windows/architecture.cmake' 'if(OBS_PARENT_ARCHITECTURE STREQUAL ARM64)' 'if(FALSE) # Reuse installed capture plugins; no child architecture build.'
ReplaceSource 'cmake/windows/architecture.cmake' 'elseif(OBS_PARENT_ARCHITECTURE STREQUAL x64)' 'elseif(FALSE) # Reuse installed capture plugins; no child architecture build.'
ReplaceSource 'CMakeLists.txt' 'add_subdirectory(plugins)' "add_subdirectory(plugins/obs-websocket)`nadd_subdirectory(plugins/obs-browser)"
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'add_executable(obs-browser-helper WIN32 EXCLUDE_FROM_ALL)' 'add_library(obs-browser-helper SHARED EXCLUDE_FROM_ALL)'
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'add_executable(OBS::browser-helper ALIAS obs-browser-helper)' 'add_library(OBS::browser-helper ALIAS obs-browser-helper)'
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'OUTPUT_NAME obs-browser-page)' 'OUTPUT_NAME obs-browser-role)'
ReplaceSource 'plugins/obs-browser/obs-browser-page/obs-browser-page-main.cpp' 'int CALLBACK WinMain(HINSTANCE, HINSTANCE, LPSTR, int)' 'extern "C" __declspec(dllexport) int RunBrowser(HINSTANCE)'
$browser = [IO.File]::ReadAllText((Join-Path $Source 'plugins/obs-browser/obs-browser-plugin.cpp'))
if (-not $browser.Contains('role_path = role_path.substr')) {
    ReplaceSource 'plugins/obs-browser/obs-browser-plugin.cpp' 'CefString(&settings.browser_subprocess_path) = abs_path;' @'
wchar_t host_path[32768];
    DWORD host_length = GetModuleFileNameW(nullptr, host_path, 32768);
    if (!host_length || host_length >= 32768) { bfree(abs_path); return; }
    CefString(&settings.browser_subprocess_path) = std::wstring(host_path, host_length);
    std::string resource_path = obs_get_module_binary_path(obs_current_module());
    resource_path = resource_path.substr(0, resource_path.find_last_of('/') + 1);
    CefString(&settings.resources_dir_path) = resource_path;
    CefString(&settings.locales_dir_path) = resource_path + "locales";
'@
    ReplaceSource 'plugins/obs-browser/obs-browser-plugin.cpp' 'CefString(&settings.browser_subprocess_path) = std::wstring(host_path, host_length);' @'
std::wstring role_path(host_path, host_length);
    role_path = role_path.substr(0, role_path.find_last_of(L"\\/") + 1) + L"replaykit-host\\obs64.exe";
    DWORD attributes = GetFileAttributesW(role_path.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & FILE_ATTRIBUTE_DIRECTORY)) {
        blog(LOG_ERROR, "[obs-browser]: The ReplayKit browser role host is missing.");
        bfree(abs_path);
        return;
    }
    CefString(&settings.browser_subprocess_path) = role_path;
'@
}
$root = Join-Path $Source 'CMakeLists.txt'
$hook = 'include("${REPLAYKIT_HOST_DIR}/host.cmake")'
$rootText = [IO.File]::ReadAllText($root)
if (-not $rootText.Contains($hook)) { [IO.File]::WriteAllText($root, $rootText + "`n" + $hook + "`n", $encoding) }
