// plays the sound of a saved clip and of a recording that starts or stops, at the volumes of the settings
#include "prelude.hpp"
#include "notify/clip_sound.hpp"

#include <QDir>
#include <QFileInfo>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <mmsystem.h>
#include <mutex>
#include <thread>

#include "core/json_lite.hpp"
#include "core/state_files.hpp"

namespace rk {

namespace {

constexpr int kSoundCount = 3;
// mci returns before directshow finishes priming the audio buffer, so wait briefly before showing the paired toast
constexpr int kToastSyncDelayMs = 100;
const wchar_t *const kAliases[kSoundCount] = {L"rk_clip_saved", L"rk_rec_start", L"rk_rec_stop"};
// unknown until the helper has answered once, and nothing plays before that
int g_clipVolume = -1;
int g_recordingVolume = -1;

struct PlayRequest {
	int slot = 0;
	QString path;
	int volume = 0;
	std::function<void()> onStarted;
};

// mci aliases belong to the thread that opened them, so one worker thread owns all three and keeps them open for low-latency synced playback
std::mutex g_mutex;
std::condition_variable g_cv;
std::deque<PlayRequest> g_queue;
std::atomic<bool> g_stop{false};
std::thread g_worker;

// touched only on the sound worker thread
bool g_opened[kSoundCount] = {};
QString g_openedPath[kSoundCount];

QString AliasFor(int slot)
{
	return QString::fromWCharArray(kAliases[slot]);
}

bool Mci(const QString &command, const QString &path)
{
	const MCIERROR error = mciSendStringW(reinterpret_cast<const wchar_t *>(command.utf16()), nullptr, 0, nullptr);
	if (error && !path.isEmpty())
		blog(LOG_WARNING, "[replaykit] clip sound: '%s' failed with mci error %lu for %s", command.section(QLatin1Char(' '), 0, 0).toUtf8().constData(),
		     (unsigned long)error, path.toUtf8().constData());
	return !error;
}

bool EnsureOpen(int slot, const QString &path)
{
	const QString alias = AliasFor(slot);
	if (g_opened[slot] && g_openedPath[slot] == path)
		return true;
	if (g_opened[slot]) {
		Mci(QStringLiteral("close ") + alias, QString());
		g_opened[slot] = false;
		g_openedPath[slot].clear();
	}
	if (!QFileInfo::exists(path)) {
		blog(LOG_WARNING, "[replaykit] clip sound: %s is missing", path.toUtf8().constData());
		return false;
	}
	// a windows path cannot hold a quote, so plain quotes are enough around it
	if (!Mci(QStringLiteral("open \"%1\" type mpegvideo alias %2").arg(path, alias), path))
		return false;
	g_opened[slot] = true;
	g_openedPath[slot] = path;
	return true;
}

void PlayOnWorker(const PlayRequest &request)
{
	for (int i = 0; i < kSoundCount; ++i)
		if (g_opened[i])
			Mci(QStringLiteral("stop ") + AliasFor(i), QString());
	const QString alias = AliasFor(request.slot);
	bool played = false;
	if (EnsureOpen(request.slot, request.path)) {
		Mci(QStringLiteral("setaudio %1 volume to %2").arg(alias).arg(request.volume * 10), request.path);
		played = Mci(QStringLiteral("play %1 from 0").arg(alias), request.path);
		if (!played) {
			// reopen once in case the audio device changed while the alias was cached
			Mci(QStringLiteral("close ") + alias, QString());
			g_opened[request.slot] = false;
			g_openedPath[request.slot].clear();
			if (EnsureOpen(request.slot, request.path)) {
				Mci(QStringLiteral("setaudio %1 volume to %2").arg(alias).arg(request.volume * 10), request.path);
				played = Mci(QStringLiteral("play %1 from 0").arg(alias), request.path);
			}
		}
	}
	if (played && request.onStarted) {
		std::unique_lock<std::mutex> lock(g_mutex);
		g_cv.wait_for(lock, std::chrono::milliseconds(kToastSyncDelayMs), []() { return g_stop.load() || !g_queue.empty(); });
	}
	if (request.onStarted && !g_stop.load())
		request.onStarted();
}

void SoundWorkerLoop()
{
	for (;;) {
		PlayRequest request;
		{
			std::unique_lock<std::mutex> lock(g_mutex);
			g_cv.wait(lock, []() { return g_stop.load() || !g_queue.empty(); });
			if (g_stop.load())
				break;
			request = std::move(g_queue.front());
			g_queue.pop_front();
		}
		PlayOnWorker(request);
	}
	for (int i = 0; i < kSoundCount; ++i) {
		if (g_opened[i]) {
			Mci(QStringLiteral("close ") + AliasFor(i), QString());
			g_opened[i] = false;
			g_openedPath[i].clear();
		}
	}
}

} // namespace

void ClipSoundApplySettings(const std::string &settingsBody)
{
	if (const auto clip = JsonIntIfPresent(settingsBody, "clipSoundVolume"))
		g_clipVolume = std::clamp(*clip, 0, 100);
	if (const auto recording = JsonIntIfPresent(settingsBody, "recordingSoundVolume"))
		g_recordingVolume = std::clamp(*recording, 0, 100);
}

void ClipSoundPlay(ClipSound sound, std::function<void()> onStarted)
{
	const int volume = sound == ClipSound::ClipSaved ? g_clipVolume : g_recordingVolume;
	if (volume <= 0) {
		if (onStarted && !g_stop.load())
			onStarted();
		return;
	}
	const int slot = sound == ClipSound::ClipSaved ? 0 : sound == ClipSound::RecordingStarted ? 1 : 2;
	const char *name = slot == 0 ? "clipping_saved.mp3" : slot == 1 ? "recording_start_saved.mp3" : "recording_ended_saved.mp3";
	const QString path = QDir::toNativeSeparators(ReplayKitRootPath() + QStringLiteral("/sounds/") + QString::fromLatin1(name));
	std::lock_guard<std::mutex> lock(g_mutex);
	if (g_stop.load())
		return;
	if (!g_worker.joinable())
		g_worker = std::thread(SoundWorkerLoop);
	g_queue.push_back(PlayRequest{slot, path, volume, std::move(onStarted)});
	g_cv.notify_one();
}

void ClipSoundStop()
{
	{
		std::lock_guard<std::mutex> lock(g_mutex);
		g_stop.store(true);
		g_queue.clear();
	}
	g_cv.notify_one();
	if (g_worker.joinable())
		g_worker.join();
}

} // namespace rk
