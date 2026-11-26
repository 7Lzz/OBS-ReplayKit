#pragma once

#include <cstdint>

class QObject;
struct ID3D11Device;

namespace rk {

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
