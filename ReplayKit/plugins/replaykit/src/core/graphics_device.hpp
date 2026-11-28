// the obs d3d11 device as replaykit sees it: lost or working, and since when
#pragma once

#include <cstdint>

class QObject;
struct ID3D11Device;

namespace rk {

// the revision goes up by one at every loss and every recovery, so it is odd while the device is lost and a recovered device is told apart from the one before the loss
struct GraphicsState {
	std::uint64_t revision = 0;

	constexpr bool Lost() const { return revision % 2 != 0; }
	constexpr GraphicsState WithLoss(bool lost) const { return {revision + (lost != Lost())}; }
	constexpr bool RecoveredSince(GraphicsState previous) const { return !Lost() && revision > previous.revision; }
};

GraphicsState GraphicsStatus();
std::uint64_t GraphicsReadySince();
// the caller holds the obs graphics context; the device is borrowed
ID3D11Device *ObsGraphicsDevice();
void StartGraphicsWatch(QObject &context, void (*release)(), void (*changed)());
void StopGraphicsWatch();

} // namespace rk
