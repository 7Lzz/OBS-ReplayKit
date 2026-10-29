#pragma once

#include <QJsonObject>
#include <cstdint>

class QWidget;

namespace rk::IdleMemory {

void WindowChanged(QWidget *window);
void Stop();
QJsonObject Probe();
bool ShouldTrimVisible(std::uint32_t idleMs, std::uint32_t lastInputTick, std::uint32_t lastTrimInputTick, bool hasTrimmedInput, std::uint32_t idleThresholdMs);

}
