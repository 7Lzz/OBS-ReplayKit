// the window side of the shared state: creates the mapping and the two signals the plugin opens, and publishes the texture the plugin draws into
#pragma once

#include <windows.h>

#include "share_protocol.hpp"

namespace rk::share {

class SharedStateHost {
public:
	~SharedStateHost();
	bool Create(SECURITY_ATTRIBUTES *security, HWND window);

	SharedState *State() const { return state; }
	HANDLE FrameEvent() const { return frameEvent; }
	HANDLE AudioEvent() const { return audioEvent; }

	// publishes a rebuilt texture, or none with a zero handle
	void PublishTexture(uint32_t handle, uint32_t width, uint32_t height, uint32_t format, uint64_t adapterLuid);
	// the producer pid when it claimed the state and is still alive; a dead one is released so the next obs can claim it
	uint32_t LiveProducer();
	uint32_t Generation() const;

private:
	HANDLE mapping = nullptr;
	SharedState *state = nullptr;
	HANDLE frameEvent = nullptr;
	HANDLE audioEvent = nullptr;
	HANDLE producerProcess = nullptr;
	uint32_t producerProcessPid = 0;
};

} // namespace rk::share
