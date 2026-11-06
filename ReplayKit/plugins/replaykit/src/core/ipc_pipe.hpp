// named pipe server the helper connects to
#pragma once

#include <QtGlobal>
#include <atomic>

namespace rk {

extern std::atomic<quintptr> g_mainWinValue;
extern std::atomic<unsigned long long> g_allowCloseUntilMs;
extern std::atomic<bool> g_pipeSendAllowCloseAck;
extern std::atomic<bool> g_pipeSendOutputsReleasedAck;
void PublishProjectorWindows();
void StartPipeServer();
void StopPipeServer();

} // namespace rk
