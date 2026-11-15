// the messages of the native chrome in the language obs runs in
#include "prelude.hpp"
#include "core/locale.hpp"

#include <QJsonDocument>
#include <QJsonObject>
#include <QRegularExpression>

#include "core/state_files.hpp"
#include "core/shared_values.hpp"

namespace rk {

namespace {

QJsonObject g_messages;

QJsonObject ReadMessages(const QString &code)
{
	QFile file(ReplayKitRootPath() + QStringLiteral("/locale/messages/") + code + QStringLiteral(".json"));
	if (!file.open(QIODevice::ReadOnly) || file.size() > 4 * 1024 * 1024)
		return {};
	return QJsonDocument::fromJson(file.readAll()).object();
}

} // namespace

void LocaleLoad()
{
	const char *locale = obs_get_locale();
	const QString code = QString::fromUtf8(locale ? locale : "");
	g_messages = ReadMessages(QStringLiteral("en-US"));
	if (code == QStringLiteral("en-US") || !std::any_of(std::begin(kSupportedLocales), std::end(kSupportedLocales),
							  [&code](const char *supported) { return code == QLatin1String(supported); }))
		return;
	const auto messages = ReadMessages(code);
	for (auto it = messages.begin(); it != messages.end(); ++it)
		g_messages.insert(it.key(), it.value());
	blog(LOG_INFO, "[replaykit] chrome language %s, %d messages", qUtf8Printable(code), int(messages.size()));
}

QString TrMessage(const char *id, std::initializer_list<std::pair<const char *, QString>> vars)
{
	const QString key = QString::fromUtf8(id);
	const QString text = g_messages.value(key).toString(key);
	static const QRegularExpression placeholder(QStringLiteral("\\{([A-Za-z0-9_]+)\\}"));
	QString result;
	qsizetype end = 0;
	for (auto it = placeholder.globalMatch(text); it.hasNext();) {
		const auto match = it.next();
		result += QStringView(text).mid(end, match.capturedStart() - end);
		const auto variable = std::find_if(vars.begin(), vars.end(), [&match](const auto &entry) {
			return match.capturedView(1) == QLatin1String(entry.first);
		});
		result += variable == vars.end() ? match.captured(0) : variable->second;
		end = match.capturedEnd();
	}
	return result + QStringView(text).mid(end);
}

QString LengthText(int seconds)
{
	if (seconds < 60)
		return TrMessage("controls.secondsS", {{"seconds", QString::number(seconds)}});
	const int rest = seconds % 60;
	const QString minutes = QString::number(seconds / 60);
	return rest == 0 ? TrMessage("common.minutesM", {{"minutes", minutes}})
			 : TrMessage("controls.minutesMSecondsS", {{"minutes", minutes}, {"seconds", QString::number(rest)}});
}

} // namespace rk
