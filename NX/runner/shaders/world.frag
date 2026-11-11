// OBNS 1.32: the world and the cab on deko3d - Android's native Vulkan default.frag (the texture, brightness, opacity,
// alpha test, lighting and fog as upstream's), its uniforms split as world.vert's.
#version 460

layout(location = 0) in vec4 oViewPos;
layout(location = 1) in vec2 oUv;
layout(location = 2) in vec4 oColor;
layout(location = 3) in vec4 oLightResult;

layout(std140, binding = 0) uniform FrameBlock
{
	mat4 uProjection;
	vec4 uLightPosition;
	vec4 uLightAmbient;
	vec4 uLightDiffuse;
	vec4 uLightSpecular;
	vec4 uLightModel;
	ivec4 uFrameFlags;
	vec4 uFog;
	vec4 uFogColor;
};

layout(std140, binding = 4) uniform DrawBlock
{
	vec4 uMaterialAmbient;
	vec4 uMaterialDiffuse;
	vec4 uMaterialSpecular;
	vec4 uMaterialEmission;
	ivec4 uDrawFlags;
	vec4 uMisc;
};

layout(binding = 0) uniform sampler2D uTexture;

layout(location = 0) out vec4 fragColor;

void main()
{
	int flags = uDrawFlags.x;
	vec4 finalColor;
	if ((flags & 16) == 0)
	{
		// only the RGB of the colour: its A is part of the opacity
		finalColor = vec4(oColor.rgb, 1.0) * texture(uTexture, oUv);
	}
	else
	{
		// no alpha channel from the texture (MSTS shape)
		finalColor = vec4(oColor.rgb, 1.0) * vec4(texture(uTexture, oUv).xyz, 1.0);
	}

	if ((flags & 1) == 0 && (flags & 4) == 0)
	{
		// not emissive, and lit: multiply by the brightness
		finalColor.rgb *= uMisc.x;
	}

	finalColor.a *= uMisc.y;

	// alpha functions by their GL enum values, as upstream
	float alphaFunction = uMisc.z;
	if (alphaFunction == 513.0) // Less
	{
		if (finalColor.a >= uMisc.w)
		{
			discard;
		}
	}
	else if (alphaFunction == 514.0) // Equal
	{
		if (!(abs(finalColor.a - uMisc.w) < 0.00001))
		{
			discard;
		}
	}
	else if (alphaFunction == 516.0) // Greater
	{
		if (finalColor.a <= uMisc.w)
		{
			discard;
		}
	}

	// lighting after the alpha test, as upstream
	if ((flags & 1) == 0 && (flags & 4) == 0)
	{
		finalColor.rgb *= oLightResult.rgb;
		finalColor.a *= oLightResult.a;
	}
	else
	{
		finalColor *= oLightResult;
	}

	float fogFactor = 1.0;
	if (uFrameFlags.y != 0 && uDrawFlags.y == 0)
	{
		if (uFrameFlags.z != 0)
		{
			fogFactor = clamp((uFog.y - length(oViewPos)) / (uFog.y - uFog.x), 0.0, 1.0);
		}
		else
		{
			fogFactor = exp(-pow(uFog.z * (gl_FragCoord.z / gl_FragCoord.w), 2.0));
		}
	}

	fragColor = vec4(mix(uFogColor.xyz, finalColor.rgb, fogFactor), finalColor.a);
}
