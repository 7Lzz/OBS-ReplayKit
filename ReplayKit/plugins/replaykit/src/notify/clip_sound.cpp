// plays the sound of a saved clip and of a recording that starts or stops, at the volumes of the settings
#include "prelude.hpp"
#include "notify/clip_sound.hpp"

#include <QDir>
#include <QFileInfo>
#include <algorithm>
#include <mmsystem.h>

#include "core/json_lite.hpp"
#include "core/state_files.hpp"

namespace rk {

namespace {

constexpr wchar_t kAlias[] = L"replaykit_clip_sound";
// unknown until the helper has answered once, and nothing plays before that
int g_clipVolume = -1;
int g_recordingVolume = -1;

bool Mci(const QString &command, const QString &path)
{
	const MCIERROR error = mciSendStringW(reinterpret_cast<const wchar_t *>(command.utf16()), nullptr, 0, nullptr);
	if (error && !path.isEmpty())
		blog(LOG_WARNING, "[replaykit] clip sound: '%s' failed with mci error %lu for %s", command.section(QLatin1Char(' '), 0, 0).toUtf8().constData(),
		     (unsigned long)error, path.toUtf8().constData());
	return !error;
}

} // namespace

void ClipSoundApplySettings(const std::string &settingsBody)
{
	if (const auto clip = JsonIntIfPresent(settingsBody, "clipSoundVolume"))
		g_clipVolume = std::clamp(*clip, 0, 100);
	if (const auto recording = JsonIntIfPresent(settingsBody, "recordingSoundVolume"))
		g_recordingVolume = std::clamp(*recording, 0, 100);
}

void ClipSoundPlay(ClipSound sound)
{
	const int volume = sound == ClipSound::ClipSaved ? g_clipVolume : g_recordingVolume;
	if (volume < 0)
		return;
	const char *name = sound == ClipSound::ClipSaved ? "clipping_saved.mp3" : sound == ClipSound::RecordingStarted ? "recording_start_saved.mp3" : "recording_ended_saved.mp3";
	const QString path = QDir::toNativeSeparators(ReplayKitRootPath() + QStringLiteral("/sounds/") + QString::fromLatin1(name));
	const QString alias = QString::fromWCharArray(kAlias);
	Mci(QStringLiteral("close ") + alias, QString());
	if (!QFileInfo::exists(path)) {
		blog(LOG_WARNING, "[replaykit] clip sound: %s is missing", path.toUtf8().constData());
		return;
	}
	// a windows path cannot hold a quote, so plain quotes are enough around it
	if (!Mci(QStringLiteral("open \"%1\" type mpegvideo alias %2").arg(path, alias), path))
		return;
	Mci(QStringLiteral("setaudio %1 volume to %2").arg(alias).arg(volume * 10), path);
	if (!Mci(QStringLiteral("play %1 from 0").arg(alias), path))
		Mci(QStringLiteral("close ") + alias, QString());
}

void ClipSoundStop()
{
	Mci(QStringLiteral("close ") + QString::fromWCharArray(kAlias), QString());
}

} // namespace rk
