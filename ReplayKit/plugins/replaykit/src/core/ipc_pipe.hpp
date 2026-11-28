// named pipe server the helper connects to
#pragma once

#include <QtGlobal>
#include <atomic>
#include <string>

namespace rk {

extern std::atomic<quintptr> g_mainWinValue;
extern std::atomic<unsigned long long> g_allowCloseUntilMs;
extern std::atomic<bool> g_pipeSendAllowCloseAck;
extern std::atomic<bool> g_pipeSendOutputsReleasedAck;
void QueueConfigReply(const std::string &id, bool ok);
// the answer to the last SHARECARD request, sent back as SHARECARD_ACK; -1 while there is nothing to send
extern std::atomic<int> g_pipeShareCardAck;
// wakes the pipe thread to send what was just queued for the helper
void PipeWake();
void StartPipeServer();
void StopPipeServer();

} // namespace rk
