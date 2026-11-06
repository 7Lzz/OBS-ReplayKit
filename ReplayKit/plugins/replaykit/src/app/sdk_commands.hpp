// a way to communicate with replaykit while it runs: the commands that live in the native plugin
#pragma once

#include <QJsonObject>
#include <functional>
#include <string>

namespace rk {

// a command takes its arguments as a json object and answers with one; it runs on the interface thread
using SdkHandler = std::function<QJsonObject(const QJsonObject &args)>;

// one call per command. the name is what the helper sends after its plugin prefix, so a command registered as ui.state is asked for as plugin.ui.state
void SdkRegister(const char *name, const char *description, SdkHandler handler);
// a command the helper sent over the pipe, without the leading word: an id, the name and the arguments as one line of json. it is run on the interface thread and the answer goes back to the helper over http
void SdkDispatch(const std::string &rest);
// takes the log of focus changes down, so no callback of this module is left behind when it unloads
void SdkStopFocusTrace();
// whether development mode and sdk mode are both on, as the settings poll last saw them
void SdkSetEnabled(bool on);
bool SdkEnabled();

} // namespace rk
