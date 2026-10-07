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
 * The Vulkan version of upstream's assets/Shaders/default.frag (see default.vert here for what
 * differs). Without shadows the shadow factor is 1, so that part of upstream's shader is left
 * out; the texture, brightness, opacity, alpha test, lighting and fog are as upstream.
 */
#version 450

layout(location = 0) in vec4 oViewPos;
layout(location = 1) in vec2 oUv;
layout(location = 2) in vec4 oColor;
layout(location = 3) in vec4 oLightResult;

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

layout(location = 0) out vec4 fragColor;

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
		finalColor.rgb *= oLightResult.rgb;
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
