// Derived from upstream's shaders, under their licence:
//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2024, Christopher Lees, S520, Aditiya Afrizal, The OpenBVE Project
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2024, Aditiya Afrizal, The OpenBVE Project
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

// OBNS: the Direct3D 11 world layer's shaders (OBNS/DIRECT3D.md): the Vulkan layer's world.vert and world.frag in HLSL
// (shader model 5.0), themselves upstream's default.vert and default.frag (OpenBVE 1.14.1.2, BSD-2-Clause, Christopher
// Lees, S520, Aditiya Afrizal, The OpenBVE Project), their uniforms read from the frame's structured buffers by the
// draw's index (an instance stream, as the draw's StartInstanceLocation). Compiled at start by d3dcompiler_47.dll:
// VS, and PS with OPAQUE_COVERAGE 1 or 0; the shadow pass's ShadowVS and ShadowPS (the Vulkan layer's shadow.vert and
// shadow.frag). Matrices are column-major, as GLSL reads the same records. The shadow maps are one array, a layer
// per cascade, drawn upside down as the world is, so a map's first row is light space's y = -1 as in Vulkan's.

struct Frame
{
	column_major float4x4 projection;
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
	column_major float4x4 inverseView;
	column_major float4x4 lightSpace[4];
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
	column_major float4x4 modelView;
	float4 normal0, normal1, normal2; // the normal matrix's columns
	column_major float4x4 textureMatrix;
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

struct MatrixRecord
{
	column_major float4x4 m;
};

StructuredBuffer<Frame> frames : register(t0);
StructuredBuffer<ObjectRecord> objects : register(t1);
StructuredBuffer<DrawRecord> draws : register(t2);
StructuredBuffer<MatrixRecord> matrices : register(t3);
Texture2D faceTexture : register(t4);
SamplerState faceSampler : register(s0);
Texture2DArray shadowMap : register(t5);
SamplerComparisonState shadowSampler : register(s1);

cbuffer Layer : register(b0)
{
	int frameIndex;
	int cascade;         // the shadow pass's
	int2 layerPad;
};

struct VertexIn
{
	float3 position : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
	float4 color : COLOR;
	int3 chain : BLENDINDICES;
	uint draw : DRAW;
};

struct PixelIn
{
	float4 position : SV_Position;
	float4 viewPos : VIEWPOS;
	float2 uv : TEXCOORD0;
	float4 color : COLOR;
	float4 lightResult : LIGHT;
	nointerpolation uint draw : DRAW;
	float3 normal : NORMAL;
	float clipW : CLIPW;
	float4 posLightSpace0 : LIGHTSPACE0;
	float4 posLightSpace1 : LIGHTSPACE1;
	float4 posLightSpace2 : LIGHTSPACE2;
	float4 posLightSpace3 : LIGHTSPACE3;
};

struct PixelOut
{
	float4 color : SV_Target0;
	float depth : SV_Target1;
};

float3 safeNormalize(float3 v)
{
	float l2 = dot(v, v);
	if (l2 < 1e-10) return float3(0.0, 1.0, 0.0);
	return v * rsqrt(l2);
}

float3 transformVector(float3 v, int m)
{
	// ignoreW per DirectX, as upstream.
	return mul(matrices[m].m, float4(v, 1.0)).xyz;
}

float3 applyChain(float3 pos, int chain, int base, int count)
{
	if (chain != 0)
	{
		// Unpacked exactly as upstream (a top byte of 0x80 or more comes out negative and is skipped).
		int m0 = (chain & (0xff << 24)) >> 24;
		int m1 = (chain >> 16) & 0xff;
		int m2 = (chain & 0xff00) >> 8;
		int m3 = chain & 0xff;
		if (m0 >= 0 && m0 < 255 && m0 < count) pos = transformVector(pos, base + m0);
		if (m1 >= 0 && m1 < 255 && m1 < count) pos = transformVector(pos, base + m1);
		if (m2 >= 0 && m2 < 255 && m2 < count) pos = transformVector(pos, base + m2);
		if (m3 >= 0 && m3 < 255 && m3 < count) pos = transformVector(pos, base + m3);
	}

	return pos;
}

PixelIn VS(VertexIn input)
{
	PixelIn output;
	DrawRecord d = draws[input.draw];
	ObjectRecord o = objects[d.objectIndex];
	Frame f = frames[frameIndex];
	output.draw = input.draw;

	float3 pos = input.position;
	output.color = input.color;
	pos = applyChain(pos, input.chain.x, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.y, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.z, o.matrixBase, o.matrixCount);
	pos.z = -pos.z;

	output.viewPos = mul(o.modelView, float4(pos, 1.0));
	float4 clip = mul(f.projection, output.viewPos);
	// OpenGL's clip depth (-w..w) to Direct3D's (0..w): the same window depth as OpenGL's.
	clip.z = 0.5 * (clip.z + clip.w);
	output.clipW = clip.w;
	// OpenGL reads the image's first row as its bottom: drawn upside down (as Vulkan's is), so it comes out upright.
	clip.y = -clip.y;
	output.position = clip;

	output.uv = mul(o.textureMatrix, float4(input.uv, 1.0, 1.0)).xy;

	float3 n = float3(input.normal.x, input.normal.y, -input.normal.z);
	output.normal = safeNormalize(o.normal0.xyz * n.x + o.normal1.xyz * n.y + o.normal2.xyz * n.z);
	if (f.shadowEnabled != 0)
	{
		// As upstream: the camera-relative world position, pushed along the world normal towards the light by each
		// cascade's normal bias (texels times a texel's size in the world).
		float4 worldPos4 = mul(f.inverseView, output.viewPos);
		float3 worldNormal = safeNormalize(mul((float3x3)f.inverseView, output.normal));
		output.posLightSpace0 = mul(f.lightSpace[0], float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.x * f.shadowTexelSize.x), 1.0));
		output.posLightSpace1 = mul(f.lightSpace[1], float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.y * f.shadowTexelSize.y), 1.0));
		output.posLightSpace2 = mul(f.lightSpace[2], float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.z * f.shadowTexelSize.z), 1.0));
		output.posLightSpace3 = mul(f.lightSpace[3], float4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.w * f.shadowTexelSize.w), 1.0));
	}
	else
	{
		output.posLightSpace0 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace1 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace2 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace3 = float4(0.0, 0.0, 0.0, 0.0);
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

static const float SHADOW_TWO_PI = 6.28318530718;
static const float SHADOW_MIN_RADIUS = 0.5;
static const float SHADOW_MAX_RADIUS = 3.0;
static const float SHADOW_MIN_BLEND = 5.0;
static const float SHADOW_MAX_BLEND = 25.0;
static const float SHADOW_BLEND_FRACTION = 0.1;
static const float SHADOW_BIAS_GUARD_TEXELS = 4.0;
static const float SHADOW_BIAS_EPSILON = 0.00002;

static const float2 VOGEL_DISK_5[5] =
{
	float2(0.31622777, 0.0),
	float2(-0.40387357, 0.36998127),
	float2(0.06181932, -0.70439930),
	float2(0.50905647, 0.66397403),
	float2(-0.93418124, -0.16524351)
};

float interleavedGradientNoise(float2 p)
{
	const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
	return frac(magic.z * frac(dot(p, magic.xy)));
}

float2 vogelDiskSample(int i, float cosPhi, float sinPhi)
{
	float2 o = VOGEL_DISK_5[i];
	return float2(o.x * cosPhi - o.y * sinPhi, o.x * sinPhi + o.y * cosPhi);
}

float GetCascadeShadowFactor(Frame f, int layerIndex, float4 posLightSpace, float bias, float normalBias, float3 vNormal, float2 fragCoord)
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

	uint mapWidth, mapHeight, mapLayers;
	shadowMap.GetDimensions(mapWidth, mapHeight, mapLayers);
	float2 texelSize = 1.0 / float2(mapWidth, mapHeight);
	float radiusScaled = f.shadowSmooth != 0 ? clamp(f.shadowFilterRadius, SHADOW_MIN_RADIUS, SHADOW_MAX_RADIUS) : SHADOW_MIN_RADIUS;
	float biasedDepth = projCoords.z - activeBias * (1.0 + (radiusScaled - SHADOW_MIN_RADIUS) * 0.25);
	biasedDepth = max(biasedDepth, projCoords.z - (bias * SHADOW_BIAS_GUARD_TEXELS + SHADOW_BIAS_EPSILON));
	float layer = (float)layerIndex;

	if (f.shadowSmooth != 0)
	{
		float phi = interleavedGradientNoise(fragCoord) * SHADOW_TWO_PI;
		float cosPhi = cos(phi);
		float sinPhi = sin(phi);
		float smoothShadow = 0.0;
		[unroll]
		for (int i = 0; i < 5; ++i)
		{
			float2 offset = vogelDiskSample(i, cosPhi, sinPhi) * texelSize * radiusScaled;
			smoothShadow += shadowMap.SampleCmpLevelZero(shadowSampler, float3(projCoords.xy + offset, layer), biasedDepth);
		}

		return smoothShadow * 0.2;
	}

	float shadow = 0.0;
	shadow += shadowMap.SampleCmpLevelZero(shadowSampler, float3(projCoords.xy + float2(-0.5, -0.5) * texelSize, layer), biasedDepth);
	shadow += shadowMap.SampleCmpLevelZero(shadowSampler, float3(projCoords.xy + float2( 0.5, -0.5) * texelSize, layer), biasedDepth);
	shadow += shadowMap.SampleCmpLevelZero(shadowSampler, float3(projCoords.xy + float2(-0.5,  0.5) * texelSize, layer), biasedDepth);
	shadow += shadowMap.SampleCmpLevelZero(shadowSampler, float3(projCoords.xy + float2( 0.5,  0.5) * texelSize, layer), biasedDepth);
	return shadow * 0.25;
}

float SampleCascadeByIndex(Frame f, int idx, PixelIn input)
{
	if (idx == 0) return GetCascadeShadowFactor(f, 0, input.posLightSpace0, f.shadowBias.x, f.shadowNormalBias.x, input.normal, input.position.xy);
	if (idx == 1) return GetCascadeShadowFactor(f, 1, input.posLightSpace1, f.shadowBias.y, f.shadowNormalBias.y, input.normal, input.position.xy);
	if (idx == 2) return GetCascadeShadowFactor(f, 2, input.posLightSpace2, f.shadowBias.z, f.shadowNormalBias.z, input.normal, input.position.xy);
	if (idx == 3) return GetCascadeShadowFactor(f, 3, input.posLightSpace3, f.shadowBias.w, f.shadowNormalBias.w, input.normal, input.position.xy);
	return 1.0;
}

float CalculateShadowFactor(Frame f, PixelIn input)
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
			shadow = SampleCascadeByIndex(f, i, input);
			if (i < cascadeCount - 1)
			{
				float blendStart = splitDist - blendRange;
				if (vViewDepth > blendStart)
				{
					float nextShadow = SampleCascadeByIndex(f, i + 1, input);
					shadow = lerp(shadow, nextShadow, smoothstep(blendStart, splitDist, vViewDepth));
				}
			}
			else
			{
				float fadeStart = splitDist - blendRange * 2.0;
				if (vViewDepth > fadeStart)
				{
					shadow = lerp(shadow, 1.0, smoothstep(fadeStart, splitDist, vViewDepth));
				}
			}

			break;
		}
	}

	return lerp(1.0, shadow, f.shadowStrength);
}

PixelOut PS(PixelIn input, bool frontFacing : SV_IsFrontFace)
{
	DrawRecord d = draws[input.draw];
	// Upstream culls OpenGL's front faces; drawn upside down with clockwise faces front, frontFacing is OpenGL's.
	if (d.cullFront != 0 && frontFacing)
	{
		discard;
	}

	float4 texel = faceTexture.Sample(faceSampler, input.uv);
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

	Frame f = frames[frameIndex];
	float shadow = CalculateShadowFactor(f, input);
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

	PixelOut output;
	output.color = float4(lerp(f.fogColor.rgb, finalColor.rgb, fogFactor), OPAQUE_COVERAGE ? 1.0 : finalColor.a);
	// The depth as a colour, for the composite (written only by passes that write depth, by the blend state's mask).
	output.depth = input.position.z;
	return output;
}

// --- the shadow pass: upstream's shadow_depth shaders (OpenBVE 1.14.1.2, BSD-2-Clause, Aditiya Afrizal, The OpenBVE
// Project), as the Vulkan layer's shadow.vert and shadow.frag ---

struct ShadowIn
{
	float4 position : SV_Position;
	float2 uv : TEXCOORD0;
	nointerpolation uint draw : DRAW;
};

ShadowIn ShadowVS(VertexIn input)
{
	ShadowIn output;
	DrawRecord d = draws[input.draw];
	ObjectRecord o = objects[d.objectIndex];
	Frame f = frames[frameIndex];
	output.draw = input.draw;

	float3 pos = input.position;
	pos = applyChain(pos, input.chain.x, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.y, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.z, o.matrixBase, o.matrixCount);
	pos.z = -pos.z;

	// The camera-relative world position (the object's model-view, taken back through the view), in the cascade's light space.
	float4 world = mul(f.inverseView, mul(o.modelView, float4(pos, 1.0)));
	float4 clip = mul(f.lightSpace[cascade], world);
	// OpenGL's clip depth (-w..w) to Direct3D's (0..w): the same depth as upstream's map holds; upside down, as the world.
	clip.z = 0.5 * (clip.z + clip.w);
	clip.y = -clip.y;
	output.position = clip;
	output.uv = mul(o.textureMatrix, float4(input.uv, 1.0, 1.0)).xy;
	return output;
}

// A fragment casts unless its alpha (material times texture) is at most 0.5, or its material casts none.
void ShadowPS(ShadowIn input)
{
	DrawRecord d = draws[input.draw];
	float alpha = d.materialColor.a * faceTexture.Sample(faceSampler, input.uv).a;
	if (alpha <= 0.5 || (d.flags & 32) != 0)
	{
		discard;
	}
}
