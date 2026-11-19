#define WIN32_LEAN_AND_MEAN
#include <windows.h>
// mscoree.h also defines eAll, so the one of the audio flow enum is renamed
#define eAll eAllAudioFlows
#include <mmdeviceapi.h>
#undef eAll
#include <audiopolicy.h>
#include <shellapi.h>
#include <shlobj.h>
#include <metahost.h>

#include "obs_audio_group.hpp"
#include <wrl/client.h>

#include <filesystem>
#include <cstdio>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;

extern "C" __declspec(dllexport) unsigned ProcessHostVersion() { return 1; }

namespace {

class ProcessGroup {
public:
    ProcessGroup()
    {
        BOOL grouped = FALSE;
        if (!IsProcessInJob(GetCurrentProcess(), nullptr, &grouped))
            throw std::runtime_error("Cannot query the OBS process group.");
        if (grouped) return;
        handle = CreateJobObjectW(nullptr, nullptr);
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        if (!handle || !SetInformationJobObject(handle, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) ||
            !AssignProcessToJobObject(handle, GetCurrentProcess())) {
            if (handle) CloseHandle(handle);
            throw std::runtime_error("Cannot establish the OBS process group.");
        }
    }
    ~ProcessGroup() { if (handle) CloseHandle(handle); }
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

// puts the pages and the helper under the grouping id the plugin gives obs, so the windows mixer shows them as one slider; a thread of its own holds the sessions for the life of the process, since windows drops a default session nobody holds and the helper needs its main thread free of the multithreaded apartment
void GroupAudioWithObs()
{
    std::thread([] {
        if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return;
        std::vector<ComPtr<IAudioSessionControl>> held;
        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IMMDeviceCollection> devices;
        UINT count = 0;
        if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator))) &&
            SUCCEEDED(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices)) && SUCCEEDED(devices->GetCount(&count))) {
            for (UINT i = 0; i < count; i++) {
                ComPtr<IMMDevice> device;
                ComPtr<IAudioSessionManager> manager;
                ComPtr<IAudioSessionControl> session;
                if (SUCCEEDED(devices->Item(i, &device)) &&
                    SUCCEEDED(device->Activate(__uuidof(IAudioSessionManager), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(manager.GetAddressOf()))) &&
                    SUCCEEDED(manager->GetAudioSessionControl(nullptr, 0, &session)) && SUCCEEDED(session->SetGroupingParam(&kObsAudioGroup, nullptr)))
                    held.push_back(session);
            }
        }
        Sleep(INFINITE);
    }).detach();
}

int Run(const fs::path &directory, const std::vector<std::wstring> &arguments)
{
    if (arguments.size() == 2 && arguments[1] == L"--version") {
        std::puts("OBS Studio role host - " OBS_HOST_VERSION);
        return 0;
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
    if (helper) {
        ProcessGroup group;
        GroupAudioWithObs();
        return Helper();
    }
    if (browser) {
        ProcessGroup group;
        // only the audio service plays sound, so the other browser processes leave the sessions alone
        for (const auto &argument : arguments)
            if (argument.find(L"audio.mojom.AudioService") != std::wstring::npos) GroupAudioWithObs();
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
