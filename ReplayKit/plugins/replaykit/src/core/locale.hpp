// the messages of the native chrome in the language obs runs in, from obs-replayKit\locale\messages\<code>.json, the same catalogs the pages render from; a message a catalog lacks stays english
#pragma once

#include <QString>

#include <initializer_list>
#include <utility>

namespace rk {

// reads the messages of the language obs settled on before any module loaded; called once from obs_module_load
void LocaleLoad();
QString TrMessage(const char *id, std::initializer_list<std::pair<const char *, QString>> vars = {});
// a clip length written the way the pages write it: 45 s, 2 min, 1 min 30 s in the words of the language
QString LengthText(int seconds);

} // namespace rk
