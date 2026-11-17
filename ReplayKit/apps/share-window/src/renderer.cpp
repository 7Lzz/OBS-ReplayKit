#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>

#include <algorithm>
#include <cmath>
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
constexpr float kPlainCard[3] = {0.094f, 0.094f, 0.106f};

// the picture shader draws a texture; the ground shader draws the theme card ground the way the card editor draws it: the ground, the shade circles, then over them the glow circles from the inside colour thru the soft band to a rim and the accent at the edge, each edge one target pixel soft
constexpr char kShader[] = R"(
Texture2D image : register(t0);
SamplerState linearSampler : register(s0);
cbuffer DrawConstants : register(b0) { float brightness; float keepAlpha; float opacity; float unused; };
cbuffer GroundConstants : register(b1)
{
	float4 ground;
	float4 accent;
	float4 mixes;
	float4 band;
	float4 origin;
	float4 circles[8];
};
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
	return float4(color.rgb * brightness, (keepAlpha > 0.5 ? color.a : 1.0) * opacity);
}
float4 PsGround(VsOut input) : SV_Target
{
	float2 at = (input.position.xy - origin.xy) * band.z;
	float3 color = ground.rgb;
	float3 shade = lerp(ground.rgb, float3(0, 0, 0), mixes.x);
	float3 inner = lerp(ground.rgb, accent.rgb, mixes.y);
	float3 rim = lerp(inner, accent.rgb, mixes.z);
	[loop] for (int i = 0; i < int(mixes.w); i++) {
		float4 circle = circles[i];
		float distanceIn = distance(at, circle.xy);
		float cover = saturate((circle.z - distanceIn) / band.z + 0.5);
		float3 fill = shade;
		if (circle.w > 0.5) {
			float t = distanceIn / circle.z;
			float soft = max(0.0, 1.0 - band.y / circle.z);
			float hard = max(soft, 1.0 - band.x / circle.z);
			fill = t < soft ? inner : t < hard ? lerp(inner, rim, (t - soft) / max(hard - soft, 1e-5)) : lerp(rim, accent.rgb, saturate((t - hard) / max(1.0 - hard, 1e-5)));
		}
		color = lerp(color, fill, cover);
	}
	return float4(color, ground.a);
}
)";

struct DrawConstants {
	float brightness;
	float keepAlpha;
	float opacity;
	float unused;
};

struct GroundConstants {
	float ground[4];
	float accent[4];
	// shade, glow, how far the rim goes toward the accent, and the number of circles
	float mixes[4];
	// rim and soft band in card pixels, then card pixels per target pixel
	float band[4];
	// the top left of the card in target pixels
	float origin[4];
	// x, y and radius in card pixels, then 1 for a glow and 0 for a shade
	float circles[kMaxBackdropCircles][4];
};

constexpr float kTau = 6.28318530718f;

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

// a picture of that size centred in the area: the whole of it with bars, or covering the area so its edges fall outside
D3D11_VIEWPORT Place(uint32_t width, uint32_t height, const D3D11_VIEWPORT &area, bool cover)
{
	float across = area.Width / float(width), down = area.Height / float(height);
	float scale = cover ? std::max(across, down) : std::min(across, down);
	D3D11_VIEWPORT viewport{};
	viewport.Width = float(width) * scale;
	viewport.Height = float(height) * scale;
	viewport.TopLeftX = area.TopLeftX + (area.Width - viewport.Width) / 2.0f;
	viewport.TopLeftY = area.TopLeftY + (area.Height - viewport.Height) / 2.0f;
	viewport.MaxDepth = 1.0f;
	return viewport;
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
	videoView.Reset();
	video.Reset();
	videoWidth = videoHeight = 0;
	frameView.Reset();
	frame.Reset();
	sharedMutex.Reset();
	shared.Reset();
	sharedHandle = 0;
	sharedWidth = 0;
	sharedHeight = 0;
	hasFrame = false;
	blendOn.Reset();
	clipOn.Reset();
	constants.Reset();
	groundConstants.Reset();
	groundShader.Reset();
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
	ComPtr<ID3DBlob> groundPs = Compile("PsGround", "ps_4_0");
	if (!vs || !ps || !groundPs)
		return false;
	if (FAILED(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &vertexShader)) ||
	    FAILED(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &pixelShader)) ||
	    FAILED(device->CreatePixelShader(groundPs->GetBufferPointer(), groundPs->GetBufferSize(), nullptr, &groundShader))) {
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
	D3D11_BUFFER_DESC groundDesc = bufferDesc;
	groundDesc.ByteWidth = sizeof(GroundConstants);
	D3D11_BLEND_DESC blendDesc{};
	blendDesc.RenderTarget[0].BlendEnable = TRUE;
	blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
	blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
	blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
	blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
	blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ZERO;
	blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
	blendDesc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
	// a filled video is cut to the card area so it never spills over the bars around the card
	D3D11_RASTERIZER_DESC rasterDesc{};
	rasterDesc.FillMode = D3D11_FILL_SOLID;
	rasterDesc.CullMode = D3D11_CULL_NONE;
	rasterDesc.DepthClipEnable = TRUE;
	rasterDesc.ScissorEnable = TRUE;
	if (FAILED(device->CreateSamplerState(&samplerDesc, &sampler)) || FAILED(device->CreateBuffer(&bufferDesc, nullptr, &constants)) ||
	    FAILED(device->CreateBuffer(&groundDesc, nullptr, &groundConstants)) || FAILED(device->CreateBlendState(&blendDesc, &blendOn)) ||
	    FAILED(device->CreateRasterizerState(&rasterDesc, &clipOn))) {
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

bool Renderer::SetCard(const std::vector<uint8_t> &pixels, uint32_t width, uint32_t height, CardMode mode)
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
	cardMode = mode;
	return true;
}

void Renderer::ClearCard()
{
	cardView.Reset();
	cardWidth = cardHeight = 0;
	cardMode = CardMode::Full;
}

bool Renderer::SetVideoFrame(const std::vector<uint8_t> &pixels, uint32_t width, uint32_t height)
{
	if (!device || !width || !height || pixels.size() != size_t(width) * height * 4)
		return false;
	if (!video || width != videoWidth || height != videoHeight) {
		videoView.Reset();
		video.Reset();
		videoWidth = videoHeight = 0;
		D3D11_TEXTURE2D_DESC desc{};
		desc.Width = width;
		desc.Height = height;
		desc.MipLevels = 1;
		desc.ArraySize = 1;
		desc.Format = kViewFormat;
		desc.SampleDesc.Count = 1;
		desc.Usage = D3D11_USAGE_DYNAMIC;
		desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		desc.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
		if (FAILED(device->CreateTexture2D(&desc, nullptr, &video)) || FAILED(device->CreateShaderResourceView(video.Get(), nullptr, &videoView))) {
			Log("could not create the video texture %ux%u", width, height);
			videoView.Reset();
			video.Reset();
			return false;
		}
		videoWidth = width;
		videoHeight = height;
	}
	D3D11_MAPPED_SUBRESOURCE mapped{};
	if (FAILED(context->Map(video.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
		return false;
	const size_t row = size_t(width) * 4;
	for (uint32_t y = 0; y < height; ++y)
		std::memcpy(static_cast<uint8_t *>(mapped.pData) + size_t(mapped.RowPitch) * y, pixels.data() + row * y, row);
	context->Unmap(video.Get(), 0);
	return true;
}

void Renderer::ClearVideo()
{
	videoView.Reset();
	video.Reset();
	videoWidth = videoHeight = 0;
}

void Renderer::SetBackdrop(const Backdrop &value)
{
	backdrop = value;
	if (backdrop.circles.size() > kMaxBackdropCircles)
		backdrop.circles.resize(kMaxBackdropCircles);
	hasBackdrop = true;
}

void Renderer::BeginFrame()
{
	const float black[4] = {0.0f, 0.0f, 0.0f, 1.0f};
	ID3D11RenderTargetView *targets[] = {target.Get()};
	context->OMSetRenderTargets(1, targets, nullptr);
	context->ClearRenderTargetView(target.Get(), black);
}

// the whole window, which every picture is letterboxed into like the projector did, so a window of another shape never stretches it
D3D11_VIEWPORT Renderer::Whole() const
{
	D3D11_VIEWPORT viewport{};
	viewport.Width = float(targetWidth);
	viewport.Height = float(targetHeight);
	viewport.MaxDepth = 1.0f;
	return viewport;
}

void Renderer::Draw(ID3D11ShaderResourceView *view, const D3D11_VIEWPORT &viewport, float brightness, bool keepAlpha, float opacity, const D3D11_RECT *clip)
{
	context->RSSetViewports(1, &viewport);
	context->RSSetState(clip ? clipOn.Get() : nullptr);
	if (clip)
		context->RSSetScissorRects(1, clip);

	const bool blend = keepAlpha || opacity < 1.0f;
	DrawConstants values{brightness, keepAlpha ? 1.0f : 0.0f, opacity, 0.0f};
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

// a plain ground in that colour, or the theme ground with its circles where they are after that many seconds
void Renderer::DrawGround(const D3D11_VIEWPORT &viewport, const float rgb[3], float opacity, const Backdrop *moving, double seconds)
{
	GroundConstants values{};
	values.ground[0] = rgb[0];
	values.ground[1] = rgb[1];
	values.ground[2] = rgb[2];
	values.ground[3] = opacity;
	values.band[2] = 1.0f;
	if (moving && cardWidth && cardHeight && viewport.Width > 0.0f) {
		const float width = float(cardWidth), height = float(cardHeight), unit = height / 1080.0f;
		std::copy(moving->accent, moving->accent + 3, values.accent);
		values.mixes[0] = moving->shade;
		values.mixes[1] = moving->glow;
		values.mixes[2] = 0.5f + moving->breathe * std::sin(kTau * float(std::fmod(seconds, double(moving->breatheSeconds))) / moving->breatheSeconds);
		values.mixes[3] = float(moving->circles.size());
		values.band[0] = moving->rim * unit;
		values.band[1] = moving->blur * unit;
		values.band[2] = width / viewport.Width;
		values.origin[0] = viewport.TopLeftX;
		values.origin[1] = viewport.TopLeftY;
		for (size_t i = 0; i < moving->circles.size(); ++i) {
			const Backdrop::Circle &circle = moving->circles[i];
			const double period = std::fabs(double(circle.period));
			const float turn = (circle.period < 0.0f ? -1.0f : 1.0f) * kTau * float(std::fmod(seconds, period) / period) + float(i);
			values.circles[i][0] = circle.x * width + moving->drift * width * std::cos(turn);
			values.circles[i][1] = circle.y * height + moving->drift * width * std::sin(turn);
			values.circles[i][2] = circle.radius * width;
			values.circles[i][3] = circle.glow ? 1.0f : 0.0f;
		}
	}
	context->UpdateSubresource(groundConstants.Get(), 0, nullptr, &values, 0, 0);
	context->RSSetViewports(1, &viewport);
	context->RSSetState(nullptr);
	ID3D11Buffer *buffers[] = {groundConstants.Get()};
	context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	context->IASetInputLayout(nullptr);
	context->VSSetShader(vertexShader.Get(), nullptr, 0);
	context->PSSetShader(groundShader.Get(), nullptr, 0);
	context->PSSetConstantBuffers(1, 1, buffers);
	const float factor[4] = {0.0f, 0.0f, 0.0f, 0.0f};
	context->OMSetBlendState(opacity < 1.0f ? blendOn.Get() : nullptr, factor, 0xffffffffu);
	context->Draw(3, 0);
}

// the card and what it is drawn over, at its opacity; its picture moves by the lift, and an image card stays put since its picture is the whole card
void Renderer::DrawCard(const CardShow &show)
{
	const float opacity = show.opacity;
	if (!cardView) {
		DrawGround(Whole(), kPlainCard, opacity, nullptr, 0.0);
		return;
	}
	const D3D11_VIEWPORT card = Place(cardWidth, cardHeight, Whole(), false);
	D3D11_VIEWPORT lifted = card;
	lifted.TopLeftY += show.lift * card.Height / 1080.0f;
	switch (cardMode) {
	case CardMode::Theme:
		DrawGround(card, hasBackdrop ? backdrop.ground : kPlainCard, opacity, hasBackdrop ? &backdrop : nullptr, show.seconds);
		Draw(cardView.Get(), lifted, 1.0f, true, opacity);
		break;
	case CardMode::OverLastFrame:
		if (hasFrame)
			Draw(frameView.Get(), Place(sharedWidth, sharedHeight, Whole(), false), kDimmedBrightness, false, opacity);
		else
			DrawGround(Whole(), kPlainCard, opacity, nullptr, 0.0);
		Draw(cardView.Get(), lifted, 1.0f, true, opacity);
		break;
	case CardMode::OverVideo:
		if (videoView) {
			const D3D11_RECT area{LONG(std::floor(card.TopLeftX)), LONG(std::floor(card.TopLeftY)), LONG(std::ceil(card.TopLeftX + card.Width)), LONG(std::ceil(card.TopLeftY + card.Height))};
			Draw(videoView.Get(), Place(videoWidth, videoHeight, card, videoFill), 1.0f, false, opacity, &area);
		} else {
			DrawGround(Whole(), kPlainCard, opacity, nullptr, 0.0);
		}
		Draw(cardView.Get(), lifted, 1.0f, true, opacity);
		break;
	case CardMode::Full:
		Draw(cardView.Get(), card, 1.0f, false, opacity);
		break;
	}
}

HRESULT Renderer::Present()
{
	// one refresh at most per present, so presenting never runs ahead of what the screen and a capture of it take
	return swapChain->Present(1, 0);
}

HRESULT Renderer::PresentFrame(const CardShow &card)
{
	BeginFrame();
	if (hasFrame)
		Draw(frameView.Get(), Place(sharedWidth, sharedHeight, Whole(), false), 1.0f, false, 1.0f);
	if (card.opacity > 0.0f)
		DrawCard(card);
	return Present();
}

HRESULT Renderer::PresentCard(const CardShow &card)
{
	BeginFrame();
	if (card.opacity < 1.0f && hasFrame)
		Draw(frameView.Get(), Place(sharedWidth, sharedHeight, Whole(), false), 1.0f, false, 1.0f);
	DrawCard(card);
	return Present();
}

} // namespace rk::share
