// where the saved state of replaykit is on the disk, by the same rule as shared/StateLayout.cs
#pragma once

#include <QString>

namespace rk {

// the obs-replayKit folder in the obs config of the user
QString ReplayKitRootPath();
// the path to read and write a state file right now: the state folder, or an older place only while the file is still there and has not been moved yet
QString StateFilePath(const QString &name);

} // namespace rk
