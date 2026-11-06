// field extraction for the small known-shape helper responses
#pragma once

#include <optional>
#include <string>

namespace rk {

bool JsonBoolField(const std::string &body, const char *field, bool defaultValue);
// the value only when the body carries the field, so a reader of the settings never stands in a default of its own for one the helper did not send
std::optional<bool> JsonBoolIfPresent(const std::string &body, const char *field);
std::optional<int> JsonIntIfPresent(const std::string &body, const char *field);
std::string ExtractJsonObjectField(const std::string &body, const char *field);
std::string ExtractJsonStringField(const std::string &obj, const char *field);
int JsonIntField(const std::string &body, const char *field, int defaultValue);
// byte counts do not fit an int
long long JsonInt64Field(const std::string &body, const char *field, long long defaultValue);
double JsonDoubleField(const std::string &body, const char *field, double defaultValue);

} // namespace rk
