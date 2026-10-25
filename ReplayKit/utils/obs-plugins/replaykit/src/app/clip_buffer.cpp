// the clip buffer: obs keeps it in memory as its replay buffer, or this keeps it on disk as short pieces and the helper joins the newest of them into a clip
#include "prelude.hpp"
#include "app/clip_buffer.hpp"

#include <QDateTime>
#include <QFileInfo>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QStandardPaths>
#include <deque>
#include <functional>
#include <memory>
#include <util/config-file.h>
#include <util/platform.h>
#include <vector>

#include "core/app_icon.hpp"
#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"
#include "core/win32.hpp"
#include "shell/shell_views.hpp"

namespace rk {

namespace {

enum class Mode { Memory, Disk };

// the length of one piece; a clip is cut from whole pieces, so the buffer holds the clip length plus about one piece
constexpr int kPieceSeconds = 10;
// a split lands on the next keyframe and the next audio packet after it
constexpr int kSplitWaitMs = 6000;

struct Piece {
	std::string path;
	qint64 startMs = 0;
	qint64 endMs = 0;
};

Mode g_mode = Mode::Memory;
obs_output_t *g_out = nullptr;
obs_encoder_t *g_video = nullptr;
std::vector<obs_encoder_t *> g_audio;
std::deque<Piece> g_pieces;
Piece g_current;
std::vector<std::string> g_doomed;
QPointer<QTimer> g_purgeTimer;
int g_clipSeconds = 20;
bool g_saving = false;
bool g_awaitingSplit = false;
int g_saveGen = 0;
// the encoders came from the recording output of obs, so a recording and the buffer share one encode
bool g_sharedEncoders = false;
// a stop that is only the first half of moving onto those encoders, which keeps the pieces already recorded
bool g_restartShared = false;
obs_hotkey_id g_saveHotkey = OBS_INVALID_HOTKEY_ID;
std::vector<obs_key_combination_t> g_mirrored;

qint64 NowMs()
{
	return QDateTime::currentMSecsSinceEpoch();
}

QString PieceDir()
{
	return QStandardPaths::writableLocation(QStandardPaths::TempLocation) + QStringLiteral("/ReplayKit/scratch/clip-buffer");
}

const char *ModeName(Mode mode)
{
	return mode == Mode::Disk ? "disk" : "memory";
}

// the mode is known before the helper is up, since obs starts the buffer while it loads: the setting is read straight from the settings file, and anything but memory is disk the way the helper reads it
Mode ModeFromSettingsFile()
{
	const QString folder = qEnvironmentVariable("APPDATA") + QStringLiteral("/obs-studio/obs-replayKit/");
	QFile file(folder + QStringLiteral("replaykit_settings.json"));
	if (!file.exists())
		file.setFileName(folder + QStringLiteral("scripts/replaykit_settings.json"));
	QString chosen;
	if (file.open(QIODevice::ReadOnly) && file.size() < 4 * 1024 * 1024)
		chosen = QJsonDocument::fromJson(file.readAll()).object().value(QStringLiteral("recordingBuffer")).toString();
	return chosen == QLatin1String("memory") ? Mode::Memory : Mode::Disk;
}

bool DiskActive()
{
	return g_out && obs_output_active(g_out);
}

void ReportState()
{
	const std::string path = std::string("/clip-buffer/state?active=") + (DiskActive() ? "1" : "0");
	RunAsync([path]() { HttpRequest("POST", path.c_str(), 8767, nullptr, 2000); });
}

void ForgetPiece(const std::string &path)
{
	if (!os_unlink(path.c_str()) || !os_file_exists(path.c_str()))
		return;
	// still held by the muxer or a reader; tried again on the next sweep
	g_doomed.push_back(path);
}

void ClearPieceDir()
{
	QDir dir(PieceDir());
	if (!dir.exists())
		return;
	for (const QFileInfo &info : dir.entryInfoList({QStringLiteral("*.mp4"), QStringLiteral("*.mkv")}, QDir::Files))
		QFile::remove(info.absoluteFilePath());
}

// pieces older than the clip length plus a spare piece are no use to any clip and go; never while a save is reading them
void Purge()
{
	std::vector<std::string> doomed;
	doomed.swap(g_doomed);
	for (const std::string &path : doomed)
		ForgetPiece(path);
	if (g_saving)
		return;
	const qint64 keepFrom = NowMs() - qint64(g_clipSeconds + 2 * kPieceSeconds) * 1000;
	while (!g_pieces.empty() && g_pieces.front().endMs < keepFrom) {
		ForgetPiece(g_pieces.front().path);
		g_pieces.pop_front();
	}
}

void ReleaseEncoders()
{
	if (g_video)
		obs_encoder_release(g_video);
	g_video = nullptr;
	for (obs_encoder_t *audio : g_audio)
		obs_encoder_release(audio);
	g_audio.clear();
	g_sharedEncoders = false;
}

void OnFileChanged(void *, calldata_t *cd);
void OnOutputStop(void *, calldata_t *cd);
void StartDisk(bool keepPieces);

void DiskStopped()
{
	if (g_out) {
		signal_handler_t *sh = obs_output_get_signal_handler(g_out);
		signal_handler_disconnect(sh, "file_changed", OnFileChanged, nullptr);
		signal_handler_disconnect(sh, "stop", OnOutputStop, nullptr);
		obs_output_release(g_out);
		g_out = nullptr;
	}
	ReleaseEncoders();
	if (g_restartShared) {
		g_restartShared = false;
		// the piece that was being written is finished by the stop and stays part of the buffer
		if (!g_current.path.empty() && os_file_exists(g_current.path.c_str())) {
			g_current.endMs = NowMs();
			g_pieces.push_back(g_current);
		}
		g_current = Piece();
		StartDisk(true);
		return;
	}
	if (g_purgeTimer)
		g_purgeTimer->stop();
	g_pieces.clear();
	g_current = Piece();
	g_saving = false;
	g_awaitingSplit = false;
	// the muxer lets go of its last file a moment after it reports the stop
	QTimer::singleShot(1500, g_callbacks, []() {
		if (!DiskActive())
			ClearPieceDir();
	});
	ReportState();
	RefreshTrayIcon();
	blog(LOG_INFO, "[replaykit] disk clip buffer stopped");
}

void SendSave();

// a piece is finished when the muxer moves on to the next file; a save waiting on that split goes ahead from here
void PieceChanged(const std::string &next)
{
	const qint64 now = NowMs();
	if (!g_current.path.empty()) {
		g_current.endMs = now;
		g_pieces.push_back(g_current);
	}
	g_current = Piece{next, now, 0};
	if (g_awaitingSplit) {
		g_awaitingSplit = false;
		SendSave();
	}
}

void OnFileChanged(void *, calldata_t *cd)
{
	const char *next = calldata_string(cd, "next_file");
	const std::string path = next ? next : "";
	if (g_callbacks)
		QMetaObject::invokeMethod(g_callbacks, [path]() { PieceChanged(path); }, Qt::QueuedConnection);
}

void OnOutputStop(void *, calldata_t *cd)
{
	const long long code = calldata_int(cd, "code");
	if (code != OBS_OUTPUT_SUCCESS)
		blog(LOG_WARNING, "[replaykit] disk clip buffer stopped with code %lld", code);
	if (g_callbacks)
		QMetaObject::invokeMethod(g_callbacks, []() { DiskStopped(); }, Qt::QueuedConnection);
}

std::string ProfileString(config_t *config, const char *section, const char *key)
{
	const char *value = config ? config_get_string(config, section, key) : nullptr;
	return value ? value : "";
}

// the encoders obs has put on its recording output once it has recorded or run its replay buffer; taking them means a recording and this buffer share one encode instead of two
bool TakeRecordingEncoders()
{
	obs_output_t *recording = obs_frontend_get_recording_output();
	if (!recording)
		return false;
	obs_encoder_t *video = obs_output_get_video_encoder(recording);
	std::vector<obs_encoder_t *> audio;
	for (size_t i = 0; i < MAX_AUDIO_MIXES; i++) {
		obs_encoder_t *track = obs_output_get_audio_encoder(recording, i);
		if (!track)
			break;
		audio.push_back(track);
	}
	if (video && !audio.empty()) {
		g_video = obs_encoder_get_ref(video);
		for (obs_encoder_t *track : audio)
			if (obs_encoder_t *held = obs_encoder_get_ref(track))
				g_audio.push_back(held);
	}
	obs_output_release(recording);
	if (g_video && !g_audio.empty() && g_audio.size() == audio.size())
		return true;
	ReleaseEncoders();
	return false;
}

// the same encoders obs builds for its recording and replay buffer, from the same profile: the advanced output encoder, its saved settings, the rescale, and one audio encoder for each recorded track
bool BuildEncoders(config_t *config)
{
	std::string encoderId = ProfileString(config, "AdvOut", "RecEncoder");
	const bool useStream = encoderId.empty() || encoderId == "none";
	if (useStream)
		encoderId = ProfileString(config, "AdvOut", "Encoder");
	if (encoderId.empty())
		return false;
	char *profile = obs_frontend_get_current_profile_path();
	const std::string settingsPath = std::string(profile ? profile : "") + (useStream ? "/streamEncoder.json" : "/recordEncoder.json");
	bfree(profile);
	obs_data_t *settings = obs_data_create_from_json_file_safe(settingsPath.c_str(), "bak");
	if (!settings)
		settings = obs_data_create();
	g_video = obs_video_encoder_create(encoderId.c_str(), "replaykit_clip_buffer_video", settings, nullptr);
	obs_data_release(settings);
	if (!g_video)
		return false;
	obs_encoder_set_video(g_video, obs_get_video());
	const std::string rescale = ProfileString(config, "AdvOut", "RecRescaleRes");
	const int filter = (int)config_get_int(config, "AdvOut", "RecRescaleFilter");
	unsigned int cx = 0, cy = 0;
	if (!useStream && filter != OBS_SCALE_DISABLE && sscanf(rescale.c_str(), "%ux%u", &cx, &cy) == 2 && cx >= 16 && cy >= 16) {
		obs_encoder_set_scaled_size(g_video, cx, cy);
		obs_encoder_set_gpu_scale_type(g_video, (enum obs_scale_type)filter);
	}

	std::string audioId = ProfileString(config, "AdvOut", "RecAudioEncoder");
	if (audioId.empty())
		audioId = "ffmpeg_aac";
	int tracks = (int)config_get_int(config, "AdvOut", "RecTracks");
	if (tracks <= 0)
		tracks = 1;
	for (size_t mix = 0; mix < MAX_AUDIO_MIXES; mix++) {
		if (!(tracks & (1 << mix)))
			continue;
		obs_data_t *audioSettings = obs_data_create();
		const std::string bitrateKey = "Track" + std::to_string(mix + 1) + "Bitrate";
		const int bitrate = (int)config_get_int(config, "AdvOut", bitrateKey.c_str());
		obs_data_set_int(audioSettings, "bitrate", bitrate > 0 ? bitrate : 160);
		const std::string name = "replaykit_clip_buffer_audio_" + std::to_string(mix);
		obs_encoder_t *audio = obs_audio_encoder_create(audioId.c_str(), name.c_str(), audioSettings, mix, nullptr);
		obs_data_release(audioSettings);
		if (!audio)
			return false;
		obs_encoder_set_audio(audio, obs_get_audio());
		g_audio.push_back(audio);
	}
	return !g_audio.empty();
}

void StartDisk(bool keepPieces)
{
	if (DiskActive() || g_mode != Mode::Disk)
		return;
	config_t *config = obs_frontend_get_profile_config();
	if (!config || ProfileString(config, "Output", "Mode") != "Advanced") {
		blog(LOG_WARNING, "[replaykit] disk clip buffer needs the advanced output mode of obs, not started");
		return;
	}
	if (obs_frontend_replay_buffer_active())
		obs_frontend_replay_buffer_stop();
	g_clipSeconds = qBound(5, (int)config_get_int(config, "AdvOut", "RecRBTime"), 3600);
	g_sharedEncoders = TakeRecordingEncoders();
	if (!g_sharedEncoders && !BuildEncoders(config)) {
		blog(LOG_WARNING, "[replaykit] disk clip buffer could not build its encoders, not started");
		ReleaseEncoders();
		return;
	}

	const QString dirPath = PieceDir();
	QDir().mkpath(dirPath);
	if (!keepPieces) {
		ClearPieceDir();
		g_pieces.clear();
	}
	const std::string dir = QDir::toNativeSeparators(dirPath).toStdString();
	const char *format = "rkbuffer %CCYY-%MM-%DD %hh-%mm-%ss";
	char *first = os_generate_formatted_filename("mp4", true, format);
	const std::string path = dir + "\\" + (first ? first : "rkbuffer.mp4");
	bfree(first);

	obs_data_t *settings = obs_data_create();
	obs_data_set_string(settings, "path", path.c_str());
	obs_data_set_string(settings, "directory", dir.c_str());
	obs_data_set_string(settings, "format", format);
	obs_data_set_string(settings, "extension", "mp4");
	obs_data_set_bool(settings, "allow_spaces", true);
	obs_data_set_bool(settings, "allow_overwrite", false);
	obs_data_set_bool(settings, "split_file", true);
	obs_data_set_int(settings, "max_time_sec", kPieceSeconds);
	obs_data_set_int(settings, "max_size_mb", 0);
	// the mp4 writer of obs runs inside obs, so the buffer needs no muxer process of its own
	g_out = obs_output_create("mp4_output", "replaykit_clip_buffer", settings, nullptr);
	obs_data_release(settings);
	if (!g_out) {
		ReleaseEncoders();
		blog(LOG_WARNING, "[replaykit] disk clip buffer output could not be created");
		return;
	}
	obs_output_set_video_encoder(g_out, g_video);
	for (size_t i = 0; i < g_audio.size(); i++)
		obs_output_set_audio_encoder(g_out, g_audio[i], i);
	signal_handler_t *sh = obs_output_get_signal_handler(g_out);
	signal_handler_connect(sh, "file_changed", OnFileChanged, nullptr);
	signal_handler_connect(sh, "stop", OnOutputStop, nullptr);
	g_current = Piece{path, NowMs(), 0};
	if (!obs_output_start(g_out)) {
		const char *error = obs_output_get_last_error(g_out);
		blog(LOG_WARNING, "[replaykit] disk clip buffer did not start: %s", error ? error : "no reason given");
		signal_handler_disconnect(sh, "file_changed", OnFileChanged, nullptr);
		signal_handler_disconnect(sh, "stop", OnOutputStop, nullptr);
		obs_output_release(g_out);
		g_out = nullptr;
		ReleaseEncoders();
		ReportState();
		return;
	}
	if (!g_purgeTimer) {
		g_purgeTimer = new QTimer(g_callbacks);
		QObject::connect(g_purgeTimer, &QTimer::timeout, g_callbacks, []() { Purge(); });
	}
	g_purgeTimer->start(5000);
	blog(LOG_INFO, "[replaykit] disk clip buffer started: %d s clips in %d s pieces, %zu audio track(s), %s encoders%s", g_clipSeconds, kPieceSeconds, g_audio.size(),
	     g_sharedEncoders ? "the recording" : "its own", keepPieces ? ", pieces kept" : "");
	ReportState();
	RefreshTrayIcon();
}

void StopDisk()
{
	if (g_out && obs_output_active(g_out))
		obs_output_stop(g_out);
}

// the newest finished pieces that cover the clip, plus one more, so the helper can always cut the full length from them
void SendSave()
{
	std::vector<std::string> paths;
	qint64 covered = 0;
	for (auto it = g_pieces.rbegin(); it != g_pieces.rend(); ++it) {
		paths.insert(paths.begin(), it->path);
		// the piece just taken is the spare once the ones after it already cover the clip
		if (covered >= qint64(g_clipSeconds) * 1000)
			break;
		covered += it->endMs - it->startMs;
	}
	if (paths.empty()) {
		g_saving = false;
		blog(LOG_WARNING, "[replaykit] disk clip buffer has nothing to save yet");
		return;
	}

	config_t *config = obs_frontend_get_profile_config();
	const std::string formatting = ProfileString(config, "Output", "FilenameFormatting");
	std::string prefix = ProfileString(config, "SimpleOutput", "RecRBPrefix");
	const std::string suffix = ProfileString(config, "SimpleOutput", "RecRBSuffix");
	std::string format = formatting.empty() ? "%CCYY-%MM-%DD %hh-%mm-%ss" : formatting;
	if (!prefix.empty())
		format = prefix + " " + format;
	if (!suffix.empty())
		format += " " + suffix;
	const std::string container = ProfileString(config, "AdvOut", "RecFormat2");
	const char *ext = container.find("mkv") != std::string::npos ? "mkv" : container.find("mov") != std::string::npos ? "mov" : "mp4";
	char *name = os_generate_formatted_filename(ext, true, format.c_str());
	QJsonArray segments;
	for (const std::string &path : paths)
		segments.append(QString::fromStdString(path));
	const QByteArray body = QJsonDocument(QJsonObject{{"segments", segments}, {"seconds", g_clipSeconds}, {"name", QString::fromUtf8(name ? name : "")}})
					.toJson(QJsonDocument::Compact);
	bfree(name);
	blog(LOG_INFO, "[replaykit] disk clip buffer saving %d s from %zu piece(s)", g_clipSeconds, paths.size());
	const std::string json = body.toStdString();
	RunAsync([json]() {
		const std::string answer = HttpRequest("POST", "/clip-buffer/save", 8767, json.c_str(), 130000);
		QMetaObject::invokeMethod(
			g_callbacks,
			[answer]() {
				g_saving = false;
				if (!JsonBoolField(answer, "ok", false)) {
					const std::string message = ExtractJsonStringField(answer, "message");
					blog(LOG_WARNING, "[replaykit] disk clip buffer save failed: %s", message.empty() ? "no answer from the helper" : message.c_str());
					return;
				}
				const std::string path = ExtractJsonStringField(answer, "path");
				blog(LOG_INFO, "[replaykit] disk clip buffer saved a clip");
				// the scripts that beep and show the clip toast hear this in place of the replay saved event of obs
				calldata_t cd = {};
				calldata_set_string(&cd, "path", path.c_str());
				signal_handler_signal(obs_get_signal_handler(), "replaykit_clip_saved", &cd);
				calldata_free(&cd);
			},
			Qt::QueuedConnection);
	});
}

void SaveDisk()
{
	if (!DiskActive())
		return;
	if (g_saving) {
		blog(LOG_INFO, "[replaykit] disk clip buffer is already saving a clip");
		return;
	}
	g_saving = true;
	g_awaitingSplit = true;
	const int gen = ++g_saveGen;
	// the piece being written is closed first, so the clip runs right up to the press
	calldata_t cd = {};
	proc_handler_call(obs_output_get_proc_handler(g_out), "split_file", &cd);
	calldata_free(&cd);
	QTimer::singleShot(kSplitWaitMs, g_callbacks, [gen]() {
		if (!g_awaitingSplit || gen != g_saveGen)
			return;
		g_awaitingSplit = false;
		blog(LOG_WARNING, "[replaykit] disk clip buffer did not split in time, saving the pieces it has");
		SendSave();
	});
}

obs_hotkey_id FindReplaySaveHotkey()
{
	obs_hotkey_id found = OBS_INVALID_HOTKEY_ID;
	obs_enum_hotkeys(
		[](void *data, obs_hotkey_id id, obs_hotkey_t *key) {
			if (obs_hotkey_get_registerer_type(key) != OBS_HOTKEY_REGISTERER_OUTPUT || strcmp(obs_hotkey_get_name(key), "ReplayBuffer.Save") != 0)
				return true;
			*static_cast<obs_hotkey_id *>(data) = id;
			return false;
		},
		&found);
	return found;
}

bool SameCombos(const std::vector<obs_key_combination_t> &a, const std::vector<obs_key_combination_t> &b)
{
	if (a.size() != b.size())
		return false;
	for (size_t i = 0; i < a.size(); i++)
		if (a[i].key != b[i].key || a[i].modifiers != b[i].modifiers)
			return false;
	return true;
}

// the clip keybind is the save hotkey of the obs replay buffer, which does nothing while that buffer is off, so in disk mode a hotkey of this plugin carries the same keys; in memory mode it carries none, or a press would save twice
void SyncSaveHotkey()
{
	if (g_saveHotkey == OBS_INVALID_HOTKEY_ID)
		return;
	std::vector<obs_key_combination_t> combos;
	const obs_hotkey_id source = g_mode == Mode::Disk ? FindReplaySaveHotkey() : OBS_INVALID_HOTKEY_ID;
	if (source != OBS_INVALID_HOTKEY_ID) {
		struct Collect {
			obs_hotkey_id id;
			std::vector<obs_key_combination_t> *combos;
		} collect{source, &combos};
		obs_enum_hotkey_bindings(
			[](void *data, size_t, obs_hotkey_binding_t *binding) {
				auto *c = static_cast<Collect *>(data);
				if (obs_hotkey_binding_get_hotkey_id(binding) == c->id)
					c->combos->push_back(obs_hotkey_binding_get_key_combination(binding));
				return true;
			},
			&collect);
	}
	if (SameCombos(combos, g_mirrored))
		return;
	g_mirrored = combos;
	obs_hotkey_load_bindings(g_saveHotkey, g_mirrored.data(), g_mirrored.size());
}

void OnSaveHotkey(void *, obs_hotkey_id, obs_hotkey_t *, bool pressed)
{
	if (!pressed || !g_callbacks)
		return;
	QMetaObject::invokeMethod(g_callbacks, []() {
		if (g_mode == Mode::Disk)
			SaveDisk();
	}, Qt::QueuedConnection);
}

// the hotkey lock is held while this is signalled, so the mirror is redone later on the ui thread; changes to the mirror itself are its own echo and are left alone
void OnHotkeyBindingsChanged(void *, calldata_t *cd)
{
	obs_hotkey_t *key = static_cast<obs_hotkey_t *>(calldata_ptr(cd, "hotkey"));
	if (!key || obs_hotkey_get_id(key) == g_saveHotkey || !g_callbacks)
		return;
	QMetaObject::invokeMethod(g_callbacks, []() { SyncSaveHotkey(); }, Qt::QueuedConnection);
}

// the auto start script asks this instead of starting the obs replay buffer itself, so a disk buffer never starts the memory one first
void ClipStartProc(void *, calldata_t *cd)
{
	calldata_set_bool(cd, "handled", true);
	if (g_callbacks)
		QMetaObject::invokeMethod(g_callbacks, []() { ClipBufferStart(); }, Qt::QueuedConnection);
}

void SwitchMode(Mode next)
{
	const bool wasActive = ClipBufferActive();
	const Mode previous = g_mode;
	g_mode = next;
	blog(LOG_INFO, "[replaykit] clip buffer moves from %s to %s", ModeName(previous), ModeName(next));
	SyncSaveHotkey();
	if (previous == Mode::Disk)
		StopDisk();
	else if (obs_frontend_replay_buffer_active())
		obs_frontend_replay_buffer_stop();
	if (!wasActive)
		return;
	if (next == Mode::Disk) {
		ClipBufferStart();
		return;
	}
	// the disk output lets go of the encoders a moment after the stop; obs builds its own, so the memory buffer can start once the old one is down
	auto tries = std::make_shared<int>(0);
	auto attempt = std::make_shared<std::function<void()>>();
	*attempt = [tries, attempt]() {
		if (g_mode != Mode::Memory)
			return;
		if (!DiskActive() || ++*tries > 50) {
			ClipBufferStart();
			return;
		}
		QTimer::singleShot(100, g_callbacks, *attempt);
	};
	(*attempt)();
}

} // namespace

void ClipBufferInit()
{
	g_mode = ModeFromSettingsFile();
	signal_handler_add(obs_get_signal_handler(), "void replaykit_clip_saved(string path)");
	proc_handler_add(obs_get_proc_handler(), "void replaykit_clip_start(out bool handled)", ClipStartProc, nullptr);
	signal_handler_connect(obs_get_signal_handler(), "hotkey_bindings_changed", OnHotkeyBindingsChanged, nullptr);
	g_saveHotkey = obs_hotkey_register_frontend("ReplayKit.SaveClip", "ReplayKit: Save Clip (disk buffer)", OnSaveHotkey, nullptr);
	blog(LOG_INFO, "[replaykit] clip buffer mode at startup: %s", ModeName(g_mode));
}

void ClipBufferShutdown()
{
	signal_handler_disconnect(obs_get_signal_handler(), "hotkey_bindings_changed", OnHotkeyBindingsChanged, nullptr);
	if (g_saveHotkey != OBS_INVALID_HOTKEY_ID)
		obs_hotkey_unregister(g_saveHotkey);
	g_saveHotkey = OBS_INVALID_HOTKEY_ID;
	if (g_out) {
		signal_handler_t *sh = obs_output_get_signal_handler(g_out);
		signal_handler_disconnect(sh, "file_changed", OnFileChanged, nullptr);
		signal_handler_disconnect(sh, "stop", OnOutputStop, nullptr);
		obs_output_force_stop(g_out);
		obs_output_release(g_out);
		g_out = nullptr;
	}
	ReleaseEncoders();
}

bool ClipBufferActive()
{
	return g_mode == Mode::Disk ? DiskActive() : obs_frontend_replay_buffer_active();
}

void ClipBufferStart()
{
	if (g_mode == Mode::Disk)
		ShellStartOutputWhenFree([]() { StartDisk(false); });
	else
		ShellStartOutputWhenFree([]() { obs_frontend_replay_buffer_start(); });
}

void ClipBufferStop()
{
	if (g_mode == Mode::Disk)
		StopDisk();
	else
		obs_frontend_replay_buffer_stop();
}

void ClipBufferToggle()
{
	const bool active = ClipBufferActive();
	blog(LOG_INFO, "[replaykit] clip buffer toggle (%s): active=%d", ModeName(g_mode), active);
	if (active)
		ClipBufferStop();
	else
		ClipBufferStart();
}

void ClipBufferSave()
{
	if (g_mode == Mode::Disk)
		SaveDisk();
	else if (obs_frontend_replay_buffer_active())
		obs_frontend_replay_buffer_save();
}

void ClipBufferApplySettings(const std::string &settingsBody)
{
	const std::string mode = ExtractJsonStringField(settingsBody, "clipBufferMode");
	// a helper that did not answer says nothing about the mode
	if (mode != "disk" && mode != "memory")
		return;
	const Mode next = mode == "disk" ? Mode::Disk : Mode::Memory;
	if (next != g_mode)
		SwitchMode(next);
	SyncSaveHotkey();
}

std::string ClipBufferPollQuery()
{
	return std::string("?clip=") + (DiskActive() ? "1" : "0");
}

namespace {

void MoveOntoRecordingEncoders()
{
	if (g_mode != Mode::Disk || !DiskActive() || g_sharedEncoders || g_restartShared)
		return;
	// a save reads the pieces as they are, so the move waits for it
	if (g_saving) {
		QTimer::singleShot(500, g_callbacks, []() { MoveOntoRecordingEncoders(); });
		return;
	}
	blog(LOG_INFO, "[replaykit] a recording started, moving the disk clip buffer onto its encoders");
	g_restartShared = true;
	StopDisk();
}

} // namespace

void ClipBufferOnFrontendEvent(enum obs_frontend_event event)
{
	if (g_mode != Mode::Disk)
		return;
	// something started the memory buffer behind the setting (the obs controls, an auto start of obs itself): it is stopped and the disk one runs instead
	if (event == OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED) {
		blog(LOG_INFO, "[replaykit] the memory replay buffer started while the clip buffer is on disk, switching it over");
		obs_frontend_replay_buffer_stop();
		ClipBufferStart();
	}
	// a recording that starts while the buffer runs on encoders of its own would make the video encode twice, so the buffer moves onto the encoders of the recording; the pieces it has stay, and only the moment of the move is missing from the buffer
	if (event == OBS_FRONTEND_EVENT_RECORDING_STARTED)
		MoveOntoRecordingEncoders();
	// another profile has its own encoders and clip length
	if (event == OBS_FRONTEND_EVENT_PROFILE_CHANGED && DiskActive()) {
		StopDisk();
		QTimer::singleShot(1500, g_callbacks, []() { ClipBufferStart(); });
	}
}

QJsonObject ClipBufferProbe()
{
	return {{"mode", ModeName(g_mode)},
		{"active", ClipBufferActive()},
		{"diskPieces", (int)g_pieces.size()},
		{"saving", g_saving},
		{"sharedEncoders", g_sharedEncoders},
		{"clipSeconds", g_clipSeconds},
		{"hotkeyKeys", (int)g_mirrored.size()}};
}

} // namespace rk
