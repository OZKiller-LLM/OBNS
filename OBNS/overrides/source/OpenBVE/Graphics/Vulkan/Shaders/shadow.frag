// OBNS: the Vulkan world layer's shadow pass (OBNS/VULKAN.md): upstream's shadow_depth.frag (OpenBVE 1.14.1.2,
// BSD-2-Clause, Aditiya Afrizal, The OpenBVE Project): a fragment casts unless its alpha (material times texture) is
// at most 0.5, or its material casts none.
// Compiled with: glslang -V --target-env vulkan1.3 -o shadow.frag.spv shadow.frag
// and the paged variant: glslang -V --target-env vulkan1.3 -DOBNS_PAGED -o shadow.paged.frag.spv shadow.frag
// and the core Vulkan 1.0 tier's: glslang -V --target-env vulkan1.0 -DOBNS_PAGED -o shadow.vk10.frag.spv shadow.frag
#version 450
#ifndef OBNS_PAGED
#extension GL_EXT_nonuniform_qualifier : require
#endif

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
#ifdef OBNS_PAGED
// The paged variant, for devices without descriptor indexing: the draw's page of textures (set 1), as large as
// the device allows; a draw's texture is the same for all its fragments (dynamically uniform), as core Vulkan needs.
layout(constant_id = 1) const int texturePage = 64;
layout(set = 1, binding = 0) uniform texture2D textures[texturePage];
#define TEXTURE(i) textures[(i) % texturePage]
#else
layout(set = 0, binding = 6) uniform texture2D textures[];
#define TEXTURE(i) textures[nonuniformEXT(i)]
#endif

layout(location = 0) in vec2 vUv;
layout(location = 1) flat in int vDraw;

void main()
{
	Draw d = draws[vDraw];
	float alpha = d.materialColor.a * texture(sampler2D(TEXTURE(d.textureIndex), samplers[d.samplerIndex]), vUv).a;
	if (alpha <= 0.5 || (d.flags & 32) != 0)
	{
		discard;
	}
}
