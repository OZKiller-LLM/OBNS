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
 * The Vulkan version of upstream's assets/Shaders/text.vert (a glyph from the font atlas, from
 * six vertex indices). See rectangle.vert here for the uniform block.
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

layout(push_constant) uniform ClipBlock
{
	vec4 uClip;
};

layout(location = 0) out vec2 textureCoord;

void main()
{
	vec2 point = uPointSize.xy;
	vec2 size = uPointSize.zw;
	vec4 atlas = uAtlasLocation;
	vec2 corner;
	switch (gl_VertexIndex)
	{
		case 0: corner = vec2(0.0, 0.0); textureCoord = vec2(atlas.x, atlas.y); break;
		case 1: corner = vec2(1.0, 0.0); textureCoord = vec2(atlas.x + atlas.z, atlas.y); break;
		case 2: corner = vec2(1.0, 1.0); textureCoord = vec2(atlas.x + atlas.z, atlas.y + atlas.w); break;
		case 3: corner = vec2(0.0, 0.0); textureCoord = vec2(atlas.x, atlas.y); break;
		case 4: corner = vec2(0.0, 1.0); textureCoord = vec2(atlas.x, atlas.y + atlas.w); break;
		default: corner = vec2(1.0, 1.0); textureCoord = vec2(atlas.x + atlas.z, atlas.y + atlas.w); break;
	}

	vec4 clip = uCurrentProjectionMatrix * (uCurrentModelViewMatrix * vec4(point + size * corner, 0.0, 1.0));
	gl_Position = vec4(uClip.x * clip.x + uClip.y * clip.y, uClip.z * clip.x + uClip.w * clip.y, 0.5 * (clip.z + clip.w), clip.w);
}
