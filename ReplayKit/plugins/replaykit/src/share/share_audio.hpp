// relays the audio obs monitors to the share window, which plays it, since discord takes the sound of a window share from the process owning the window; the sources with monitoring on are the ones discord heard from obs before, and share_monitoring and the helper keep deciding which those are
#pragma once

#include <QJsonObject>

namespace rk {

// rechecks which sources are monitored and starts or stops relaying them; once a second from the share link
void ShareAudioRefresh();
// stops every relay and lets go of the sources, before obs tears its scenes down
void ShareAudioStop();
QJsonObject ShareAudioProbe();
// the helper says whether discord streams something else with its sound, which already carries desktop audio, so share_monitoring leaves desktop audio unmonitored while it holds; a hold the helper stops repeating runs out after 10 s
void ShareAudioSetHeld(bool held);
bool ShareAudioHeld();

} // namespace rk
