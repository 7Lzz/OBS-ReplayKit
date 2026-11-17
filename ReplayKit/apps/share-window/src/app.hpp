// the share window as a whole: shows live frames while obs sends them and the card while it is away, and closes itself once obs stayed away for the keep time, unless the helper said obs is coming back
#pragma once

#include <windows.h>

#include <memory>
#include <string>

#include "audio_out.hpp"
#include "card.hpp"
#include "control_pipe.hpp"
#include "renderer.hpp"
#include "security.hpp"
#include "shared_state.hpp"
#include "window.hpp"

namespace rk::share {

struct Options {
	std::wstring title;
	// how long the card stays once obs is gone; below zero keeps it until obs comes back
	long long keepSeconds = 0;
};

class ShareApp {
public:
	int Run(const Options &options);

private:
	// one round of the app after the messages: commands, a resize, then a frame or the card; the window runs it too while windows holds it in a move or its menu
	void Step(bool frameSignal);
	void Tick(bool frameSignal);
	bool RebuildRenderer(uint64_t luid);
	bool EnsureSharedTexture();
	std::string Execute(const std::string &line);
	std::string StateJson();

	UserOnlySecurity security;
	ShareWindow window;
	SharedStateHost shared;
	Renderer renderer;
	ControlPipe pipe;
	AudioOut audio;

	long long keepSeconds = 0;
	bool quit = false;
	CardImage card;
	bool cardOverLastFrame = false;
	std::wstring cardPath;
	std::wstring soundPath;

	ULONGLONG lastFrameAt = 0;
	ULONGLONG lastPresentAt = 0;
	ULONGLONG awaySince = 0;
	ULONGLONG holdUntil = 0;
	ULONGLONG showCardUntil = 0;
	ULONGLONG rebuildAt = 0;
	uint64_t lastFramesWritten = 0;
	bool live = false;

	uint64_t framesReceived = 0;
	uint64_t framesPresented = 0;
	uint64_t cardsPresented = 0;
	uint64_t presentsThisSecond = 0;
	uint64_t presentsPerSecond = 0;
	ULONGLONG secondStartedAt = 0;
};

} // namespace rk::share
