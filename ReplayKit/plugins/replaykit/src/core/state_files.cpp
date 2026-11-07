// where the saved state of replaykit is on the disk, by the same rule as shared/StateLayout.cs
#include "prelude.hpp"
#include "core/state_files.hpp"

#include <QDir>
#include <QFileInfo>

#include "core/shared_values.hpp"

namespace rk {

QString ReplayKitRootPath()
{
	return qEnvironmentVariable("APPDATA") + QStringLiteral("/obs-studio/obs-replayKit");
}

QString StateFilePath(const QString &name)
{
	const QString root = ReplayKitRootPath();
	const QString folder = root + QLatin1Char('/') + QString::fromLatin1(kStateFolder);
	const QString current = folder + QLatin1Char('/') + name;
	if (QFileInfo::exists(current))
		return current;
	// the places older installs kept this file in, from the table of StateLayout.cs; the installer and the helper move each file over once, so these only matter until then
	for (const StateOldPlaces &entry : kStateOldPlaces) {
		if (name != QLatin1String(entry.file))
			continue;
		for (const char *place : entry.places) {
			if (!place)
				break;
			const QString legacy = root + QLatin1Char('/') + QString::fromLatin1(place) + name;
			if (QFileInfo::exists(legacy))
				return legacy;
		}
		break;
	}
	QDir().mkpath(folder);
	return current;
}

} // namespace rk
