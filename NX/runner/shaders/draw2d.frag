// OBNS 1.32: deko3d's 2D drawing - rounded rectangles and outlines from a distance function (smoothed over a pixel),
// text from the glyph atlas (its red channel as coverage), images from their own texture.
#version 460

layout (location = 0) in vec2 inUv;
layout (location = 1) in vec4 inLocal;
layout (location = 2) in vec3 inShape;
layout (location = 3) in vec4 inColor;

layout (location = 0) out vec4 outColor;

layout (binding = 0) uniform sampler2D atlas;
layout (binding = 1) uniform sampler2D image;

void main()
{
	vec4 color = inColor;
	if (inShape.z > 1.5)
	{
		color *= texture(image, inUv);
	}
	else if (inShape.z > 0.5)
	{
		color.a *= texture(atlas, inUv).r;
	}
	else
	{
		float radius = inShape.x;
		vec2 q = abs(inLocal.xy) - inLocal.zw + radius;
		float distance = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
		float coverage = clamp(0.5 - distance, 0.0, 1.0);
		if (inShape.y > 0.0)
		{
			coverage *= clamp(distance + inShape.y + 0.5, 0.0, 1.0);
		}

		color.a *= coverage;
	}

	outColor = color;
}
