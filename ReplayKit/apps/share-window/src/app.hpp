// the share window as a whole: shows live frames while obs sends them and the card while it is away, and closes itself once obs stayed away for the keep time, unless the helper said obs is coming back
#pragma once

#include <windows.h>

#include <memory>
#include <string>

#include "audio_out.hpp"
#include "card.hpp"
#include "card_video.hpp"
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
	void ShowVideoFrame(ULONGLONG now);

	UserOnlySecurity security;
	ShareWindow window;
	SharedStateHost shared;
	Renderer renderer;
	ControlPipe pipe;
	AudioOut audio;

	long long keepSeconds = 0;
	bool quit = false;
	CardImage card;
	CardMode cardMode = CardMode::Full;
	std::wstring cardPath;
	std::wstring soundPath;

	bool mediaFoundation = false;
	CardVideo video;
	std::wstring videoPath;
	std::wstring videoStamp;
	bool videoFill = false;
	VideoFrame videoFrame;
	uint64_t videoFramesShown = 0;

	ULONGLONG lastFrameAt = 0;
	ULONGLONG lastPresentAt = 0;
	// when the card last came up and when live frames last came back, which the fades and the motion of the card count from
	ULONGLONG cardShownAt = 0;
	ULONGLONG liveSince = 0;
	// a moving card is presented once a frame of the rate obs records at, which the helper sends with FPS; Run starts it at the rate a still card is presented at
	double animateEveryMs = 0.0;
	double nextAnimateAt = 0.0;
	// when the loop next needs to run, which the one shot timer is armed for after every round
	double wakeInMs = 0.0;
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
