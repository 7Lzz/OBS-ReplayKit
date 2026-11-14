// the strings of the native chrome in the language obs runs in, from obs-replayKit\locale\<code>.json, the same files the pages translate from: a json object of english text to its translation; a string a file does not have stays english
#pragma once

#include <QString>

#include <initializer_list>
#include <utility>

namespace rk {

// reads the table of the language obs settled on before any module loaded; called once from obs_module_load
void LocaleLoad();
QString Tr(const char *english);
// Tr with every {name} placeholder filled from vars
QString Tr(const char *english, std::initializer_list<std::pair<const char *, QString>> vars);
QString TrMessage(const char *id, std::initializer_list<std::pair<const char *, QString>> vars = {});

} // namespace rk
