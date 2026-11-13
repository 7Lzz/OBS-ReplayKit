// the card that slides in at the top left of the screen when a clip is saved or a recording starts or stops
#pragma once

#include <string>

class QWidget;

namespace rk {

enum class ClipToastKind { Clip, RecordingStarted, RecordingStopped };

// shows the card for kind when its setting is on; any thread
void ClipToastShow(ClipToastKind kind);
// the four settings the card goes by, from the one second settings poll
void ClipToastApplySettings(const std::string &settingsBody);
// the card while it is on the screen, for the sdk snapshot
QWidget *ClipToastWidget();
// before the code of the card is unloaded with the module
void ClipToastDestroy();

} // namespace rk
