// Derived from upstream's rectangle.vert, text.vert and rectangle.frag, under their licence (BSD-2-Clause, The OpenBVE Project).
// OBNS: the 2D part of a frame drawn by Direct3D 11 (OBNS/RENDERERS.md): a quad per instance, the six vertices upstream's shaders
// make, as a list. Clip space is OpenGL's; as with the world, Y is turned over, so row 0 of the image is the bottom of the screen.

struct Input
{
	float4 position : POSITION;
	float4 coordinates : TEXCOORD0;
	float4 color : COLOR;
	float2 alphaTest : TEXCOORD1;
	float depth : DEPTHVALUE;
	float4 fog : FOGVALUE;
	uint vertex : SV_VertexID;
};

struct Output
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	float4 color : COLOR;
	float2 alphaTest : TEXCOORD1;
	float4 fog : TEXCOORD2;
};

Texture2D quadTexture : register(t0);
SamplerState quadSampler : register(s0);

Output VS(Input input)
{
	static const float2 corners[6] = { float2(0, 0), float2(1, 0), float2(1, 1), float2(0, 0), float2(0, 1), float2(1, 1) };
	float2 corner = corners[input.vertex];
	Output output;
	float2 position = input.position.xy + corner * input.position.zw;
	output.position = float4(position.x, -position.y, input.depth, 1.0);
	output.uv = input.coordinates.xy + corner * input.coordinates.zw;
	output.color = input.color;
	output.alphaTest = input.alphaTest;
	output.fog = input.fog;
	return output;
}

float4 PS(Output input) : SV_Target0
{
	float4 finalColor = quadTexture.Sample(quadSampler, input.uv) * input.color;

	// The numbers are those of OpenGL's alpha functions, as upstream's shader has them.
	if (input.alphaTest.x == 513) // Less
	{
		if (finalColor.a > input.alphaTest.y)
		{
			discard;
		}
	}
	else if (input.alphaTest.x == 514) // Equal
	{
		if (!(abs(finalColor.a - 1.0) < 0.00001))
		{
			discard;
		}
	}
	else if (input.alphaTest.x == 516) // Greater
	{
		if (finalColor.a <= input.alphaTest.y)
		{
			discard;
		}
	}

	return float4(lerp(finalColor.rgb, input.fog.rgb, input.fog.a), finalColor.a);
}
