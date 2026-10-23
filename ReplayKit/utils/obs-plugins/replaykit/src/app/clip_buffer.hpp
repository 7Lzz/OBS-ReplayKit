// the clip buffer: obs keeps it in memory as its replay buffer, or this keeps it on disk as short pieces and the helper joins the newest of them into a clip
#pragma once

#include <string>

class QJsonObject;

namespace rk {

void ClipBufferInit();
void ClipBufferShutdown();
bool ClipBufferActive();
void ClipBufferStart();
void ClipBufferStop();
void ClipBufferToggle();
void ClipBufferSave();
// the mode the helper settled on ("disk" or "memory"), read off every settings poll
void ClipBufferApplySettings(const std::string &settingsBody);
// what the settings poll tells the helper about the disk buffer
std::string ClipBufferPollQuery();
void ClipBufferOnFrontendEvent(enum obs_frontend_event event);
QJsonObject ClipBufferProbe();

} // namespace rk
