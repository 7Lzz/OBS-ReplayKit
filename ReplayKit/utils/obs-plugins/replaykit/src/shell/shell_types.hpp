// views and metrics of the clean shell
#pragma once

namespace rk {

enum class ShellView { Home, Projector, Clips, Settings, Setup, ObsSettings };

// the title bar is as tall as the rail is wide, so the two frame the stage as one even L
inline constexpr int kShellTitleH = 56;
inline constexpr int kShellRailW = kShellTitleH;
// the strip along the top of the projector view is as tall as the tab bar of the clips view, hairline included
inline constexpr int kShellBarH = 51;
// radius of the rounded top left corner of the stage, where the title bar meets the rail
inline constexpr int kShellStageR = 10;
inline constexpr int kShellCtlW = 46;
// resize ring around a frameless window, in logical px; corners reach further along each edge so they are easy to hit
inline constexpr int kShellEdge = 5;
inline constexpr int kShellCorner = 12;

inline constexpr int kShellGrip = 18;

// the smallest the window can be made, in logical px: the stage is as big as the clips window at its smallest (850 x 620) with the rail and the title bar around it, so nothing the shell shows is ever squeezed out of its layout
inline constexpr int kShellMinW = 850 + kShellRailW;
inline constexpr int kShellMinH = 620 + kShellTitleH;

inline constexpr int kShellNavW = 232;

} // namespace rk
