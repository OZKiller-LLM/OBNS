//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2024, Christopher Lees, S520, Aditiya Afrizal, The OpenBVE Project
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

// OBNS: Potato mode's world shader (potato.vert): upstream's default.frag without the shadow map's sampling and with the
// fog factor the vertex shader worked out. The colour, brightness, opacity and alpha tests are upstream's.
#version 410 core

precision highp float;

in vec2 oUv;
in vec4 oColor;
in vec4 oLightResult;
in float oFog;

uniform vec2 uAlphaTest;
uniform sampler2D uTexture;
uniform int uMaterialFlags;
uniform float uBrightness;
uniform float uOpacity;
uniform vec3 uFogColor;

out vec4 fragColor;

void main(void)
{
	vec4 finalColor;
	if((uMaterialFlags & 16) == 0)
	{
		finalColor = vec4(oColor.rgb, 1.0) * texture(uTexture, oUv); // only the colour's RGB: its A is part of the opacity
	}
	else
	{
		// the texture's alpha unused (MSTS shapes)
		finalColor = vec4(oColor.rgb, 1.0) * vec4(texture(uTexture, oUv).xyz, 1.0);
	}

	bool lit = (uMaterialFlags & 1) == 0 && (uMaterialFlags & 4) == 0;
	if(lit)
	{
		finalColor.rgb *= uBrightness;
	}

	finalColor.a *= uOpacity;

	// The alpha tests as upstream's, by GL.AlphaFunction's numbers: Less, Equal, Greater.
	if(uAlphaTest.x == 513)
	{
		if(finalColor.a >= uAlphaTest.y)
		{
			discard;
		}
	}
	else if(uAlphaTest.x == 514)
	{
		if(!(abs(finalColor.a - uAlphaTest.y) < 0.00001))
		{
			discard;
		}
	}
	else if(uAlphaTest.x == 516)
	{
		if(finalColor.a <= uAlphaTest.y)
		{
			discard;
		}
	}

	// The light after the alpha test, as upstream's (light through a translucent face mustn't decide whether it is drawn).
	if(lit)
	{
		finalColor.rgb *= oLightResult.rgb;
		finalColor.a *= oLightResult.a;
	}
	else
	{
		finalColor *= oLightResult;
	}

	fragColor = vec4(mix(uFogColor, finalColor.rgb, oFog), finalColor.a);
}
