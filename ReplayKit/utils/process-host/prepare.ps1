param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
$encoding = [Text.UTF8Encoding]::new($false)
function ReplaceSource([string]$relative, [string]$before, [string]$after) {
    $path = Join-Path $Source $relative
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if ($text.Contains($after)) { return }
    if (-not $text.Contains($before)) { throw "Unsupported source layout: $relative" }
    [IO.File]::WriteAllText($path, $text.Replace($before, $after), $encoding)
}
ReplaceSource 'cmake/windows/architecture.cmake' 'if(OBS_PARENT_ARCHITECTURE STREQUAL ARM64)' 'if(FALSE) # Reuse installed capture plugins; no child architecture build.'
ReplaceSource 'cmake/windows/architecture.cmake' 'elseif(OBS_PARENT_ARCHITECTURE STREQUAL x64)' 'elseif(FALSE) # Reuse installed capture plugins; no child architecture build.'
ReplaceSource 'CMakeLists.txt' 'add_subdirectory(plugins)' "add_subdirectory(plugins/obs-websocket)`nadd_subdirectory(plugins/obs-browser)"
ReplaceSource 'frontend/CMakeLists.txt' 'add_executable(obs-studio)' 'add_library(obs-studio SHARED)'
ReplaceSource 'frontend/CMakeLists.txt' 'add_executable(OBS::studio ALIAS obs-studio)' 'add_library(OBS::studio ALIAS obs-studio)'
ReplaceSource 'frontend/CMakeLists.txt' 'OUTPUT_NAME "$<IF:$<PLATFORM_ID:Windows>,obs64,obs>"' 'OUTPUT_NAME "obs-frontend-host"'
ReplaceSource 'frontend/CMakeLists.txt' 'list(JOIN obs_module_list "|" SAFE_MODULES)' @'
list(APPEND obs_module_list coreaudio-encoder decklink decklink-captions decklink-output-ui frontend-tools image-source
     obs-browser obs-ffmpeg obs-filters obs-libfdk obs-nvenc obs-outputs obs-qsv11 obs-text obs-transitions obs-vst
     obs-webrtc obs-websocket obs-x264 rtmp-services text-freetype2 vlc-video win-capture win-dshow win-wasapi)
list(REMOVE_DUPLICATES obs_module_list)
list(JOIN obs_module_list "|" SAFE_MODULES)
'@
ReplaceSource 'frontend/obs-main.cpp' 'int main(int argc, char *argv[])' 'extern "C" __declspec(dllexport) int RunObs(int argc, char *argv[])'
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'add_executable(obs-browser-helper WIN32 EXCLUDE_FROM_ALL)' 'add_library(obs-browser-helper SHARED EXCLUDE_FROM_ALL)'
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'add_executable(OBS::browser-helper ALIAS obs-browser-helper)' 'add_library(OBS::browser-helper ALIAS obs-browser-helper)'
ReplaceSource 'plugins/obs-browser/cmake/os-windows.cmake' 'OUTPUT_NAME obs-browser-page)' 'OUTPUT_NAME obs-browser-role)'
ReplaceSource 'plugins/obs-browser/obs-browser-page/obs-browser-page-main.cpp' 'int CALLBACK WinMain(HINSTANCE, HINSTANCE, LPSTR, int)' 'extern "C" __declspec(dllexport) int RunBrowser(HINSTANCE)'
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
$root = Join-Path $Source 'CMakeLists.txt'
$hook = 'include("${REPLAYKIT_HOST_DIR}/host.cmake")'
$rootText = [IO.File]::ReadAllText($root)
if (-not $rootText.Contains($hook)) { [IO.File]::WriteAllText($root, $rootText + "`n" + $hook + "`n", $encoding) }
