// what the interface does, written to the obs log while development mode and interface logging are both on
#include "prelude.hpp"
#include "core/dev_log.hpp"

#include <atomic>
#include <cstdarg>
#include <cstdio>

namespace rk {

namespace {

std::atomic<bool> g_devLog{false};

} // namespace

void DevLogSetEnabled(bool on)
{
	g_devLog.store(on);
}

bool DevLogEnabled()
{
	return g_devLog.load();
}

void DevLog(const char *format, ...)
{
	if (!g_devLog.load())
		return;
	char text[512];
	va_list args;
	va_start(args, format);
	vsnprintf(text, sizeof(text), format, args);
	va_end(args);
	blog(LOG_INFO, "[replaykit] ui: %s", text);
}

} // namespace rk
