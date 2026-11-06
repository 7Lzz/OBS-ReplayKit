// live app icon swap and taskbar identity
#pragma once

#include <QJsonObject>
#include <QPixmap>
#include <QString>
#include <QWidget>
#include <QtGlobal>

#include "core/win32.hpp"

namespace rk {

extern HICON g_ownedIconSettings;
extern HICON g_ownedIconSetup;
extern int g_taggedSettings;
extern int g_taggedSetup;
void ForceTaskbarButton(QWidget *w);
extern QString g_rkIconPath;
extern bool g_recordingDotEnabled;
void ApplyAuxWindowIcon(QWidget *w, HICON *owned, int *tagged, const wchar_t *aumid);
void CaptureAppIconDefaults();
void RefreshAppIcon();
void RefreshTrayIcon();
QPixmap ShellLogoPixmap(int logicalPx, qreal dpr);
void ApplyAppIcon(const QString &path);
QJsonObject TrayIconReport();

} // namespace rk
