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
QString g_obsVersionText;
bool g_appVersionInFlight = false;

} // namespace

QString AppVersionText()
{
	return g_appVersionText;
}

QString ObsVersionText()
{
	return g_obsVersionText;
}

// same shape as the notification poll: one small request, skipped while one is already in flight, and a helper that is down or answers something else leaves the last known text alone
void RefreshAppVersion(std::function<void()> changed)
{
	if (g_appVersionInFlight)
		return;
	g_appVersionInFlight = true;
	RunAsync([changed]() {
		const std::string body = HttpRequest("GET", "/version", nullptr, 2500);
		const std::string version = ExtractJsonStringField(body, "version");
		const std::string obsVersion = ExtractJsonStringField(body, "obsVersion");
		QMetaObject::invokeMethod(
			g_callbacks,
			[changed, version, obsVersion]() {
				g_appVersionInFlight = false;
				if (version.empty())
					return;
				const QString text = QStringLiteral("ReplayKit ") + QString::fromStdString(version);
				const QString obsText = obsVersion.empty() ? QString() : QStringLiteral("OBS ") + QString::fromStdString(obsVersion);
				if (text == g_appVersionText && obsText == g_obsVersionText)
					return;
				g_appVersionText = text;
				g_obsVersionText = obsText;
				if (changed)
					changed();
			},
			Qt::QueuedConnection);
	});
}

} // namespace rk
