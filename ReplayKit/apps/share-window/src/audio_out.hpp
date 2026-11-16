// plays the share audio from the window process, since discord takes the sound of a window share from the process that owns the window: the sources obs monitors, relayed by the plugin, and the looping sound of the card while it shows; all of it goes to the device obs monitors to, so the user never hears it twice
#pragma once

#include <windows.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "share_protocol.hpp"

namespace rk::share {

struct CardSound {
	std::vector<float> samples;
	uint32_t sampleRate = 0;
	uint32_t channels = 0;
};

// a pcm or float wav of one to eight channels, or false when the file is missing or is not one
bool LoadCardSound(const std::wstring &path, CardSound &out);

class AudioOut {
public:
	AudioOut();
	~AudioOut();
	bool Start(SharedState *state, HANDLE audioEvent);
	void Stop();

	void SetCardSound(std::shared_ptr<const CardSound> sound, float volume);
	void SetCardShowing(bool showing);
	std::string StateJson();

private:
	struct Stream;
	void Run();
	void OpenDevice();
	void CloseStreams();
	void FeedSlot(uint32_t index);
	void FeedCard();

	SharedState *state = nullptr;
	HANDLE audioEvent = nullptr;
	HANDLE stopping = nullptr;
	std::thread thread;

	std::mutex lock;
	std::shared_ptr<const CardSound> cardSound;
	float cardVolume = 1.0f;
	std::atomic<bool> cardShowing{false};

	// only touched on the audio thread
	std::wstring deviceId;
	uint32_t deviceSeq = 0;
	Microsoft::WRL::ComPtr<IMMDevice> device;
	bool deviceReady = false;
	ULONGLONG retryAt = 0;
	std::unique_ptr<Stream> slots[kAudioSlots];
	std::unique_ptr<Stream> card;
	size_t cardPosition = 0;
	bool cardWasShowing = false;

	std::atomic<uint64_t> underruns{0};
	std::atomic<uint64_t> dropped{0};
	std::atomic<uint64_t> relayedFrames{0};
	std::atomic<uint64_t> cardFrames{0};
	std::atomic<uint32_t> activeSlots{0};
	std::atomic<bool> deviceOk{false};
};

} // namespace rk::share
