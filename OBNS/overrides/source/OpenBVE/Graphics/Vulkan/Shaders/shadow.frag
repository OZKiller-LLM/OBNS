// OBNS: the Vulkan world layer's shadow pass (OBNS/VULKAN.md): upstream's shadow_depth.frag (OpenBVE 1.14.1.2,
// BSD-2-Clause, Aditiya Afrizal, The OpenBVE Project): a fragment casts unless its alpha (material times texture) is
// at most 0.5, or its material casts none.
// Compiled with: glslang -V --target-env vulkan1.3 -o shadow.frag.spv shadow.frag
#version 450
#extension GL_EXT_nonuniform_qualifier : require

struct Draw
{
	vec4 materialColor;
	vec4 materialSpecular;
	vec4 materialEmission;
	int flags;
	float brightness;
	float opacity;
	int textureIndex;
	int samplerIndex;
	int alphaFunction;
	float alphaReference;
	int objectIndex;
	int fog;
	int cullFront;
	int pad0, pad1;
};

layout(std430, set = 0, binding = 2) readonly buffer Draws { Draw draws[]; };
layout(set = 0, binding = 4) uniform sampler samplers[4];
layout(set = 0, binding = 6) uniform texture2D textures[];

layout(location = 0) in vec2 vUv;
layout(location = 1) flat in int vDraw;

void main()
{
	Draw d = draws[vDraw];
	float alpha = d.materialColor.a * texture(sampler2D(textures[nonuniformEXT(d.textureIndex)], samplers[d.samplerIndex]), vUv).a;
	if (alpha <= 0.5 || (d.flags & 32) != 0)
	{
		discard;
	}
}
