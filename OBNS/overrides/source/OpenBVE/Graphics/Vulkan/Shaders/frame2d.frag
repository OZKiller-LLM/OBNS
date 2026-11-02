// Derived from upstream's rectangle.frag, under its licence (BSD-2-Clause, The OpenBVE Project).
// OBNS: the 2D part of a frame drawn by Vulkan (OBNS/RENDERERS.md). The textures are the world layer's bindless array.
// Compiled with: glslang -V --target-env vulkan1.3 -o frame2d.frag.spv frame2d.frag
#version 450
#extension GL_EXT_nonuniform_qualifier : require

layout(push_constant) uniform Quad
{
	vec4 position;
	vec4 coordinates;
	vec4 color;
	vec2 alphaTest;
	int textureIndex;
	int samplerIndex;
	float depth;
	float fogR, fogG, fogB, fogAmount;
} quad;

layout(set = 0, binding = 4) uniform sampler samplers[4];
layout(set = 0, binding = 6) uniform texture2D textures[];

layout(location = 0) in vec2 textureCoord;
layout(location = 0) out vec4 fragColor;

void main()
{
	vec4 finalColor = texture(sampler2D(textures[quad.textureIndex], samplers[quad.samplerIndex]), textureCoord) * quad.color;

	// The numbers are those of OpenGL's alpha functions, as upstream's shader has them.
	if (quad.alphaTest.x == 513) // Less
	{
		if (finalColor.a > quad.alphaTest.y)
		{
			discard;
		}
	}
	else if (quad.alphaTest.x == 514) // Equal
	{
		if (!(abs(finalColor.a - 1.0) < 0.00001))
		{
			discard;
		}
	}
	else if (quad.alphaTest.x == 516) // Greater
	{
		if (finalColor.a <= quad.alphaTest.y)
		{
			discard;
		}
	}

	fragColor = vec4(mix(finalColor.rgb, vec3(quad.fogR, quad.fogG, quad.fogB), quad.fogAmount), finalColor.a);
}
