#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include "card.hpp"
#include "log.hpp"

namespace rk::share {

using Microsoft::WRL::ComPtr;

bool LoadCardImage(const std::wstring &path, CardImage &out)
{
	out = CardImage{};
	ComPtr<IWICImagingFactory> factory;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
	ComPtr<IWICBitmapDecoder> decoder;
	if (SUCCEEDED(hr))
		hr = factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder);
	GUID container{};
	if (SUCCEEDED(hr))
		hr = decoder->GetContainerFormat(&container);
	if (SUCCEEDED(hr) && container != GUID_ContainerFormatPng)
		hr = E_INVALIDARG;
	ComPtr<IWICBitmapFrameDecode> frame;
	if (SUCCEEDED(hr))
		hr = decoder->GetFrame(0, &frame);
	UINT width = 0, height = 0;
	if (SUCCEEDED(hr))
		hr = frame->GetSize(&width, &height);
	if (SUCCEEDED(hr) && (!width || !height || width > 8192 || height > 8192))
		hr = E_INVALIDARG;
	ComPtr<IWICFormatConverter> converter;
	if (SUCCEEDED(hr))
		hr = factory->CreateFormatConverter(&converter);
	if (SUCCEEDED(hr))
		hr = converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
	if (SUCCEEDED(hr)) {
		out.pixels.resize(size_t(width) * height * 4);
		hr = converter->CopyPixels(nullptr, width * 4, UINT(out.pixels.size()), out.pixels.data());
	}
	if (FAILED(hr)) {
		Log("could not read the card image %ls: 0x%08lx", path.c_str(), hr);
		out = CardImage{};
		return false;
	}
	out.width = width;
	out.height = height;
	return true;
}

} // namespace rk::share
