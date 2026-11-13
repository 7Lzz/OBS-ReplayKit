// where the saved state and the temp files of replaykit are on the disk, by the same rules as shared/StateLayout.cs and shared/TempLayout.cs
#pragma once

#include <QString>

namespace rk {

// the obs-replayKit folder in the obs config of the user
QString ReplayKitRootPath();
// the path to read and write a state file right now: the state folder, or an older place only while the file is still there and has not been moved yet
QString StateFilePath(const QString &name);
// the folder of replaykit under the temp folder of windows, and the scratch folder inside it
QString TempRootPath();
QString TempScratchPath();

} // namespace rk
