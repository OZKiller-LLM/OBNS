// Derived from upstream's rectangle.vert, text.vert and rectangle.frag, under their licence (BSD-2-Clause, The OpenBVE Project).
// OBNS: the 2D part of a frame drawn by Metal (OBNS/RENDERERS.md): the six vertices upstream's shaders make, as a list. Clip space is
// OpenGL's; as with the world, Y is turned over, so row 0 of the image is the bottom of the screen. Compiled at the start.

#include <metal_stdlib>
using namespace metal;

struct Quad
{
	float4 position;    // left, top, width, height, in clip space
	float4 coordinates; // left, top, width, height, in the texture
	float4 color;
	float2 alphaTest;
	int textureIndex;
	int samplerIndex;
	float depth;
	float fogR, fogG, fogB, fogAmount;
};

struct PixelIn
{
	float4 position [[position]];
	float2 uv;
};

vertex PixelIn FrameVS(uint vertexId [[vertex_id]], constant Quad& quad [[buffer(0)]])
{
	const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(0, 1), float2(1, 1) };
	float2 corner = corners[vertexId];
	float2 position = quad.position.xy + corner * quad.position.zw;
	PixelIn output;
	output.position = float4(position.x, -position.y, quad.depth, 1.0);
	output.uv = quad.coordinates.xy + corner * quad.coordinates.zw;
	return output;
}

fragment float4 FramePS(PixelIn input [[stage_in]], constant Quad& quad [[buffer(0)]], texture2d<float> quadTexture [[texture(0)]], sampler quadSampler [[sampler(0)]])
{
	float4 finalColor = quadTexture.sample(quadSampler, input.uv) * quad.color;

	// The numbers are those of OpenGL's alpha functions, as upstream's shader has them.
	if (quad.alphaTest.x == 513) // Less
	{
		if (finalColor.a > quad.alphaTest.y)
		{
			discard_fragment();
		}
	}
	else if (quad.alphaTest.x == 514) // Equal
	{
		if (!(abs(finalColor.a - 1.0) < 0.00001))
		{
			discard_fragment();
		}
	}
	else if (quad.alphaTest.x == 516) // Greater
	{
		if (finalColor.a <= quad.alphaTest.y)
		{
			discard_fragment();
		}
	}

	return float4(mix(finalColor.rgb, float3(quad.fogR, quad.fogG, quad.fogB), quad.fogAmount), finalColor.a);
}
