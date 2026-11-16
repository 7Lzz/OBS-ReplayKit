#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>

#include <algorithm>
#include <cstring>

#include "log.hpp"
#include "renderer.hpp"

namespace rk::share {

namespace {

// typeless, since obs only opens the typeless 8 bit formats and makes both a plain and an srgb view of what it opens
constexpr DXGI_FORMAT kSharedFormat = DXGI_FORMAT_B8G8R8A8_TYPELESS;
constexpr DXGI_FORMAT kViewFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
// how dark the last frame gets under a card drawn over it
constexpr float kDimmedBrightness = 0.35f;
// the plain card while no card image was given
constexpr float kPlainCard[4] = {0.094f, 0.094f, 0.106f, 1.0f};

constexpr char kShader[] = R"(
Texture2D image : register(t0);
SamplerState linearSampler : register(s0);
cbuffer DrawConstants : register(b0) { float brightness; float keepAlpha; float2 unused; };
struct VsOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };
VsOut VsMain(uint id : SV_VertexID)
{
	VsOut result;
	float2 uv = float2((id << 1) & 2, id & 2);
	result.uv = uv;
	result.position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
	return result;
}
float4 PsMain(VsOut input) : SV_Target
{
	float4 color = image.Sample(linearSampler, input.uv);
	return float4(color.rgb * brightness, keepAlpha > 0.5 ? color.a : 1.0);
}
)";

struct DrawConstants {
	float brightness;
	float keepAlpha;
	float unused[2];
};

ComPtr<ID3DBlob> Compile(const char *entry, const char *profile)
{
	ComPtr<ID3DBlob> code;
	ComPtr<ID3DBlob> errors;
	HRESULT hr = D3DCompile(kShader, sizeof(kShader) - 1, "share", nullptr, nullptr, entry, profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
	if (FAILED(hr)) {
		Log("could not compile the %s shader: 0x%08lx %s", entry, hr, errors ? static_cast<const char *>(errors->GetBufferPointer()) : "");
		return nullptr;
	}
	return code;
}

ComPtr<IDXGIAdapter1> FindAdapter(IDXGIFactory1 *factory, uint64_t luid)
{
	ComPtr<IDXGIAdapter1> adapter;
	if (luid) {
		ComPtr<IDXGIFactory4> factory4;
		if (SUCCEEDED(factory->QueryInterface(IID_PPV_ARGS(&factory4)))) {
			LUID wanted{DWORD(luid & 0xffffffffu), LONG(luid >> 32)};
			if (SUCCEEDED(factory4->EnumAdapterByLuid(wanted, IID_PPV_ARGS(&adapter))))
				return adapter;
		}
		Log("the adapter obs renders on (luid %016llx) was not found, using the default one", luid);
	}
	factory->EnumAdapters1(0, &adapter);
	return adapter;
}

} // namespace

bool Renderer::Init(HWND windowHandle, uint64_t luid)
{
	Release();
	window = windowHandle;
	ComPtr<IDXGIFactory1> factory;
	HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
	if (FAILED(hr)) {
		Log("could not create the dxgi factory: 0x%08lx", hr);
		return false;
	}
	ComPtr<IDXGIAdapter1> adapter = FindAdapter(factory.Get(), luid);
	if (!adapter) {
		Log("no graphics adapter was found");
		return false;
	}
	DXGI_ADAPTER_DESC1 desc{};
	adapter->GetDesc1(&desc);
	adapterLuid = (uint64_t(uint32_t(desc.AdapterLuid.HighPart)) << 32) | desc.AdapterLuid.LowPart;

	const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0};
	hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, UINT(std::size(levels)), D3D11_SDK_VERSION,
			       &device, nullptr, &context);
	if (FAILED(hr)) {
		Log("could not create the d3d11 device: 0x%08lx", hr);
		return false;
	}
	Log("rendering on %ls (luid %016llx)", desc.Description, adapterLuid);

	ComPtr<IDXGIFactory2> factory2;
	if (FAILED(adapter->GetParent(IID_PPV_ARGS(&factory2)))) {
		Log("the dxgi factory has no flip model support");
		return false;
	}
	DXGI_SWAP_CHAIN_DESC1 chain{};
	chain.Format = kViewFormat;
	chain.SampleDesc.Count = 1;
	chain.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
	chain.BufferCount = 2;
	chain.Scaling = DXGI_SCALING_STRETCH;
	chain.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
	chain.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
	hr = factory2->CreateSwapChainForHwnd(device.Get(), window, &chain, nullptr, nullptr, &swapChain);
	if (FAILED(hr)) {
		Log("could not create the swap chain: 0x%08lx", hr);
		return false;
	}
	factory2->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER | DXGI_MWA_NO_WINDOW_CHANGES);
	return CreatePipeline() && CreateTargets();
}

void Renderer::Release()
{
	if (context) {
		context->ClearState();
		context->Flush();
	}
	cardView.Reset();
	frameView.Reset();
	frame.Reset();
	sharedMutex.Reset();
	shared.Reset();
	sharedHandle = 0;
	sharedWidth = 0;
	sharedHeight = 0;
	hasFrame = false;
	blendOn.Reset();
	constants.Reset();
	sampler.Reset();
	pixelShader.Reset();
	vertexShader.Reset();
	target.Reset();
	swapChain.Reset();
	context.Reset();
	device.Reset();
}

bool Renderer::CreatePipeline()
{
	ComPtr<ID3DBlob> vs = Compile("VsMain", "vs_4_0");
	ComPtr<ID3DBlob> ps = Compile("PsMain", "ps_4_0");
	if (!vs || !ps)
		return false;
	if (FAILED(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertexShader)) ||
	    FAILED(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &pixelShader))) {
		Log("could not create the shaders");
		return false;
	}
	D3D11_SAMPLER_DESC samplerDesc{};
	samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
	samplerDesc.AddressU = samplerDesc.AddressV = samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
	D3D11_BUFFER_DESC bufferDesc{};
	bufferDesc.ByteWidth = sizeof(DrawConstants);
	bufferDesc.Usage = D3D11_USAGE_DEFAULT;
	bufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
	D3D11_BLEND_DESC blendDesc{};
	blendDesc.RenderTarget[0].BlendEnable = TRUE;
	blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
	blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
	blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
	blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
	blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ZERO;
	blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
	blendDesc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
	if (FAILED(device->CreateSamplerState(&samplerDesc, &sampler)) || FAILED(device->CreateBuffer(&bufferDesc, nullptr, &constants)) ||
	    FAILED(device->CreateBlendState(&blendDesc, &blendOn))) {
		Log("could not create the draw states");
		return false;
	}
	return true;
}

bool Renderer::CreateTargets()
{
	target.Reset();
	ComPtr<ID3D11Texture2D> backBuffer;
	HRESULT hr = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
	if (FAILED(hr) || FAILED(device->CreateRenderTargetView(backBuffer.Get(), nullptr, &target))) {
		Log("could not get the swap chain buffer: 0x%08lx", hr);
		return false;
	}
	D3D11_TEXTURE2D_DESC desc{};
	backBuffer->GetDesc(&desc);
	targetWidth = desc.Width;
	targetHeight = desc.Height;
	return true;
}

bool Renderer::Resize()
{
	if (!swapChain)
		return false;
	RECT client{};
	GetClientRect(window, &client);
	UINT width = UINT(std::max<LONG>(1, client.right - client.left));
	UINT height = UINT(std::max<LONG>(1, client.bottom - client.top));
	if (width == targetWidth && height == targetHeight)
		return true;
	context->OMSetRenderTargets(0, nullptr, nullptr);
	target.Reset();
	HRESULT hr = swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
	if (FAILED(hr)) {
		Log("could not resize the swap chain to %ux%u: 0x%08lx", width, height, hr);
		return false;
	}
	return CreateTargets();
}

bool Renderer::CreateSharedTexture(uint32_t width, uint32_t height)
{
	sharedMutex.Reset();
	shared.Reset();
	frameView.Reset();
	frame.Reset();
	sharedHandle = 0;
	sharedWidth = sharedHeight = 0;
	hasFrame = false;
	if (!width || !height || width > 16384 || height > 16384) {
		Log("refused a shared texture of %ux%u", width, height);
		return false;
	}
	D3D11_TEXTURE2D_DESC desc{};
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = kSharedFormat;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_DEFAULT;
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
	desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
	HRESULT hr = device->CreateTexture2D(&desc, nullptr, &shared);
	if (FAILED(hr)) {
		Log("could not create the shared texture %ux%u: 0x%08lx", width, height, hr);
		return false;
	}
	ComPtr<IDXGIResource> resource;
	HANDLE handle = nullptr;
	if (FAILED(shared.As(&resource)) || FAILED(resource->GetSharedHandle(&handle)) || FAILED(shared.As(&sharedMutex))) {
		Log("could not share the texture");
		shared.Reset();
		return false;
	}
	desc.MiscFlags = 0;
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	D3D11_SHADER_RESOURCE_VIEW_DESC view{};
	view.Format = kViewFormat;
	view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
	view.Texture2D.MipLevels = 1;
	if (FAILED(device->CreateTexture2D(&desc, nullptr, &frame)) || FAILED(device->CreateShaderResourceView(frame.Get(), &view, &frameView))) {
		Log("could not create the frame copy");
		return false;
	}
	// legacy shared handles are 32 bit values that any process on the same adapter opens
	sharedHandle = uint32_t(uintptr_t(handle));
	sharedWidth = width;
	sharedHeight = height;
	Log("shared texture %ux%u ready (handle %08x)", width, height, sharedHandle);
	return true;
}

uint32_t Renderer::SharedFormat() const
{
	return uint32_t(kSharedFormat);
}

bool Renderer::TakeFrame()
{
	if (!sharedMutex)
		return false;
	HRESULT hr = sharedMutex->AcquireSync(0, 4);
	if (hr == static_cast<HRESULT>(WAIT_TIMEOUT))
		return false;
	if (FAILED(hr)) {
		Log("could not take the shared texture: 0x%08lx", hr);
		return false;
	}
	// an obs that died while drawing leaves the texture abandoned, which still hands it over
	context->CopyResource(frame.Get(), shared.Get());
	sharedMutex->ReleaseSync(0);
	hasFrame = true;
	return true;
}

bool Renderer::SetCard(const std::vector<uint8_t> &pixels, uint32_t width, uint32_t height, bool overLastFrame)
{
	cardView.Reset();
	if (!width || !height || pixels.size() != size_t(width) * height * 4)
		return false;
	D3D11_TEXTURE2D_DESC desc{};
	desc.Width = width;
	desc.Height = height;
	desc.MipLevels = 1;
	desc.ArraySize = 1;
	desc.Format = kViewFormat;
	desc.SampleDesc.Count = 1;
	desc.Usage = D3D11_USAGE_IMMUTABLE;
	desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 4, 0};
	ComPtr<ID3D11Texture2D> texture;
	if (FAILED(device->CreateTexture2D(&desc, &data, &texture)) || FAILED(device->CreateShaderResourceView(texture.Get(), nullptr, &cardView))) {
		Log("could not create the card texture %ux%u", width, height);
		return false;
	}
	cardWidth = width;
	cardHeight = height;
	cardOverLastFrame = overLastFrame;
	return true;
}

void Renderer::ClearCard()
{
	cardView.Reset();
	cardWidth = cardHeight = 0;
	cardOverLastFrame = false;
}

void Renderer::BeginFrame()
{
	const float black[4] = {0.0f, 0.0f, 0.0f, 1.0f};
	ID3D11RenderTargetView *targets[] = {target.Get()};
	context->OMSetRenderTargets(1, targets, nullptr);
	context->ClearRenderTargetView(target.Get(), black);
}

void Renderer::Draw(ID3D11ShaderResourceView *view, uint32_t width, uint32_t height, float brightness, bool blend)
{
	// letterboxed to the window like the projector, so a window of another shape never stretches the picture
	float scale = std::min(float(targetWidth) / float(width), float(targetHeight) / float(height));
	D3D11_VIEWPORT viewport{};
	viewport.Width = float(width) * scale;
	viewport.Height = float(height) * scale;
	viewport.TopLeftX = (float(targetWidth) - viewport.Width) / 2.0f;
	viewport.TopLeftY = (float(targetHeight) - viewport.Height) / 2.0f;
	viewport.MaxDepth = 1.0f;
	context->RSSetViewports(1, &viewport);

	DrawConstants values{brightness, blend ? 1.0f : 0.0f, {0.0f, 0.0f}};
	context->UpdateSubresource(constants.Get(), 0, nullptr, &values, 0, 0);
	ID3D11Buffer *buffers[] = {constants.Get()};
	ID3D11SamplerState *samplers[] = {sampler.Get()};
	ID3D11ShaderResourceView *views[] = {view};
	context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	context->IASetInputLayout(nullptr);
	context->VSSetShader(vertexShader.Get(), nullptr, 0);
	context->PSSetShader(pixelShader.Get(), nullptr, 0);
	context->PSSetConstantBuffers(0, 1, buffers);
	context->PSSetSamplers(0, 1, samplers);
	context->PSSetShaderResources(0, 1, views);
	const float factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
	context->OMSetBlendState(blend ? blendOn.Get() : nullptr, factor, 0xffffffffu);
	context->Draw(3, 0);
	ID3D11ShaderResourceView *none[] = {nullptr};
	context->PSSetShaderResources(0, 1, none);
}

HRESULT Renderer::Present()
{
	// one refresh at most per present, so presenting never runs ahead of what the screen and a capture of it take
	return swapChain->Present(1, 0);
}

HRESULT Renderer::PresentFrame()
{
	BeginFrame();
	if (hasFrame)
		Draw(frameView.Get(), sharedWidth, sharedHeight, 1.0f, false);
	return Present();
}

HRESULT Renderer::PresentCard()
{
	BeginFrame();
	if (!cardView) {
		context->ClearRenderTargetView(target.Get(), kPlainCard);
	} else if (cardOverLastFrame) {
		if (hasFrame)
			Draw(frameView.Get(), sharedWidth, sharedHeight, kDimmedBrightness, false);
		else
			context->ClearRenderTargetView(target.Get(), kPlainCard);
		Draw(cardView.Get(), cardWidth, cardHeight, 1.0f, true);
	} else {
		Draw(cardView.Get(), cardWidth, cardHeight, 1.0f, false);
	}
	return Present();
}

} // namespace rk::share
