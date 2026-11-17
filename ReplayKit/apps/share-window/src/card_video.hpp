// the video that loops under the card: the mp4 the helper converts, read into memory so the helper can replace the file at any time, decoded by media foundation a few frames ahead on a thread of its own, so the present loop only takes the frame that is due
#pragma once

#include <windows.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace rk::share {

struct VideoFrame {
	// bgrx rows of width * 4 bytes
	std::vector<uint8_t> pixels;
	uint32_t width = 0;
	uint32_t height = 0;
	// milliseconds from the start of play, counted on across loops
	ULONGLONG at = 0;
};

class CardVideo {
public:
	~CardVideo();
	// media foundation for the process; false on a windows without it, where no video plays
	static bool Startup();
	static void Shutdown();

	// reads the file and decodes its first frames; false when it is no video media foundation plays
	bool Open(const std::wstring &path);
	void Close();
	bool IsOpen() const { return thread.joinable(); }
	// play starts the clock at the first frame; rewind goes back to the beginning, decodes the first frames ahead and waits
	void Play(ULONGLONG now);
	void Rewind();
	// the newest frame that is due now, swapped into out so its buffer comes back for a later frame; false when no new frame is due
	bool TakeDue(ULONGLONG now, VideoFrame &out);
	uint64_t FramesDecoded() const { return decoded.load(); }

private:
	void Run(HGLOBAL data);

	std::thread thread;
	std::mutex lock;
	std::condition_variable wake;
	// zero until the decode thread knows whether the file plays, then 1 or -1
	int opened = 0;
	bool stopping = false;
	bool rewinding = false;
	bool playing = false;
	ULONGLONG playStartedAt = 0;
	std::deque<VideoFrame> ahead;
	std::vector<std::vector<uint8_t>> spare;
	std::atomic<uint64_t> decoded{0};
};

} // namespace rk::share
