// remembers window positions across sessions
#pragma once

#include <QPointer>
#include <QString>
#include <QTimer>
#include <QWidget>

namespace rk {

QString WindowStateIniPath();
void SaveWindowGeometry(const QString &key, QWidget *w);
void SaveClipsWindowGeometry(QWidget *window = nullptr);
void RestoreWindowGeometry(const QString &key, QWidget *w);

extern QPointer<QTimer> g_clipsGeoSaveTimer;
extern QPointer<QTimer> g_obsGeoSaveTimer;
extern QPointer<QTimer> g_geoPollTimer;
void ScheduleClipsGeometrySave();
void ScheduleObsMainGeometrySave();
void StartWindowGeometryPoll();

} // namespace rk
