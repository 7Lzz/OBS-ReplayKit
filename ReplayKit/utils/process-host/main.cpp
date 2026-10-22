#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <metahost.h>
#include <wrl/client.h>

#include <filesystem>
#include <algorithm>
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

std::string Utf8(const std::wstring &value)
{
    if (value.empty()) return {};
    int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), int(value.size()), nullptr, 0, nullptr, nullptr);
    if (!length) throw std::runtime_error("Invalid command-line text.");
    std::string output(length, '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), int(value.size()), output.data(), length, nullptr, nullptr))
        throw std::runtime_error("Cannot convert command-line text.");
    return output;
}

HMODULE Load(const fs::path &path)
{
    HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) throw std::runtime_error("Cannot load a required OBS host component (Windows error " + std::to_string(GetLastError()) + ").");
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
        return fs::is_regular_file(directory / L"obs-frontend-host.dll") &&
                       fs::is_regular_file(directory / L".." / L".." / L"obs-plugins" / L"64bit" / L"obs-browser-role.dll") ? 0 : 2;
    }
    if (helper) return Helper();
    if (browser) {
        HMODULE module = Load(directory / L".." / L".." / L"obs-plugins" / L"64bit" / L"obs-browser-role.dll");
        auto run = reinterpret_cast<int (*)(HINSTANCE)>(GetProcAddress(module, "RunBrowser"));
        if (!run) throw std::runtime_error("The browser host component is incompatible.");
        return run(GetModuleHandleW(nullptr));
    }
    ProcessGroup group;
    HMODULE module = Load(directory / L"obs-frontend-host.dll");
    auto run = reinterpret_cast<int (*)(int, char **)>(GetProcAddress(module, "RunObs"));
    if (!run) throw std::runtime_error("The OBS frontend host component is incompatible.");
    if (!SetEnvironmentVariableW(L"OBS_REPLAYKIT_HOST_EXE", Executable().c_str()))
        throw std::runtime_error("Cannot configure the helper host.");
    std::vector<std::string> utf8;
    for (const auto &argument : arguments) utf8.push_back(Utf8(argument));
    if (std::find(utf8.begin(), utf8.end(), "--disable-updater") == utf8.end()) utf8.push_back("--disable-updater");
    std::vector<char *> argv;
    for (auto &argument : utf8) argv.push_back(argument.data());
    argv.push_back(nullptr);
    return run(int(utf8.size()), argv.data());
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
        Check(SetCurrentProcessExplicitAppUserModelID(L"OBS.ReplayKit.Clips"));
        return Run(fs::path(Executable()).parent_path(), arguments);
    } catch (const std::exception &error) {
        OutputDebugStringA(error.what());
        std::fprintf(stderr, "%s\n", error.what());
        const std::wstring command = GetCommandLineW();
        const bool diagnostic = command.find(L"--host-check") != std::wstring::npos || command.find(L"--host-info") != std::wstring::npos ||
                                command.find(L"--version") != std::wstring::npos || command.find(L"--help") != std::wstring::npos;
        if (!diagnostic && command.find(L"--type=") == std::wstring::npos)
            MessageBoxA(nullptr, error.what(), "ReplayKit OBS host", MB_OK | MB_ICONERROR);
        return 1;
    }
}
