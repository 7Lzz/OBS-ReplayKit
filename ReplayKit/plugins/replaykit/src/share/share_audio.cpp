// the audio relay into the slots of the share window
#include "prelude.hpp"
#include "share/share_audio.hpp"
#include "share/share_link.hpp"

#include <QJsonArray>

#include <algorithm>
#include <atomic>
#include <memory>
#include <mutex>
#include <vector>

namespace rk {

namespace {

struct Tap {
	obs_source_t *source = nullptr;
	uint32_t slot = 0;
	uint32_t channels = 0;
	// the link serial the slot was set up for; the audio thread writes nothing until it matches the attached window
	std::atomic<uint32_t> serial{0};
	std::atomic<uint64_t> frames{0};
};

std::mutex g_tapsLock;
std::vector<std::unique_ptr<Tap>> g_taps;
std::atomic<bool> g_stopped{false};

void OnAudio(void *param, obs_source_t *source, const struct audio_data *data, bool)
{
	// mute is left alone and only a source shown in the program plays, the way the obs monitor does it
	auto *tap = static_cast<Tap *>(param);
	if (!data || !data->frames || !obs_source_active(source))
		return;
	const float volume = obs_source_get_volume(source);
	ShareLinkLock lock;
	const ShareLinkView &view = lock.View();
	if (!view.state || view.serial != tap->serial.load())
		return;
	share::AudioSlot &slot = view.state->audio[tap->slot];
	const uint64_t written = slot.writtenFrames.load(std::memory_order_relaxed);
	for (uint32_t frame = 0; frame < data->frames; ++frame) {
		float *out = slot.ring + size_t((written + frame) % share::kAudioRingFrames) * share::kAudioMaxChannels;
		for (uint32_t channel = 0; channel < tap->channels; ++channel) {
			const float *plane = reinterpret_cast<const float *>(data->data[channel]);
			out[channel] = plane ? plane[frame] * volume : 0.0f;
		}
	}
	slot.writtenFrames.store(written + data->frames, std::memory_order_release);
	SetEvent(view.audioEvent);
	tap->frames.fetch_add(data->frames);
}

bool Monitored(obs_source_t *source)
{
	return source && !obs_source_removed(source) && (obs_source_get_output_flags(source) & OBS_SOURCE_AUDIO) && obs_source_get_monitoring_enabled(source);
}

std::vector<obs_source_t *> MonitoredSources()
{
	std::vector<obs_source_t *> found;
	auto add = [&found](obs_source_t *source) {
		for (obs_source_t *known : found)
			if (known == source)
				return;
		if (Monitored(source)) {
			obs_source_t *ref = obs_source_get_ref(source);
			if (ref)
				found.push_back(ref);
		}
	};
	obs_enum_sources(
		[](void *param, obs_source_t *source) {
			(*static_cast<decltype(add) *>(param))(source);
			return true;
		},
		&add);
	for (uint32_t channel = 0; channel < MAX_CHANNELS; ++channel) {
		obs_source_t *source = obs_get_output_source(channel);
		if (source) {
			add(source);
			obs_source_release(source);
		}
	}
	return found;
}

void ClearSlot(uint32_t index)
{
	ShareLinkLock lock;
	if (lock.View().state)
		lock.View().state->audio[index].active.store(0);
}

void RemoveTap(std::unique_ptr<Tap> &tap)
{
	// removing takes the lock obs holds while it calls the callback, so no call is still running afterwards
	obs_source_remove_audio_capture_callback(tap->source, OnAudio, tap.get());
	ClearSlot(tap->slot);
	blog(LOG_INFO, "[replaykit] share audio stopped relaying '%s'", obs_source_get_name(tap->source));
	obs_source_release(tap->source);
	tap.reset();
}

} // namespace

void ShareAudioRefresh()
{
	if (g_stopped.load())
		return;
	std::vector<obs_source_t *> sources = MonitoredSources();
	audio_t *audio = obs_get_audio();
	const uint32_t channels = audio ? uint32_t(audio_output_get_channels(audio)) : 0;
	const uint32_t rate = audio ? audio_output_get_sample_rate(audio) : 0;

	std::lock_guard<std::mutex> guard(g_tapsLock);
	for (auto &tap : g_taps) {
		bool still = false;
		for (obs_source_t *source : sources)
			still = still || source == tap->source;
		if (!still || tap->channels != channels)
			RemoveTap(tap);
	}
	g_taps.erase(std::remove(g_taps.begin(), g_taps.end(), nullptr), g_taps.end());

	for (obs_source_t *source : sources) {
		bool known = false;
		for (auto &tap : g_taps)
			known = known || tap->source == source;
		if (known || !channels || channels > share::kAudioMaxChannels)
			continue;
		uint32_t slot = 0;
		while (slot < share::kAudioSlots) {
			bool used = false;
			for (auto &tap : g_taps)
				used = used || tap->slot == slot;
			if (!used)
				break;
			++slot;
		}
		if (slot == share::kAudioSlots) {
			blog(LOG_WARNING, "[replaykit] share audio has no slot left for '%s'", obs_source_get_name(source));
			continue;
		}
		auto tap = std::make_unique<Tap>();
		tap->source = obs_source_get_ref(source);
		tap->slot = slot;
		tap->channels = channels;
		obs_source_add_audio_capture_callback(tap->source, OnAudio, tap.get());
		blog(LOG_INFO, "[replaykit] share audio relays '%s' in slot %u", obs_source_get_name(source), slot);
		g_taps.push_back(std::move(tap));
	}
	for (obs_source_t *source : sources)
		obs_source_release(source);

	// a slot is set up again for every window attached, since a new window starts its shared state over
	ShareLinkLock lock;
	const ShareLinkView &view = lock.View();
	if (!view.state)
		return;
	for (auto &tap : g_taps) {
		if (tap->serial.load() == view.serial)
			continue;
		share::AudioSlot &slot = view.state->audio[tap->slot];
		slot.active.store(0);
		slot.sampleRate.store(rate);
		slot.channels.store(channels);
		slot.writtenFrames.store(0);
		slot.generation.fetch_add(1);
		slot.active.store(1, std::memory_order_release);
		tap->serial.store(view.serial);
	}
}

void ShareAudioStop()
{
	g_stopped.store(true);
	std::lock_guard<std::mutex> guard(g_tapsLock);
	for (auto &tap : g_taps)
		RemoveTap(tap);
	g_taps.clear();
}

QJsonObject ShareAudioProbe()
{
	QJsonObject out;
	QJsonArray taps;
	std::lock_guard<std::mutex> guard(g_tapsLock);
	for (auto &tap : g_taps) {
		QJsonObject entry;
		entry["source"] = QString::fromUtf8(obs_source_get_name(tap->source));
		entry["slot"] = qint64(tap->slot);
		entry["channels"] = qint64(tap->channels);
		entry["frames"] = qint64(tap->frames.load());
		taps.append(entry);
	}
	out["relays"] = taps;
	return out;
}

} // namespace rk
