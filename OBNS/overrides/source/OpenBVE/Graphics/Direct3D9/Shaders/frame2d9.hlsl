// Derived from upstream's rectangle.vert, text.vert and rectangle.frag, under their licence (BSD-2-Clause, The OpenBVE Project).
// OBNS: the 2D part of a frame drawn by Direct3D 9 (OBNS/RENDERERS.md), in shader model 3: six vertices a quad, as upstream's shaders
// make. Clip space is OpenGL's; as with the world, Y is turned over, so row 0 of the image is the bottom of the screen.

float4 pixelOffset : register(c0);   // a pixel in clip space (1 / width, 1 / height): Direct3D 9's pixel centres

sampler2D quadTexture : register(s0);

struct Input
{
	float3 position : POSITION;
	float2 uv : TEXCOORD0;
	float4 color : COLOR0;
	float2 alphaTest : TEXCOORD1;
	float4 fog : TEXCOORD2;
};

struct Output
{
	float4 position : POSITION;
	float2 uv : TEXCOORD0;
	float4 color : COLOR0;
	float2 alphaTest : TEXCOORD1;
	float4 fog : TEXCOORD2;
};

Output VS(Input input)
{
	Output output;
	output.position = float4(input.position.x - pixelOffset.x, -input.position.y + pixelOffset.y, input.position.z, 1.0);
	output.uv = input.uv;
	output.color = input.color;
	output.alphaTest = input.alphaTest;
	output.fog = input.fog;
	return output;
}

float4 PS(Output input) : COLOR0
{
	float4 finalColor = tex2D(quadTexture, input.uv) * input.color;

	// The numbers are those of OpenGL's alpha functions, as upstream's shader has them.
	if (abs(input.alphaTest.x - 513.0) < 0.5) // Less
	{
		clip(input.alphaTest.y - finalColor.a);
	}
	else if (abs(input.alphaTest.x - 514.0) < 0.5) // Equal
	{
		clip(abs(finalColor.a - 1.0) < 0.00001 ? 1.0 : -1.0);
	}
	else if (abs(input.alphaTest.x - 516.0) < 0.5) // Greater
	{
		clip(finalColor.a > input.alphaTest.y ? 1.0 : -1.0);
	}

	return float4(lerp(finalColor.rgb, input.fog.rgb, input.fog.a), finalColor.a);
}
