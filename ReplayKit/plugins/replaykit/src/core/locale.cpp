// the strings of the native chrome in the language obs runs in
#include "prelude.hpp"
#include "core/locale.hpp"

#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonParseError>
#include <QRegularExpression>

#include "core/state_files.hpp"
#include "core/shared_values.hpp"

namespace rk {

namespace {

QHash<QString, QString> g_strings;
QJsonObject g_messages;

QJsonObject ReadMessages(const QString &code)
{
	QFile file(ReplayKitRootPath() + QStringLiteral("/locale/messages/") + code + QStringLiteral(".json"));
	if (!file.open(QIODevice::ReadOnly) || file.size() > 4 * 1024 * 1024)
		return {};
	return QJsonDocument::fromJson(file.readAll()).object();
}

QStringList Placeholders(const QString &text)
{
	static const QRegularExpression placeholder(QStringLiteral("\\{[A-Za-z0-9_]+\\}"));
	QStringList names;
	for (auto it = placeholder.globalMatch(text); it.hasNext();)
		names.append(it.next().captured(0));
	names.sort();
	return names;
}

} // namespace

void LocaleLoad()
{
	g_strings.clear();
	const char *locale = obs_get_locale();
	const QString code = QString::fromUtf8(locale ? locale : "");
	g_messages = ReadMessages(QStringLiteral("en-US"));
	if (code == QStringLiteral("en-US") || !std::any_of(std::begin(kSupportedLocales), std::end(kSupportedLocales),
							  [&code](const char *supported) { return code == QLatin1String(supported); }))
		return;
	const auto messages = ReadMessages(code);
	for (auto it = messages.begin(); it != messages.end(); ++it)
		g_messages.insert(it.key(), it.value());
	QFile file(ReplayKitRootPath() + QStringLiteral("/locale/") + code + QStringLiteral(".json"));
	if (!file.open(QIODevice::ReadOnly))
		return;
	QJsonParseError error;
	const QJsonDocument doc = QJsonDocument::fromJson(file.readAll(), &error);
	if (error.error != QJsonParseError::NoError || !doc.isObject()) {
		blog(LOG_WARNING, "[replaykit] the %s translations could not be read, the chrome stays english: %s", qUtf8Printable(code), qUtf8Printable(error.errorString()));
		return;
	}
	const QJsonObject strings = doc.object();
	for (auto it = strings.begin(); it != strings.end(); ++it) {
		const QString value = it.value().isString() ? it.value().toString() : QString();
		// a translation that lost or renamed a placeholder would drop a value from the text, so the english text stays instead
		if (!value.isEmpty() && Placeholders(it.key()) == Placeholders(value))
			g_strings.insert(it.key(), value);
	}
	blog(LOG_INFO, "[replaykit] chrome language %s, %d strings", qUtf8Printable(code), int(g_strings.size()));
}

QString Tr(const char *english)
{
	const QString source = QString::fromUtf8(english);
	const auto it = g_strings.constFind(source);
	return it == g_strings.constEnd() ? source : it.value();
}

QString Tr(const char *english, std::initializer_list<std::pair<const char *, QString>> vars)
{
	QString text = Tr(english);
	for (const auto &var : vars)
		text.replace(QLatin1Char('{') + QString::fromUtf8(var.first) + QLatin1Char('}'), var.second);
	return text;
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

} // namespace rk
