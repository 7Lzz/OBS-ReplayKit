#include <windows.h>
#include <wincodec.h>
#include <wrl/client.h>

#include "card.hpp"
#include "log.hpp"

namespace rk::share {

using Microsoft::WRL::ComPtr;

namespace {

HRESULT DecodePng(IWICImagingFactory *factory, IWICBitmapDecoder *decoder, CardImage &out)
{
	GUID container{};
	HRESULT hr = decoder->GetContainerFormat(&container);
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
	if (SUCCEEDED(hr)) {
		out.width = width;
		out.height = height;
	}
	return hr;
}

} // namespace

bool LoadCardImage(const std::wstring &path, CardImage &out)
{
	out = CardImage{};
	ComPtr<IWICImagingFactory> factory;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
	ComPtr<IWICBitmapDecoder> decoder;
	if (SUCCEEDED(hr))
		hr = factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder);
	if (SUCCEEDED(hr))
		hr = DecodePng(factory.Get(), decoder.Get(), out);
	if (SUCCEEDED(hr))
		return true;
	Log("could not read the card image %ls: 0x%08lx", path.c_str(), hr);
	out = CardImage{};
	return false;
}

bool LoadSwirlMask(CardImage &out)
{
	const HMODULE module = GetModuleHandleW(nullptr);
	const HRSRC resource = FindResourceW(module, L"OBS_SWIRL_MASK", RT_RCDATA);
	const DWORD size = resource ? SizeofResource(module, resource) : 0;
	const HGLOBAL data = size ? LoadResource(module, resource) : nullptr;
	auto *bytes = data ? static_cast<BYTE *>(LockResource(data)) : nullptr;
	if (!bytes)
		return false;

	ComPtr<IWICImagingFactory> factory;
	ComPtr<IWICStream> stream;
	ComPtr<IWICBitmapDecoder> decoder;
	HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
	if (SUCCEEDED(hr))
		hr = factory->CreateStream(&stream);
	if (SUCCEEDED(hr))
		hr = stream->InitializeFromMemory(bytes, size);
	if (SUCCEEDED(hr))
		hr = factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnLoad, &decoder);
	if (SUCCEEDED(hr))
		hr = DecodePng(factory.Get(), decoder.Get(), out);
	if (FAILED(hr)) {
		Log("could not decode the obs swirl mask: 0x%08lx", hr);
		out = CardImage{};
		return false;
	}
	return true;
}

} // namespace rk::share
