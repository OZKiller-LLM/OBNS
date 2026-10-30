// OBNS: the Direct3D 11 world layer's shaders (OBNS/DIRECT3D.md): the Vulkan layer's world.vert and world.frag in HLSL
// (shader model 5.0), themselves upstream's default.vert and default.frag (OpenBVE 1.14.1.2, BSD-2-Clause, Christopher
// Lees, S520, Aditiya Afrizal, The OpenBVE Project), their uniforms read from the frame's structured buffers by the
// draw's index (an instance stream, as the draw's StartInstanceLocation). Compiled at start by d3dcompiler_47.dll:
// VS, and PS with OPAQUE_COVERAGE 1 or 0. Matrices are column-major, as GLSL reads the same records.
// Shadows are not drawn yet (step 5).

struct Frame
{
	column_major float4x4 projection;
	float4 lightPosition;  // xyz, pre-normalized
	float4 lightAmbient;   // rgb
	float4 lightDiffuse;   // rgb
	float4 lightSpecular;  // rgb
	float4 lightModel;
	float4 fogColor;       // rgb
	float fogStart, fogEnd, fogDensity;
	int fogIsLinear;
	int isLight;
	int shadowEnabled;
	int shadowCascades;
	int shadowSmooth;
	column_major float4x4 inverseView;
	column_major float4x4 lightSpace[4];
	float4 shadowSplit;
	float4 shadowBias;
	float4 shadowNormalBias;
	float4 shadowTexelSize;
	float shadowStrength;
	float shadowFilterRadius;
	float pad0, pad1;
};

struct ObjectRecord
{
	column_major float4x4 modelView;
	float4 normal0, normal1, normal2; // the normal matrix's columns
	column_major float4x4 textureMatrix;
	int matrixBase;
	int matrixFlag;
	int matrixCount;
	int pad2;
};

struct DrawRecord
{
	float4 materialColor;
	float4 materialSpecular;
	float4 materialEmission; // rgb; w: shininess
	int flags;
	float brightness;
	float opacity;
	int textureIndex;
	int samplerIndex;
	int alphaFunction;
	float alphaReference;
	int objectIndex;
	int fog;
	int cullFront;
	int pad0, pad1;
};

struct MatrixRecord
{
	column_major float4x4 m;
};

StructuredBuffer<Frame> frames : register(t0);
StructuredBuffer<ObjectRecord> objects : register(t1);
StructuredBuffer<DrawRecord> draws : register(t2);
StructuredBuffer<MatrixRecord> matrices : register(t3);
Texture2D faceTexture : register(t4);
SamplerState faceSampler : register(s0);

cbuffer Layer : register(b0)
{
	int frameIndex;
	int3 layerPad;
};

struct VertexIn
{
	float3 position : POSITION;
	float3 normal : NORMAL;
	float2 uv : TEXCOORD0;
	float4 color : COLOR;
	int3 chain : BLENDINDICES;
	uint draw : DRAW;
};

struct PixelIn
{
	float4 position : SV_Position;
	float4 viewPos : VIEWPOS;
	float2 uv : TEXCOORD0;
	float4 color : COLOR;
	float4 lightResult : LIGHT;
	nointerpolation uint draw : DRAW;
	float3 normal : NORMAL;
	float clipW : CLIPW;
};

struct PixelOut
{
	float4 color : SV_Target0;
	float depth : SV_Target1;
};

float3 safeNormalize(float3 v)
{
	float l2 = dot(v, v);
	if (l2 < 1e-10) return float3(0.0, 1.0, 0.0);
	return v * rsqrt(l2);
}

float3 transformVector(float3 v, int m)
{
	// ignoreW per DirectX, as upstream.
	return mul(matrices[m].m, float4(v, 1.0)).xyz;
}

float3 applyChain(float3 pos, int chain, int base, int count)
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

PixelIn VS(VertexIn input)
{
	PixelIn output;
	DrawRecord d = draws[input.draw];
	ObjectRecord o = objects[d.objectIndex];
	Frame f = frames[frameIndex];
	output.draw = input.draw;

	float3 pos = input.position;
	output.color = input.color;
	pos = applyChain(pos, input.chain.x, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.y, o.matrixBase, o.matrixCount);
	pos = applyChain(pos, input.chain.z, o.matrixBase, o.matrixCount);
	pos.z = -pos.z;

	output.viewPos = mul(o.modelView, float4(pos, 1.0));
	float4 clip = mul(f.projection, output.viewPos);
	// OpenGL's clip depth (-w..w) to Direct3D's (0..w): the same window depth as OpenGL's.
	clip.z = 0.5 * (clip.z + clip.w);
	output.clipW = clip.w;
	// OpenGL reads the image's first row as its bottom: drawn upside down (as Vulkan's is), so it comes out upright.
	clip.y = -clip.y;
	output.position = clip;

	output.uv = mul(o.textureMatrix, float4(input.uv, 1.0, 1.0)).xy;

	float3 n = float3(input.normal.x, input.normal.y, -input.normal.z);
	output.normal = safeNormalize(o.normal0.xyz * n.x + o.normal1.xyz * n.y + o.normal2.xyz * n.z);

	if (f.isLight != 0 && (d.flags & 4) == 0)
	{
		float3 normal = output.normal;
		float nDotVP = max(0.0, dot(normal, f.lightPosition.xyz));
		float nDotHV = max(0.0, dot(normal, normalize(output.viewPos.xyz + f.lightPosition.xyz)));
		float pf = nDotVP == 0.0 ? 0.0 : pow(nDotHV, d.materialEmission.w);
		float4 ambient = float4(f.lightAmbient.rgb, 1.0);
		float4 diffuse = float4(f.lightDiffuse.rgb, 1.0) * nDotVP;
		float4 specular = float4(f.lightSpecular.rgb, 1.0) * pf;
		float4 sceneColor = (d.flags & 1) != 0 ? float4(d.materialEmission.rgb, 1.0) + d.materialColor * f.lightModel : f.lightModel;
		float4 finalColor = sceneColor + ambient * d.materialColor + diffuse * d.materialColor + specular * d.materialSpecular;
		output.lightResult = clamp(finalColor, 0.0, 1.0);
	}
	else
	{
		output.lightResult = d.materialColor;
	}

	return output;
}

PixelOut PS(PixelIn input, bool frontFacing : SV_IsFrontFace)
{
	DrawRecord d = draws[input.draw];
	// Upstream culls OpenGL's front faces; drawn upside down with clockwise faces front, frontFacing is OpenGL's.
	if (d.cullFront != 0 && frontFacing)
	{
		discard;
	}

	float4 texel = faceTexture.Sample(faceSampler, input.uv);
	float4 finalColor;
	if ((d.flags & 16) == 0)
	{
		finalColor = float4(input.color.rgb, 1.0) * texel;
	}
	else
	{
		// Texture alpha disabled (MSTS shapes).
		finalColor = float4(input.color.rgb, 1.0) * float4(texel.rgb, 1.0);
	}

	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= d.brightness;
	}

	finalColor.a *= d.opacity;

	if (d.alphaFunction == 513) // Less
	{
		if (finalColor.a >= d.alphaReference) discard;
	}
	else if (d.alphaFunction == 514) // Equal
	{
		if (!(abs(finalColor.a - d.alphaReference) < 0.00001)) discard;
	}
	else if (d.alphaFunction == 516) // Greater
	{
		if (finalColor.a <= d.alphaReference) discard;
	}

	Frame f = frames[frameIndex];
	float shadow = 1.0;
	if ((d.flags & 1) == 0 && (d.flags & 4) == 0)
	{
		finalColor.rgb *= input.lightResult.rgb * shadow;
		finalColor.a *= input.lightResult.a;
	}
	else
	{
		finalColor *= input.lightResult;
	}

	float fogFactor = 1.0;
	if (d.fog != 0)
	{
		if (f.fogIsLinear != 0)
		{
			fogFactor = clamp((f.fogEnd - length(input.viewPos)) / (f.fogEnd - f.fogStart), 0.0, 1.0);
		}
		else
		{
			// OpenGL's gl_FragCoord.z / gl_FragCoord.w: the window depth times the clip w.
			fogFactor = exp(-pow(f.fogDensity * (input.position.z * input.clipW), 2.0));
		}
	}

	PixelOut output;
	output.color = float4(lerp(f.fogColor.rgb, finalColor.rgb, fogFactor), OPAQUE_COVERAGE ? 1.0 : finalColor.a);
	// The depth as a colour, for the composite (written only by passes that write depth, by the blend state's mask).
	output.depth = input.position.z;
	return output;
}
