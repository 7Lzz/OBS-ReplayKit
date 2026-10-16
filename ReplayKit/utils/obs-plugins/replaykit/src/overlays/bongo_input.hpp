// raw relative mouse motion for the bongo cat source, read the way the plugin hook thread read it: one message-only window on a thread of its own for all sources, registered for raw mouse input only while one of them is showing with relative mouse on
#pragma once

#include <atomic>
#include <cstdint>

namespace rk {

class BongoInput {
public:
	BongoInput();
	~BongoInput();
	BongoInput(const BongoInput &) = delete;
	BongoInput &operator=(const BongoInput &) = delete;

	// called every video tick; registering and unregistering happens on the input thread so the graphics thread never waits on it
	void SetActive(bool active);
	void Take(int64_t &dx, int64_t &dy);
	void Add(int64_t dx, int64_t dy);
	bool Active() const { return active.load(); }

private:
	std::atomic<bool> active{false};
	std::atomic<int64_t> pendingX{0}, pendingY{0};
};

} // namespace rk
