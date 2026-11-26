#include "prelude.hpp"
#include "core/graphics_device.hpp"

#include <d3d11.h>
#include <util/platform.h>

namespace rk {

namespace {

constexpr ULONGLONG CheckIntervalMs = 250;
std::atomic<std::uint64_t> revision{0};
std::atomic<std::uint64_t> readySince{0};
QObject *uiContext = nullptr;
void (*releaseGraphics)() = nullptr;
void (*graphicsChanged)() = nullptr;
ULONGLONG checkAfter = 0;

void Publish(HRESULT result)
{
	const GraphicsState current = GraphicsStatus();
	const GraphicsState next = current.WithLoss(FAILED(result));
	if (current.revision == next.revision)
		return;
	if (!next.Lost())
		readySince.store(os_gettime_ns());
	revision.store(next.revision);
	if (next.Lost()) {
		blog(LOG_WARNING, "[replaykit] obs lost its graphics device (0x%08lx)", result);
		releaseGraphics();
	} else {
		blog(LOG_INFO, "[replaykit] obs has a working graphics device again");
	}
	QMetaObject::invokeMethod(uiContext, [next]() {
		if (graphicsChanged && next.revision == GraphicsStatus().revision)
			graphicsChanged();
	}, Qt::QueuedConnection);
}

void CheckDevice(void *)
{
	const auto now = GetTickCount64();
	if (now < checkAfter)
		return;
	checkAfter = now + CheckIntervalMs;
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
	return {revision.load()};
}

std::uint64_t GraphicsReadySince()
{
	return readySince.load();
}

ID3D11Device *ObsGraphicsDevice()
{
	return gs_get_context() && gs_get_device_type() == GS_DEVICE_DIRECT3D_11 ? static_cast<ID3D11Device *>(gs_get_device_obj()) : nullptr;
}

void StartGraphicsWatch(QObject &context, void (*release)(), void (*changed)())
{
	uiContext = &context;
	releaseGraphics = release;
	graphicsChanged = changed;
	obs_enter_graphics();
	if (ObsGraphicsDevice()) {
		const gs_device_loss callbacks{ReleaseDevice, RebuiltDevice, &revision};
		gs_register_loss_callbacks(&callbacks);
	}
	obs_leave_graphics();
	obs_add_main_rendered_callback(CheckDevice, nullptr);
}

void StopGraphicsWatch()
{
	if (!uiContext)
		return;
	obs_remove_main_rendered_callback(CheckDevice, nullptr);
	obs_enter_graphics();
	if (ObsGraphicsDevice())
		gs_unregister_loss_callbacks(&revision);
	obs_leave_graphics();
	graphicsChanged = nullptr;
	uiContext = nullptr;
}

} // namespace rk
