#pragma once

#include <string>
#include <vector>
#include <QtGlobal>
class QJsonObject;
class QJsonArray;

namespace rk {
void CaptureContextStart();
void CaptureContextStop();
// the game recognition switch of the settings
void CaptureContextSetRecognition(bool on);
void CaptureContextRequested();
void CaptureContextRefresh();
void CaptureContextResetBuffer();
void CaptureContextName(const QJsonObject &data);
void CaptureContextEvent(enum obs_frontend_event event);
QJsonObject CaptureContextProbe();
void CaptureContextReplayFile(const std::string &path);
void CaptureContextFile(const std::string &path, qint64 end = 0);
void CaptureContextBindSave(qint64 id, const QJsonArray &pieces);
std::vector<std::string> CaptureContextMessages();
}
