// OBNS: the Vulkan world layer's vertex shader (OBNS/VULKAN.md): upstream's default.vert (OpenBVE 1.14.1.2,
// BSD-2-Clause, Christopher Lees, S520, Aditiya Afrizal, The OpenBVE Project), its uniforms read from the frame's
// buffers by the draw's index (gl_InstanceIndex, the indirect draw's firstInstance).
// Compiled with: glslang -V --target-env vulkan1.3 -o world.vert.spv world.vert
// and the core Vulkan 1.0 tier's: glslang -V --target-env vulkan1.0 -o world.vk10.vert.spv world.vert
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

layout(push_constant) uniform Push { int frameIndex; } push;

layout(location = 0) in vec3 iPosition;
layout(location = 1) in vec3 iNormal;
layout(location = 2) in vec2 iUv;
layout(location = 3) in vec4 iColor;
layout(location = 4) in ivec3 iMatrixChain;

layout(location = 0) out vec4 oViewPos;
layout(location = 1) out vec2 oUv;
layout(location = 2) out vec4 oColor;
layout(location = 3) out vec4 oLightResult;
layout(location = 4) flat out int oDraw;
layout(location = 5) out vec3 vNormal;
layout(location = 6) out vec4 vPosLightSpace0;
layout(location = 7) out vec4 vPosLightSpace1;
layout(location = 8) out vec4 vPosLightSpace2;
layout(location = 9) out vec4 vPosLightSpace3;

vec3 safeNormalizeVec3(vec3 v)
{
	float l2 = dot(v, v);
	if (l2 < 1e-10) return vec3(0.0, 1.0, 0.0);
	return v * inversesqrt(l2);
}

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
	oDraw = gl_InstanceIndex;

	vec3 pos = iPosition;
	oColor = iColor;
	pos = applyChain(pos, iMatrixChain.x, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, iMatrixChain.y, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, iMatrixChain.z, o.matrixBase, o.matrixCount);
	pos.z = -pos.z;

	oViewPos = o.modelView * vec4(pos, 1.0);
	gl_Position = f.projection * oViewPos;
	// OpenGL's clip depth (-w..w) to Vulkan's (0..w): the same window depth as OpenGL's.
	gl_Position.z = 0.5 * (gl_Position.z + gl_Position.w);

	oUv = (o.textureMatrix * vec4(iUv, 1.0, 1.0)).xy;

	mat3 normalMatrix = mat3(o.normal0.xyz, o.normal1.xyz, o.normal2.xyz);
	vNormal = safeNormalizeVec3(normalMatrix * vec3(iNormal.x, iNormal.y, -iNormal.z));
	if (f.shadowEnabled != 0)
	{
		// As upstream: the camera-relative world position, pushed along the world normal towards the light by each
		// cascade's normal bias (texels times a texel's size in the world).
		vec4 worldPos4 = f.inverseView * oViewPos;
		vec3 worldNormal = safeNormalizeVec3(mat3(f.inverseView) * vNormal);
		vPosLightSpace0 = f.lightSpace[0] * vec4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.x * f.shadowTexelSize.x), 1.0);
		vPosLightSpace1 = f.lightSpace[1] * vec4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.y * f.shadowTexelSize.y), 1.0);
		vPosLightSpace2 = f.lightSpace[2] * vec4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.z * f.shadowTexelSize.z), 1.0);
		vPosLightSpace3 = f.lightSpace[3] * vec4(worldPos4.xyz + worldNormal * (f.shadowNormalBias.w * f.shadowTexelSize.w), 1.0);
	}
	else
	{
		vPosLightSpace0 = vec4(0.0);
		vPosLightSpace1 = vec4(0.0);
		vPosLightSpace2 = vec4(0.0);
		vPosLightSpace3 = vec4(0.0);
	}

	if (f.isLight != 0 && (d.flags & 4) == 0)
	{
		vec3 normal = vNormal;
		float nDotVP = max(0.0, dot(normal, f.lightPosition.xyz));
		float nDotHV = max(0.0, dot(normal, normalize(oViewPos.xyz + f.lightPosition.xyz)));
		float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, d.materialEmission.w);
		vec4 ambient = vec4(f.lightAmbient.rgb, 1.0);
		vec4 diffuse = vec4(f.lightDiffuse.rgb, 1.0) * nDotVP;
		vec4 specular = vec4(f.lightSpecular.rgb, 1.0) * pf;
		vec4 sceneColor = (d.flags & 1) != 0 ? vec4(d.materialEmission.rgb, 1.0) + d.materialColor * f.lightModel : f.lightModel;
		vec4 finalColor = sceneColor + ambient * d.materialColor + diffuse * d.materialColor + specular * d.materialSpecular;
		oLightResult = clamp(finalColor, 0.0, 1.0);
	}
	else
	{
		oLightResult = d.materialColor;
	}
}
