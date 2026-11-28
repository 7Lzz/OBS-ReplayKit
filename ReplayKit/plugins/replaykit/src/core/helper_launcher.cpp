// starts the helper with obs and keeps it running: a helper that stops answering is asked to shut down and started again, and one that never ends is stopped after a grace
#include "prelude.hpp"
#include "core/helper_launcher.hpp"

#include <QDir>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QSaveFile>
#include <optional>

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/shared_values.hpp"
#include "core/state.hpp"
#include "core/state_files.hpp"
#include "replaykit_folder.hpp"

namespace rk {

namespace {

constexpr int kStartWaitMs = 5000;
constexpr int kWatchdogMs = 10000;
constexpr int kProbeTimeoutMs = 2000;
// a healthy helper can take a couple of seconds to answer, so only this many missed checks in a row count
constexpr int kUnhealthyChecks = 3;
constexpr ULONGLONG kRestartCooldownMs = 15000;
// time for a graceful shutdown and for a disk clip save to finish before an unresponsive helper is stopped
constexpr ULONGLONG kShutdownGraceMs = 150000;
constexpr qint64 kMaxSettingsBytes = 64 * 1024;

struct Command {
	std::wstring line;
	bool hosted = false;
};

QPointer<QTimer> g_watchdog;
HANDLE g_process = nullptr;
// whether g_process runs under the role host, and whether the host refused or ended a helper this session, after which the helper runs without it until obs starts again
bool g_hosted = false;
bool g_hostRefused = false;
// whether the helper answered since it was last started
bool g_answered = false;
bool g_probing = false;
bool g_startWaiting = false;
bool g_stopped = false;
bool g_updateCheckStarted = false;
int g_fails = 0;
ULONGLONG g_lastRestartAt = 0;
ULONGLONG g_shutdownSince = 0;

QString HelperExePath()
{
	return QDir::toNativeSeparators(ReplayKitRootPath() + QLatin1Char('/') + QString::fromWCharArray(kHelperExe));
}

// one argument the way CommandLineToArgvW reads it back
QString Quote(const QString &argument)
{
	if (!argument.isEmpty() && !argument.contains(QLatin1Char(' ')) && !argument.contains(QLatin1Char('\t')) && !argument.contains(QLatin1Char('"')))
		return argument;
	QString out = QStringLiteral("\"");
	int slashes = 0;
	for (const QChar c : argument) {
		if (c == QLatin1Char('\\')) {
			++slashes;
			continue;
		}
		out += QString(c == QLatin1Char('"') ? slashes * 2 + 1 : slashes, QLatin1Char('\\'));
		out += c;
		slashes = 0;
	}
	return out + QString(slashes * 2, QLatin1Char('\\')) + QLatin1Char('"');
}

// logging is a development setting; a file from before development mode has no key for it and keeps its logging
bool LoggingOn()
{
	QFile file(StateFilePath(QString::fromLatin1(kSettingsFile)));
	if (!file.open(QIODevice::ReadOnly) || file.size() > kMaxSettingsBytes)
		return false;
	const QJsonObject settings = QJsonDocument::fromJson(file.readAll()).object();
	const bool logging = settings.value(QStringLiteral("debugLoggingEnabled")).toBool(false);
	return logging && settings.value(QStringLiteral("developmentMode")).toBool(true);
}

// the folder of the helper tells it where the replaykit folder is; the clip folder comes from the settings, which the helper reads first
bool WriteConfig()
{
	const QString path = StateFilePath(QString::fromLatin1(kHelperConfigFile));
	QDir().mkpath(QFileInfo(path).absolutePath());
	const QJsonObject config{{QStringLiteral("scriptDir"), QDir::fromNativeSeparators(QFileInfo(HelperExePath()).absolutePath()) + QLatin1Char('/')},
				 {QStringLiteral("clipDir"), QString()},
				 {QStringLiteral("loggingEnabled"), LoggingOn()}};
	QSaveFile file(path);
	if (!file.open(QIODevice::WriteOnly) || file.write(QJsonDocument(config).toJson(QJsonDocument::Indented)) < 0 || !file.commit()) {
		blog(LOG_ERROR, "[replaykit] helper: could not write %s", path.toUtf8().constData());
		return false;
	}
	return true;
}

// the helper runs under the obs role host when there is one, which groups it with obs
std::optional<Command> HelperCommand(const QString &arguments)
{
	if (!g_hostRefused) {
		wchar_t obs[32768];
		const DWORD length = GetModuleFileNameW(nullptr, obs, DWORD(std::size(obs)));
		if (!length || length >= std::size(obs)) {
			blog(LOG_ERROR, "[replaykit] helper: the path of obs could not be read");
			return std::nullopt;
		}
		const QString host = QString::fromWCharArray(obs, int(length));
		if (GetProcAddress(GetModuleHandleW(nullptr), "ProcessHostVersion"))
			return Command{(Quote(host) + QStringLiteral(" --replaykit-helper") + arguments).toStdWString(), true};
		const QString launcher = QDir::toNativeSeparators(QFileInfo(host).absolutePath() + QStringLiteral("/replaykit-host/obs64.exe"));
		if (QFileInfo::exists(launcher))
			return Command{(Quote(launcher) + QStringLiteral(" --replaykit-helper") + arguments).toStdWString(), true};
	}
	return Command{(Quote(HelperExePath()) + arguments).toStdWString(), false};
}

bool CreateHidden(std::wstring line, DWORD flags, HANDLE *process, DWORD &error)
{
	STARTUPINFOW startup{};
	startup.cb = sizeof(startup);
	startup.dwFlags = STARTF_USESHOWWINDOW;
	startup.wShowWindow = SW_HIDE;
	PROCESS_INFORMATION info{};
	if (!CreateProcessW(nullptr, line.data(), nullptr, nullptr, FALSE, flags, nullptr, nullptr, &startup, &info)) {
		error = GetLastError();
		return false;
	}
	CloseHandle(info.hThread);
	if (process)
		*process = info.hProcess;
	else
		CloseHandle(info.hProcess);
	return true;
}

bool SpawnHidden(const Command &command, HANDLE *process, DWORD &error)
{
	if (command.hosted)
		return CreateHidden(command.line, CREATE_NO_WINDOW, process, error);
	// some job policies refuse a breakaway, and the helper runs fine without one
	return CreateHidden(command.line, CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB | DETACHED_PROCESS, process, error) ||
	       CreateHidden(command.line, CREATE_NO_WINDOW, process, error);
}

// antivirus can block the role host while the helper exe still runs on its own, so a hosted launch windows refuses is made once more without the host
bool SpawnHelper(const QString &arguments, HANDLE *process, bool &hosted)
{
	const auto command = HelperCommand(arguments);
	if (!command)
		return false;
	DWORD error = 0;
	if (SpawnHidden(*command, process, error)) {
		hosted = command->hosted;
		return true;
	}
	if (command->hosted) {
		g_hostRefused = true;
		blog(LOG_WARNING, "[replaykit] helper: the obs role host could not start the helper (error %lu), starting it without the host", (unsigned long)error);
		if (SpawnHidden(Command{(Quote(HelperExePath()) + arguments).toStdWString(), false}, process, error)) {
			hosted = false;
			return true;
		}
	}
	blog(LOG_ERROR, "[replaykit] helper: could not be started (error %lu)", (unsigned long)error);
	return false;
}

void ReleaseExited()
{
	if (!g_process || WaitForSingleObject(g_process, 0) != WAIT_OBJECT_0)
		return;
	// antivirus that stops the role host after it started ends the helper with an error code, and the next helper starts without the host; a stop of an unresponsive helper does not count
	DWORD code = 0;
	if (g_hosted && !g_shutdownSince && GetExitCodeProcess(g_process, &code) && code != 0) {
		g_hostRefused = true;
		blog(LOG_WARNING, "[replaykit] helper: the helper under the obs role host ended with code %lu, the next one starts without the host", (unsigned long)code);
	}
	CloseHandle(g_process);
	g_process = nullptr;
	g_shutdownSince = 0;
}

// a missing exe is a broken install, which running the setup again repairs
bool Spawn()
{
	ReleaseExited();
	if (g_process)
		return true;
	if (!QFileInfo::exists(HelperExePath())) {
		blog(LOG_ERROR, "[replaykit] helper: %s is missing", HelperExePath().toUtf8().constData());
		return false;
	}
	if (!WriteConfig())
		return false;
	HANDLE process = nullptr;
	bool hosted = false;
	if (!SpawnHelper(QStringLiteral(" -ConfigPath ") + Quote(QDir::toNativeSeparators(StateFilePath(QString::fromLatin1(kHelperConfigFile)))), &process, hosted))
		return false;
	g_process = process;
	g_hosted = hosted;
	return true;
}

// the startup update check runs in a helper of its own, so its prompt also shows while obs stays in the tray
void StartUpdateCheck()
{
	if (g_updateCheckStarted || !QFileInfo::exists(HelperExePath()))
		return;
	bool hosted = false;
	g_updateCheckStarted = SpawnHelper(QStringLiteral(" --update-startup-check"), nullptr, hosted);
}

void PostShutdown()
{
	RunAsync([]() { HttpRequest("POST", "/shutdown", nullptr, 3000); });
}

void OnHealth(bool ok)
{
	ReleaseExited();
	if (ok) {
		g_answered = true;
		g_fails = 0;
		g_shutdownSince = 0;
		StartUpdateCheck();
		return;
	}
	if (++g_fails < kUnhealthyChecks)
		return;
	const ULONGLONG now = GetTickCount64();
	if (g_process) {
		if (!g_shutdownSince) {
			g_shutdownSince = now;
			PostShutdown();
		} else if (now - g_shutdownSince >= kShutdownGraceMs && WaitForSingleObject(g_process, 0) == WAIT_TIMEOUT && TerminateProcess(g_process, 1)) {
			blog(LOG_WARNING, "[replaykit] helper: stopped the unresponsive helper this obs session started");
		}
		return;
	}
	if (g_answered || now - g_lastRestartAt >= kRestartCooldownMs) {
		g_answered = false;
		g_lastRestartAt = now;
		g_fails = 0;
		// not waited for here; the next check sees whether it came up
		if (Spawn())
			StartUpdateCheck();
	}
}

// a real http round trip, since a bound port alone does not prove the helper still answers
void WatchdogTick()
{
	if (g_probing || g_startWaiting || g_stopped)
		return;
	g_probing = true;
	RunAsync([]() {
		const bool ok = HttpRequest("GET", "/status", nullptr, kProbeTimeoutMs).rfind("HTTP/1.1 200", 0) == 0;
		QMetaObject::invokeMethod(
			g_callbacks,
			[ok]() {
				g_probing = false;
				if (!g_stopped)
					OnHealth(ok);
			},
			Qt::QueuedConnection);
	});
}

} // namespace

void HelperLauncherStart()
{
	if (g_watchdog || !g_callbacks)
		return;
	if (Spawn()) {
		g_startWaiting = true;
		WhenHelperAnswers(kStartWaitMs, [](bool up, int) {
			g_startWaiting = false;
			if (up && !g_stopped)
				OnHealth(true);
		});
	}
	g_watchdog = new QTimer(g_callbacks);
	QObject::connect(g_watchdog, &QTimer::timeout, g_callbacks, WatchdogTick);
	g_watchdog->start(kWatchdogMs);
}

void HelperLauncherStop()
{
	if (g_stopped)
		return;
	g_stopped = true;
	if (g_watchdog)
		g_watchdog->stop();
	HttpRequest("POST", "/shutdown", nullptr, 500);
	if (g_process) {
		CloseHandle(g_process);
		g_process = nullptr;
	}
}

} // namespace rk
