#include "core/audio_group.hpp"

#include <windows.h>
#include <audiopolicy.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <memory>
#include <mutex>
#include <vector>

#include "obs_audio_group.hpp"

namespace rk {

using Microsoft::WRL::ComPtr;

namespace {

void GroupIfOurs(IAudioSessionControl *session)
{
	ComPtr<IAudioSessionControl2> owned;
	DWORD pid = 0;
	if (session && SUCCEEDED(session->QueryInterface(IID_PPV_ARGS(&owned))) && SUCCEEDED(owned->GetProcessId(&pid)) &&
	    pid == GetCurrentProcessId())
		session->SetGroupingParam(&kObsAudioGroup, nullptr);
}

// tells about every session opened on one output, so a session obs opens later (monitoring, the clip sounds) is grouped the moment it exists
class SessionWatch final : public IAudioSessionNotification {
public:
	explicit SessionWatch(ComPtr<IAudioSessionManager2> owner) : manager(std::move(owner)) {}
	ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
	ULONG STDMETHODCALLTYPE Release() override { return 1; }
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **out) override
	{
		if (!out)
			return E_POINTER;
		if (iid == __uuidof(IUnknown) || iid == __uuidof(IAudioSessionNotification)) {
			*out = static_cast<IAudioSessionNotification *>(this);
			return S_OK;
		}
		*out = nullptr;
		return E_NOINTERFACE;
	}
	HRESULT STDMETHODCALLTYPE OnSessionCreated(IAudioSessionControl *session) override
	{
		GroupIfOurs(session);
		return S_OK;
	}
	ComPtr<IAudioSessionManager2> manager;
};

std::mutex g_lock;
bool g_stopped = false;
std::vector<std::wstring> g_watchedIds;
std::vector<std::unique_ptr<SessionWatch>> g_watches;

void WatchActiveOutputs()
{
	ComPtr<IMMDeviceEnumerator> enumerator;
	ComPtr<IMMDeviceCollection> devices;
	UINT count = 0;
	if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator))) ||
	    FAILED(enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &devices)) || FAILED(devices->GetCount(&count)))
		return;
	std::lock_guard<std::mutex> hold(g_lock);
	if (g_stopped)
		return;
	for (UINT i = 0; i < count; i++) {
		ComPtr<IMMDevice> device;
		LPWSTR id = nullptr;
		if (FAILED(devices->Item(i, &device)) || FAILED(device->GetId(&id)))
			continue;
		std::wstring key(id);
		CoTaskMemFree(id);
		bool known = false;
		for (const auto &watched : g_watchedIds)
			known = known || watched == key;
		ComPtr<IAudioSessionManager2> manager;
		if (known || FAILED(device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(manager.GetAddressOf()))))
			continue;
		auto watch = std::make_unique<SessionWatch>(manager);
		if (FAILED(manager->RegisterSessionNotification(watch.get())))
			continue;
		// the sessions that already exist, which windows also needs listed once before it sends new ones
		ComPtr<IAudioSessionEnumerator> sessions;
		int total = 0;
		if (SUCCEEDED(manager->GetSessionEnumerator(&sessions)) && SUCCEEDED(sessions->GetCount(&total)))
			for (int s = 0; s < total; s++) {
				ComPtr<IAudioSessionControl> session;
				if (SUCCEEDED(sessions->GetSession(s, &session)))
					GroupIfOurs(session.Get());
			}
		g_watchedIds.push_back(key);
		g_watches.push_back(std::move(watch));
	}
}

// an output plugged in or turned on while obs runs gets watched too
class OutputWatch final : public IMMNotificationClient {
public:
	ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
	ULONG STDMETHODCALLTYPE Release() override { return 1; }
	HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void **out) override
	{
		if (!out)
			return E_POINTER;
		if (iid == __uuidof(IUnknown) || iid == __uuidof(IMMNotificationClient)) {
			*out = static_cast<IMMNotificationClient *>(this);
			return S_OK;
		}
		*out = nullptr;
		return E_NOINTERFACE;
	}
	HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD state) override
	{
		if (state == DEVICE_STATE_ACTIVE)
			WatchActiveOutputs();
		return S_OK;
	}
	HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override
	{
		WatchActiveOutputs();
		return S_OK;
	}
	HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { return S_OK; }
	HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow, ERole, LPCWSTR) override { return S_OK; }
	HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }
};

OutputWatch g_outputWatch;
ComPtr<IMMDeviceEnumerator> g_outputEnumerator;

} // namespace

void GroupAudioWithHost()
{
	{
		std::lock_guard<std::mutex> hold(g_lock);
		g_stopped = false;
	}
	// the obs main thread already has com, so this only starts it when it does not and never ends it while the watches live
	CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	WatchActiveOutputs();
	if (!g_outputEnumerator && SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&g_outputEnumerator)))) {
		if (FAILED(g_outputEnumerator->RegisterEndpointNotificationCallback(&g_outputWatch)))
			g_outputEnumerator.Reset();
	}
}

void StopAudioGroup()
{
	if (g_outputEnumerator) {
		g_outputEnumerator->UnregisterEndpointNotificationCallback(&g_outputWatch);
		g_outputEnumerator.Reset();
	}
	std::lock_guard<std::mutex> hold(g_lock);
	g_stopped = true;
	for (auto &watch : g_watches)
		watch->manager->UnregisterSessionNotification(watch.get());
	g_watches.clear();
	g_watchedIds.clear();
}

} // namespace rk
