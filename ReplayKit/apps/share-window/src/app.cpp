#include <windows.h>

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <string>

#include "app.hpp"
#include "log.hpp"
#include "share_window_values.hpp"

namespace rk::share {

namespace {

// how long a live obs may send no frame before the card shows; obs keeps rendering while it runs, so this only covers a stall
constexpr ULONGLONG kFrameStaleMs = 1000;
// the card and a frozen frame are presented again this often, since discord gives up on a window that stops presenting
constexpr ULONGLONG kRepresentMs = 33;
// a hold never keeps the window past this, so an update that never brings obs back cannot leave it forever
constexpr long long kMaxHoldSeconds = 1800;

// milliseconds from the performance counter, since the tick count moves in 16 ms steps and would let every other present slip
ULONGLONG NowMs()
{
	static const LONGLONG frequency = [] {
		LARGE_INTEGER value;
		QueryPerformanceFrequency(&value);
		return value.QuadPart;
	}();
	LARGE_INTEGER counter;
	QueryPerformanceCounter(&counter);
	return ULONGLONG(counter.QuadPart / frequency * 1000 + counter.QuadPart % frequency * 1000 / frequency);
}

std::wstring Widen(const std::string &text)
{
	if (text.empty())
		return {};
	int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), nullptr, 0);
	if (length <= 0)
		return {};
	std::wstring out(size_t(length), L'\0');
	MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), int(text.size()), out.data(), length);
	return out;
}

std::string Ok()
{
	return "{\"ok\":true}";
}

std::string Fail(const char *message)
{
	return std::string("{\"ok\":false,\"message\":\"") + message + "\"}";
}

bool ParseInt(const std::string &text, long long low, long long high, long long &out)
{
	if (text.empty())
		return false;
	char *end = nullptr;
	long long value = std::strtoll(text.c_str(), &end, 10);
	if (!end || *end != '\0' || value < low || value > high)
		return false;
	out = value;
	return true;
}

// a colour as #rrggbb
bool ParseColor(const std::string &text, COLORREF &out)
{
	if (text.size() != 7 || text[0] != '#')
		return false;
	unsigned value = 0;
	for (size_t i = 1; i < text.size(); ++i) {
		char c = text[i];
		unsigned digit = c >= '0' && c <= '9' ? unsigned(c - '0') : c >= 'a' && c <= 'f' ? unsigned(c - 'a' + 10) : c >= 'A' && c <= 'F' ? unsigned(c - 'A' + 10) : 16u;
		if (digit > 15)
			return false;
		value = value * 16 + digit;
	}
	out = RGB((value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff);
	return true;
}

// an absolute local path to an existing file, the only kind the helper hands over
bool UsableFile(const std::wstring &path)
{
	if (path.size() < 4 || path[1] != L':' || (path[2] != L'\\' && path[2] != L'/'))
		return false;
	DWORD attributes = GetFileAttributesW(path.c_str());
	return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

} // namespace

int ShareApp::Run(const Options &options)
{
	keepSeconds = options.keepSeconds;
	if (!security.Ok()) {
		Log("could not build the user only security descriptor");
		return 1;
	}
	if (!window.Create(options.title))
		return 1;
	if (!shared.Create(security.Attributes(), window.Handle()))
		return 1;
	if (!renderer.Init(window.Handle(), 0))
		return 1;
	// layered only after the swap chain exists, the order the obs projector got it in
	if (!window.MakeSeeThrough())
		return 1;
	window.Show();
	if (!pipe.Start(security.Attributes()) || !audio.Start(shared.State(), shared.AudioEvent())) {
		Log("could not start the command pipe or the audio");
		return 1;
	}
	window.SetModalTick([this] { Step(false); });

	HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
	if (!timer)
		timer = CreateWaitableTimerW(nullptr, FALSE, nullptr);
	LARGE_INTEGER due{};
	due.QuadPart = -LONGLONG(kRepresentMs) * 10000;
	SetWaitableTimer(timer, &due, LONG(kRepresentMs), nullptr, nullptr, FALSE);
	Log("share window ready: hwnd %p, keep %lld s", static_cast<void *>(window.Handle()), keepSeconds);

	awaySince = NowMs();
	HANDLE waits[] = {shared.FrameEvent(), pipe.Ready(), timer};
	while (!quit) {
		DWORD result = MsgWaitForMultipleObjectsEx(3, waits, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
		MSG message;
		while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
			if (message.message == WM_QUIT)
				quit = true;
			TranslateMessage(&message);
			DispatchMessageW(&message);
		}
		if (window.CloseRequested() || !window.Handle())
			quit = true;
		Step(result == WAIT_OBJECT_0);
	}
	Log("share window closing");
	CancelWaitableTimer(timer);
	CloseHandle(timer);
	audio.Stop();
	pipe.Stop();
	renderer.Release();
	return 0;
}

void ShareApp::Step(bool frameSignal)
{
	pipe.Drain([this](const std::string &line) { return Execute(line); });
	if (quit)
		return;
	if (window.TakeResized() && !renderer.Resize())
		rebuildAt = NowMs();
	Tick(frameSignal);
}

bool ShareApp::RebuildRenderer(uint64_t luid)
{
	Log("rebuilding the renderer on luid %016llx", luid);
	if (!renderer.Init(window.Handle(), luid))
		return false;
	if (!card.pixels.empty())
		renderer.SetCard(card.pixels, card.width, card.height, cardOverLastFrame);
	// the old texture is gone with the old device, so the plugin is told to open the new one
	shared.PublishTexture(0, 0, 0, 0, renderer.AdapterLuid());
	return true;
}

bool ShareApp::EnsureSharedTexture()
{
	SharedState *state = shared.State();
	uint32_t width = state->wantWidth.load();
	uint32_t height = state->wantHeight.load();
	if (!width || !height)
		return false;
	if (renderer.SharedHandle() && renderer.SharedWidth() == width && renderer.SharedHeight() == height)
		return true;
	if (!renderer.CreateSharedTexture(width, height)) {
		shared.PublishTexture(0, 0, 0, 0, renderer.AdapterLuid());
		return false;
	}
	shared.PublishTexture(renderer.SharedHandle(), width, height, renderer.SharedFormat(), renderer.AdapterLuid());
	return true;
}

void ShareApp::Tick(bool frameSignal)
{
	const ULONGLONG now = NowMs();
	SharedState *state = shared.State();
	if (rebuildAt && now >= rebuildAt) {
		rebuildAt = RebuildRenderer(renderer.AdapterLuid()) ? 0 : now + 2000;
		if (rebuildAt)
			return;
	}

	uint32_t producer = shared.LiveProducer();
	uint32_t producerState = state->producerState.load();
	bool present = producer && producerState == ProducerLive;
	if (present) {
		uint64_t luid = state->producerAdapterLuid.load();
		if (luid && luid != renderer.AdapterLuid() && !RebuildRenderer(luid)) {
			rebuildAt = now + 2000;
			return;
		}
		EnsureSharedTexture();
		if (awaySince) {
			Log("obs (pid %lu) is sending frames", producer);
			awaySince = 0;
			holdUntil = 0;
		}
	} else if (!awaySince) {
		awaySince = now;
		Log("obs is away%s", holdUntil > now ? ", held for a restart or update" : "");
	}

	uint64_t written = state->framesWritten.load(std::memory_order_acquire);
	bool newFrame = false;
	if ((frameSignal || written != lastFramesWritten) && written != lastFramesWritten && renderer.TakeFrame()) {
		lastFramesWritten = written;
		lastFrameAt = now;
		framesReceived++;
		newFrame = true;
	}

	bool showLive = present && renderer.HasFrame() && now - lastFrameAt < kFrameStaleMs && now >= showCardUntil;
	if (showLive != live) {
		live = showLive;
		Log(live ? "showing live frames" : "showing the card");
	}
	audio.SetCardShowing(!live);

	if (!present && awaySince && holdUntil <= now && keepSeconds >= 0 && now - awaySince >= ULONGLONG(keepSeconds) * 1000) {
		Log("obs stayed away for %lld s, the keep time", keepSeconds);
		quit = true;
		return;
	}

	if (!(live && newFrame) && now - lastPresentAt < kRepresentMs)
		return;
	HRESULT hr = live ? renderer.PresentFrame() : renderer.PresentCard();
	lastPresentAt = now;
	if (Renderer::DeviceLost(hr)) {
		Log("the graphics device was lost (0x%08lx)", hr);
		rebuildAt = now;
		return;
	}
	(live ? framesPresented : cardsPresented)++;
	presentsThisSecond++;
	if (now - secondStartedAt >= 1000) {
		presentsPerSecond = presentsThisSecond;
		presentsThisSecond = 0;
		secondStartedAt = now;
	}
}

std::string ShareApp::Execute(const std::string &line)
{
	size_t space = line.find(' ');
	std::string verb = line.substr(0, space);
	std::string rest = space == std::string::npos ? std::string() : line.substr(space + 1);
	const ULONGLONG now = NowMs();

	if (verb == "HELLO") {
		char text[256];
		snprintf(text, sizeof(text), "{\"ok\":true,\"protocol\":%u,\"version\":\"%s\",\"pid\":%lu,\"hwnd\":%llu}", kShareWindowProtocolVersion, REPLAYKIT_VERSION,
			 GetCurrentProcessId(), static_cast<unsigned long long>(uintptr_t(window.Handle())));
		return text;
	}
	if (verb == "STATE")
		return StateJson();
	if (verb == "TITLE") {
		std::wstring title = Widen(rest);
		if (title.empty() || title.size() > 256)
			return Fail("the title is empty or too long");
		SetWindowTextW(window.Handle(), title.c_str());
		return Ok();
	}
	if (verb == "THEME") {
		// THEME <caption #rrggbb> <text #rrggbb> <dark 0|1>, the title bar the window has while the helper shows it for a look
		size_t first = rest.find(' '), second = first == std::string::npos ? first : rest.find(' ', first + 1);
		COLORREF caption = 0, text = 0;
		std::string dark = second == std::string::npos ? std::string() : rest.substr(second + 1);
		if (second == std::string::npos || !ParseColor(rest.substr(0, first), caption) || !ParseColor(rest.substr(first + 1, second - first - 1), text) || (dark != "0" && dark != "1"))
			return Fail("theme takes a caption colour, a text colour and 0 or 1 for dark");
		if (!window.SetTitleBarColors(caption, text, dark == "1"))
			return Fail("windows did not take the title bar colours");
		return Ok();
	}
	if (verb == "HOLD") {
		long long seconds = 0;
		if (!ParseInt(rest, 0, kMaxHoldSeconds, seconds))
			return Fail("hold takes seconds");
		holdUntil = now + ULONGLONG(seconds) * 1000;
		Log("held for %lld s", seconds);
		return Ok();
	}
	if (verb == "KEEP") {
		long long seconds = 0;
		if (!ParseInt(rest, -1, 86400, seconds))
			return Fail("keep takes seconds, or -1");
		keepSeconds = seconds;
		return Ok();
	}
	if (verb == "SHOWCARD") {
		long long seconds = 0;
		if (!ParseInt(rest, 0, 60, seconds))
			return Fail("showcard takes seconds");
		showCardUntil = now + ULONGLONG(seconds) * 1000;
		return Ok();
	}
	if (verb == "CARD") {
		// CARD none, or CARD full|over <png path>
		if (rest == "none") {
			card = CardImage{};
			cardPath.clear();
			renderer.ClearCard();
			return Ok();
		}
		size_t split = rest.find(' ');
		std::string mode = rest.substr(0, split);
		std::wstring path = split == std::string::npos ? std::wstring() : Widen(rest.substr(split + 1));
		if ((mode != "full" && mode != "over") || !UsableFile(path))
			return Fail("card takes none, or full or over and a png path");
		CardImage image;
		if (!LoadCardImage(path, image))
			return Fail("the card image could not be read");
		card = std::move(image);
		cardPath = path;
		cardOverLastFrame = mode == "over";
		if (!renderer.SetCard(card.pixels, card.width, card.height, cardOverLastFrame))
			return Fail("the card could not be shown");
		Log("card %ls (%s)", path.c_str(), mode.c_str());
		return Ok();
	}
	if (verb == "SOUND") {
		// SOUND none, or SOUND <volume 0-100> <wav path>
		if (rest == "none") {
			soundPath.clear();
			audio.SetCardSound(nullptr, 0.0f);
			return Ok();
		}
		size_t split = rest.find(' ');
		long long volume = 0;
		std::wstring path = split == std::string::npos ? std::wstring() : Widen(rest.substr(split + 1));
		if (!ParseInt(rest.substr(0, split), 0, 100, volume) || !UsableFile(path))
			return Fail("sound takes none, or a volume and a wav path");
		auto sound = std::make_shared<CardSound>();
		if (!LoadCardSound(path, *sound))
			return Fail("the sound could not be read");
		soundPath = path;
		audio.SetCardSound(sound, float(volume) / 100.0f);
		return Ok();
	}
	if (verb == "QUIT") {
		Log("asked to quit");
		quit = true;
		return Ok();
	}
	return Fail("unknown command");
}

std::string ShareApp::StateJson()
{
	SharedState *state = shared.State();
	const ULONGLONG now = NowMs();
	char text[1024];
	snprintf(text, sizeof(text),
		 "{\"ok\":true,\"state\":\"%s\",\"pid\":%lu,\"hwnd\":%llu,\"protocol\":%u,\"version\":\"%s\",\"generation\":%u,\"texture\":[%u,%u],"
		 "\"adapterLuid\":\"%016llx\",\"producerPid\":%u,\"producerState\":%u,\"framesWritten\":%llu,\"framesReceived\":%llu,\"framesPresented\":%llu,"
		 "\"cardsPresented\":%llu,\"presentsPerSecond\":%llu,\"awaySeconds\":%llu,\"holdSeconds\":%llu,\"keepSeconds\":%lld,\"card\":%s,\"cardOverLastFrame\":%s,"
		 "\"audio\":",
		 live ? "live" : "card", GetCurrentProcessId(), static_cast<unsigned long long>(uintptr_t(window.Handle())), kShareWindowProtocolVersion, REPLAYKIT_VERSION,
		 shared.Generation(), renderer.SharedWidth(), renderer.SharedHeight(), renderer.AdapterLuid(), state->producerPid.load(), state->producerState.load(),
		 state->framesWritten.load(), framesReceived, framesPresented, cardsPresented, presentsPerSecond, awaySince ? (now - awaySince) / 1000 : 0ull,
		 holdUntil > now ? (holdUntil - now) / 1000 : 0ull, keepSeconds, card.pixels.empty() ? "false" : "true", cardOverLastFrame ? "true" : "false");
	return std::string(text) + audio.StateJson() + "}";
}

} // namespace rk::share
