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
	// the places older installs kept the state in, newest first; the installer and the helper move each file over once, so these only matter until then
	for (const QString &legacy : {root + QLatin1Char('/') + name, root + QStringLiteral("/scripts/") + name, root + QStringLiteral("/scripts/helper/") + name}) {
		if (QFileInfo::exists(legacy))
			return legacy;
	}
	QDir().mkpath(folder);
	return current;
}

} // namespace rk
