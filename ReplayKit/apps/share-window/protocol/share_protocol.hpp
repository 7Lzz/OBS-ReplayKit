// the shared state between the share window and the plugin: the one place its layout is defined; the window creates it, the plugin includes this header to read and write it
#pragma once

#include <atomic>
#include <cstdint>

namespace rk::share {

// the bytes RKSW in memory
inline constexpr uint32_t kMagic = 0x57534B52;
inline constexpr uint32_t kAudioSlots = 4;
inline constexpr uint32_t kAudioMaxChannels = 8;
// half a second at 48 khz, the most the window ever holds back
inline constexpr uint32_t kAudioRingFrames = 24000;
inline constexpr uint32_t kDeviceIdChars = 512;

enum ProducerState : uint32_t {
	ProducerNone = 0,
	ProducerLive = 1,
	// set by the plugin when obs starts closing, so the window shows the card at once instead of waiting for frames to stop
	ProducerClosing = 2,
};

// one source the plugin relays, written by the plugin and read by the window
struct AudioSlot {
	// bumps whenever the slot is given to another source or its format changes, so the window starts reading it afresh
	std::atomic<uint32_t> generation;
	std::atomic<uint32_t> active;
	std::atomic<uint32_t> sampleRate;
	std::atomic<uint32_t> channels;
	// frames written since the slot generation began; the window keeps its own read count
	std::atomic<uint64_t> writtenFrames;
	// frame f sits at (f % kAudioRingFrames) * kAudioMaxChannels, its channels side by side, whatever the channel count
	float ring[kAudioRingFrames * kAudioMaxChannels];
};

struct SharedState {
	// written once by the window before anything else
	uint32_t magic;
	uint32_t protocol;
	uint32_t size;
	uint32_t windowPid;
	uint64_t windowHwnd;

	// the texture, published like a seqlock: odd while the window rebuilds it, so a reader that sees the same even value before and after reading the fields read a consistent set
	std::atomic<uint32_t> textureSeq;
	uint32_t textureHandle;
	uint32_t textureWidth;
	uint32_t textureHeight;
	uint32_t textureFormat;
	uint64_t windowAdapterLuid;

	// written by the plugin that claimed the state; producerPid is claimed with a compare-exchange so a second obs cannot take over a live one
	std::atomic<uint32_t> producerPid;
	std::atomic<uint32_t> producerState;
	std::atomic<uint64_t> producerAdapterLuid;
	std::atomic<uint32_t> wantWidth;
	std::atomic<uint32_t> wantHeight;
	std::atomic<uint64_t> framesWritten;

	// the device obs monitors to, the same seqlock way as the texture: the endpoint id, or default for the default device the way obs names it; empty until a plugin wrote it
	std::atomic<uint32_t> deviceSeq;
	wchar_t monitorDeviceId[kDeviceIdChars];

	AudioSlot audio[kAudioSlots];
};

static_assert(std::atomic<uint32_t>::is_always_lock_free && std::atomic<uint64_t>::is_always_lock_free, "the shared state needs lock free atomics across processes");

inline uint64_t AdapterLuidValue(uint32_t low, int32_t high)
{
	return (uint64_t(uint32_t(high)) << 32) | low;
}

} // namespace rk::share
