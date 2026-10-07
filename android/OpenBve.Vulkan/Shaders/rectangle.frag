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
 * The Vulkan version of upstream's assets/Shaders/rectangle.frag (2D rectangles and text): the
 * texture times the colour, with upstream's alpha test. See rectangle.vert for the block.
 */
#version 450

layout(std140, set = 0, binding = 0) uniform Block2D
{
	mat4 uCurrentProjectionMatrix;
	mat4 uCurrentModelViewMatrix;
	vec4 uPointSize;
	vec4 uCoordinates;
	vec4 uAtlasLocation;
	vec4 uColor;
	vec4 uAlphaTest;
};

layout(set = 1, binding = 0) uniform sampler2D uTexture;

layout(location = 0) in vec2 textureCoord;
layout(location = 0) out vec4 fragColor;

void main()
{
	vec4 finalColor = texture(uTexture, textureCoord) * uColor;
	if (uAlphaTest.x == 513.0) // Less
	{
		if (finalColor.a > uAlphaTest.y)
		{
			discard;
		}
	}
	else if (uAlphaTest.x == 514.0) // Equal
	{
		if (!(abs(finalColor.a - 1.0) < 0.00001))
		{
			discard;
		}
	}
	else if (uAlphaTest.x == 516.0) // Greater
	{
		if (finalColor.a <= uAlphaTest.y)
		{
			discard;
		}
	}

	fragColor = finalColor;
}
