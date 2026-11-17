// draws the share window: the obs program frames the plugin copies into the shared texture, or the be right back card over its ground, letterboxed into the window the way the obs projector drew it
#pragma once

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <vector>

namespace rk::share {

using Microsoft::WRL::ComPtr;

// what the card is drawn over: nothing, since it has its own ground, the last frame dimmed, or the card video
enum class CardMode { Full, OverLastFrame, OverVideo };

class Renderer {
public:
	// a device on the adapter with that luid, or the default adapter for zero
	bool Init(HWND window, uint64_t adapterLuid);
	void Release();

	uint64_t AdapterLuid() const { return adapterLuid; }
	bool Resize();

	bool CreateSharedTexture(uint32_t width, uint32_t height);
	uint32_t SharedHandle() const { return sharedHandle; }
	uint32_t SharedWidth() const { return sharedWidth; }
	uint32_t SharedHeight() const { return sharedHeight; }
	uint32_t SharedFormat() const;

	// copies the shared texture into the last frame; false when the plugin held it or nothing was shared yet
	bool TakeFrame();
	bool HasFrame() const { return hasFrame; }

	// the card from straight bgra pixels; over the last frame or the video it is drawn with its alpha on top of them
	bool SetCard(const std::vector<uint8_t> &pixels, uint32_t width, uint32_t height, CardMode mode);
	void ClearCard();

	// the frame of the card video as bgrx rows; filling covers the card and cuts the edges of the video, otherwise the whole video shows with bars
	bool SetVideoFrame(const std::vector<uint8_t> &pixels, uint32_t width, uint32_t height);
	void SetVideoFill(bool fill) { videoFill = fill; }
	void ClearVideo();

	HRESULT PresentFrame();
	HRESULT PresentCard();
	static bool DeviceLost(HRESULT result) { return result == DXGI_ERROR_DEVICE_REMOVED || result == DXGI_ERROR_DEVICE_RESET; }

private:
	bool CreatePipeline();
	bool CreateTargets();
	void BeginFrame();
	D3D11_VIEWPORT Whole() const;
	void Draw(ID3D11ShaderResourceView *view, const D3D11_VIEWPORT &viewport, float brightness, bool blend, const D3D11_RECT *clip = nullptr);
	HRESULT Present();

	HWND window = nullptr;
	uint64_t adapterLuid = 0;
	ComPtr<ID3D11Device> device;
	ComPtr<ID3D11DeviceContext> context;
	ComPtr<IDXGISwapChain1> swapChain;
	ComPtr<ID3D11RenderTargetView> target;
	UINT targetWidth = 0;
	UINT targetHeight = 0;

	ComPtr<ID3D11VertexShader> vertexShader;
	ComPtr<ID3D11PixelShader> pixelShader;
	ComPtr<ID3D11SamplerState> sampler;
	ComPtr<ID3D11Buffer> constants;
	ComPtr<ID3D11BlendState> blendOn;
	ComPtr<ID3D11RasterizerState> clipOn;

	ComPtr<ID3D11Texture2D> shared;
	ComPtr<IDXGIKeyedMutex> sharedMutex;
	uint32_t sharedHandle = 0;
	uint32_t sharedWidth = 0;
	uint32_t sharedHeight = 0;

	ComPtr<ID3D11Texture2D> frame;
	ComPtr<ID3D11ShaderResourceView> frameView;
	bool hasFrame = false;

	ComPtr<ID3D11ShaderResourceView> cardView;
	uint32_t cardWidth = 0;
	uint32_t cardHeight = 0;
	CardMode cardMode = CardMode::Full;

	ComPtr<ID3D11Texture2D> video;
	ComPtr<ID3D11ShaderResourceView> videoView;
	uint32_t videoWidth = 0;
	uint32_t videoHeight = 0;
	bool videoFill = false;
};

} // namespace rk::share
