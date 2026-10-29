// OBNS: the Vulkan world layer's fragment shader (OBNS/VULKAN.md): upstream's default.frag (OpenBVE 1.14.1.2,
// BSD-2-Clause, Christopher Lees, S520, Aditiya Afrizal, The OpenBVE Project), its uniforms read from the frame's
// buffers; its shadow maps are one array, a layer per cascade. Output alpha is coverage for the composite: opaque pipelines write 1.
// Compiled with: glslang -V --target-env vulkan1.3 -o world.frag.spv world.frag
// and the paged variant: glslang -V --target-env vulkan1.3 -DOBNS_PAGED -o world.paged.frag.spv world.frag
#version 450
#ifndef OBNS_PAGED
#extension GL_EXT_nonuniform_qualifier : require
#endif

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
	int shadowEnabled;       // the world layer's, with shadows on
	int shadowCascades;
	int shadowSmooth;
	mat4 inverseView;        // view to camera-relative world (the shadows' space)
	mat4 lightSpace[4];      // upstream's cascades' light-space matrices
	vec4 shadowSplit;        // where each cascade ends (view depth)
	vec4 shadowBias;
	vec4 shadowNormalBias;   // texels
	vec4 shadowTexelSize;    // a texel's size in the world, per cascade
	float shadowStrength;
	float shadowFilterRadius;
	float pad0, pad1;
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
layout(set = 0, binding = 5) uniform sampler2DArrayShadow shadowMap;
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

layout(push_constant) uniform Push { int frameIndex; } push;

// Whether the pipeline draws without blending, where coverage is complete.
layout(constant_id = 0) const bool opaqueCoverage = false;

layout(location = 0) in vec4 oViewPos;
layout(location = 1) in vec2 oUv;
layout(location = 2) in vec4 oColor;
layout(location = 3) in vec4 oLightResult;
layout(location = 4) flat in int oDraw;
layout(location = 5) in vec3 vNormal;
layout(location = 6) in vec4 vPosLightSpace0;
layout(location = 7) in vec4 vPosLightSpace1;
layout(location = 8) in vec4 vPosLightSpace2;
layout(location = 9) in vec4 vPosLightSpace3;

layout(location = 0) out vec4 fragColor;

// --- shadows: upstream's CalculateShadowFactor and its helpers, with the cascades as an array's layers ---

const float SHADOW_TWO_PI = 6.28318530718;
const float SHADOW_MIN_RADIUS = 0.5;
const float SHADOW_MAX_RADIUS = 3.0;
const float SHADOW_MIN_BLEND = 5.0;
const float SHADOW_MAX_BLEND = 25.0;
const float SHADOW_BLEND_FRACTION = 0.1;
const float SHADOW_BIAS_GUARD_TEXELS = 4.0;
const float SHADOW_BIAS_EPSILON = 0.00002;

float interleavedGradientNoise(vec2 p)
{
	const vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
	return fract(magic.z * fract(dot(p, magic.xy)));
}

const vec2 VOGEL_DISK_5[5] = vec2[5](
	vec2(0.31622777, 0.0),
	vec2(-0.40387357, 0.36998127),
	vec2(0.06181932, -0.70439930),
	vec2(0.50905647, 0.66397403),
	vec2(-0.93418124, -0.16524351)
);

vec2 vogelDiskSample(int i, float cosPhi, float sinPhi)
{
	vec2 o = VOGEL_DISK_5[i];
	return vec2(o.x * cosPhi - o.y * sinPhi, o.x * sinPhi + o.y * cosPhi);
}

float GetCascadeShadowFactor(Frame f, int cascade, vec4 posLightSpace, float bias, float normalBias)
{
	vec3 projCoords = posLightSpace.xyz / posLightSpace.w;
	projCoords = projCoords * 0.5 + 0.5;
	if (projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0 || projCoords.z < 0.0 || projCoords.z > 1.0)
	{
		return 1.0;
	}

	vec3 lightDir = f.lightPosition.xyz;
	float nLen2 = dot(vNormal, vNormal);
	vec3 normal = nLen2 < 1e-10 ? lightDir : vNormal * inversesqrt(nLen2);
	float slope = clamp(1.0 - dot(normal, lightDir), 0.0, 1.0);
	float slopeScale = clamp(normalBias, 0.0, 1.5);
	float activeBias = bias * (1.0 + slope * slopeScale);

	vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0).xy);
	float radiusScaled = f.shadowSmooth != 0 ? clamp(f.shadowFilterRadius, SHADOW_MIN_RADIUS, SHADOW_MAX_RADIUS) : SHADOW_MIN_RADIUS;
	float biasedDepth = projCoords.z - activeBias * (1.0 + (radiusScaled - SHADOW_MIN_RADIUS) * 0.25);
	biasedDepth = max(biasedDepth, projCoords.z - (bias * SHADOW_BIAS_GUARD_TEXELS + SHADOW_BIAS_EPSILON));
	float layer = float(cascade);

	if (f.shadowSmooth != 0)
	{
		float phi = interleavedGradientNoise(gl_FragCoord.xy) * SHADOW_TWO_PI;
		float cosPhi = cos(phi);
		float sinPhi = sin(phi);
		float shadow = 0.0;
		for (int i = 0; i < 5; ++i)
		{
			vec2 offset = vogelDiskSample(i, cosPhi, sinPhi) * texelSize * radiusScaled;
			shadow += texture(shadowMap, vec4(projCoords.xy + offset, layer, biasedDepth));
		}

		return shadow * 0.2;
	}

	float shadow = 0.0;
	shadow += texture(shadowMap, vec4(projCoords.xy + vec2(-0.5, -0.5) * texelSize, layer, biasedDepth));
	shadow += texture(shadowMap, vec4(projCoords.xy + vec2( 0.5, -0.5) * texelSize, layer, biasedDepth));
	shadow += texture(shadowMap, vec4(projCoords.xy + vec2(-0.5,  0.5) * texelSize, layer, biasedDepth));
	shadow += texture(shadowMap, vec4(projCoords.xy + vec2( 0.5,  0.5) * texelSize, layer, biasedDepth));
	return shadow * 0.25;
}

float SampleCascadeByIndex(Frame f, int idx)
{
	if (idx == 0) return GetCascadeShadowFactor(f, 0, vPosLightSpace0, f.shadowBias.x, f.shadowNormalBias.x);
	if (idx == 1) return GetCascadeShadowFactor(f, 1, vPosLightSpace1, f.shadowBias.y, f.shadowNormalBias.y);
	if (idx == 2) return GetCascadeShadowFactor(f, 2, vPosLightSpace2, f.shadowBias.z, f.shadowNormalBias.z);
	if (idx == 3) return GetCascadeShadowFactor(f, 3, vPosLightSpace3, f.shadowBias.w, f.shadowNormalBias.w);
	return 1.0;
}

float CalculateShadowFactor(Frame f)
{
	if (f.shadowEnabled == 0 || f.shadowStrength <= 0.0) return 1.0;
	float vViewDepth = abs(oViewPos.z);
	float shadow = 1.0;
	int cascadeCount = f.shadowCascades;
	for (int i = 0; i < cascadeCount; i++)
	{
		float splitDist = f.shadowSplit[i];
		float blendRange = clamp(splitDist * SHADOW_BLEND_FRACTION, SHADOW_MIN_BLEND, SHADOW_MAX_BLEND);
		if (vViewDepth < splitDist)
		{
			shadow = SampleCascadeByIndex(f, i);
			if (i < cascadeCount - 1)
			{
				float blendStart = splitDist - blendRange;
				if (vViewDepth > blendStart)
				{
					float nextShadow = SampleCascadeByIndex(f, i + 1);
					shadow = mix(shadow, nextShadow, smoothstep(blendStart, splitDist, vViewDepth));
				}
			}
			else
			{
				float fadeStart = splitDist - blendRange * 2.0;
				if (vViewDepth > fadeStart)
				{
					shadow = mix(shadow, 1.0, smoothstep(fadeStart, splitDist, vViewDepth));
				}
			}

			break;
		}
	}

	return mix(1.0, shadow, f.shadowStrength);
}

void main()
{
	Draw d = draws[oDraw];
	// Upstream culls OpenGL's front faces; the pipeline's front face is set so gl_FrontFacing is the same.
	if (d.cullFront != 0 && gl_FrontFacing)
	{
		discard;
	}

	vec4 texel = texture(sampler2D(TEXTURE(d.textureIndex), samplers[d.samplerIndex]), oUv);
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

	Frame f = frames[push.frameIndex];
	float shadow = CalculateShadowFactor(f);
	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= oLightResult.rgb * shadow;
		finalColor.a *= oLightResult.a;
	}
	else
	{
		finalColor *= oLightResult;
	}

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
