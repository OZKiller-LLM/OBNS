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
 * The Vulkan version of upstream's assets/Shaders/rectangle.vert (2D rectangles, from six vertex
 * indices and no vertex data). The uniforms are one std140 block shared with text.vert and
 * rectangle.frag (Block2D in VulkanGL); gl_Position goes into Vulkan's clip space as in
 * default.vert.
 */
#version 450

layout(std140, set = 0, binding = 0) uniform Block2D
{
	mat4 uCurrentProjectionMatrix;   //   0
	mat4 uCurrentModelViewMatrix;    //  64
	vec4 uPointSize;                 // 128 xy: point, zw: size
	vec4 uCoordinates;               // 144 xy
	vec4 uAtlasLocation;             // 160
	vec4 uColor;                     // 176
	vec4 uAlphaTest;                 // 192 xy
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
	vec2 corner;
	switch (gl_VertexIndex)
	{
		case 0: corner = vec2(0.0, 0.0); textureCoord = vec2(0.0, 0.0); break;
		case 1: corner = vec2(1.0, 0.0); textureCoord = vec2(uCoordinates.x, 0.0); break;
		case 2: corner = vec2(1.0, 1.0); textureCoord = uCoordinates.xy; break;
		case 3: corner = vec2(0.0, 0.0); textureCoord = vec2(0.0, 0.0); break;
		case 4: corner = vec2(0.0, 1.0); textureCoord = vec2(0.0, uCoordinates.y); break;
		default: corner = vec2(1.0, 1.0); textureCoord = uCoordinates.xy; break;
	}

	vec4 clip = uCurrentProjectionMatrix * (uCurrentModelViewMatrix * vec4(point + size * corner, 0.0, 1.0));
	gl_Position = vec4(uClip.x * clip.x + uClip.y * clip.y, uClip.z * clip.x + uClip.w * clip.y, 0.5 * (clip.z + clip.w), clip.w);
}
