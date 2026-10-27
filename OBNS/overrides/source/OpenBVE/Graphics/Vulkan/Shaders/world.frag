// OBNS: the Vulkan world layer's fragment shader (OBNS/VULKAN.md): upstream's default.frag (OpenBVE 1.14.1.2,
// BSD-2-Clause, Christopher Lees, S520, Aditiya Afrizal, The OpenBVE Project) without shadows, its uniforms read
// from the frame's buffers. Output alpha is coverage for the composite: opaque pipelines write 1.
// Compiled with: glslang -V --target-env vulkan1.3 -o world.frag.spv world.frag
#version 450
#extension GL_EXT_nonuniform_qualifier : require

struct Frame
{
	mat4 projection;
	vec4 lightPosition;
	vec4 lightAmbient;
	vec4 lightDiffuse;
	vec4 lightSpecular;
	vec4 lightModel;
	vec4 fogColor;
	float fogStart, fogEnd, fogDensity;
	int fogIsLinear;
	int isLight;
	int pad0, pad1, pad2;
};

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

layout(std430, set = 0, binding = 0) readonly buffer Frames { Frame frames[]; };
layout(std430, set = 0, binding = 2) readonly buffer Draws { Draw draws[]; };
layout(set = 0, binding = 4) uniform sampler samplers[4];
layout(set = 0, binding = 5) uniform texture2D textures[];

layout(push_constant) uniform Push { int frameIndex; } push;

// Whether the pipeline draws without blending, where coverage is complete.
layout(constant_id = 0) const bool opaqueCoverage = false;

layout(location = 0) in vec4 oViewPos;
layout(location = 1) in vec2 oUv;
layout(location = 2) in vec4 oColor;
layout(location = 3) in vec4 oLightResult;
layout(location = 4) flat in int oDraw;

layout(location = 0) out vec4 fragColor;

void main()
{
	Draw d = draws[oDraw];
	// Upstream culls OpenGL's front faces; the pipeline's front face is set so gl_FrontFacing is the same.
	if (d.cullFront != 0 && gl_FrontFacing)
	{
		discard;
	}

	vec4 texel = texture(sampler2D(textures[nonuniformEXT(d.textureIndex)], samplers[d.samplerIndex]), oUv);
	vec4 finalColor;
	if ((d.flags & 16) == 0)
	{
		finalColor = vec4(oColor.rgb, 1.0) * texel;
	}
	else
	{
		// Texture alpha disabled (MSTS shapes).
		finalColor = vec4(oColor.rgb, 1.0) * vec4(texel.rgb, 1.0);
	}

	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= d.brightness;
	}

	finalColor.a *= d.opacity;

	if (d.alphaFunction == 513) // Less
	{
		if (finalColor.a >= d.alphaReference) discard;
	}
	else if (d.alphaFunction == 514) // Equal
	{
		if (!(abs(finalColor.a - d.alphaReference) < 0.00001)) discard;
	}
	else if (d.alphaFunction == 516) // Greater
	{
		if (finalColor.a <= d.alphaReference) discard;
	}

	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= oLightResult.rgb;
		finalColor.a *= oLightResult.a;
	}
	else
	{
		finalColor *= oLightResult;
	}

	Frame f = frames[push.frameIndex];
	float fogFactor = 1.0;
	if (d.fog != 0)
	{
		if (f.fogIsLinear != 0)
		{
			fogFactor = clamp((f.fogEnd - length(oViewPos)) / (f.fogEnd - f.fogStart), 0.0, 1.0);
		}
		else
		{
			fogFactor = exp(-pow(f.fogDensity * (gl_FragCoord.z / gl_FragCoord.w), 2.0));
		}
	}

	fragColor = vec4(mix(f.fogColor.rgb, finalColor.rgb, fogFactor), opaqueCoverage ? 1.0 : finalColor.a);
}
