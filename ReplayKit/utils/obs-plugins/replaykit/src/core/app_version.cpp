// the installed replaykit version, asked from the helper and cached
#include "prelude.hpp"
#include "core/app_version.hpp"

#include "core/async.hpp"
#include "core/helper_client.hpp"
#include "core/json_lite.hpp"
#include "core/state.hpp"

namespace rk {

namespace {

QString g_appVersionText;
bool g_appVersionInFlight = false;

} // namespace

QString AppVersionText()
{
	return g_appVersionText;
}

// same shape as the notification poll: one small request, skipped while one is already in flight, and a helper that is down or answers something else leaves the last known text alone
void RefreshAppVersion(std::function<void()> changed)
{
	if (g_appVersionInFlight)
		return;
	g_appVersionInFlight = true;
	RunAsync([changed]() {
		const std::string version = ExtractJsonStringField(HttpRequest("GET", "/version", 8767, nullptr, 2500), "version");
		QMetaObject::invokeMethod(
			g_callbacks,
			[changed, version]() {
				g_appVersionInFlight = false;
				if (version.empty())
					return;
				const QString text = QStringLiteral("v") + QString::fromStdString(version);
				if (text == g_appVersionText)
					return;
				g_appVersionText = text;
				if (changed)
					changed();
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
