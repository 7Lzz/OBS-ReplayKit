// the font the native chrome draws its text in, the uiFont setting of the helper
#include "prelude.hpp"
#include "core/ui_font.hpp"

#include <QFontDatabase>
#include <QSet>

#include "core/json_lite.hpp"
#include "core/window_state.hpp"

namespace rk {

namespace {

// what the chrome drew with before the font could be picked, until the helper or the last run names one
QString g_family = QStringLiteral("Segoe UI");
QString g_file;
bool g_lastRunRead = false;
QSet<QString> g_addedFiles;
QSet<QString> g_failedFiles;

// qt finds the family of a font replaykit ships only once its file is added; a file that failed is not tried again on every poll
bool AddFontFile(const QString &file)
{
	if (file.isEmpty() || g_addedFiles.contains(file))
		return true;
	if (g_failedFiles.contains(file))
		return false;
	if (QFontDatabase::addApplicationFont(file) < 0) {
		g_failedFiles.insert(file);
		blog(LOG_WARNING, "[replaykit] could not load the interface font %s", qUtf8Printable(file));
		return false;
	}
	g_addedFiles.insert(file);
	return true;
}

void ReadLastRun()
{
	if (g_lastRunRead)
		return;
	g_lastRunRead = true;
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	const QString family = ini.value("uiFontFamily").toString();
	const QString file = ini.value("uiFontFile").toString();
	if (family.isEmpty() || !AddFontFile(file))
		return;
	g_family = family;
	g_file = file;
}

} // namespace

QString UiFontFamily()
{
	ReadLastRun();
	return g_family;
}

bool UiFontApplySettings(const std::string &settingsBody)
{
	ReadLastRun();
	// a body without the setting leaves the font as it was
	const QString family = QString::fromStdString(ExtractJsonStringField(settingsBody, "uiFont"));
	if (family.isEmpty())
		return false;
	const QString file = QString::fromStdString(ExtractJsonStringField(settingsBody, "uiFontFile"));
	if (family == g_family && file == g_file)
		return false;
	if (!AddFontFile(file))
		return false;
	g_family = family;
	g_file = file;
	QSettings ini(WindowStateIniPath(), QSettings::IniFormat);
	ini.setValue("uiFontFamily", g_family);
	ini.setValue("uiFontFile", g_file);
	return true;
}

} // namespace rk
