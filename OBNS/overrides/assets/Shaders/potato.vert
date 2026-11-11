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

// OBNS: Potato mode's world shader (the user's choice, 2026-11-11, for a Broadwell MacBook Pro): upstream's default.vert
// with the work a slow integrated GPU can be spared. Lighting stays per vertex as upstream's, but the normal is turned by
// the model-view matrix itself rather than its inverse transpose (two 4x4 inverses a vertex upstream, where the matrices
// are rotations and translations); no shadow coordinates (Potato mode turns shadows off); and the fog factor is worked
// out here, once a vertex, rather than for every pixel. Eleven floats are interpolated for each pixel rather than 33.
#version 410 core

precision highp int;
precision highp float;

struct Light
{
	vec3 position;
	vec3 ambient;
	vec3 diffuse;
	vec3 specular;
	vec4 lightModel;
};

struct MaterialColor
{
	vec4 ambient;
	vec4 diffuse;
	vec4 specular;
	vec3 emission;
	float shininess;
};

layout(location = 0) in vec3 iPosition;
layout(location = 1) in vec3 iNormal;
layout(location = 2) in vec2 iUv;
layout(location = 3) in vec4 iColor;
layout(location = 4) in ivec3 iMatrixChain;

uniform mat4 uCurrentProjectionMatrix;
uniform mat4 uCurrentModelViewMatrix;
uniform mat4 uCurrentTextureMatrix;
uniform bool uIsLight;
uniform Light uLight;
uniform MaterialColor uMaterial;
uniform int uMaterialFlags;
uniform bool uIsFog;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uFogDensity;
uniform bool uFogIsLinear;

layout (std140) uniform uAnimationMatricies {
    mat4 modelMatricies[128];
};

out vec2 oUv;
out vec4 oColor;
out vec4 oLightResult;
out float oFog;

vec4 getLightResult(vec3 viewPos)
{
	// The model-view matrix's own rotation for the normal: right for rotations, and for uniform scales once normalized.
	vec3 n = mat3(uCurrentModelViewMatrix) * vec3(iNormal.x, iNormal.y, -iNormal.z);
	float l2 = dot(n, n);
	n = l2 < 1e-10 ? vec3(0.0, 1.0, 0.0) : n * inversesqrt(l2);
	float nDotVP = max(0.0, dot(n, vec3(uLight.position))); // pre-normalized on CPU in SetLightPosition
	float nDotHV = max(0.0, dot(n, normalize(viewPos + uLight.position)));
	float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, uMaterial.shininess);
	vec4 ambient = vec4(uLight.ambient, 1.0);
	vec4 diffuse = vec4(uLight.diffuse, 1.0) * nDotVP;
	vec4 specular = vec4(uLight.specular, 1.0) * pf;
	vec4 sceneColor = (uMaterialFlags & 1) != 0 ? vec4(uMaterial.emission, 1.0) + uMaterial.ambient * uLight.lightModel : uLight.lightModel;
	vec4 finalColor = sceneColor + ambient * uMaterial.ambient + diffuse * uMaterial.diffuse + specular * uMaterial.specular;
	return clamp(finalColor, 0.0, 1.0);
}

vec3 transformVector(vec3 initialVector, int matrixIndex)
{
	float X = (initialVector.x * modelMatricies[matrixIndex][0].x) + (initialVector.y * modelMatricies[matrixIndex][1].x) + (initialVector.z * modelMatricies[matrixIndex][2].x);
	float Y = (initialVector.x * modelMatricies[matrixIndex][0].y) + (initialVector.y * modelMatricies[matrixIndex][1].y) + (initialVector.z * modelMatricies[matrixIndex][2].y);
	float Z = (initialVector.x * modelMatricies[matrixIndex][0].z) + (initialVector.y * modelMatricies[matrixIndex][1].z) + (initialVector.z * modelMatricies[matrixIndex][2].z);
	// ignoreW per DirectX

	X += 1 * modelMatricies[matrixIndex][3].x;
	Y += 1 * modelMatricies[matrixIndex][3].y;
	Z += 1 * modelMatricies[matrixIndex][3].z;
	return vec3(X, Y, Z);
}

void main()
{
	vec3 pos = vec3(iPosition);
	oColor = iColor;
	if(iMatrixChain.x != 0)
	{	
		// unpack packed matrix indicies
		int m0 = (iMatrixChain.x & (0xff << 24)) >> 24;
		int m1 = (iMatrixChain.x >> 16) & 0xff;
		int m2 = (iMatrixChain.x & 0xff00) >> 8;
		int m3 = (iMatrixChain.x & 0xff);
        
		if(m0 >=0 && m0 < 255)
		{	
			pos = transformVector(pos, m0);
		}
		
		if(m1 >=0 && m1 < 255)
		{
			pos = transformVector(pos, m1);
		}
		
		if(m2 >=0 && m2 < 255)
		{
			pos = transformVector(pos, m2);
		}

		if(m3 >=0 && m3 < 255)
		{
			pos = transformVector(pos, m3);
		}		
	}
	
	if(iMatrixChain.y != 0)
	{	
		// unpack packed matrix indicies
		int m0 = (iMatrixChain.y & (0xff << 24)) >> 24;
		int m1 = (iMatrixChain.y >> 16) & 0xff;
		int m2 = (iMatrixChain.y & 0xff00) >> 8;
		int m3 = (iMatrixChain.y & 0xff);
        
		if(m0 >=0 && m0 < 255)
		{	
			pos = transformVector(pos, m0);
		}
		
		if(m1 >=0 && m1 < 255)
		{
			pos = transformVector(pos, m1);
		}
		
		if(m2 >=0 && m2 < 255)
		{
			pos = transformVector(pos, m2);
		}

		if(m3 >=0 && m3 < 255)
		{
			pos = transformVector(pos, m3);
		}		
	}

	if(iMatrixChain.z != 0)
	{	
		// unpack packed matrix indicies
		int m0 = (iMatrixChain.z & (0xff << 24)) >> 24;
		int m1 = (iMatrixChain.z >> 16) & 0xff;
		int m2 = (iMatrixChain.z & 0xff00) >> 8;
		int m3 = (iMatrixChain.z & 0xff);
        
		if(m0 >=0 && m0 < 255)
		{	
			pos = transformVector(pos, m0);
		}
		
		if(m1 >=0 && m1 < 255)
		{
			pos = transformVector(pos, m1);
		}
		
		if(m2 >=0 && m2 < 255)
		{
			pos = transformVector(pos, m2);
		}

		if(m3 >=0 && m3 < 255)
		{
			pos = transformVector(pos, m3);
		}		
	}
	
	pos.z = -pos.z;
	vec4 viewPos = uCurrentModelViewMatrix * vec4(pos, 1.0);
	gl_Position = uCurrentProjectionMatrix * viewPos;
	oUv = (uCurrentTextureMatrix * vec4(iUv, 1.0, 1.0)).xy;
	oLightResult = uIsLight && (uMaterialFlags & 4) == 0 ? getLightResult(viewPos.xyz) : uMaterial.ambient;
	// Fog, as upstream's fragment shader works it out, at the vertex: linear by distance, else exponential-squared by depth.
	oFog = 1.0;
	if (uIsFog)
	{
		oFog = uFogIsLinear ? clamp((uFogEnd - length(viewPos.xyz)) / (uFogEnd - uFogStart), 0.0, 1.0) : exp(-pow(uFogDensity * gl_Position.w, 2.0));
	}
}
