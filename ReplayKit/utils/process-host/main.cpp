#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <metahost.h>
#include <wrl/client.h>

#include <filesystem>
#include <cstdio>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

extern "C" __declspec(dllexport) unsigned ProcessHostVersion() { return 1; }

namespace {

class ProcessGroup {
public:
    ProcessGroup()
    {
        handle = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        if (!handle || !SetInformationJobObject(handle, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
            !AssignProcessToJobObject(handle, GetCurrentProcess())) {
            if (handle) CloseHandle(handle);
            throw std::runtime_error("Cannot establish the OBS process group.");
        }
    }
    ~ProcessGroup() { CloseHandle(handle); }
    ProcessGroup(const ProcessGroup &) = delete;
    ProcessGroup &operator=(const ProcessGroup &) = delete;
private:
    HANDLE handle = nullptr;
};

std::wstring Executable()
{
    std::vector<wchar_t> path(32768);
    DWORD length = GetModuleFileNameW(nullptr, path.data(), DWORD(path.size()));
    if (!length || length >= path.size()) throw std::runtime_error("Cannot locate the OBS host.");
    return {path.data(), length};
}

HMODULE Load(const fs::path &path)
{
    HMODULE module = LoadLibraryExW(fs::weakly_canonical(path).c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) throw std::runtime_error("Cannot load " + path.filename().u8string() + " (Windows error " + std::to_string(GetLastError()) + ").");
    return module;
}

void Check(HRESULT result)
{
    if (FAILED(result)) throw std::runtime_error("The ReplayKit runtime could not start (HRESULT " + std::to_string(unsigned(result)) + ").");
}

int Helper()
{
    Check(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED));
    fs::path root;
    std::vector<wchar_t> environment(32768);
    DWORD length = GetEnvironmentVariableW(L"APPDATA", environment.data(), DWORD(environment.size()));
    if (length >= environment.size()) throw std::runtime_error("The application data directory is invalid.");
    if (length) {
        root = fs::path(std::wstring(environment.data(), length));
    } else {
        PWSTR roaming = nullptr;
        Check(SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &roaming));
        root = fs::path(roaming);
        CoTaskMemFree(roaming);
    }
    if (!root.is_absolute()) throw std::runtime_error("The application data directory must be absolute.");
    fs::path assembly = root / L"obs-studio" / L"obs-replayKit" / L"scripts" / L"helper" / L"OBSReplayKit.exe";
    if (!fs::is_regular_file(assembly)) throw std::runtime_error("The ReplayKit helper assembly is missing.");
    if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) || !AddDllDirectory(assembly.parent_path().c_str()))
        throw std::runtime_error("Cannot configure the helper library directory.");

    HMODULE runtimeLibrary = LoadLibraryExW(L"mscoree.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!runtimeLibrary) throw std::runtime_error("The .NET Framework runtime is unavailable.");
    auto create = reinterpret_cast<HRESULT(STDAPICALLTYPE *)(REFCLSID, REFIID, LPVOID *)>(GetProcAddress(runtimeLibrary, "CLRCreateInstance"));
    if (!create) throw std::runtime_error("The .NET Framework hosting API is unavailable.");
    ComPtr<ICLRMetaHost> meta;
    Check(create(CLSID_CLRMetaHost, IID_PPV_ARGS(&meta)));
    ComPtr<ICLRRuntimeInfo> info;
    Check(meta->GetRuntime(L"v4.0.30319", IID_PPV_ARGS(&info)));
    BOOL loadable = FALSE;
    Check(info->IsLoadable(&loadable));
    if (!loadable) throw std::runtime_error("The .NET Framework runtime cannot be loaded.");
    ComPtr<ICLRRuntimeHost> runtime;
    Check(info->GetInterface(CLSID_CLRRuntimeHost, IID_PPV_ARGS(&runtime)));
    Check(runtime->Start());
    DWORD result = 1;
    Check(runtime->ExecuteInDefaultAppDomain(assembly.c_str(), L"ReplayKitHelper.HostEntry", L"Run", GetCommandLineW(), &result));
    return int(result);
}

int Run(const fs::path &directory, const std::vector<std::wstring> &arguments)
{
    if (arguments.size() == 2 && arguments[1] == L"--version") {
        std::puts("OBS Studio role host - " OBS_HOST_VERSION);
        return 0;
    }
    if (arguments.size() == 2 && arguments[1] == L"--host-child-check") {
        BOOL grouped = FALSE;
        return IsProcessInJob(GetCurrentProcess(), nullptr, &grouped) && grouped ? 0 : 2;
    }
    if (arguments.size() == 2 && arguments[1] == L"--host-group-check") {
        ProcessGroup group;
        std::wstring executable = Executable();
        std::wstring command = L"\"" + executable + L"\" --host-child-check";
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION child{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
                            nullptr, directory.c_str(), &startup, &child))
            throw std::runtime_error("Cannot verify the OBS process group.");
        CloseHandle(child.hThread);
        DWORD result = 1;
        if (WaitForSingleObject(child.hProcess, 10000) == WAIT_OBJECT_0) GetExitCodeProcess(child.hProcess, &result);
        else TerminateProcess(child.hProcess, 1);
        CloseHandle(child.hProcess);
        return int(result);
    }
    bool helper = false;
    bool browser = false;
    bool check = false;
    for (size_t i = 1; i < arguments.size(); ++i) {
        const auto &argument = arguments[i];
        if (argument == L"--replaykit-helper") {
            if (i != 1 || helper) throw std::runtime_error("Invalid helper startup arguments.");
            helper = true;
        }
        if (argument.rfind(L"--type=", 0) == 0) {
            if (browser || argument.size() <= 7 || argument.size() > 80) throw std::runtime_error("Invalid browser startup arguments.");
            browser = true;
        }
        if (argument == L"--host-check") check = true;
    }
    if (browser && helper) throw std::runtime_error("Conflicting OBS host roles.");
    if (check) {
        if (arguments.size() != 2 || browser || helper) throw std::runtime_error("Invalid host verification arguments.");
        HMODULE role = Load(directory / OBS_BROWSER_ROLE_PATH);
        if (!GetProcAddress(role, "RunBrowser"))
            throw std::runtime_error("The OBS host components are incompatible.");
        return 0;
    }
    if (helper) return Helper();
    if (browser) {
        HMODULE module = Load(directory / OBS_BROWSER_ROLE_PATH);
        auto run = reinterpret_cast<int (*)(HINSTANCE)>(GetProcAddress(module, "RunBrowser"));
        if (!run) throw std::runtime_error("The browser host component is incompatible.");
        return run(GetModuleHandleW(nullptr));
    }
    throw std::runtime_error("This OBS role host only starts browser or ReplayKit helper processes. Start OBS Studio normally.");
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX);
    try {
        int count = 0;
        PWSTR *parsed = CommandLineToArgvW(GetCommandLineW(), &count);
        if (!parsed) throw std::runtime_error("Cannot read startup arguments.");
        std::vector<std::wstring> arguments(parsed, parsed + count);
        LocalFree(parsed);
        fs::path directory = fs::path(Executable()).parent_path();
        if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) || !AddDllDirectory(directory.parent_path().c_str()))
            throw std::runtime_error("Cannot configure the OBS library directory.");
        Check(SetCurrentProcessExplicitAppUserModelID(L"OBS.ReplayKit.Clips"));
        return Run(directory, arguments);
    } catch (const std::exception &error) {
        OutputDebugStringA(error.what());
        std::fprintf(stderr, "%s\n", error.what());
        const std::wstring command = GetCommandLineW();
        const bool diagnostic = command.find(L"--host-") != std::wstring::npos ||
                                command.find(L"--version") != std::wstring::npos || command.find(L"--help") != std::wstring::npos;
        if (!diagnostic && command.find(L"--type=") == std::wstring::npos)
            MessageBoxA(nullptr, error.what(), "ReplayKit OBS host", MB_OK | MB_ICONERROR);
        return 1;
    }
}
