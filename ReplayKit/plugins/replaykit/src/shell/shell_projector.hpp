// the projector view: the preview and the sources panel under it, and what the window knows about the preview
#pragma once

#include <functional>

namespace rk {

// once the window exists: no right click menu on the preview, and none of the notice obs puts in the stage when the preview is off
void ShellProjectorSetup();
// the sources panel comes up under the preview in this view and goes away in every other, ahead of the layout that reads what is left of the stage
void ShellProjectorPrepare();
// the view changed or the preview or its lock changed: the strip, the cover behind an off preview and the corner of the stage follow
void ShellProjectorApply();
// the tick of the shell: obs has other ways to change the preview (its own menus) that this window never hears about, so what the window shows follows the real state
void ShellProjectorRefresh();
// the preview switch of the strip: while the projector view is up the change goes through the same dip as a swap of views
void ShellProjectorSetPreview(bool on);
// what the preview will be once a change that is waiting for its dip has gone through, and what was chosen when none is waiting
bool ShellPreviewTarget();
// the lock of the strip: the scene and the overlay in the preview cannot be selected or moved while it holds
void ShellProjectorSetLocked(bool locked);
// a swap between the preview and the browser is a dip through the stage colour: the cover goes up over the stage (true when there is a stage on screen to cover), the view swaps under it, and the reveal takes it away again. the fade out ends in the callback, which runs at once when there is nothing on screen to dip whole is the whole of the window under the title bar and beside the rail, which a swap of views needs since the strip of the projector view and the sources panel come and go around the stage; false is the stage alone, for a change of the preview
bool ShellProjectorCover(bool whole);
void ShellProjectorFadeOut(std::function<void()> then, bool whole);
void ShellProjectorReveal();
// the stage changed size under a cover that is up
void ShellProjectorFollow();
// gone at once, for a swap that ends somewhere the cover has no business
void ShellProjectorDropCover();
// what obs shows right now
bool ShellPreviewIsOn();
// what was chosen: the same as what shows, except where obs has the preview out of the way by itself (a window in the tray, a dialog of obs over the stage)
bool ShellPreviewWanted();
// studio mode holds the preview on, and obs ignores the request to turn it off
bool ShellStudioModeOn();
// the lock of the preview, the same one as Lock Preview in the Edit menu of obs
bool ShellSceneLocked();

} // namespace rk
