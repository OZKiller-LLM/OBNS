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
 * The Vulkan version of upstream's assets/Shaders/shadow_depth.vert: a face's position in a cascade's
 * light space. The cascade's shadow map is read as GL reads it (u = x * 0.5 + 0.5, v = y * 0.5 + 0.5,
 * depth = z * 0.5 + 0.5), so Y is not flipped here as it is on the screen, and depth only goes from
 * GL's -1..1 to Vulkan's 0..1. VulkanGL's uniform table must match DepthBlock.
 */
#version 450

layout(location = 0) in vec3 iPosition;
layout(location = 2) in vec2 iUv;
layout(location = 4) in ivec3 iMatrixChain;

layout(std140, set = 0, binding = 0) uniform DepthBlock
{
	mat4 uLightSpaceMatrix;          // 0
	mat4 uModelMatrix;               // 64
	mat4 uTextureMatrix;             // 128
	int uHasTexture;                 // 192
	float uAlphaCutoff;              // 196
	float uMaterialAlpha;            // 200
	int uMaterialFlags;              // 204
};

layout(std140, set = 0, binding = 1) uniform uAnimationMatricies
{
	mat4 modelMatricies[128];
};

layout(location = 0) out vec2 vUv;

vec3 transformVector(vec3 v, int m)
{
	// ignoreW per DirectX, as upstream
	return vec3(
		v.x * modelMatricies[m][0].x + v.y * modelMatricies[m][1].x + v.z * modelMatricies[m][2].x + modelMatricies[m][3].x,
		v.x * modelMatricies[m][0].y + v.y * modelMatricies[m][1].y + v.z * modelMatricies[m][2].y + modelMatricies[m][3].y,
		v.x * modelMatricies[m][0].z + v.y * modelMatricies[m][1].z + v.z * modelMatricies[m][2].z + modelMatricies[m][3].z);
}

vec3 applyChain(vec3 pos, int chain)
{
	if (chain != 0)
	{
		// packed matrix indices, outermost first
		int m0 = (chain & (0xff << 24)) >> 24;
		int m1 = (chain >> 16) & 0xff;
		int m2 = (chain & 0xff00) >> 8;
		int m3 = chain & 0xff;
		if (m0 >= 0 && m0 < 255) pos = transformVector(pos, m0);
		if (m1 >= 0 && m1 < 255) pos = transformVector(pos, m1);
		if (m2 >= 0 && m2 < 255) pos = transformVector(pos, m2);
		if (m3 >= 0 && m3 < 255) pos = transformVector(pos, m3);
	}

	return pos;
}

void main()
{
	vec3 pos = iPosition;
	pos = applyChain(pos, iMatrixChain.x);
	pos = applyChain(pos, iMatrixChain.y);
	pos = applyChain(pos, iMatrixChain.z);

	// OpenBVE negates Z for world geometry.
	pos.z = -pos.z;

	vUv = (uTextureMatrix * vec4(iUv, 1.0, 1.0)).xy;
	vec4 clip = uLightSpaceMatrix * uModelMatrix * vec4(pos, 1.0);
	gl_Position = vec4(clip.x, clip.y, 0.5 * (clip.z + clip.w), clip.w);
}
