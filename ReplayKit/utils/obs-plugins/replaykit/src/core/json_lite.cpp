// field extraction for the small known-shape helper responses
#include "prelude.hpp"
#include "core/json_lite.hpp"

namespace rk {

bool JsonBoolField(const std::string &body, const char *field, bool defaultValue)
{
	if (body.find(std::string("\"") + field + "\":true") != std::string::npos)
		return true;
	if (body.find(std::string("\"") + field + "\":false") != std::string::npos)
		return false;
	return defaultValue;
}

// pulls one flat {"key":"OBS_KEY_X","shift":true,...} object out of a larger json blob by field name -- clipKeybind/recordingKeybind never nest anything deeper than string/bool leaves, so a substring search from the fields opening brace to the next closing one is exact, same "no real parser needed for a known shape" approach as JsonBoolField above rather than pulling in a json library for two callers.
std::string ExtractJsonObjectField(const std::string &body, const char *field)
{
	std::string marker = std::string("\"") + field + "\":{";
	size_t start = body.find(marker);
	if (start == std::string::npos)
		return std::string();
	start += marker.size() - 1;
	size_t end = body.find('}', start);
	if (end == std::string::npos)
		return std::string();
	return body.substr(start, end - start + 1);
}

std::string ExtractJsonStringField(const std::string &obj, const char *field)
{
	std::string marker = std::string("\"") + field + "\":\"";
	size_t start = obj.find(marker);
	if (start == std::string::npos)
		return std::string();
	start += marker.size();
	size_t end = obj.find('"', start);
	if (end == std::string::npos)
		return std::string();
	return obj.substr(start, end - start);
}

// pulls an integer field out of the small known-shape helper responses, same reasoning as JsonBoolField
int JsonIntField(const std::string &body, const char *field, int defaultValue)
{
	std::string needle = std::string("\"") + field + "\":";
	size_t at = body.find(needle);
	if (at == std::string::npos)
		return defaultValue;
	at += needle.size();
	while (at < body.size() && (body[at] == ' ' || body[at] == '\t'))
		at++;
	bool negative = at < body.size() && body[at] == '-';
	if (negative)
		at++;
	if (at >= body.size() || body[at] < '0' || body[at] > '9')
		return defaultValue;
	int value = 0;
	while (at < body.size() && body[at] >= '0' && body[at] <= '9') {
		value = value * 10 + (body[at] - '0');
		at++;
	}
	return negative ? -value : value;
}

// the position just after "field": with the blanks skipped, or npos when the field is not there
static size_t JsonValueAt(const std::string &body, const char *field)
{
	std::string needle = std::string("\"") + field + "\":";
	size_t at = body.find(needle);
	if (at == std::string::npos)
		return std::string::npos;
	at += needle.size();
	while (at < body.size() && (body[at] == ' ' || body[at] == '\t'))
		at++;
	return at;
}

long long JsonInt64Field(const std::string &body, const char *field, long long defaultValue)
{
	size_t at = JsonValueAt(body, field);
	if (at == std::string::npos)
		return defaultValue;
	bool negative = at < body.size() && body[at] == '-';
	if (negative)
		at++;
	if (at >= body.size() || body[at] < '0' || body[at] > '9')
		return defaultValue;
	long long value = 0;
	while (at < body.size() && body[at] >= '0' && body[at] <= '9') {
		value = value * 10 + (body[at] - '0');
		at++;
	}
	return negative ? -value : value;
}

double JsonDoubleField(const std::string &body, const char *field, double defaultValue)
{
	size_t at = JsonValueAt(body, field);
	if (at == std::string::npos || at >= body.size())
		return defaultValue;
	const char *start = body.c_str() + at;
	char *end = nullptr;
	double value = std::strtod(start, &end);
	return end == start ? defaultValue : value;
}

} // namespace rk
