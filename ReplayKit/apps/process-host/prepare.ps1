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
    wchar_t *page_path = nullptr;
    os_utf8_to_wcs_ptr(abs_path, 0, &page_path);
    std::filesystem::path role_dll = std::filesystem::path(page_path ? page_path : L"").replace_filename(L"obs-browser-role.dll");
    bfree(page_path);
    // antivirus can quarantine or block the role host or its browser role, or stop the host after it started, so the host is used only when no stop of these same files was saved and a check run of it succeeds; the stock obs-browser-page.exe beside this dll runs the pages otherwise
    bool role_ready = false;
    std::string role_stamp = ReplayKitRoleStamp(role_path, role_dll.wstring());
    if (role_stamp.empty()) {
        blog(LOG_WARNING, "[obs-browser]: The ReplayKit browser role host is missing.");
    } else if (ReplayKitReadStopped() == role_stamp) {
        blog(LOG_WARNING, "[obs-browser]: The ReplayKit browser role host was stopped in an earlier session.");
    } else {
        std::wstring check = L"\"" + role_path + L"\" --host-check";
        STARTUPINFOW startup = {sizeof(startup)};
        PROCESS_INFORMATION process = {};
        if (CreateProcessW(role_path.c_str(), check.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
            DWORD code = 1;
            if (WaitForSingleObject(process.hProcess, 10000) == WAIT_OBJECT_0) GetExitCodeProcess(process.hProcess, &code);
            else TerminateProcess(process.hProcess, 1);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
            role_ready = code == 0;
            if (!role_ready) blog(LOG_WARNING, "[obs-browser]: The ReplayKit browser role host failed its check (exit code %lu).", code);
        } else {
            DWORD error = GetLastError();
            blog(LOG_WARNING, "[obs-browser]: The ReplayKit browser role host could not start (Windows error %lu).", error);
        }
    }
    if (role_ready) {
        replaykit_role_stamp = role_stamp;
        CefString(&settings.browser_subprocess_path) = role_path;
    } else {
        replaykit_role_stamp.clear();
        blog(LOG_WARNING, "[obs-browser]: Running the browser pages in obs-browser-page.exe instead of the ReplayKit browser role host.");
        CefString(&settings.browser_subprocess_path) = abs_path;
    }
'@
}
ReplaceSource 'plugins/obs-browser/obs-browser-plugin.cpp' "static void BrowserInit(void)`n{" @'
#include <replaykit_folder.hpp>
#include <filesystem>
#include <fstream>

// the sizes and write times of the role host exe and its browser role, so a stop saved for one build of them never holds back another
static std::string ReplayKitRoleStamp(const std::wstring &host, const std::wstring &role)
{
    std::string stamp;
    for (const std::wstring *path : {&host, &role}) {
        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!GetFileAttributesExW(path->c_str(), GetFileExInfoStandard, &data) || (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
            return {};
        stamp += std::to_string((uint64_t(data.nFileSizeHigh) << 32) | data.nFileSizeLow) + ":" +
                 std::to_string((uint64_t(data.ftLastWriteTime.dwHighDateTime) << 32) | data.ftLastWriteTime.dwLowDateTime) + ";";
    }
    return stamp;
}

static std::filesystem::path ReplayKitStoppedPath()
{
    wchar_t appdata[32768];
    DWORD length = GetEnvironmentVariableW(L"APPDATA", appdata, 32768);
    if (!length || length >= 32768) return {};
    return std::filesystem::path(std::wstring(appdata, length)) / L"obs-studio" / kReplayKitFolder / kStateFolder / kRoleHostStoppedFile;
}

static std::string ReplayKitReadStopped()
{
    std::ifstream file(ReplayKitStoppedPath(), std::ios::binary);
    std::string stamp;
    std::getline(file, stamp);
    return stamp;
}

// the stamp of the role host the pages run in this session, empty while they run in obs-browser-page.exe; set and read on the cef thread only
static std::string replaykit_role_stamp;
static bool replaykit_role_stop_saved = false;
static int replaykit_role_ended = 0;

// cef takes the subprocess path once per obs session, so a host that antivirus stops after it started is saved here and the next start runs the pages in obs-browser-page.exe; chromium reports a process ended from outside with any code but 1 as crashed, so an ended page process only counts once a second one ends in the session (one page crashing by itself ends one), while a host windows refuses to start again counts at once, and running out of memory never does
void ReplayKitRoleHostStopped(cef_termination_status_t status)
{
    if (replaykit_role_stamp.empty() || replaykit_role_stop_saved) return;
    if (status == TS_PROCESS_CRASHED || status == TS_PROCESS_WAS_KILLED || status == TS_ABNORMAL_TERMINATION) {
        if (++replaykit_role_ended < 2) return;
    } else if (status != TS_LAUNCH_FAILED && status != TS_INTEGRITY_FAILURE) {
        return;
    }
    replaykit_role_stop_saved = true;
    std::ofstream file(ReplayKitStoppedPath(), std::ios::binary | std::ios::trunc);
    file << replaykit_role_stamp;
    file.close();
    if (file) blog(LOG_WARNING, "[obs-browser]: A page process of the ReplayKit browser role host was stopped (status %d), so the next start of OBS runs the pages in obs-browser-page.exe.", int(status));
    else blog(LOG_ERROR, "[obs-browser]: A page process of the ReplayKit browser role host was stopped (status %d) and the stop could not be saved.", int(status));
}

static void BrowserInit(void)
{
'@
# the browser sources and the docks have their own cef clients, and both report a page process that stopped
ReplaceSource 'plugins/obs-browser/browser-client.cpp' "void BrowserClient::OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus`n#if CHROME_VERSION_BUILD >= 6367`n`t`t`t`t`t      ,`n`t`t`t`t`t      int, const CefString &error_string`n#endif`n)`n{`n" "void ReplayKitRoleHostStopped(cef_termination_status_t status);`n`nvoid BrowserClient::OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus status`n#if CHROME_VERSION_BUILD >= 6367`n`t`t`t`t`t      ,`n`t`t`t`t`t      int, const CefString &error_string`n#endif`n)`n{`n`tReplayKitRoleHostStopped(status);`n"
ReplaceSource 'plugins/obs-browser/panel/browser-panel-client.hpp' "`tvirtual void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;" "`tvirtual void OnRenderProcessTerminated(CefRefPtr<CefBrowser> browser, TerminationStatus status, int error_code, const CefString &error_string) override;`n`tvirtual void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;"
ReplaceSource 'plugins/obs-browser/panel/browser-panel-client.cpp' '#include "browser-panel-client.hpp"' @'
#include "browser-panel-client.hpp"

void ReplayKitRoleHostStopped(cef_termination_status_t status);

void QCefBrowserClient::OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus status, int, const CefString &)
{
	ReplayKitRoleHostStopped(status);
}
'@
$root = Join-Path $Source 'CMakeLists.txt'
$hook = 'include("${REPLAYKIT_HOST_DIR}/host.cmake")'
$rootText = [IO.File]::ReadAllText($root)
if (-not $rootText.Contains($hook)) { [IO.File]::WriteAllText($root, $rootText + "`n" + $hook + "`n", $encoding) }
