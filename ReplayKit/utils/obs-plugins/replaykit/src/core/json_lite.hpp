// field extraction for the small known-shape helper responses
#pragma once

#include <string>

namespace rk {

bool JsonBoolField(const std::string &body, const char *field, bool defaultValue);
std::string ExtractJsonObjectField(const std::string &body, const char *field);
std::string ExtractJsonStringField(const std::string &obj, const char *field);
int JsonIntField(const std::string &body, const char *field, int defaultValue);
// byte counts do not fit an int
long long JsonInt64Field(const std::string &body, const char *field, long long defaultValue);
double JsonDoubleField(const std::string &body, const char *field, double defaultValue);

} // namespace rk
