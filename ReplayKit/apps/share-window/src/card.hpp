// reads the card image the helper renders, a png with its text already drawn in, so the window itself never draws text
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace rk::share {

struct CardImage {
	std::vector<uint8_t> pixels;
	uint32_t width = 0;
	uint32_t height = 0;
};

// straight bgra pixels of a png no larger than 8k on a side, or false when the file is missing or is not one
bool LoadCardImage(const std::wstring &path, CardImage &out);
// the obs swirl mask the exe carries as a resource, decoded the same way; false when it is missing or no png
bool LoadSwirlMask(CardImage &out);

} // namespace rk::share
