#include <windows.h>
#include <objbase.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cstring>

#include "card_video.hpp"
#include "log.hpp"

namespace rk::share {

using Microsoft::WRL::ComPtr;

namespace {

// frames decoded ahead of the clock, enough to ride out a slow frame without holding much memory at 1080p
constexpr size_t kAhead = 4;
// the helper caps its bitrate far below this, so a bigger file is no video it made
constexpr LONGLONG kMaxFileBytes = 128ll * 1024 * 1024;

// where the picture sits in the decoded buffer: the decoder pads its height to whole macroblocks and names the shown part as the display aperture
struct Layout {
	uint32_t width = 0;
	uint32_t height = 0;
	uint32_t left = 0;
	uint32_t top = 0;
	uint32_t bufferHeight = 0;
	LONG stride = 0;
};

HRESULT ReadLayout(IMFSourceReader *reader, Layout &out)
{
	ComPtr<IMFMediaType> type;
	HRESULT hr = reader->GetCurrentMediaType(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &type);
	UINT32 width = 0, height = 0;
	if (SUCCEEDED(hr))
		hr = MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &width, &height);
	if (SUCCEEDED(hr) && (!width || !height || width > 8192 || height > 8192))
		hr = MF_E_INVALIDMEDIATYPE;
	if (FAILED(hr))
		return hr;
	Layout layout;
	layout.width = width;
	layout.height = height;
	layout.bufferHeight = height;
	UINT32 stride = 0;
	if (SUCCEEDED(type->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride))) {
		layout.stride = LONG(stride);
	} else {
		LONG computed = 0;
		hr = MFGetStrideForBitmapInfoHeader(MFVideoFormat_RGB32.Data1, width, &computed);
		if (FAILED(hr))
			return hr;
		layout.stride = computed;
	}
	MFVideoArea area{};
	if (SUCCEEDED(type->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8 *>(&area), sizeof(area), nullptr))) {
		LONG left = area.OffsetX.value, top = area.OffsetY.value;
		if (left >= 0 && top >= 0 && area.Area.cx > 0 && area.Area.cy > 0 && left + area.Area.cx <= LONG(width) && top + area.Area.cy <= LONG(height)) {
			layout.left = uint32_t(left);
			layout.top = uint32_t(top);
			layout.width = uint32_t(area.Area.cx);
			layout.height = uint32_t(area.Area.cy);
		}
	}
	out = layout;
	return S_OK;
}

bool CopyFrame(IMFSample *sample, const Layout &layout, std::vector<uint8_t> &out)
{
	ComPtr<IMFMediaBuffer> buffer;
	if (FAILED(sample->ConvertToContiguousBuffer(&buffer)))
		return false;
	BYTE *first = nullptr;
	LONG pitch = 0;
	ComPtr<IMF2DBuffer> flat;
	bool twoD = SUCCEEDED(buffer.As(&flat)) && SUCCEEDED(flat->Lock2D(&first, &pitch));
	if (!twoD) {
		BYTE *data = nullptr;
		DWORD length = 0;
		if (FAILED(buffer->Lock(&data, nullptr, &length)))
			return false;
		pitch = layout.stride;
		size_t rowBytes = size_t(pitch < 0 ? -pitch : pitch);
		if (rowBytes < size_t(layout.left + layout.width) * 4 || length < rowBytes * layout.bufferHeight) {
			buffer->Unlock();
			return false;
		}
		// a negative stride is a picture stored bottom row first
		first = pitch < 0 ? data + rowBytes * (layout.bufferHeight - 1) : data;
	}
	const size_t row = size_t(layout.width) * 4;
	out.resize(row * layout.height);
	for (uint32_t y = 0; y < layout.height; ++y)
		std::memcpy(out.data() + row * y, first + LONG_PTR(layout.top + y) * pitch + size_t(layout.left) * 4, row);
	if (twoD)
		flat->Unlock2D();
	else
		buffer->Unlock();
	return true;
}

HRESULT SeekToStart(IMFSourceReader *reader)
{
	PROPVARIANT position;
	PropVariantInit(&position);
	position.vt = VT_I8;
	position.hVal.QuadPart = 0;
	HRESULT hr = reader->SetCurrentPosition(GUID_NULL, position);
	PropVariantClear(&position);
	return hr;
}

} // namespace

CardVideo::~CardVideo()
{
	Close();
}

bool CardVideo::Startup()
{
	HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
	if (FAILED(hr))
		Log("media foundation did not start (0x%08lx), so a card video does not play", hr);
	return SUCCEEDED(hr);
}

void CardVideo::Shutdown()
{
	MFShutdown();
}

bool CardVideo::Open(const std::wstring &path)
{
	Close();
	HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
	if (file == INVALID_HANDLE_VALUE) {
		Log("could not open the card video %ls (%lu)", path.c_str(), GetLastError());
		return false;
	}
	LARGE_INTEGER size{};
	HGLOBAL data = nullptr;
	bool read = GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= kMaxFileBytes;
	if (read) {
		data = GlobalAlloc(GMEM_MOVEABLE, SIZE_T(size.QuadPart));
		void *bytes = data ? GlobalLock(data) : nullptr;
		read = bytes != nullptr;
		for (LONGLONG done = 0; read && done < size.QuadPart;) {
			DWORD got = 0;
			DWORD want = DWORD(std::min<LONGLONG>(size.QuadPart - done, 1 << 20));
			read = ReadFile(file, static_cast<BYTE *>(bytes) + done, want, &got, nullptr) && got > 0;
			done += got;
		}
		if (bytes)
			GlobalUnlock(data);
	}
	CloseHandle(file);
	if (!read) {
		Log("could not read the card video %ls (%lld bytes)", path.c_str(), size.QuadPart);
		if (data)
			GlobalFree(data);
		return false;
	}

	{
		std::lock_guard<std::mutex> hold(lock);
		stopping = false;
		rewinding = false;
		playing = false;
		opened = 0;
		ahead.clear();
	}
	decoded = 0;
	thread = std::thread([this, data] { Run(data); });
	std::unique_lock<std::mutex> hold(lock);
	// the reader is made on the decode thread, which says once it knows whether the file plays; the wait stays under the six seconds the helper waits for an answer
	wake.wait_for(hold, std::chrono::seconds(4), [this] { return opened != 0; });
	bool ok = opened > 0;
	hold.unlock();
	if (!ok) {
		Close();
		return false;
	}
	Log("card video %ls loaded", path.c_str());
	return true;
}

void CardVideo::Close()
{
	if (!thread.joinable())
		return;
	{
		std::lock_guard<std::mutex> hold(lock);
		stopping = true;
	}
	wake.notify_all();
	thread.join();
	std::lock_guard<std::mutex> hold(lock);
	ahead.clear();
	spare.clear();
	playing = false;
}

void CardVideo::Play(ULONGLONG now)
{
	std::lock_guard<std::mutex> hold(lock);
	playing = true;
	playStartedAt = now;
}

void CardVideo::Rewind()
{
	{
		std::lock_guard<std::mutex> hold(lock);
		playing = false;
		rewinding = true;
		for (VideoFrame &frame : ahead)
			spare.push_back(std::move(frame.pixels));
		ahead.clear();
	}
	wake.notify_all();
}

bool CardVideo::TakeDue(ULONGLONG now, VideoFrame &out)
{
	bool took = false;
	{
		std::lock_guard<std::mutex> hold(lock);
		if (!playing)
			return false;
		const ULONGLONG elapsed = now - playStartedAt;
		while (!ahead.empty() && ahead.front().at <= elapsed) {
			if (!out.pixels.empty())
				spare.push_back(std::move(out.pixels));
			out = std::move(ahead.front());
			ahead.pop_front();
			took = true;
		}
	}
	if (took)
		wake.notify_all();
	return took;
}

void CardVideo::Run(HGLOBAL data)
{
	HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
	ComPtr<IStream> stream;
	HRESULT hr = CreateStreamOnHGlobal(data, TRUE, &stream);
	if (FAILED(hr))
		GlobalFree(data);
	ComPtr<IMFByteStream> bytes;
	if (SUCCEEDED(hr))
		hr = MFCreateMFByteStreamOnStream(stream.Get(), &bytes);
	// the bytes carry no file name, so the content type says which parser reads them
	ComPtr<IMFAttributes> bytesAttributes;
	if (SUCCEEDED(hr) && SUCCEEDED(bytes.As(&bytesAttributes)))
		bytesAttributes->SetString(MF_BYTESTREAM_CONTENT_TYPE, L"video/mp4");
	ComPtr<IMFAttributes> attributes;
	if (SUCCEEDED(hr))
		hr = MFCreateAttributes(&attributes, 1);
	// the reader converts the decoded yuv to bgrx, the format the card texture takes
	if (SUCCEEDED(hr))
		hr = attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
	ComPtr<IMFSourceReader> reader;
	if (SUCCEEDED(hr))
		hr = MFCreateSourceReaderFromByteStream(bytes.Get(), attributes.Get(), &reader);
	if (SUCCEEDED(hr))
		hr = reader->SetStreamSelection(DWORD(MF_SOURCE_READER_ALL_STREAMS), FALSE);
	if (SUCCEEDED(hr))
		hr = reader->SetStreamSelection(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE);
	ComPtr<IMFMediaType> wanted;
	if (SUCCEEDED(hr))
		hr = MFCreateMediaType(&wanted);
	if (SUCCEEDED(hr))
		hr = wanted->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	if (SUCCEEDED(hr))
		hr = wanted->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
	if (SUCCEEDED(hr))
		hr = reader->SetCurrentMediaType(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, wanted.Get());
	Layout layout;
	if (SUCCEEDED(hr))
		hr = ReadLayout(reader.Get(), layout);
	// one loop is the length of the file, the length the helper made its sound, so the two loop together
	LONGLONG length = 0;
	PROPVARIANT value;
	PropVariantInit(&value);
	if (SUCCEEDED(hr) && SUCCEEDED(reader->GetPresentationAttribute(DWORD(MF_SOURCE_READER_MEDIASOURCE), MF_PD_DURATION, &value)) && value.vt == VT_UI8)
		length = LONGLONG(value.uhVal.QuadPart);
	PropVariantClear(&value);
	if (FAILED(hr))
		Log("the card video cannot be played: 0x%08lx", hr);
	else
		Log("card video %ux%u, %.2f s", layout.width, layout.height, double(length) / 1e7);
	{
		std::lock_guard<std::mutex> hold(lock);
		opened = SUCCEEDED(hr) ? 1 : -1;
	}
	wake.notify_all();

	LONGLONG offset = 0;
	LONGLONG lastEnd = 0;
	bool frameThisLoop = false;
	bool seek = false;
	while (SUCCEEDED(hr)) {
		std::vector<uint8_t> pixels;
		{
			std::unique_lock<std::mutex> hold(lock);
			wake.wait(hold, [this] { return stopping || rewinding || ahead.size() < kAhead; });
			if (stopping)
				break;
			if (rewinding) {
				rewinding = false;
				seek = true;
			}
			if (!spare.empty()) {
				pixels = std::move(spare.back());
				spare.pop_back();
			}
		}
		if (seek) {
			seek = false;
			offset = 0;
			lastEnd = 0;
			frameThisLoop = false;
			if (FAILED(hr = SeekToStart(reader.Get())))
				break;
		}
		DWORD flags = 0;
		LONGLONG time = 0;
		ComPtr<IMFSample> sample;
		hr = reader->ReadSample(DWORD(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, nullptr, &flags, &time, &sample);
		if (SUCCEEDED(hr) && (flags & MF_SOURCE_READERF_ERROR))
			hr = E_FAIL;
		if (FAILED(hr))
			break;
		if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) && FAILED(hr = ReadLayout(reader.Get(), layout)))
			break;
		if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
			// a loop that gave no frame would spin here forever
			if (!frameThisLoop) {
				hr = E_FAIL;
				break;
			}
			offset += length > 0 ? length : lastEnd;
			lastEnd = 0;
			frameThisLoop = false;
			if (FAILED(hr = SeekToStart(reader.Get())))
				break;
			continue;
		}
		if (!sample)
			continue;
		LONGLONG sampleLength = 0;
		sample->GetSampleDuration(&sampleLength);
		lastEnd = std::max(lastEnd, time + sampleLength);
		VideoFrame frame;
		frame.pixels = std::move(pixels);
		if (!CopyFrame(sample.Get(), layout, frame.pixels)) {
			hr = E_UNEXPECTED;
			break;
		}
		frame.width = layout.width;
		frame.height = layout.height;
		frame.at = ULONGLONG(std::max<LONGLONG>(0, offset + time) / 10000);
		frameThisLoop = true;
		decoded++;
		std::lock_guard<std::mutex> hold(lock);
		// a rewind that came in while this frame was decoded makes it a frame of the old place
		if (rewinding || stopping)
			spare.push_back(std::move(frame.pixels));
		else
			ahead.push_back(std::move(frame));
	}
	if (FAILED(hr))
		Log("the card video stopped: 0x%08lx", hr);
	reader.Reset();
	bytes.Reset();
	stream.Reset();
	if (SUCCEEDED(co))
		CoUninitialize();
}

} // namespace rk::share
