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

// OBNS: the Direct3D 9.0c world layer's shaders (OBNS/DIRECT3D.md): Direct3D11/Shaders/world.hlsl (the Vulkan layer's,
// themselves upstream's default.vert and default.frag, OpenBVE 1.14.1.2, BSD-2-Clause, Christopher Lees, S520, Aditiya
// Afrizal, The OpenBVE Project) in shader model 3.0. Shader model 3 has no structured buffers, integers, texture arrays or
// instance stream, so: the frame's record is in constant registers; the draws', objects' and matrices' records are float4
// texels of one float texture (records), read in both stages by the draw's index, which each draw sets; the vertices'
// matrix chains are read as bytes; the cascades' shadow maps are the quarters of one R32F atlas, compared by hand with the
// 2x2 filtering Direct3D 11's comparison sampler gives. Compiled at start by d3dcompiler_47.dll: VS, PS with
// OPAQUE_COVERAGE 1 or 0, ShadowVS, ShadowPS.

// --- the frame (c0-c36; its integers as floats), the records' texture, the layer ---

column_major float4x4 projection : register(c0);
float4 lightPosition : register(c4);  // xyz, pre-normalized
float4 lightAmbient : register(c5);
float4 lightDiffuse : register(c6);
float4 lightSpecular : register(c7);
float4 lightModel : register(c8);
float4 fogColor : register(c9);
float4 fogParams : register(c10);     // start, end, density, isLinear
float4 frameFlags : register(c11);    // isLight, shadowEnabled, shadowCascades, shadowSmooth
column_major float4x4 inverseView : register(c12);
column_major float4x4 lightSpace[4] : register(c16);
float4 shadowSplit : register(c32);
float4 shadowBias : register(c33);
float4 shadowNormalBias : register(c34);
float4 shadowTexelSize : register(c35);
float4 shadowParams : register(c36);  // strength, filter radius
float4 recordsSize : register(c37);   // width, 1 / width, 1 / height
float4 recordBases : register(c38);   // texels where the draws', objects' and matrices' records start; the shadow pass's cascade
float4 pixelOffset : register(c39);   // a pixel in clip space (1 / width, 1 / height): Direct3D 9's pixel centres
float4 drawIndex : register(c40);     // the draw's record
float4 shadowAtlas : register(c41);   // a cascade's size in texels, 1 / the atlas's size

sampler2D vertexRecords : register(s0);   // the vertex shaders' (D3DVERTEXTEXTURESAMPLER0)
sampler2D faceTexture : register(s0);     // the pixel shaders'
sampler2D shadowMap : register(s1);
sampler2D pixelRecords : register(s2);

float4 Record(sampler2D records, float index)
{
	float y = floor(index * recordsSize.y);
	float x = index - y * recordsSize.x;
	return tex2Dlod(records, float4((x + 0.5) * recordsSize.y, (y + 0.5) * recordsSize.z, 0.0, 0.0));
}

// A draw's record (6 texels): colour, specular, emission and shininess, (flags, brightness, opacity, texture),
// (sampler, alpha function, alpha reference, object), (fog, cull front).
float4 DrawTexel(sampler2D records, float draw, float t) { return Record(records, recordBases.x + draw * 6.0 + t); }

// An object's record (12 texels): model-view's columns, the normal matrix's, the texture matrix's, (matrix base, flag, count).
float4 ObjectTexel(float o, float t) { return Record(vertexRecords, recordBases.y + o * 12.0 + t); }

bool HasFlag(float flags, float bit)
{
	return frac(floor(flags / bit) * 0.5) > 0.25;
}

float4 MulColumns(float4 c0, float4 c1, float4 c2, float4 c3, float4 v)
{
	return c0 * v.x + c1 * v.y + c2 * v.z + c3 * v.w;
}

float3 safeNormalize(float3 v)
{
	float l2 = dot(v, v);
	if (l2 < 1e-10) return float3(0.0, 1.0, 0.0);
	return v * rsqrt(l2);
}

float3 transformVector(float3 v, float m)
{
	// ignoreW per DirectX, as upstream.
	float t = recordBases.z + m * 4.0;
	return MulColumns(Record(vertexRecords, t), Record(vertexRecords, t + 1.0), Record(vertexRecords, t + 2.0), Record(vertexRecords, t + 3.0), float4(v, 1.0)).xyz;
}

// A chain's four bytes, little-endian (x the lowest): upstream's m3, m2, m1, m0; a top byte of 0x80 or more is negative to
// upstream (an int's arithmetic shift) and skipped.
float3 applyChain(float3 pos, float4 chain, float base, float count)
{
	if (any(chain != 0.0))
	{
		if (chain.w < 128.0 && chain.w < count) pos = transformVector(pos, base + chain.w);
		if (chain.z < 255.0 && chain.z < count) pos = transformVector(pos, base + chain.z);
		if (chain.y < 255.0 && chain.y < count) pos = transformVector(pos, base + chain.y);
		if (chain.x < 255.0 && chain.x < count) pos = transformVector(pos, base + chain.x);
	}

	return pos;
}

struct VertexIn
{
	float3 position : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
	float4 color : COLOR0;
	float4 chain0 : BLENDINDICES0;
	float4 chain1 : BLENDINDICES1;
	float4 chain2 : BLENDINDICES2;
};

struct Varyings
{
	float4 viewPos : TEXCOORD0;
	float4 uvDraw : TEXCOORD1;      // uv, the draw, clip w
	float4 normalDepth : TEXCOORD2; // the normal, clip z
	float4 lightResult : TEXCOORD3;
	float4 color : TEXCOORD4;
	float4 posLightSpace0 : TEXCOORD5;
	float4 posLightSpace1 : TEXCOORD6;
	float4 posLightSpace2 : TEXCOORD7;
	float4 posLightSpace3 : TEXCOORD8;
};

struct PixelOut
{
	float4 color : COLOR0;
	float4 depth : COLOR1;
};

/// The vertex as world.hlsl's VS: the object's chain and model-view, OpenGL's depth range and upside down, lighting.
float3 ChainedPosition(VertexIn input, float objectIndex)
{
	float4 matrixInfo = ObjectTexel(objectIndex, 11.0);
	float3 pos = input.position;
	pos = applyChain(pos, input.chain0, matrixInfo.x, matrixInfo.z);
	pos = applyChain(pos, input.chain1, matrixInfo.x, matrixInfo.z);
	pos = applyChain(pos, input.chain2, matrixInfo.x, matrixInfo.z);
	pos.z = -pos.z;
	return pos;
}

/// OpenGL's clip depth (-w..w) to Direct3D's (0..w), drawn upside down (OpenGL reads the first row as its bottom), and
/// moved half a pixel (Direct3D 9's pixel centres are at whole coordinates).
float4 ToTarget(float4 clip)
{
	clip.z = 0.5 * (clip.z + clip.w);
	clip.y = -clip.y;
	clip.xy += float2(-pixelOffset.x, pixelOffset.y) * clip.w;
	return clip;
}

Varyings VS(VertexIn input, out float4 position : POSITION)
{
	Varyings output;
	float draw = drawIndex.x;
	float4 drawInfo = DrawTexel(vertexRecords, draw, 3.0);   // flags, brightness, opacity, texture
	float4 drawObject = DrawTexel(vertexRecords, draw, 4.0); // sampler, alpha function, alpha reference, object
	float o = drawObject.w;
	float3 pos = ChainedPosition(input, o);
	output.color = input.color;

	output.viewPos = MulColumns(ObjectTexel(o, 0.0), ObjectTexel(o, 1.0), ObjectTexel(o, 2.0), ObjectTexel(o, 3.0), float4(pos, 1.0));
	position = ToTarget(mul(projection, output.viewPos));
	output.uvDraw.zw = float2(draw, position.w);
	output.normalDepth.w = position.z;

	output.uvDraw.xy = MulColumns(ObjectTexel(o, 7.0), ObjectTexel(o, 8.0), ObjectTexel(o, 9.0), ObjectTexel(o, 10.0), float4(input.uv, 1.0, 1.0)).xy;

	float3 n = float3(input.normal.x, input.normal.y, -input.normal.z);
	float3 normal = safeNormalize(ObjectTexel(o, 4.0).xyz * n.x + ObjectTexel(o, 5.0).xyz * n.y + ObjectTexel(o, 6.0).xyz * n.z);
	output.normalDepth.xyz = normal;
	if (frameFlags.y != 0.0)
	{
		// As upstream: the camera-relative world position, pushed along the world normal towards the light by each
		// cascade's normal bias (texels times a texel's size in the world).
		float4 worldPos4 = mul(inverseView, output.viewPos);
		float3 worldNormal = safeNormalize(mul((float3x3)inverseView, normal));
		output.posLightSpace0 = mul(lightSpace[0], float4(worldPos4.xyz + worldNormal * (shadowNormalBias.x * shadowTexelSize.x), 1.0));
		output.posLightSpace1 = mul(lightSpace[1], float4(worldPos4.xyz + worldNormal * (shadowNormalBias.y * shadowTexelSize.y), 1.0));
		output.posLightSpace2 = mul(lightSpace[2], float4(worldPos4.xyz + worldNormal * (shadowNormalBias.z * shadowTexelSize.z), 1.0));
		output.posLightSpace3 = mul(lightSpace[3], float4(worldPos4.xyz + worldNormal * (shadowNormalBias.w * shadowTexelSize.w), 1.0));
	}
	else
	{
		output.posLightSpace0 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace1 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace2 = float4(0.0, 0.0, 0.0, 0.0);
		output.posLightSpace3 = float4(0.0, 0.0, 0.0, 0.0);
	}

	float4 materialColor = DrawTexel(vertexRecords, draw, 0.0);
	if (frameFlags.x != 0.0 && !HasFlag(drawInfo.x, 4.0))
	{
		float4 materialSpecular = DrawTexel(vertexRecords, draw, 1.0);
		float4 materialEmission = DrawTexel(vertexRecords, draw, 2.0);
		float nDotVP = max(0.0, dot(normal, lightPosition.xyz));
		float nDotHV = max(0.0, dot(normal, normalize(output.viewPos.xyz + lightPosition.xyz)));
		float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, materialEmission.w);
		float4 ambient = float4(lightAmbient.rgb, 1.0);
		float4 diffuse = float4(lightDiffuse.rgb, 1.0) * nDotVP;
		float4 specular = float4(lightSpecular.rgb, 1.0) * pf;
		float4 sceneColor = HasFlag(drawInfo.x, 1.0) ? float4(materialEmission.rgb, 1.0) + materialColor * lightModel : lightModel;
		float4 finalColor = sceneColor + ambient * materialColor + diffuse * materialColor + specular * materialSpecular;
		output.lightResult = clamp(finalColor, 0.0, 1.0);
	}
	else
	{
		output.lightResult = materialColor;
	}

	return output;
}

// --- shadows: upstream's CalculateShadowFactor and its helpers, the cascades the atlas's quarters ---

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

/// 1 where a texel of a cascade's quarter is not nearer than the depth (clamped to the quarter, as a clamping sampler).
float Compare(float2 texel, float2 quarter, float depth)
{
	texel = clamp(texel, 0.0, shadowAtlas.x - 1.0);
	return depth <= tex2Dlod(shadowMap, float4((quarter * shadowAtlas.x + texel + 0.5) * shadowAtlas.y, 0.0, 0.0)).r ? 1.0 : 0.0;
}

/// As a linear comparison sampler: the four nearest texels compared, filtered by the position between them.
float SampleCompare(float2 uv, float2 quarter, float depth)
{
	float2 t = uv * shadowAtlas.x - 0.5;
	float2 f = frac(t);
	float2 b = t - f;
	float c00 = Compare(b, quarter, depth), c10 = Compare(b + float2(1.0, 0.0), quarter, depth);
	float c01 = Compare(b + float2(0.0, 1.0), quarter, depth), c11 = Compare(b + float2(1.0, 1.0), quarter, depth);
	return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
}

float GetCascadeShadowFactor(float layerIndex, float4 posLightSpace, float bias, float normalBias, float3 vNormal, float2 fragCoord)
{
	float3 projCoords = posLightSpace.xyz / posLightSpace.w;
	projCoords = projCoords * 0.5 + 0.5;
	if (projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0 || projCoords.z < 0.0 || projCoords.z > 1.0)
	{
		return 1.0;
	}

	float3 lightDir = lightPosition.xyz;
	float nLen2 = dot(vNormal, vNormal);
	float3 normal = nLen2 < 1e-10 ? lightDir : vNormal * rsqrt(nLen2);
	float slope = clamp(1.0 - dot(normal, lightDir), 0.0, 1.0);
	float slopeScale = clamp(normalBias, 0.0, 1.5);
	float activeBias = bias * (1.0 + slope * slopeScale);

	float2 texelSize = 1.0 / shadowAtlas.xx;
	float radiusScaled = frameFlags.w != 0.0 ? clamp(shadowParams.y, SHADOW_MIN_RADIUS, SHADOW_MAX_RADIUS) : SHADOW_MIN_RADIUS;
	float biasedDepth = projCoords.z - activeBias * (1.0 + (radiusScaled - SHADOW_MIN_RADIUS) * 0.25);
	biasedDepth = max(biasedDepth, projCoords.z - (bias * SHADOW_BIAS_GUARD_TEXELS + SHADOW_BIAS_EPSILON));
	float2 quarter = float2(fmod(layerIndex, 2.0), floor(layerIndex * 0.5));

	if (frameFlags.w != 0.0)
	{
		float phi = interleavedGradientNoise(fragCoord) * SHADOW_TWO_PI;
		float cosPhi = cos(phi);
		float sinPhi = sin(phi);
		float smoothShadow = 0.0;
		[unroll]
		for (int i = 0; i < 5; ++i)
		{
			float2 offset = vogelDiskSample(i, cosPhi, sinPhi) * texelSize * radiusScaled;
			smoothShadow += SampleCompare(projCoords.xy + offset, quarter, biasedDepth);
		}

		return smoothShadow * 0.2;
	}

	float shadow = 0.0;
	shadow += SampleCompare(projCoords.xy + float2(-0.5, -0.5) * texelSize, quarter, biasedDepth);
	shadow += SampleCompare(projCoords.xy + float2( 0.5, -0.5) * texelSize, quarter, biasedDepth);
	shadow += SampleCompare(projCoords.xy + float2(-0.5,  0.5) * texelSize, quarter, biasedDepth);
	shadow += SampleCompare(projCoords.xy + float2( 0.5,  0.5) * texelSize, quarter, biasedDepth);
	return shadow * 0.25;
}

float SampleCascadeByIndex(float idx, Varyings input, float2 fragCoord)
{
	float3 n = input.normalDepth.xyz;
	if (idx == 0.0) return GetCascadeShadowFactor(0.0, input.posLightSpace0, shadowBias.x, shadowNormalBias.x, n, fragCoord);
	if (idx == 1.0) return GetCascadeShadowFactor(1.0, input.posLightSpace1, shadowBias.y, shadowNormalBias.y, n, fragCoord);
	if (idx == 2.0) return GetCascadeShadowFactor(2.0, input.posLightSpace2, shadowBias.z, shadowNormalBias.z, n, fragCoord);
	if (idx == 3.0) return GetCascadeShadowFactor(3.0, input.posLightSpace3, shadowBias.w, shadowNormalBias.w, n, fragCoord);
	return 1.0;
}

float CalculateShadowFactor(Varyings input, float2 fragCoord)
{
	if (frameFlags.y == 0.0 || shadowParams.x <= 0.0) return 1.0;
	float vViewDepth = abs(input.viewPos.z);
	float shadow = 1.0;
	float cascadeCount = frameFlags.z;
	float4 splits = shadowSplit;
	[loop]
	for (int i = 0; i < 4; i++)
	{
		if (i >= cascadeCount)
		{
			break;
		}

		float splitDist = i == 0 ? splits.x : i == 1 ? splits.y : i == 2 ? splits.z : splits.w;
		float blendRange = clamp(splitDist * SHADOW_BLEND_FRACTION, SHADOW_MIN_BLEND, SHADOW_MAX_BLEND);
		if (vViewDepth < splitDist)
		{
			shadow = SampleCascadeByIndex(i, input, fragCoord);
			if (i < cascadeCount - 1)
			{
				float blendStart = splitDist - blendRange;
				if (vViewDepth > blendStart)
				{
					float nextShadow = SampleCascadeByIndex(i + 1, input, fragCoord);
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

	return lerp(1.0, shadow, shadowParams.x);
}

PixelOut PS(Varyings input, float2 fragCoord : VPOS, float face : VFACE)
{
	float draw = input.uvDraw.z;
	float4 drawInfo = DrawTexel(pixelRecords, draw, 3.0);   // flags, brightness, opacity
	float4 alphaTest = DrawTexel(pixelRecords, draw, 4.0);  // sampler, alpha function, alpha reference
	float4 drawFog = DrawTexel(pixelRecords, draw, 5.0);    // fog, cull front
	// Upstream culls OpenGL's front faces; drawn upside down with clockwise faces front, a front face here is OpenGL's.
	if (drawFog.y != 0.0 && face > 0.0)
	{
		discard;
	}

	float4 texel = tex2D(faceTexture, input.uvDraw.xy);
	float4 finalColor;
	if (!HasFlag(drawInfo.x, 16.0))
	{
		finalColor = float4(input.color.rgb, 1.0) * texel;
	}
	else
	{
		// Texture alpha disabled (MSTS shapes).
		finalColor = float4(input.color.rgb, 1.0) * float4(texel.rgb, 1.0);
	}

	bool unlit = HasFlag(drawInfo.x, 1.0) || HasFlag(drawInfo.x, 4.0);
	if (!unlit)
	{
		finalColor.rgb *= drawInfo.y;
	}

	finalColor.a *= drawInfo.z;

	if (alphaTest.y == 513.0) // Less
	{
		if (finalColor.a >= alphaTest.z) discard;
	}
	else if (alphaTest.y == 514.0) // Equal
	{
		if (!(abs(finalColor.a - alphaTest.z) < 0.00001)) discard;
	}
	else if (alphaTest.y == 516.0) // Greater
	{
		if (finalColor.a <= alphaTest.z) discard;
	}

	float shadow = CalculateShadowFactor(input, fragCoord);
	if (!unlit)
	{
		finalColor.rgb *= input.lightResult.rgb * shadow;
		finalColor.a *= input.lightResult.a;
	}
	else
	{
		finalColor *= input.lightResult;
	}

	float fogFactor = 1.0;
	if (drawFog.x != 0.0)
	{
		if (fogParams.w != 0.0)
		{
			fogFactor = clamp((fogParams.y - length(input.viewPos)) / (fogParams.y - fogParams.x), 0.0, 1.0);
		}
		else
		{
			// OpenGL's gl_FragCoord.z / gl_FragCoord.w: the window depth times the clip w, the clip z.
			fogFactor = exp(-pow(fogParams.z * input.normalDepth.w, 2.0));
		}
	}

	PixelOut output;
	output.color = float4(lerp(fogColor.rgb, finalColor.rgb, fogFactor), OPAQUE_COVERAGE ? 1.0 : finalColor.a);
	// The depth as a colour, for the composite (its target is bound only for draws that write depth); alpha 1, so blending keeps it.
	float depth = input.normalDepth.w / input.uvDraw.w;
	output.depth = float4(depth, depth, depth, 1.0);
	return output;
}

// --- the shadow pass: upstream's shadow_depth shaders (OpenBVE 1.14.1.2, BSD-2-Clause, Aditiya Afrizal, The OpenBVE
// Project), as the Vulkan layer's shadow.vert and shadow.frag; the depth written as a colour into the cascade's quarter ---

struct ShadowVaryings
{
	float4 uvDraw : TEXCOORD0; // uv, the draw
	float4 depth : TEXCOORD1;  // clip z, clip w
};

ShadowVaryings ShadowVS(VertexIn input, out float4 position : POSITION)
{
	ShadowVaryings output;
	float draw = drawIndex.x;
	float o = DrawTexel(vertexRecords, draw, 4.0).w;
	float3 pos = ChainedPosition(input, o);

	// The camera-relative world position (the object's model-view, taken back through the view), in the cascade's light space.
	float4 world = mul(inverseView, MulColumns(ObjectTexel(o, 0.0), ObjectTexel(o, 1.0), ObjectTexel(o, 2.0), ObjectTexel(o, 3.0), float4(pos, 1.0)));
	float4 clip = mul(lightSpace[(int)recordBases.w], world);
	position = ToTarget(clip);
	output.depth = float4(position.z, position.w, 0.0, 0.0);
	output.uvDraw = float4(MulColumns(ObjectTexel(o, 7.0), ObjectTexel(o, 8.0), ObjectTexel(o, 9.0), ObjectTexel(o, 10.0), float4(input.uv, 1.0, 1.0)).xy, draw, 0.0);
	return output;
}

// A fragment casts unless its alpha (material times texture) is at most 0.5, or its material casts none.
float4 ShadowPS(ShadowVaryings input) : COLOR0
{
	float draw = input.uvDraw.z;
	float alpha = DrawTexel(pixelRecords, draw, 0.0).a * tex2D(faceTexture, input.uvDraw.xy).a;
	if (alpha <= 0.5 || HasFlag(DrawTexel(pixelRecords, draw, 3.0).x, 32.0))
	{
		discard;
	}

	float depth = input.depth.x / input.depth.y;
	return float4(depth, depth, depth, 1.0);
}
