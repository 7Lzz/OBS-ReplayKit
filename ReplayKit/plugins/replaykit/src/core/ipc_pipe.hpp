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
// the answer to the last SHARECARD request, sent back as SHARECARD_ACK; -1 while there is nothing to send
extern std::atomic<int> g_pipeShareCardAck;
void StartPipeServer();
void StopPipeServer();

} // namespace rk
