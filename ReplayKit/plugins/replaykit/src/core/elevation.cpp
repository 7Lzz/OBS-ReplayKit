// starts obs again with administrator rights when Run OBS as administrator is on and this copy has none, or lacks the switches every launch by replaykit carries
#include "prelude.hpp"
#include "core/elevation.hpp"

#include <QDir>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <shellapi.h>

#include "core/shared_values.hpp"
#include "core/state_files.hpp"
#include "replaykit_folder.hpp"

namespace rk {

namespace {

constexpr qint64 kMaxSettingsBytes = 64 * 1024;

bool RunAsAdminOn()
{
	QFile file(StateFilePath(QString::fromLatin1(kSettingsFile)));
	if (!file.open(QIODevice::ReadOnly) || file.size() > kMaxSettingsBytes)
		return false;
	return QJsonDocument::fromJson(file.readAll()).object().value(QStringLiteral("runObsAsAdmin")).toBool(false);
}

bool Elevated()
{
	HANDLE token = nullptr;
	if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
		return false;
	TOKEN_ELEVATION elevation{};
	DWORD size = 0;
	const bool known = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &size);
	CloseHandle(token);
	return known && elevation.TokenIsElevated;
}

// every launch of obs by replaykit carries the same switches (shared/ObsLaunch.cs), so an obs without all of them was started some other way
bool HasLaunchArgs()
{
	const QString line = QString::fromWCharArray(GetCommandLineW()).toLower();
	for (const QString &option : QString::fromLatin1(kObsLaunchArgs).toLower().split(QLatin1Char(' '), Qt::SkipEmptyParts))
		if (!line.contains(option))
			return false;
	return true;
}

// starts the relaunch mode of the helper with administrator rights through the uac prompt and stops this obs at once; the relauncher waits until this process is gone, clears its crash marker and starts obs with the replaykit switches, and obs takes over its rights. obs is not started from here, since it refuses a second instance and reaches that check before this copy is gone. a declined prompt leaves this obs running as it is
void RelaunchElevated()
{
	wchar_t obsPath[32768];
	const DWORD length = GetModuleFileNameW(nullptr, obsPath, DWORD(std::size(obsPath)));
	if (!length || length >= std::size(obsPath)) {
		blog(LOG_ERROR, "[replaykit] elevation: the path of obs could not be read");
		return;
	}
	const QString helper = QDir::toNativeSeparators(ReplayKitRootPath() + QLatin1Char('/') + QString::fromWCharArray(kHelperExe));
	if (!QFileInfo::exists(helper)) {
		blog(LOG_ERROR, "[replaykit] elevation: the helper is missing, so obs cannot be started again: %s", helper.toUtf8().constData());
		return;
	}
	const QString directory = QFileInfo(helper).absolutePath();
	// a windows path cannot hold a quote, so plain quotes are enough around it
	const QString parameters = QStringLiteral("--relaunch-obs -ObsPath \"%1\" -ObsPid %2").arg(QString::fromWCharArray(obsPath, int(length))).arg(GetCurrentProcessId());
	const std::wstring file = helper.toStdWString(), folder = QDir::toNativeSeparators(directory).toStdWString(), arguments = parameters.toStdWString();
	SHELLEXECUTEINFOW info{};
	info.cbSize = sizeof(info);
	info.hwnd = GetForegroundWindow();
	info.lpVerb = L"runas";
	info.lpFile = file.c_str();
	info.lpParameters = arguments.c_str();
	info.lpDirectory = folder.c_str();
	info.nShow = SW_SHOWNORMAL;
	if (!ShellExecuteExW(&info)) {
		blog(LOG_INFO, "[replaykit] elevation: the uac prompt was declined or failed (%lu), obs keeps running as it is", (unsigned long)GetLastError());
		return;
	}
	blog(LOG_INFO, "[replaykit] elevation: the elevated relauncher started, stopping this copy");
	TerminateProcess(GetCurrentProcess(), 0);
}

} // namespace

void ElevateIfAsked()
{
	if (!RunAsAdminOn())
		return;
	const bool elevated = Elevated();
	if (elevated && HasLaunchArgs())
		return;
	blog(LOG_INFO, elevated ? "[replaykit] elevation: the replaykit launch switches are missing, starting obs again with them" : "[replaykit] elevation: not elevated, starting obs again through uac");
	RelaunchElevated();
}

} // namespace rk
