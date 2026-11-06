// remembers window positions across sessions
#include "prelude.hpp"
#include "core/window_state.hpp"

#include "core/state.hpp"

namespace rk {

namespace {

QByteArray g_lastPolledObsGeo;
QByteArray g_lastPolledClipsGeo;

// belt-and-suspenders for a forced close. taskkill /F, a crash, or an OS shutdown can end obs without firing the close/hide handlers or letting the debounced savers above flush, losing whatever move happened since the last event. this slow poll writes the current geometry every few seconds so a hard kill loses at most one interval of position. change-checked against an in-memory copy, and skips hidden/minimized windows so it never clobbers a good saved position with a tray-hidden or minimized rect.
void PollWindowGeometry()
{
	QByteArray obsGeo, clipsGeo;
	if (g_mainWindow && !g_shellNativeFullscreen && g_mainWindow->isVisible() && !g_mainWindow->isMinimized())
		obsGeo = g_mainWindow->saveGeometry();
	if (g_clipsWindow && !g_clipsFullscreenActive && g_clipsWindow->isVisible() && !g_clipsWindow->isMinimized())
		clipsGeo = g_clipsWindow->saveGeometry();

	const bool obsChanged = !obsGeo.isEmpty() && obsGeo != g_lastPolledObsGeo;
	const bool clipsChanged = !clipsGeo.isEmpty() && clipsGeo != g_lastPolledClipsGeo;
	if (!obsChanged && !clipsChanged)
		return;

	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	if (obsChanged) {
		s.setValue("obsMainWindow", obsGeo);
		g_lastPolledObsGeo = obsGeo;
	}
	if (clipsChanged) {
		s.setValue("clipsWindow", clipsGeo);
		g_lastPolledClipsGeo = clipsGeo;
	}
}

} // namespace

// remembers the OBS main window + Clips window position/size across sessions, since obss own geometry save is unreliable here (force-kill restarts skip it, and .NETs window heuristics can grab the wrong window). one ini in the replaykit config dir; Qts saveGeometry/restoreGeometry handle dpi, maximized state, and multi-monitor, and restoreGeometry refuses a rect that would land fully off-screen.
QString WindowStateIniPath()
{
	QString dir = qEnvironmentVariable("APPDATA") + "/obs-studio/obs-replayKit";
	QDir().mkpath(dir);
	return dir + "/window_state.ini";
}

void SaveWindowGeometry(const QString &key, QWidget *w)
{
	if (!w || (g_shellNativeFullscreen && key == QLatin1String("obsMainWindow")))
		return;
	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	s.setValue(key, w->saveGeometry());
}

void SaveClipsWindowGeometry(QWidget *window)
{
	QWidget *target = window ? window : g_clipsWindow.data();
	if (!target || g_clipsFullscreenActive)
		return;
	SaveWindowGeometry("clipsWindow", target);
}

void RestoreWindowGeometry(const QString &key, QWidget *w)
{
	if (!w)
		return;
	QSettings s(WindowStateIniPath(), QSettings::IniFormat);
	QByteArray geo = s.value(key).toByteArray();
	if (!geo.isEmpty())
		w->restoreGeometry(geo);
}
QPointer<QTimer> g_clipsGeoSaveTimer;
QPointer<QTimer> g_obsGeoSaveTimer;
QPointer<QTimer> g_geoPollTimer;

// coalesce the flood of move/resize events into one write a little after motion stops.
void ScheduleClipsGeometrySave()
{
	if (!g_clipsWindow)
		return;
	if (!g_clipsGeoSaveTimer) {
		g_clipsGeoSaveTimer = new QTimer(g_callbacks);
		g_clipsGeoSaveTimer->setSingleShot(true);
		QObject::connect(g_clipsGeoSaveTimer, &QTimer::timeout, g_callbacks,
			 []() { SaveClipsWindowGeometry(); });
	}
	g_clipsGeoSaveTimer->start(400);
}

void ScheduleObsMainGeometrySave()
{
	if (!g_mainWindow)
		return;
	if (!g_obsGeoSaveTimer) {
		g_obsGeoSaveTimer = new QTimer(g_callbacks);
		g_obsGeoSaveTimer->setSingleShot(true);
		QObject::connect(g_obsGeoSaveTimer, &QTimer::timeout, g_callbacks,
				 []() { SaveWindowGeometry("obsMainWindow", g_mainWindow); });
	}
	g_obsGeoSaveTimer->start(600);
}

void StartWindowGeometryPoll()
{
	if (g_geoPollTimer)
		return;
	g_geoPollTimer = new QTimer(g_callbacks);
	QObject::connect(g_geoPollTimer, &QTimer::timeout, g_callbacks, PollWindowGeometry);
	g_geoPollTimer->start(5000);
}

} // namespace rk
