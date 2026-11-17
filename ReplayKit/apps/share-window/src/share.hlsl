// the shaders of the share window, compiled by fxc at build time with warnings as errors: the picture shader draws a texture, the ground shader a plain ground or the moving theme ground of share_motion.hlsl, which cmake makes from ui/_share-motion.html
Texture2D image : register(t0);
Texture2D swirlMask : register(t1);
SamplerState linearSampler : register(s0);
#define SampleSwirl(at) swirlMask.SampleLevel(linearSampler, at, 0.0).r
#include "share_motion.hlsl"

cbuffer DrawConstants : register(b0)
{
	float brightness;
	float keepAlpha;
	float opacity;
	float unused;
};
cbuffer GroundConstants : register(b1)
{
	float4 ground;
	float4 accent;
	// aspect of the card, seconds the motion has run, 1 for the moving ground, and the seed of the shines
	float4 motion;
};

struct VsOut {
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
};

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
	float3 color = ground.rgb;
	// a plain ground skips the motion, which a ?: would run for every pixel and then throw away
	[branch] if (motion.z > 0.5)
		color = ShareMotion(input.uv, motion.x, motion.y, motion.w, ground.rgb, accent.rgb);
	return float4(color, ground.a);
}
