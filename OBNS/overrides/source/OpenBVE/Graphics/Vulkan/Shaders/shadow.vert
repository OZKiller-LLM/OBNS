// Derived from upstream's shaders, under their licence:
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

// OBNS: the Vulkan world layer's shadow pass (OBNS/VULKAN.md): upstream's shadow_depth.vert (OpenBVE 1.14.1.2,
// BSD-2-Clause, Aditiya Afrizal, The OpenBVE Project) - a caster in the cascade's light space - with the world
// layer's records: the draw's object, its animation matrices and texture matrix.
// Compiled with: glslang -V --target-env vulkan1.3 -o shadow.vert.spv shadow.vert
// and the core Vulkan 1.0 tier's: glslang -V --target-env vulkan1.0 -o shadow.vk10.vert.spv shadow.vert
#version 450

struct Frame
{
	mat4 projection;
	vec4 lightPosition;  // xyz, pre-normalized
	vec4 lightAmbient;   // rgb
	vec4 lightDiffuse;   // rgb
	vec4 lightSpecular;  // rgb
	vec4 lightModel;
	vec4 fogColor;       // rgb
	float fogStart, fogEnd, fogDensity;
	int fogIsLinear;
	int isLight;
	int shadowEnabled;       // the world layer's, with shadows on
	int shadowCascades;
	int shadowSmooth;
	mat4 inverseView;        // view to camera-relative world (the shadows' space)
	mat4 lightSpace[4];      // upstream's cascades' light-space matrices
	vec4 shadowSplit;        // where each cascade ends (view depth)
	vec4 shadowBias;
	vec4 shadowNormalBias;   // texels
	vec4 shadowTexelSize;    // a texel's size in the world, per cascade
	float shadowStrength;
	float shadowFilterRadius;
	float pad0, pad1;
};

struct Object
{
	mat4 modelView;
	vec4 normal0, normal1, normal2; // the normal matrix's columns
	mat4 textureMatrix;
	int matrixBase;                 // the object's animation matrices in the frame's matrix buffer
	int matrixFlag;                 // (the CPU's: whether the base is moved when its records are merged)
	int matrixCount;                // how many: a chain's indices beyond them are skipped (the buffer ends there)
	int pad2;
};

struct Draw
{
	vec4 materialColor;   // ambient and diffuse
	vec4 materialSpecular;
	vec4 materialEmission; // rgb; w: shininess
	int flags;
	float brightness;
	float opacity;
	int textureIndex;
	int samplerIndex;
	int alphaFunction;
	float alphaReference;
	int objectIndex;
	int fog;              // whether fog applies (additive faces have none)
	int cullFront;        // discard GL-front-facing fragments (ordered passes, drawn without pipeline culling)
	int pad0, pad1;
};

layout(std430, set = 0, binding = 0) readonly buffer Frames { Frame frames[]; };
layout(std430, set = 0, binding = 1) readonly buffer Objects { Object objects[]; };
layout(std430, set = 0, binding = 2) readonly buffer Draws { Draw draws[]; };
layout(std430, set = 0, binding = 3) readonly buffer Matrices { mat4 matrices[]; };

layout(push_constant) uniform Push { int frameIndex; int cascade; } push;

layout(location = 0) in vec3 iPosition;
layout(location = 2) in vec2 iUv;
layout(location = 4) in ivec3 iMatrixChain;

layout(location = 0) out vec2 vUv;
layout(location = 1) flat out int vDraw;

vec3 transformVector(vec3 v, int m)
{
	mat4 a = matrices[m];
	// ignoreW per DirectX, as upstream.
	return vec3(v.x * a[0].x + v.y * a[1].x + v.z * a[2].x + a[3].x,
	            v.x * a[0].y + v.y * a[1].y + v.z * a[2].y + a[3].y,
	            v.x * a[0].z + v.y * a[1].z + v.z * a[2].z + a[3].z);
}

vec3 applyChain(vec3 pos, int chain, int base, int count)
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

void main()
{
	Draw d = draws[gl_InstanceIndex];
	Object o = objects[d.objectIndex];
	Frame f = frames[push.frameIndex];
	vDraw = gl_InstanceIndex;

	vec3 pos = iPosition;
	pos = applyChain(pos, iMatrixChain.x, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, iMatrixChain.y, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, iMatrixChain.z, o.matrixBase, o.matrixCount);
	pos.z = -pos.z;

	// The camera-relative world position (the object's model-view, taken back through the view), in the cascade's light space.
	vec4 world = f.inverseView * (o.modelView * vec4(pos, 1.0));
	gl_Position = f.lightSpace[push.cascade] * world;
	// OpenGL's clip depth (-w..w) to Vulkan's (0..w): the same depth as upstream's map holds.
	gl_Position.z = 0.5 * (gl_Position.z + gl_Position.w);
	vUv = (o.textureMatrix * vec4(iUv, 1.0, 1.0)).xy;
}
