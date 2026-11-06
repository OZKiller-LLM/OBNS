// OBNS 1.31N: the world and the cab on deko3d (DekoGL's world records, NX/OpenBve.NX/Deko/DekoGL.World.cs): Android's
// native Vulkan default.vert (upstream's lighting and matrix-chain animation, line for line), its uniforms split by how
// often they change - the frame's (projection, lights, fog), the object's (its matrices), the draw's (its material,
// pushed with the draw) - so a face costs no more than its own few values. Compiled by uam at build time.
#version 460

layout(location = 0) in vec3 iPosition;
layout(location = 1) in vec3 iNormal;
layout(location = 2) in vec2 iUv;
layout(location = 3) in vec4 iColor;
layout(location = 4) in ivec3 iMatrixChain;

layout(std140, binding = 0) uniform FrameBlock
{
	mat4 uProjection;        //   0
	vec4 uLightPosition;     //  64 xyz
	vec4 uLightAmbient;      //  80 xyz
	vec4 uLightDiffuse;      //  96 xyz
	vec4 uLightSpecular;     // 112 xyz
	vec4 uLightModel;        // 128
	ivec4 uFrameFlags;       // 144 x: is light, y: is fog, z: fog is linear
	vec4 uFog;               // 160 x: start, y: end, z: density
	vec4 uFogColor;          // 176 xyz
};

layout(std140, binding = 1) uniform uAnimationMatricies
{
	mat4 modelMatricies[128];
};

layout(std140, binding = 2) uniform ClipBlock
{
	vec4 uClip;
};

layout(std140, binding = 3) uniform ObjectBlock
{
	mat4 uModelView;         //   0
	mat4 uTextureMatrix;     //  64
	mat4 uNormalMatrix;      // 128 (mat3 in the upper left)
};

layout(std140, binding = 4) uniform DrawBlock
{
	vec4 uMaterialAmbient;   //  0
	vec4 uMaterialDiffuse;   // 16
	vec4 uMaterialSpecular;  // 32
	vec4 uMaterialEmission;  // 48 xyz, w: shininess
	ivec4 uDrawFlags;        // 64 x: material flags, y: no fog
	vec4 uMisc;              // 80 x: brightness, y: opacity, z: alpha function, w: its reference
};

layout(location = 0) out vec4 oViewPos;
layout(location = 1) out vec2 oUv;
layout(location = 2) out vec4 oColor;
layout(location = 3) out vec4 oLightResult;

vec4 getLightResult(vec3 normal)
{
	float nDotVP = max(0.0, dot(normal, normalize(uLightPosition.xyz)));
	float nDotHV = max(0.0, dot(normal, normalize(vec3(oViewPos.xyz + uLightPosition.xyz))));
	float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, uMaterialEmission.w);

	vec4 ambient = vec4(uLightAmbient.xyz, 1.0);
	vec4 diffuse = vec4(uLightDiffuse.xyz, 1.0) * nDotVP;
	vec4 specular = vec4(uLightSpecular.xyz, 1.0) * pf;

	vec4 sceneColor = (uDrawFlags.x & 1) != 0 ? vec4(uMaterialEmission.xyz, 1.0) + uMaterialAmbient * uLightModel : uLightModel;
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
	oViewPos = uModelView * vec4(pos, 1.0);
	vec4 clip = uProjection * oViewPos;
	gl_Position = vec4(uClip.x * clip.x + uClip.y * clip.y, uClip.z * clip.x + uClip.w * clip.y, 0.5 * (clip.z + clip.w), clip.w);

	oUv = (uTextureMatrix * vec4(iUv, 1.0, 1.0)).xy;
	if (uFrameFlags.x != 0 && (uDrawFlags.x & 4) == 0)
	{
		vec3 normal = normalize(mat3(uNormalMatrix) * vec3(iNormal.x, iNormal.y, -iNormal.z));
		oLightResult = getLightResult(normal);
	}
	else
	{
		oLightResult = uMaterialAmbient;
	}
}
