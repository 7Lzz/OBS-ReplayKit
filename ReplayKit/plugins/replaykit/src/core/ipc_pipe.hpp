// named pipe server the helper connects to
#pragma once

#include <QtGlobal>
#include <atomic>

namespace rk {

extern std::atomic<quintptr> g_mainWinValue;
extern std::atomic<unsigned long long> g_allowCloseUntilMs;
extern std::atomic<bool> g_pipeSendAllowCloseAck;
extern std::atomic<bool> g_pipeSendOutputsReleasedAck;
extern std::atomic<bool> g_pipeSendLanguageAck;
// the window of the share projector just opened, sent back with SHAREPROJECTOR_ACK; -1 while there is nothing to send, 0 when obs showed none
extern std::atomic<long long> g_pipeShareProjectorAck;
void PublishProjectorWindows();
void StartPipeServer();
void StopPipeServer();

} // namespace rk
