// relays the audio obs monitors to the share window, which plays it, since discord takes the sound of a window share from the process owning the window; the sources with monitoring on are the ones discord heard from obs before, and the lua and helper keep deciding which those are
#pragma once

#include <QJsonObject>

namespace rk {

// rechecks which sources are monitored and starts or stops relaying them; once a second from the share link
void ShareAudioRefresh();
// stops every relay and lets go of the sources, before obs tears its scenes down
void ShareAudioStop();
QJsonObject ShareAudioProbe();

} // namespace rk
