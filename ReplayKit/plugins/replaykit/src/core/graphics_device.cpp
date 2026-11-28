// watches the obs d3d11 device for loss and recovery and lets replaykit drop its graphics before obs rebuilds
#include "prelude.hpp"
#include "core/graphics_device.hpp"

#include <d3d11.h>
#include <util/platform.h>

namespace rk {

namespace {

constexpr ULONGLONG kCheckIntervalMs = 250;
std::atomic<std::uint64_t> g_revision{0};
std::atomic<std::uint64_t> g_readySince{0};
QObject *g_uiContext = nullptr;
void (*g_releaseGraphics)() = nullptr;
void (*g_graphicsChanged)() = nullptr;
ULONGLONG g_checkAfter = 0;

void Publish(HRESULT result)
{
	const GraphicsState current = GraphicsStatus();
	const GraphicsState next = current.WithLoss(FAILED(result));
	if (current.revision == next.revision)
		return;
	if (!next.Lost())
		g_readySince.store(os_gettime_ns());
	g_revision.store(next.revision);
	if (next.Lost()) {
		blog(LOG_WARNING, "[replaykit] obs lost its graphics device (0x%08lx)", result);
		g_releaseGraphics();
	} else {
		blog(LOG_INFO, "[replaykit] obs has a working graphics device again");
	}
	QMetaObject::invokeMethod(g_uiContext, [next]() {
		if (g_graphicsChanged && next.revision == GraphicsStatus().revision)
			g_graphicsChanged();
	}, Qt::QueuedConnection);
}

void CheckDevice(void *)
{
	const auto now = GetTickCount64();
	if (now < g_checkAfter)
		return;
	g_checkAfter = now + kCheckIntervalMs;
	if (auto *device = ObsGraphicsDevice())
		Publish(device->GetDeviceRemovedReason());
}

void ReleaseDevice(void *)
{
	auto *device = ObsGraphicsDevice();
	const HRESULT result = device ? device->GetDeviceRemovedReason() : S_OK;
	Publish(FAILED(result) ? result : DXGI_ERROR_DEVICE_RESET);
}

void RebuiltDevice(void *device, void *)
{
	if (device)
		Publish(static_cast<ID3D11Device *>(device)->GetDeviceRemovedReason());
}

} // namespace

GraphicsState GraphicsStatus()
{
	return {g_revision.load()};
}

std::uint64_t GraphicsReadySince()
{
	return g_readySince.load();
}

ID3D11Device *ObsGraphicsDevice()
{
	return gs_get_context() && gs_get_device_type() == GS_DEVICE_DIRECT3D_11 ? static_cast<ID3D11Device *>(gs_get_device_obj()) : nullptr;
}

void StartGraphicsWatch(QObject &context, void (*release)(), void (*changed)())
{
	g_uiContext = &context;
	g_releaseGraphics = release;
	g_graphicsChanged = changed;
	obs_enter_graphics();
	if (ObsGraphicsDevice()) {
		const gs_device_loss callbacks{ReleaseDevice, RebuiltDevice, &g_revision};
		gs_register_loss_callbacks(&callbacks);
	}
	obs_leave_graphics();
	obs_add_main_rendered_callback(CheckDevice, nullptr);
}

void StopGraphicsWatch()
{
	if (!g_uiContext)
		return;
	obs_remove_main_rendered_callback(CheckDevice, nullptr);
	obs_enter_graphics();
	if (ObsGraphicsDevice())
		gs_unregister_loss_callbacks(&g_revision);
	obs_leave_graphics();
	g_graphicsChanged = nullptr;
	g_uiContext = nullptr;
}

} // namespace rk
