#include <windows.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <avrt.h>
#include <ksmedia.h>
#include <mmdeviceapi.h>

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fstream>

#include "audio_out.hpp"
#include "log.hpp"

namespace rk::share {

using Microsoft::WRL::ComPtr;

namespace {

// every stream of the window shares one session, so the windows mixer shows the window once
constexpr GUID kSessionGuid = {0x6b1d3c5e, 0x29f4, 0x4f0a, {0x9c, 0x61, 0x3e, 0x52, 0x8a, 0x17, 0xd4, 0x90}};
constexpr REFERENCE_TIME kBufferDuration = 1000000; // 100 ms
constexpr uint32_t kDeviceTargetMs = 50;
constexpr uint32_t kRingTargetMs = 20;
constexpr uint32_t kRingMaxLagMs = 120;
constexpr size_t kMaxSoundBytes = 100u * 1024 * 1024;

DWORD ChannelMask(uint32_t channels)
{
	switch (channels) {
	case 1:
		return KSAUDIO_SPEAKER_MONO;
	case 2:
		return KSAUDIO_SPEAKER_STEREO;
	case 3:
		return KSAUDIO_SPEAKER_2POINT1;
	case 4:
		return KSAUDIO_SPEAKER_SURROUND;
	case 5:
		return KSAUDIO_SPEAKER_SURROUND | SPEAKER_LOW_FREQUENCY;
	case 6:
		return KSAUDIO_SPEAKER_5POINT1;
	case 8:
		return KSAUDIO_SPEAKER_7POINT1_SURROUND;
	default:
		return 0;
	}
}

uint32_t FramesFor(uint32_t ms, uint32_t rate)
{
	return uint32_t(uint64_t(rate) * ms / 1000);
}

} // namespace

struct AudioOut::Stream {
	ComPtr<IAudioClient> client;
	ComPtr<IAudioRenderClient> render;
	UINT32 bufferFrames = 0;
	uint32_t channels = 0;
	uint32_t sampleRate = 0;
	uint32_t generation = 0;
	uint64_t readFrames = 0;
	bool fed = false;

	~Stream()
	{
		if (client)
			client->Stop();
	}

	bool Open(IMMDevice *device, uint32_t rate, uint32_t channelCount)
	{
		sampleRate = rate;
		channels = channelCount;
		HRESULT hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void **>(client.GetAddressOf()));
		WAVEFORMATEXTENSIBLE format{};
		format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
		format.Format.nChannels = WORD(channels);
		format.Format.nSamplesPerSec = rate;
		format.Format.wBitsPerSample = 32;
		format.Format.nBlockAlign = WORD(4 * channels);
		format.Format.nAvgBytesPerSec = rate * format.Format.nBlockAlign;
		format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
		format.Samples.wValidBitsPerSample = 32;
		format.dwChannelMask = ChannelMask(channels);
		format.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
		// windows converts to the format of the device, so the source format and rate go in as they are
		const DWORD flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
		if (SUCCEEDED(hr))
			hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, kBufferDuration, 0, &format.Format, &kSessionGuid);
		if (SUCCEEDED(hr))
			hr = client->GetBufferSize(&bufferFrames);
		if (SUCCEEDED(hr))
			hr = client->GetService(IID_PPV_ARGS(&render));
		if (SUCCEEDED(hr)) {
			ComPtr<IAudioSessionControl> session;
			if (SUCCEEDED(client->GetService(IID_PPV_ARGS(&session))))
				session->SetDisplayName(L"ReplayKit Share", nullptr);
			hr = client->Start();
		}
		if (FAILED(hr)) {
			Log("could not open an audio stream (%u Hz, %u channels): 0x%08lx", rate, channels, hr);
			client.Reset();
			render.Reset();
			return false;
		}
		return true;
	}

	// room left before the device holds the target, or zero when the stream broke
	UINT32 Room(HRESULT &hr, UINT32 &padding)
	{
		padding = 0;
		hr = client->GetCurrentPadding(&padding);
		if (FAILED(hr))
			return 0;
		UINT32 target = std::min(bufferFrames, FramesFor(kDeviceTargetMs, sampleRate));
		return padding >= target ? 0 : target - padding;
	}
};

AudioOut::AudioOut() = default;

AudioOut::~AudioOut()
{
	Stop();
}

bool AudioOut::Start(SharedState *sharedState, HANDLE signal)
{
	state = sharedState;
	audioEvent = signal;
	stopping = CreateEventW(nullptr, TRUE, FALSE, nullptr);
	if (!stopping)
		return false;
	thread = std::thread([this] { Run(); });
	return true;
}

void AudioOut::Stop()
{
	if (thread.joinable()) {
		SetEvent(stopping);
		thread.join();
	}
	if (stopping) {
		CloseHandle(stopping);
		stopping = nullptr;
	}
}

void AudioOut::SetCardSound(std::shared_ptr<const CardSound> sound, float volume)
{
	std::lock_guard<std::mutex> guard(lock);
	cardSound = std::move(sound);
	cardVolume = std::clamp(volume, 0.0f, 1.0f);
}

void AudioOut::SetCardShowing(bool showing)
{
	cardShowing.store(showing);
}

std::string AudioOut::StateJson()
{
	bool hasSound = false;
	{
		std::lock_guard<std::mutex> guard(lock);
		hasSound = cardSound != nullptr;
	}
	char text[384];
	snprintf(text, sizeof(text),
		 "{\"device\":%s,\"slots\":%u,\"relayedFrames\":%llu,\"underruns\":%llu,\"dropped\":%llu,\"cardSound\":%s,\"cardFrames\":%llu}",
		 deviceOk.load() ? "true" : "false", activeSlots.load(), relayedFrames.load(), underruns.load(), dropped.load(), hasSound ? "true" : "false",
		 cardFrames.load());
	return text;
}

void AudioOut::CloseStreams()
{
	for (auto &slot : slots)
		slot.reset();
	card.reset();
}

void AudioOut::OpenDevice()
{
	CloseStreams();
	device.Reset();
	deviceReady = false;
	deviceOk.store(false);
	if (deviceId.empty())
		return;
	ComPtr<IMMDeviceEnumerator> enumerator;
	HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
	if (SUCCEEDED(hr)) {
		// the default device only when obs monitors to it as well; a named device that is gone plays nothing rather than the speakers
		if (deviceId == L"default")
			hr = enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device);
		else
			hr = enumerator->GetDevice(deviceId.c_str(), &device);
	}
	if (FAILED(hr)) {
		Log("the monitoring device %ls is not available: 0x%08lx", deviceId.c_str(), hr);
		device.Reset();
		retryAt = GetTickCount64() + 2000;
		return;
	}
	deviceReady = true;
	deviceOk.store(true);
	Log("share audio goes to %ls", deviceId.c_str());
}

void AudioOut::FeedSlot(uint32_t index)
{
	AudioSlot &slot = state->audio[index];
	std::unique_ptr<Stream> &stream = slots[index];
	if (!slot.active.load(std::memory_order_acquire) || !deviceReady) {
		stream.reset();
		return;
	}
	uint32_t generation = slot.generation.load(std::memory_order_acquire);
	uint32_t rate = slot.sampleRate.load();
	uint32_t channels = slot.channels.load();
	if (rate < 8000 || rate > 192000 || channels < 1 || channels > kAudioMaxChannels || !ChannelMask(channels)) {
		stream.reset();
		return;
	}
	uint64_t written = slot.writtenFrames.load(std::memory_order_acquire);
	if (!stream || stream->generation != generation || stream->sampleRate != rate || stream->channels != channels) {
		stream = std::make_unique<Stream>();
		if (!stream->Open(device.Get(), rate, channels)) {
			stream.reset();
			deviceReady = false;
			retryAt = GetTickCount64() + 2000;
			return;
		}
		stream->generation = generation;
		stream->readFrames = written - std::min<uint64_t>(written, FramesFor(kRingTargetMs, rate));
	}
	uint64_t pending = written - stream->readFrames;
	// fallen too far behind (the device runs slower than obs, or the window stalled): skip ahead rather than let the share drift late
	if (written < stream->readFrames || pending > FramesFor(kRingMaxLagMs, rate)) {
		stream->readFrames = written - std::min<uint64_t>(written, FramesFor(kRingTargetMs, rate));
		pending = written - stream->readFrames;
		dropped.fetch_add(1);
	}
	HRESULT hr = S_OK;
	UINT32 padding = 0;
	UINT32 room = stream->Room(hr, padding);
	if (FAILED(hr)) {
		Log("the share audio stream broke (0x%08lx), opening it again", hr);
		stream.reset();
		deviceReady = false;
		retryAt = GetTickCount64() + 1000;
		return;
	}
	UINT32 frames = UINT32(std::min<uint64_t>(room, pending));
	if (!frames) {
		if (!padding && stream->fed)
			underruns.fetch_add(1);
		return;
	}
	BYTE *data = nullptr;
	if (FAILED(stream->render->GetBuffer(frames, &data)))
		return;
	float *out = reinterpret_cast<float *>(data);
	for (UINT32 i = 0; i < frames; ++i) {
		const float *frame = slot.ring + size_t((stream->readFrames + i) % kAudioRingFrames) * kAudioMaxChannels;
		std::memcpy(out + size_t(i) * channels, frame, channels * sizeof(float));
	}
	stream->render->ReleaseBuffer(frames, 0);
	stream->readFrames += frames;
	stream->fed = true;
	relayedFrames.fetch_add(frames);
}

void AudioOut::FeedCard()
{
	bool showing = cardShowing.load();
	std::shared_ptr<const CardSound> sound;
	float volume = 1.0f;
	{
		std::lock_guard<std::mutex> guard(lock);
		sound = cardSound;
		volume = cardVolume;
	}
	if (!showing || !sound || sound->samples.empty() || !deviceReady || volume <= 0.0f) {
		card.reset();
		playingSound.reset();
		return;
	}
	const size_t total = sound->samples.size();
	if (playingSound != sound) {
		card.reset();
		playingSound = sound;
		cardPosition = 0;
	}
	if (cardPosition >= total)
		cardPosition = 0;
	if (!card || card->sampleRate != sound->sampleRate || card->channels != sound->channels) {
		card = std::make_unique<Stream>();
		if (!card->Open(device.Get(), sound->sampleRate, sound->channels)) {
			card.reset();
			return;
		}
	}
	HRESULT hr = S_OK;
	UINT32 padding = 0;
	UINT32 room = card->Room(hr, padding);
	if (FAILED(hr)) {
		card.reset();
		deviceReady = false;
		retryAt = GetTickCount64() + 1000;
		return;
	}
	if (!room)
		return;
	BYTE *data = nullptr;
	if (FAILED(card->render->GetBuffer(room, &data)))
		return;
	float *out = reinterpret_cast<float *>(data);
	const size_t needed = size_t(room) * sound->channels;
	for (size_t i = 0; i < needed; ++i) {
		out[i] = sound->samples[cardPosition] * volume;
		cardPosition = (cardPosition + 1) % total;
	}
	card->render->ReleaseBuffer(room, 0);
	cardFrames.fetch_add(room);
}

void AudioOut::Run()
{
	CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	DWORD taskIndex = 0;
	HANDLE task = AvSetMmThreadCharacteristicsW(L"Audio", &taskIndex);
	HANDLE waits[] = {stopping, audioEvent};
	while (WaitForMultipleObjects(2, waits, FALSE, 10) != WAIT_OBJECT_0) {
		uint32_t seq = state->deviceSeq.load(std::memory_order_acquire);
		if (seq != deviceSeq && !(seq & 1)) {
			wchar_t id[kDeviceIdChars];
			std::memcpy(id, state->monitorDeviceId, sizeof(id));
			std::atomic_thread_fence(std::memory_order_acquire);
			if (state->deviceSeq.load() == seq) {
				id[kDeviceIdChars - 1] = L'\0';
				deviceSeq = seq;
				if (deviceId != id || !deviceReady) {
					deviceId = id;
					OpenDevice();
				}
			}
		}
		if (!deviceReady && !deviceId.empty() && GetTickCount64() >= retryAt)
			OpenDevice();
		uint32_t active = 0;
		for (uint32_t i = 0; i < kAudioSlots; ++i) {
			FeedSlot(i);
			if (slots[i])
				++active;
		}
		activeSlots.store(active);
		FeedCard();
	}
	CloseStreams();
	device.Reset();
	if (task)
		AvRevertMmThreadCharacteristics(task);
	CoUninitialize();
}

bool LoadCardSound(const std::wstring &path, CardSound &out)
{
	out = CardSound{};
	std::ifstream file(path, std::ios::binary | std::ios::ate);
	if (!file) {
		Log("could not open the card sound %ls", path.c_str());
		return false;
	}
	std::streamoff length = file.tellg();
	if (length < 44 || size_t(length) > kMaxSoundBytes) {
		Log("the card sound %ls has an unusable size", path.c_str());
		return false;
	}
	std::vector<uint8_t> bytes(static_cast<size_t>(length));
	file.seekg(0);
	file.read(reinterpret_cast<char *>(bytes.data()), length);
	if (std::memcmp(bytes.data(), "RIFF", 4) || std::memcmp(bytes.data() + 8, "WAVE", 4)) {
		Log("the card sound %ls is not a wav file", path.c_str());
		return false;
	}
	auto u16 = [&](size_t at) { return uint32_t(bytes[at] | (bytes[at + 1] << 8)); };
	auto u32 = [&](size_t at) { return uint32_t(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (uint32_t(bytes[at + 3]) << 24)); };
	uint32_t tag = 0, channels = 0, rate = 0, bits = 0;
	const uint8_t *data = nullptr;
	size_t dataBytes = 0;
	for (size_t at = 12; at + 8 <= bytes.size();) {
		uint32_t chunk = u32(at + 4);
		size_t body = at + 8;
		if (chunk > bytes.size() - body)
			chunk = uint32_t(bytes.size() - body);
		if (!std::memcmp(bytes.data() + at, "fmt ", 4) && chunk >= 16) {
			tag = u16(body);
			channels = u16(body + 2);
			rate = u32(body + 4);
			bits = u16(body + 14);
			// the extensible form names the real format in the first two bytes of its sub format
			if (tag == WAVE_FORMAT_EXTENSIBLE && chunk >= 40)
				tag = u16(body + 24);
		} else if (!std::memcmp(bytes.data() + at, "data", 4)) {
			data = bytes.data() + body;
			dataBytes = chunk;
		}
		at = body + chunk + (chunk & 1);
	}
	bool usable = data && channels >= 1 && channels <= kAudioMaxChannels && ChannelMask(channels) && rate >= 8000 && rate <= 192000 &&
		      ((tag == WAVE_FORMAT_PCM && (bits == 8 || bits == 16 || bits == 24 || bits == 32)) || (tag == WAVE_FORMAT_IEEE_FLOAT && bits == 32));
	if (!usable) {
		Log("the card sound %ls has a format the share window does not play (tag %u, %u bits, %u channels, %u Hz)", path.c_str(), tag, bits, channels, rate);
		return false;
	}
	const size_t sampleBytes = bits / 8;
	const size_t count = dataBytes / sampleBytes / channels * channels;
	out.samples.resize(count);
	for (size_t i = 0; i < count; ++i) {
		const uint8_t *s = data + i * sampleBytes;
		float value = 0.0f;
		if (tag == WAVE_FORMAT_IEEE_FLOAT) {
			std::memcpy(&value, s, 4);
		} else if (bits == 8) {
			value = (float(s[0]) - 128.0f) / 128.0f;
		} else if (bits == 16) {
			value = float(int16_t(uint16_t(s[0] | (s[1] << 8)))) / 32768.0f;
		} else if (bits == 24) {
			int32_t v = int32_t(uint32_t(s[0] << 8) | (uint32_t(s[1]) << 16) | (uint32_t(s[2]) << 24)) >> 8;
			value = float(v) / 8388608.0f;
		} else {
			int32_t v = int32_t(uint32_t(s[0]) | (uint32_t(s[1]) << 8) | (uint32_t(s[2]) << 16) | (uint32_t(s[3]) << 24));
			value = float(v) / 2147483648.0f;
		}
		out.samples[i] = std::clamp(value, -1.0f, 1.0f);
	}
	if (out.samples.empty()) {
		Log("the card sound %ls holds no audio", path.c_str());
		return false;
	}
	out.sampleRate = rate;
	out.channels = channels;
	Log("card sound %ls loaded (%u Hz, %u channels, %.1f s)", path.c_str(), rate, channels, double(count / channels) / rate);
	return true;
}

} // namespace rk::share
