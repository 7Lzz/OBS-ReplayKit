// the plugin side of the share window: attach, the frame copy and letting go
#include "prelude.hpp"
#include "share/share_link.hpp"
#include "share/share_audio.hpp"
#include "core/shared_values.hpp"

#include <QJsonObject>

#include <d3d11.h>
#include <dxgi.h>

#include <atomic>
#include <mutex>
#include <string>

namespace rk {

namespace {

std::mutex g_lock;
ShareLinkView g_view;
HANDLE g_mapping = nullptr;
HANDLE g_windowProcess = nullptr;
uint32_t g_windowPid = 0;
uint32_t g_serial = 0;
std::atomic<bool> g_closing{false};
QTimer *g_timer = nullptr;
uint64_t g_adapterLuid = 0;
bool g_refusedLogged = false;

// only touched on the graphics thread
gs_texture_t *g_texture = nullptr;
gs_texrender_t *g_render = nullptr;
uint32_t g_textureSeq = 0;
uint32_t g_textureSerial = 0;
uint64_t g_lastFrameTime = 0;
bool g_openFailedLogged = false;

std::atomic<uint64_t> g_copied{0};
std::atomic<uint64_t> g_busy{0};
std::atomic<uint64_t> g_sizeMismatch{0};

std::wstring LocalName(const wchar_t *name)
{
	return std::wstring(L"Local\\") + name;
}

void DropTexture()
{
	if (g_texture) {
		gs_texture_destroy(g_texture);
		g_texture = nullptr;
	}
	g_textureSeq = 0;
}

// the gpu obs renders on, which the window has to render on too for the shared texture to open
uint64_t ObsAdapterLuid()
{
	uint64_t luid = 0;
	obs_enter_graphics();
	auto *device = static_cast<ID3D11Device *>(gs_get_device_obj());
	IDXGIDevice *dxgiDevice = nullptr;
	if (device && SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void **>(&dxgiDevice)))) {
		IDXGIAdapter *adapter = nullptr;
		if (SUCCEEDED(dxgiDevice->GetAdapter(&adapter))) {
			DXGI_ADAPTER_DESC desc{};
			if (SUCCEEDED(adapter->GetDesc(&desc)))
				luid = share::AdapterLuidValue(desc.AdapterLuid.LowPart, desc.AdapterLuid.HighPart);
			adapter->Release();
		}
		dxgiDevice->Release();
	}
	obs_leave_graphics();
	return luid;
}

void WriteMonitoringDevice(share::SharedState *state)
{
	const char *name = nullptr;
	const char *id = nullptr;
	obs_get_audio_monitoring_device(&name, &id);
	std::wstring wide;
	if (id && *id) {
		int length = MultiByteToWideChar(CP_UTF8, 0, id, -1, nullptr, 0);
		if (length > 1 && length <= int(share::kDeviceIdChars)) {
			wide.resize(size_t(length - 1));
			MultiByteToWideChar(CP_UTF8, 0, id, -1, wide.data(), length);
		}
	}
	if (wide == state->monitorDeviceId)
		return;
	uint32_t seq = state->deviceSeq.load();
	state->deviceSeq.store(seq | 1);
	std::atomic_thread_fence(std::memory_order_release);
	wcsncpy_s(state->monitorDeviceId, share::kDeviceIdChars, wide.c_str(), _TRUNCATE);
	std::atomic_thread_fence(std::memory_order_release);
	state->deviceSeq.store((seq | 1) + 1);
}

void WriteCanvas(share::SharedState *state)
{
	obs_video_info info{};
	if (!obs_get_video_info(&info))
		return;
	state->wantWidth.store(info.base_width);
	state->wantHeight.store(info.base_height);
}

// called with g_lock held
void Detach(const char *why)
{
	if (!g_view.state)
		return;
	blog(LOG_INFO, "[replaykit] share window let go: %s", why);
	uint32_t mine = GetCurrentProcessId();
	if (g_view.state->producerPid.load() == mine) {
		g_view.state->producerState.store(share::ProducerClosing);
		uint32_t expected = mine;
		g_view.state->producerPid.compare_exchange_strong(expected, 0);
	}
	UnmapViewOfFile(g_view.state);
	if (g_view.frameEvent)
		CloseHandle(g_view.frameEvent);
	if (g_view.audioEvent)
		CloseHandle(g_view.audioEvent);
	if (g_mapping)
		CloseHandle(g_mapping);
	if (g_windowProcess)
		CloseHandle(g_windowProcess);
	g_view = ShareLinkView{};
	g_mapping = nullptr;
	g_windowProcess = nullptr;
	g_windowPid = 0;
}

bool WindowAlive()
{
	DWORD code = STILL_ACTIVE;
	return g_windowProcess && GetExitCodeProcess(g_windowProcess, &code) && code == STILL_ACTIVE;
}

// called with g_lock held
void TryAttach()
{
	HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, LocalName(kShareWindowStateMappingName).c_str());
	if (!mapping)
		return;
	auto *state = static_cast<share::SharedState *>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
	MEMORY_BASIC_INFORMATION region{};
	bool sized = state && VirtualQuery(state, &region, sizeof(region)) && region.RegionSize >= sizeof(share::SharedState);
	std::atomic_thread_fence(std::memory_order_acquire);
	// a window of another release has another layout, and is left alone until the helper replaces it
	if (!sized || state->magic != share::kMagic || state->protocol != kShareWindowProtocolVersion || state->size != sizeof(share::SharedState)) {
		if (state)
			UnmapViewOfFile(state);
		CloseHandle(mapping);
		return;
	}
	uint32_t mine = GetCurrentProcessId();
	uint32_t holder = 0;
	if (!state->producerPid.compare_exchange_strong(holder, mine) && holder != mine) {
		if (!g_refusedLogged)
			blog(LOG_WARNING, "[replaykit] the share window is held by another obs (pid %u)", holder);
		g_refusedLogged = true;
		UnmapViewOfFile(state);
		CloseHandle(mapping);
		return;
	}
	g_refusedLogged = false;
	HANDLE frameEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, LocalName(kShareWindowFrameEventName).c_str());
	HANDLE audioEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, LocalName(kShareWindowAudioEventName).c_str());
	HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, state->windowPid);
	if (!frameEvent || !audioEvent || !process) {
		blog(LOG_WARNING, "[replaykit] the share window could not be opened: %lu", GetLastError());
		uint32_t expected = mine;
		state->producerPid.compare_exchange_strong(expected, 0);
		for (HANDLE handle : {frameEvent, audioEvent, process})
			if (handle)
				CloseHandle(handle);
		UnmapViewOfFile(state);
		CloseHandle(mapping);
		return;
	}
	g_mapping = mapping;
	g_windowProcess = process;
	g_windowPid = state->windowPid;
	g_view.state = state;
	g_view.frameEvent = frameEvent;
	g_view.audioEvent = audioEvent;
	g_view.serial = ++g_serial;
	state->producerAdapterLuid.store(g_adapterLuid);
	WriteCanvas(state);
	WriteMonitoringDevice(state);
	state->producerState.store(share::ProducerLive);
	blog(LOG_INFO, "[replaykit] share window attached (pid %u)", g_windowPid);
}

void Tick()
{
	if (g_closing.load())
		return;
	{
		std::lock_guard<std::mutex> guard(g_lock);
		if (g_view.state) {
			share::SharedState *state = g_view.state;
			if (!WindowAlive())
				Detach("the window exited");
			else if (state->magic != share::kMagic || state->windowPid != g_windowPid)
				Detach("the window started over");
			else if (state->producerPid.load() != GetCurrentProcessId())
				Detach("the window took the claim back");
			else {
				WriteCanvas(state);
				WriteMonitoringDevice(state);
			}
		}
		if (!g_view.state)
			TryAttach();
	}
	ShareAudioRefresh();
}

void OnRendered(void *)
{
	if (g_closing.load())
		return;
	// obs calls this once per video mix, and the program picture only needs to go out once a frame
	uint64_t frameTime = obs_get_video_frame_time();
	if (frameTime == g_lastFrameTime)
		return;
	g_lastFrameTime = frameTime;

	std::lock_guard<std::mutex> guard(g_lock);
	share::SharedState *state = g_view.state;
	if (!state) {
		DropTexture();
		return;
	}
	if (g_view.serial != g_textureSerial) {
		DropTexture();
		g_textureSerial = g_view.serial;
	}
	uint32_t seq = state->textureSeq.load(std::memory_order_acquire);
	if (seq & 1)
		return;
	uint32_t handle = state->textureHandle;
	uint32_t width = state->textureWidth;
	uint32_t height = state->textureHeight;
	std::atomic_thread_fence(std::memory_order_acquire);
	if (state->textureSeq.load() != seq)
		return;
	if (!handle) {
		DropTexture();
		return;
	}
	if (!g_texture || g_textureSeq != seq) {
		DropTexture();
		g_texture = gs_texture_open_shared(handle);
		if (!g_texture) {
			if (!g_openFailedLogged)
				blog(LOG_WARNING, "[replaykit] the share window texture %08x could not be opened", handle);
			g_openFailedLogged = true;
			return;
		}
		g_openFailedLogged = false;
		g_textureSeq = seq;
	}
	gs_texture_t *main = obs_get_main_texture();
	if (!main)
		return;
	// the window rebuilds the texture at the canvas size it was told, so a frame of another size waits for that
	if (gs_texture_get_width(main) != width || gs_texture_get_height(main) != height) {
		g_sizeMismatch.fetch_add(1);
		return;
	}
	if (!g_render)
		g_render = gs_texrender_create(GS_BGRA, GS_ZS_NONE);
	gs_texrender_reset(g_render);
	if (!gs_texrender_begin(g_render, width, height))
		return;
	vec4 clear;
	vec4_zero(&clear);
	gs_clear(GS_CLEAR_COLOR, &clear, 0.0f, 0);
	gs_ortho(0.0f, float(width), 0.0f, float(height), -100.0f, 100.0f);
	gs_blend_state_push();
	gs_enable_blending(false);
	// the same call the program projector draws with, so the share shows exactly what that projector showed
	obs_render_main_texture_src_color_only();
	gs_blend_state_pop();
	gs_texrender_end(g_render);

	int sync = gs_texture_acquire_sync(g_texture, 0, 0);
	if (sync != 0) {
		g_busy.fetch_add(1);
		// anything but busy means the texture is not usable any more, and it is opened again
		if (sync != ETIMEDOUT)
			DropTexture();
		return;
	}
	gs_copy_texture(g_texture, gs_texrender_get_texture(g_render));
	gs_texture_release_sync(g_texture, 0);
	state->framesWritten.fetch_add(1, std::memory_order_release);
	SetEvent(g_view.frameEvent);
	g_copied.fetch_add(1);
}

} // namespace

ShareLinkLock::ShareLinkLock()
{
	g_lock.lock();
}

ShareLinkLock::~ShareLinkLock()
{
	g_lock.unlock();
}

const ShareLinkView &ShareLinkLock::View() const
{
	return g_view;
}

void ShareLinkStart()
{
	g_adapterLuid = ObsAdapterLuid();
	obs_add_main_rendered_callback(OnRendered, nullptr);
	g_timer = new QTimer(QCoreApplication::instance());
	g_timer->setInterval(1000);
	QObject::connect(g_timer, &QTimer::timeout, g_timer, Tick);
	g_timer->start();
}

void ShareLinkStop()
{
	if (g_closing.exchange(true))
		return;
	if (g_timer) {
		g_timer->stop();
		g_timer->deleteLater();
		g_timer = nullptr;
	}
	obs_remove_main_rendered_callback(OnRendered, nullptr);
	ShareAudioStop();
	obs_enter_graphics();
	DropTexture();
	if (g_render) {
		gs_texrender_destroy(g_render);
		g_render = nullptr;
	}
	obs_leave_graphics();
	std::lock_guard<std::mutex> guard(g_lock);
	Detach("obs is closing");
}

QJsonObject ShareLinkProbe()
{
	QJsonObject out;
	{
	std::lock_guard<std::mutex> guard(g_lock);
	out["attached"] = g_view.state != nullptr;
	out["windowPid"] = qint64(g_windowPid);
	out["serial"] = qint64(g_serial);
	out["closing"] = g_closing.load();
	out["adapterLuid"] = QString::number(qulonglong(g_adapterLuid), 16);
	out["framesCopied"] = qint64(g_copied.load());
	out["framesSkippedBusy"] = qint64(g_busy.load());
	out["framesSkippedSize"] = qint64(g_sizeMismatch.load());
	if (g_view.state) {
		out["generation"] = qint64(g_view.state->textureSeq.load() / 2);
		out["texture"] = QString("%1x%2").arg(g_view.state->textureWidth).arg(g_view.state->textureHeight);
		out["framesWritten"] = qint64(g_view.state->framesWritten.load());
	}
	}
	// after the link lock is let go, since the relay takes its own lock first and the link lock second
	out["audio"] = ShareAudioProbe();
	return out;
}

} // namespace rk
