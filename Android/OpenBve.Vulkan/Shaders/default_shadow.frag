//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2024, Christopher Lees, S520, The OpenBVE Project
//Copyright (c) 2026, The OpenBVE Android port contributors
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

/*
 * default.frag (see there) with upstream's cascaded shadows, for faces drawn while shadows are on. The
 * shadows are upstream's assets/Shaders/default.frag's, with one change for phones: the position in the
 * cascade's light space is worked out here, for the cascade the fragment reads, from the view space
 * position and normal, rather than for all four cascades per vertex and interpolated (the same result,
 * as light space is orthographic, with far fewer values to pass along).
 */
#version 450

layout(location = 0) in vec4 oViewPos;
layout(location = 1) in vec2 oUv;
layout(location = 2) in vec4 oColor;
layout(location = 3) in vec4 oLightResult;
layout(location = 4) in vec3 vNormal;

layout(std140, set = 0, binding = 0) uniform DrawBlock
{
	mat4 uCurrentProjectionMatrix;
	mat4 uCurrentModelViewMatrix;
	mat4 uCurrentTextureMatrix;
	mat4 uNormalMatrix;
	vec4 uLightPosition;
	vec4 uLightAmbient;
	vec4 uLightDiffuse;
	vec4 uLightSpecular;
	vec4 uLightModel;
	vec4 uMaterialAmbient;
	vec4 uMaterialDiffuse;
	vec4 uMaterialSpecular;
	vec4 uMaterialEmission;
	ivec4 uFlags;
	vec4 uMisc;
	vec4 uFog;
	vec4 uFogColor;
};

layout(set = 1, binding = 0) uniform sampler2D uTexture;

// One frame's cascades. VulkanGL's shadow uniform table must match the offsets.
layout(std140, set = 2, binding = 0) uniform ShadowBlock
{
	mat4 uInverseView;               // 0: worked out from the view matrix
	mat4 uCurrentViewMatrix;         // 64
	mat4 uLightSpaceMatrix[4];       // 128
	vec4 uShadowSplit;               // 384
	vec4 uShadowBias;                // 400
	vec4 uShadowNormalBias;          // 416
	vec4 uShadowTexelWorldSize;      // 432
	float uShadowStrength;           // 448
	float uShadowFilterRadius;       // 452
	int uShadowSmooth;               // 456
	int uShadowCascadeCount;         // 460
	int uShadowEnabled;              // 464
};

layout(set = 2, binding = 1) uniform sampler2DShadow uShadowMap0;
layout(set = 2, binding = 2) uniform sampler2DShadow uShadowMap1;
layout(set = 2, binding = 3) uniform sampler2DShadow uShadowMap2;
layout(set = 2, binding = 4) uniform sampler2DShadow uShadowMap3;

layout(location = 0) out vec4 fragColor;

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

float GetCascadeShadowFactor(sampler2DShadow shadowMap, int cascade)
{
	// World position, moved along the normal by the receiver's bias, as upstream's vertex shader does.
	float bias = uShadowBias[cascade], normalBias = uShadowNormalBias[cascade];
	vec3 world = (uInverseView * vec4(oViewPos.xyz, 1.0)).xyz;
	float lengthSquared = dot(vNormal, vNormal);
	vec3 normal = lengthSquared < 1e-10 ? uLightPosition.xyz : vNormal * inversesqrt(lengthSquared);
	vec3 worldNormal = lengthSquared < 1e-10 ? vec3(0.0) : mat3(uInverseView) * normal;
	vec4 posLightSpace = uLightSpaceMatrix[cascade] * vec4(world + worldNormal * (normalBias * uShadowTexelWorldSize[cascade]), 1.0);
	vec3 projCoords = posLightSpace.xyz / posLightSpace.w * 0.5 + 0.5;
	if (projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0 || projCoords.z < 0.0 || projCoords.z > 1.0)
	{
		return 1.0;
	}

	float slope = clamp(1.0 - dot(normal, uLightPosition.xyz), 0.0, 1.0);
	float activeBias = bias * (1.0 + slope * clamp(normalBias, 0.0, 1.5));
	vec2 texelSize = 1.0 / vec2(textureSize(shadowMap, 0));
	float radiusScaled = uShadowSmooth != 0 ? clamp(uShadowFilterRadius, SHADOW_MIN_RADIUS, SHADOW_MAX_RADIUS) : SHADOW_MIN_RADIUS;
	float biasedDepth = projCoords.z - activeBias * (1.0 + (radiusScaled - SHADOW_MIN_RADIUS) * 0.25);
	biasedDepth = max(biasedDepth, projCoords.z - (bias * SHADOW_BIAS_GUARD_TEXELS + SHADOW_BIAS_EPSILON));
	float shadow = 0.0;
	if (uShadowSmooth != 0)
	{
		float phi = interleavedGradientNoise(gl_FragCoord.xy) * SHADOW_TWO_PI;
		float cosPhi = cos(phi);
		float sinPhi = sin(phi);
		for (int i = 0; i < 5; ++i)
		{
			vec2 offset = vogelDiskSample(i, cosPhi, sinPhi) * texelSize * radiusScaled;
			shadow += texture(shadowMap, vec3(projCoords.xy + offset, biasedDepth));
		}

		return shadow * 0.2;
	}

	shadow += texture(shadowMap, vec3(projCoords.xy + vec2(-0.5, -0.5) * texelSize, biasedDepth));
	shadow += texture(shadowMap, vec3(projCoords.xy + vec2(0.5, -0.5) * texelSize, biasedDepth));
	shadow += texture(shadowMap, vec3(projCoords.xy + vec2(-0.5, 0.5) * texelSize, biasedDepth));
	shadow += texture(shadowMap, vec3(projCoords.xy + vec2(0.5, 0.5) * texelSize, biasedDepth));
	return shadow * 0.25;
}

float SampleCascadeByIndex(int index)
{
	if (index == 0) return GetCascadeShadowFactor(uShadowMap0, 0);
	if (index == 1) return GetCascadeShadowFactor(uShadowMap1, 1);
	if (index == 2) return GetCascadeShadowFactor(uShadowMap2, 2);
	if (index == 3) return GetCascadeShadowFactor(uShadowMap3, 3);
	return 1.0;
}

float CalculateShadowFactor()
{
	if (uShadowStrength <= 0.0)
	{
		return 1.0;
	}

	float depth = abs(oViewPos.z);
	float shadow = 1.0;
	for (int i = 0; i < uShadowCascadeCount; i++)
	{
		float split = uShadowSplit[i];
		float blendRange = clamp(split * SHADOW_BLEND_FRACTION, SHADOW_MIN_BLEND, SHADOW_MAX_BLEND);
		if (depth < split)
		{
			shadow = SampleCascadeByIndex(i);
			if (i < uShadowCascadeCount - 1)
			{
				float blendStart = split - blendRange;
				if (depth > blendStart)
				{
					shadow = mix(shadow, SampleCascadeByIndex(i + 1), smoothstep(blendStart, split, depth));
				}
			}
			else
			{
				float fadeStart = split - blendRange * 2.0;
				if (depth > fadeStart)
				{
					shadow = mix(shadow, 1.0, smoothstep(fadeStart, split, depth));
				}
			}

			break;
		}
	}

	return mix(1.0, shadow, uShadowStrength);
}

void main()
{
	int flags = uFlags.x;
	vec4 finalColor;
	if ((flags & 16) == 0)
	{
		// only the RGB of the colour: its A is part of the opacity
		finalColor = vec4(oColor.rgb, 1.0) * texture(uTexture, oUv);
	}
	else
	{
		// no alpha channel from the texture (MSTS shape)
		finalColor = vec4(oColor.rgb, 1.0) * vec4(texture(uTexture, oUv).xyz, 1.0);
	}

	if ((flags & 1) == 0 && (flags & 4) == 0)
	{
		// not emissive, and lit: multiply by the brightness
		finalColor.rgb *= uMisc.x;
	}

	finalColor.a *= uMisc.y;

	// alpha functions by their GL enum values, as upstream
	float alphaFunction = uMisc.z;
	if (alphaFunction == 513.0) // Less
	{
		if (finalColor.a >= uMisc.w)
		{
			discard;
		}
	}
	else if (alphaFunction == 514.0) // Equal
	{
		if (!(abs(finalColor.a - uMisc.w) < 0.00001))
		{
			discard;
		}
	}
	else if (alphaFunction == 516.0) // Greater
	{
		if (finalColor.a <= uMisc.w)
		{
			discard;
		}
	}

	// lighting after the alpha test, as upstream
	if ((flags & 1) == 0 && (flags & 4) == 0)
	{
		finalColor.rgb *= oLightResult.rgb * CalculateShadowFactor();
		finalColor.a *= oLightResult.a;
	}
	else
	{
		finalColor *= oLightResult;
	}

	float fogFactor = 1.0;
	if (uFlags.z != 0)
	{
		if (uFlags.w != 0)
		{
			fogFactor = clamp((uFog.y - length(oViewPos)) / (uFog.y - uFog.x), 0.0, 1.0);
		}
		else
		{
			fogFactor = exp(-pow(uFog.z * (gl_FragCoord.z / gl_FragCoord.w), 2.0));
		}
	}

	fragColor = vec4(mix(uFogColor.xyz, finalColor.rgb, fogFactor), finalColor.a);
}
