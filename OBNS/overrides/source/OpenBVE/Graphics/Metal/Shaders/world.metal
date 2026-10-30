// OBNS: the Metal world layer's shaders (OBNS/METAL.md): Direct3D11/Shaders/world.hlsl (the Vulkan layer's, themselves
// upstream's default.vert and default.frag, OpenBVE 1.14.1.2, BSD-2-Clause, Christopher Lees, S520, Aditiya Afrizal, The
// OpenBVE Project) in the Metal Shading Language. The records are the same buffers' layouts; a draw's record is its base
// instance. Compiled at start (MTLDevice newLibraryWithSource): VS, the fragment functions PSWorld / PSCab for blended
// draws and PSWorldOpaque / PSCabOpaque for opaque ones (full coverage), ShadowVS and ShadowPS. Drawn upside down as
// Direct3D's are (OpenGL reads the first row as its bottom), clockwise faces front.

#include <metal_stdlib>
using namespace metal;

struct Frame
{
	float4x4 projection;
	float4 lightPosition;  // xyz, pre-normalized
	float4 lightAmbient;   // rgb
	float4 lightDiffuse;   // rgb
	float4 lightSpecular;  // rgb
	float4 lightModel;
	float4 fogColor;       // rgb
	float fogStart, fogEnd, fogDensity;
	int fogIsLinear;
	int isLight;
	int shadowEnabled;
	int shadowCascades;
	int shadowSmooth;
	float4x4 inverseView;
	float4x4 lightSpace[4];
	float4 shadowSplit;
	float4 shadowBias;
	float4 shadowNormalBias;
	float4 shadowTexelSize;
	float shadowStrength;
	float shadowFilterRadius;
	float pad0, pad1;
};

struct ObjectRecord
{
	float4x4 modelView;
	float4 normal0, normal1, normal2; // the normal matrix's columns
	float4x4 textureMatrix;
	int matrixBase;
	int matrixFlag;
	int matrixCount;
	int pad2;
};

struct DrawRecord
{
	float4 materialColor;
	float4 materialSpecular;
	float4 materialEmission; // rgb; w: shininess
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

struct Layer
{
	int frameIndex;
	int cascade;         // the shadow pass's
	int2 layerPad;
};

struct VertexIn
{
	float3 position [[attribute(0)]];
	float3 normal [[attribute(1)]];
	float2 uv [[attribute(2)]];
	float4 color [[attribute(3)]];
	int3 chain [[attribute(4)]];
};

struct PixelIn
{
	float4 position [[position]];
	float4 viewPos;
	float2 uv;
	float4 color;
	float4 lightResult;
	uint draw [[flat]];
	float3 normal;
	float clipW;
	float4 posLightSpace0;
	float4 posLightSpace1;
	float4 posLightSpace2;
	float4 posLightSpace3;
};

static float3 safeNormalize(float3 v)
{
	float l2 = dot(v, v);
	if (l2 < 1e-10) return float3(0.0, 1.0, 0.0);
	return v * rsqrt(l2);
}

static float3 applyChain(float3 pos, int chain, int base, int count, device const float4x4* matrices)
{
	if (chain != 0)
	{
		// Unpacked exactly as upstream (a top byte of 0x80 or more comes out negative and is skipped).
		int m0 = (chain & (0xff << 24)) >> 24;
		int m1 = (chain >> 16) & 0xff;
		int m2 = (chain & 0xff00) >> 8;
		int m3 = chain & 0xff;
		// ignoreW per DirectX, as upstream.
		if (m0 >= 0 && m0 < 255 && m0 < count) pos = (matrices[base + m0] * float4(pos, 1.0)).xyz;
		if (m1 >= 0 && m1 < 255 && m1 < count) pos = (matrices[base + m1] * float4(pos, 1.0)).xyz;
		if (m2 >= 0 && m2 < 255 && m2 < count) pos = (matrices[base + m2] * float4(pos, 1.0)).xyz;
		if (m3 >= 0 && m3 < 255 && m3 < count) pos = (matrices[base + m3] * float4(pos, 1.0)).xyz;
	}

	return pos;
}

/// OpenGL's clip depth (-w..w) to Metal's (0..w), drawn upside down.
static float4 toTarget(float4 clip)
{
	clip.z = 0.5 * (clip.z + clip.w);
	clip.y = -clip.y;
	return clip;
}

vertex PixelIn VS(VertexIn input [[stage_in]], uint draw [[base_instance]],
	constant Layer& layer [[buffer(1)]],
	device const Frame* frames [[buffer(2)]],
	device const ObjectRecord* objects [[buffer(3)]],
	device const DrawRecord* draws [[buffer(4)]],
	device const float4x4* matrices [[buffer(5)]])
{
	PixelIn output;
	DrawRecord d = draws[draw];
	ObjectRecord o = objects[d.objectIndex];
	Frame f = frames[layer.frameIndex];
	output.draw = draw;

	float3 pos = input.position;
	output.color = input.color;
	pos = applyChain(pos, input.chain.x, o.matrixBase, o.matrixCount, matrices);
	pos = applyChain(pos, input.chain.y, o.matrixBase, o.matrixCount, matrices);
	pos = applyChain(pos, input.chain.z, o.matrixBase, o.matrixCount, matrices);
	pos.z = -pos.z;

	output.viewPos = o.modelView * float4(pos, 1.0);
	float4 clip = f.projection * output.viewPos;
	output.clipW = clip.w;
	output.position = toTarget(clip);
	output.uv = (o.textureMatrix * float4(input.uv, 1.0, 1.0)).xy;

	float3 n = float3(input.normal.x, input.normal.y, -input.normal.z);
	output.normal = safeNormalize(o.normal0.xyz * n.x + o.normal1.xyz * n.y + o.normal2.xyz * n.z);
	if (f.shadowEnabled != 0)
	{
		// As upstream: the camera-relative world position, pushed along the world normal towards the light by each
		// cascade's normal bias (texels times a texel's size in the world).
		float4 worldPos4 = f.inverseView * output.viewPos;
		float3x3 inverseView3 = float3x3(f.inverseView[0].xyz, f.inverseView[1].xyz, f.inverseView[2].xyz);
		float3 worldNormal = safeNormalize(inverseView3 * output.normal);
		output.posLightSpace0 = f.lightSpace[0] * float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.x * f.shadowTexelSize.x), 1.0);
		output.posLightSpace1 = f.lightSpace[1] * float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.y * f.shadowTexelSize.y), 1.0);
		output.posLightSpace2 = f.lightSpace[2] * float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.z * f.shadowTexelSize.z), 1.0);
		output.posLightSpace3 = f.lightSpace[3] * float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.w * f.shadowTexelSize.w), 1.0);
	}
	else
	{
		output.posLightSpace0 = float4(0.0);
		output.posLightSpace1 = float4(0.0);
		output.posLightSpace2 = float4(0.0);
		output.posLightSpace3 = float4(0.0);
	}

	if (f.isLight != 0 && (d.flags & 4) == 0)
	{
		float3 normal = output.normal;
		float nDotVP = max(0.0, dot(normal, f.lightPosition.xyz));
		float nDotHV = max(0.0, dot(normal, normalize(output.viewPos.xyz + f.lightPosition.xyz)));
		float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, d.materialEmission.w);
		float4 ambient = float4(f.lightAmbient.rgb, 1.0);
		float4 diffuse = float4(f.lightDiffuse.rgb, 1.0) * nDotVP;
		float4 specular = float4(f.lightSpecular.rgb, 1.0) * pf;
		float4 sceneColor = (d.flags & 1) != 0 ? float4(d.materialEmission.rgb, 1.0) + d.materialColor * f.lightModel : f.lightModel;
		float4 finalColor = sceneColor + ambient * d.materialColor + diffuse * d.materialColor + specular * d.materialSpecular;
		output.lightResult = clamp(finalColor, 0.0, 1.0);
	}
	else
	{
		output.lightResult = d.materialColor;
	}

	return output;
}

// --- shadows: upstream's CalculateShadowFactor and its helpers, with the cascades as an array's layers ---

constant float SHADOW_TWO_PI = 6.28318530718;
constant float SHADOW_MIN_RADIUS = 0.5;
constant float SHADOW_MAX_RADIUS = 3.0;
constant float SHADOW_MIN_BLEND = 5.0;
constant float SHADOW_MAX_BLEND = 25.0;
constant float SHADOW_BLEND_FRACTION = 0.1;
constant float SHADOW_BIAS_GUARD_TEXELS = 4.0;
constant float SHADOW_BIAS_EPSILON = 0.00002;

constant float2 VOGEL_DISK_5[5] =
{
	float2(0.31622777, 0.0),
	float2(-0.40387357, 0.36998127),
	float2(0.06181932, -0.70439930),
	float2(0.50905647, 0.66397403),
	float2(-0.93418124, -0.16524351)
};

// Comparison with linear filtering: a sample is the 2x2 hardware PCF that upstream's sampler2DShadow gives.
constexpr sampler shadowSampler(coord::normalized, address::clamp_to_edge, filter::linear, compare_func::less_equal);

static float interleavedGradientNoise(float2 p)
{
	const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
	return fract(magic.z * fract(dot(p, magic.xy)));
}

static float2 vogelDiskSample(int i, float cosPhi, float sinPhi)
{
	float2 o = VOGEL_DISK_5[i];
	return float2(o.x * cosPhi - o.y * sinPhi, o.x * sinPhi + o.y * cosPhi);
}

static float getCascadeShadowFactor(constant Frame& f, depth2d_array<float> shadowMap, int layerIndex, float4 posLightSpace, float bias, float normalBias, float3 vNormal, float2 fragCoord)
{
	float3 projCoords = posLightSpace.xyz / posLightSpace.w;
	projCoords = projCoords * 0.5 + 0.5;
	if (projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0 || projCoords.z < 0.0 || projCoords.z > 1.0)
	{
		return 1.0;
	}

	float3 lightDir = f.lightPosition.xyz;
	float nLen2 = dot(vNormal, vNormal);
	float3 normal = nLen2 < 1e-10 ? lightDir : vNormal * rsqrt(nLen2);
	float slope = clamp(1.0 - dot(normal, lightDir), 0.0, 1.0);
	float slopeScale = clamp(normalBias, 0.0, 1.5);
	float activeBias = bias * (1.0 + slope * slopeScale);

	float2 texelSize = 1.0 / float2(shadowMap.get_width(), shadowMap.get_height());
	float radiusScaled = f.shadowSmooth != 0 ? clamp(f.shadowFilterRadius, SHADOW_MIN_RADIUS, SHADOW_MAX_RADIUS) : SHADOW_MIN_RADIUS;
	float biasedDepth = projCoords.z - activeBias * (1.0 + (radiusScaled - SHADOW_MIN_RADIUS) * 0.25);
	biasedDepth = max(biasedDepth, projCoords.z - (bias * SHADOW_BIAS_GUARD_TEXELS + SHADOW_BIAS_EPSILON));
	uint layer = uint(layerIndex);

	if (f.shadowSmooth != 0)
	{
		float phi = interleavedGradientNoise(fragCoord) * SHADOW_TWO_PI;
		float cosPhi = cos(phi);
		float sinPhi = sin(phi);
		float smoothShadow = 0.0;
		for (int i = 0; i < 5; ++i)
		{
			float2 offset = vogelDiskSample(i, cosPhi, sinPhi) * texelSize * radiusScaled;
			smoothShadow += shadowMap.sample_compare(shadowSampler, projCoords.xy + offset, layer, biasedDepth, level(0));
		}

		return smoothShadow * 0.2;
	}

	float shadow = 0.0;
	shadow += shadowMap.sample_compare(shadowSampler, projCoords.xy + float2(-0.5, -0.5) * texelSize, layer, biasedDepth, level(0));
	shadow += shadowMap.sample_compare(shadowSampler, projCoords.xy + float2( 0.5, -0.5) * texelSize, layer, biasedDepth, level(0));
	shadow += shadowMap.sample_compare(shadowSampler, projCoords.xy + float2(-0.5,  0.5) * texelSize, layer, biasedDepth, level(0));
	shadow += shadowMap.sample_compare(shadowSampler, projCoords.xy + float2( 0.5,  0.5) * texelSize, layer, biasedDepth, level(0));
	return shadow * 0.25;
}

static float sampleCascadeByIndex(constant Frame& f, depth2d_array<float> shadowMap, int idx, PixelIn input)
{
	if (idx == 0) return getCascadeShadowFactor(f, shadowMap, 0, input.posLightSpace0, f.shadowBias.x, f.shadowNormalBias.x, input.normal, input.position.xy);
	if (idx == 1) return getCascadeShadowFactor(f, shadowMap, 1, input.posLightSpace1, f.shadowBias.y, f.shadowNormalBias.y, input.normal, input.position.xy);
	if (idx == 2) return getCascadeShadowFactor(f, shadowMap, 2, input.posLightSpace2, f.shadowBias.z, f.shadowNormalBias.z, input.normal, input.position.xy);
	if (idx == 3) return getCascadeShadowFactor(f, shadowMap, 3, input.posLightSpace3, f.shadowBias.w, f.shadowNormalBias.w, input.normal, input.position.xy);
	return 1.0;
}

static float calculateShadowFactor(constant Frame& f, depth2d_array<float> shadowMap, PixelIn input)
{
	if (f.shadowEnabled == 0 || f.shadowStrength <= 0.0) return 1.0;
	float vViewDepth = abs(input.viewPos.z);
	float shadow = 1.0;
	int cascadeCount = f.shadowCascades;
	for (int i = 0; i < cascadeCount; i++)
	{
		float splitDist = f.shadowSplit[i];
		float blendRange = clamp(splitDist * SHADOW_BLEND_FRACTION, SHADOW_MIN_BLEND, SHADOW_MAX_BLEND);
		if (vViewDepth < splitDist)
		{
			shadow = sampleCascadeByIndex(f, shadowMap, i, input);
			if (i < cascadeCount - 1)
			{
				float blendStart = splitDist - blendRange;
				if (vViewDepth > blendStart)
				{
					float nextShadow = sampleCascadeByIndex(f, shadowMap, i + 1, input);
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

/// The face's colour, as world.hlsl's PS; false where the fragment is discarded.
static bool shade(PixelIn input, bool frontFacing, constant Frame& f, device const DrawRecord* draws, texture2d<float> faceTexture, sampler faceSampler, depth2d_array<float> shadowMap, thread float4& color)
{
	DrawRecord d = draws[input.draw];
	// Upstream culls OpenGL's front faces; drawn upside down with clockwise faces front, frontFacing is OpenGL's.
	if (d.cullFront != 0 && frontFacing)
	{
		return false;
	}

	float4 texel = faceTexture.sample(faceSampler, input.uv);
	float4 finalColor;
	if ((d.flags & 16) == 0)
	{
		finalColor = float4(input.color.rgb, 1.0) * texel;
	}
	else
	{
		// Texture alpha disabled (MSTS shapes).
		finalColor = float4(input.color.rgb, 1.0) * float4(texel.rgb, 1.0);
	}

	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= d.brightness;
	}

	finalColor.a *= d.opacity;

	if (d.alphaFunction == 513) // Less
	{
		if (finalColor.a >= d.alphaReference) return false;
	}
	else if (d.alphaFunction == 514) // Equal
	{
		if (!(abs(finalColor.a - d.alphaReference) < 0.00001)) return false;
	}
	else if (d.alphaFunction == 516) // Greater
	{
		if (finalColor.a <= d.alphaReference) return false;
	}

	float shadow = calculateShadowFactor(f, shadowMap, input);
	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= input.lightResult.rgb * shadow;
		finalColor.a *= input.lightResult.a;
	}
	else
	{
		finalColor *= input.lightResult;
	}

	float fogFactor = 1.0;
	if (d.fog != 0)
	{
		if (f.fogIsLinear != 0)
		{
			fogFactor = clamp((f.fogEnd - length(input.viewPos)) / (f.fogEnd - f.fogStart), 0.0, 1.0);
		}
		else
		{
			// OpenGL's gl_FragCoord.z / gl_FragCoord.w: the window depth times the clip w.
			fogFactor = exp(-pow(f.fogDensity * (input.position.z * input.clipW), 2.0));
		}
	}

	color = float4(mix(f.fogColor.rgb, finalColor.rgb, fogFactor), finalColor.a);
	return true;
}

struct WorldOut
{
	float4 color [[color(0)]];
	float depth [[color(1)]];
};

// The world layer's: the colour (alpha the coverage, or full), and the depth as a colour for the composite (written only
// by pipelines that write depth, by their write mask). The cab layer's: the colour only.

#define FRAGMENT_ARGUMENTS PixelIn input [[stage_in]], bool frontFacing [[front_facing]], constant Layer& layer [[buffer(1)]], \
	constant Frame* frames [[buffer(2)]], device const DrawRecord* draws [[buffer(4)]], \
	texture2d<float> faceTexture [[texture(0)]], sampler faceSampler [[sampler(0)]], depth2d_array<float> shadowMap [[texture(1)]]

fragment WorldOut PSWorld(FRAGMENT_ARGUMENTS)
{
	WorldOut output;
	if (!shade(input, frontFacing, frames[layer.frameIndex], draws, faceTexture, faceSampler, shadowMap, output.color)) discard_fragment();
	output.depth = input.position.z;
	return output;
}

fragment WorldOut PSWorldOpaque(FRAGMENT_ARGUMENTS)
{
	WorldOut output;
	if (!shade(input, frontFacing, frames[layer.frameIndex], draws, faceTexture, faceSampler, shadowMap, output.color)) discard_fragment();
	output.color.a = 1.0;
	output.depth = input.position.z;
	return output;
}

fragment float4 PSCab(FRAGMENT_ARGUMENTS)
{
	float4 color;
	if (!shade(input, frontFacing, frames[layer.frameIndex], draws, faceTexture, faceSampler, shadowMap, color)) discard_fragment();
	return color;
}

fragment float4 PSCabOpaque(FRAGMENT_ARGUMENTS)
{
	float4 color;
	if (!shade(input, frontFacing, frames[layer.frameIndex], draws, faceTexture, faceSampler, shadowMap, color)) discard_fragment();
	return float4(color.rgb, 1.0);
}

// --- the shadow pass: upstream's shadow_depth shaders (OpenBVE 1.14.1.2, BSD-2-Clause, Aditiya Afrizal, The OpenBVE
// Project), as the Vulkan layer's shadow.vert and shadow.frag ---

struct ShadowIn
{
	float4 position [[position]];
	float2 uv;
	uint draw [[flat]];
};

vertex ShadowIn ShadowVS(VertexIn input [[stage_in]], uint draw [[base_instance]],
	constant Layer& layer [[buffer(1)]],
	device const Frame* frames [[buffer(2)]],
	device const ObjectRecord* objects [[buffer(3)]],
	device const DrawRecord* draws [[buffer(4)]],
	device const float4x4* matrices [[buffer(5)]])
{
	ShadowIn output;
	DrawRecord d = draws[draw];
	ObjectRecord o = objects[d.objectIndex];
	Frame f = frames[layer.frameIndex];
	output.draw = draw;

	float3 pos = input.position;
	pos = applyChain(pos, input.chain.x, o.matrixBase, o.matrixCount, matrices);
	pos = applyChain(pos, input.chain.y, o.matrixBase, o.matrixCount, matrices);
	pos = applyChain(pos, input.chain.z, o.matrixBase, o.matrixCount, matrices);
	pos.z = -pos.z;

	// The camera-relative world position (the object's model-view, taken back through the view), in the cascade's light space.
	float4 world = f.inverseView * (o.modelView * float4(pos, 1.0));
	output.position = toTarget(f.lightSpace[layer.cascade] * world);
	output.uv = (o.textureMatrix * float4(input.uv, 1.0, 1.0)).xy;
	return output;
}

// A fragment casts unless its alpha (material times texture) is at most 0.5, or its material casts none.
fragment void ShadowPS(ShadowIn input [[stage_in]], device const DrawRecord* draws [[buffer(4)]],
	texture2d<float> faceTexture [[texture(0)]], sampler faceSampler [[sampler(0)]])
{
	DrawRecord d = draws[input.draw];
	float alpha = d.materialColor.a * faceTexture.sample(faceSampler, input.uv).a;
	if (alpha <= 0.5 || (d.flags & 32) != 0)
	{
		discard_fragment();
	}
}
