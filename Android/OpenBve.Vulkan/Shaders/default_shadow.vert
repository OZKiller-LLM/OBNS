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
 * default.vert (see there) for faces that receive shadows: it also hands on the view space normal,
 * for default_shadow.frag. Only the faces drawn while shadows are on use this one.
 *
 * The Vulkan version of upstream's assets/Shaders/default.vert, for the native Vulkan renderer.
 * The same lighting and matrix-chain animation, line for line; what differs:
 *  - the uniforms are one std140 block (DrawBlock below), whose byte offsets VulkanGL's
 *    uniform table must match;
 *  - the normal matrix, transpose(inverse(mat3(modelview))), comes from the CPU once per
 *    modelview change rather than being worked out for every vertex;
 *  - no shadow cascades (the Android port draws without shadows);
 *  - gl_Position is turned from OpenGL's clip space into Vulkan's (Y down, depth 0..1) and
 *    rotated for the swapchain's pre-rotation (uClip).
 */
#version 450

layout(location = 0) in vec3 iPosition;
layout(location = 1) in vec3 iNormal;
layout(location = 2) in vec2 iUv;
layout(location = 3) in vec4 iColor;
layout(location = 4) in ivec3 iMatrixChain;

layout(std140, set = 0, binding = 0) uniform DrawBlock
{
	mat4 uCurrentProjectionMatrix;   //   0
	mat4 uCurrentModelViewMatrix;    //  64
	mat4 uCurrentTextureMatrix;      // 128
	mat4 uNormalMatrix;              // 192 (mat3 in the upper left)
	vec4 uLightPosition;             // 256 xyz
	vec4 uLightAmbient;              // 272 xyz
	vec4 uLightDiffuse;              // 288 xyz
	vec4 uLightSpecular;             // 304 xyz
	vec4 uLightModel;                // 320
	vec4 uMaterialAmbient;           // 336
	vec4 uMaterialDiffuse;           // 352
	vec4 uMaterialSpecular;          // 368
	vec4 uMaterialEmission;          // 384 xyz, w: shininess
	ivec4 uFlags;                    // 400 x: material flags, y: is light, z: is fog, w: fog is linear
	vec4 uMisc;                      // 416 x: brightness, y: opacity, zw: alpha test
	vec4 uFog;                       // 432 x: start, y: end, z: density
	vec4 uFogColor;                  // 448 xyz
};

layout(std140, set = 0, binding = 1) uniform uAnimationMatricies
{
	mat4 modelMatricies[128];
};

layout(push_constant) uniform ClipBlock
{
	vec4 uClip;
};

layout(location = 0) out vec4 oViewPos;
layout(location = 1) out vec2 oUv;
layout(location = 2) out vec4 oColor;
layout(location = 3) out vec4 oLightResult;
layout(location = 4) out vec3 vNormal;

vec4 getLightResult(vec3 normal)
{
	float nDotVP = max(0.0, dot(normal, normalize(uLightPosition.xyz)));
	float nDotHV = max(0.0, dot(normal, normalize(vec3(oViewPos.xyz + uLightPosition.xyz))));
	float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, uMaterialEmission.w);

	vec4 ambient = vec4(uLightAmbient.xyz, 1.0);
	vec4 diffuse = vec4(uLightDiffuse.xyz, 1.0) * nDotVP;
	vec4 specular = vec4(uLightSpecular.xyz, 1.0) * pf;

	vec4 sceneColor = (uFlags.x & 1) != 0 ? vec4(uMaterialEmission.xyz, 1.0) + uMaterialAmbient * uLightModel : uLightModel;
	vec4 finalColor = sceneColor + ambient * uMaterialAmbient + diffuse * uMaterialDiffuse + specular * uMaterialSpecular;
	return clamp(finalColor, 0.0, 1.0);
}

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
	oColor = iColor;
	pos = applyChain(pos, iMatrixChain.x);
	pos = applyChain(pos, iMatrixChain.y);
	pos = applyChain(pos, iMatrixChain.z);

	pos.z = -pos.z;
	oViewPos = uCurrentModelViewMatrix * vec4(pos, 1.0);
	vec4 clip = uCurrentProjectionMatrix * oViewPos;
	gl_Position = vec4(uClip.x * clip.x + uClip.y * clip.y, uClip.z * clip.x + uClip.w * clip.y, 0.5 * (clip.z + clip.w), clip.w);

	oUv = (uCurrentTextureMatrix * vec4(iUv, 1.0, 1.0)).xy;
	// Unit length where it can be: a zero length normal (a mesh without normals) stays zero, and shadows then use the light's direction.
	vec3 viewNormal = mat3(uNormalMatrix) * vec3(iNormal.x, iNormal.y, -iNormal.z);
	float normalLength = dot(viewNormal, viewNormal);
	vNormal = normalLength > 1e-10 ? viewNormal * inversesqrt(normalLength) : vec3(0.0);
	if (uFlags.y != 0 && (uFlags.x & 4) == 0)
	{
		oLightResult = getLightResult(normalize(viewNormal));
	}
	else
	{
		oLightResult = uMaterialAmbient;
	}
}
