// the game out of focus card: a source that covers the canvas with the frozen game picture, blurred and dimmed, while a game of the auto game list is open but not in front
#pragma once

#include <string>

namespace rk {

// registers the source kind; called once from obs_module_load
void RegisterFocusCard();
// keeps the card drawn in the theme and at the canvas size, and puts it into every scene that holds a managed capture; called once obs finished loading
void FocusCardStart();
// a scene collection loaded after startup gets its card too
void FocusCardEnsureScenes();
// takes the Show Game Out of Focus switch from a settings body of the helper; one it did not send leaves it as it was
void FocusCardApplySettings(const std::string &settingsBody);
// stops redrawing and copying the program once obs is closing; safe to call twice
void FocusCardStop();

} // namespace rk
