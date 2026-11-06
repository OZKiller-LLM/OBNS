// OBNS 1.31N: deko3d's 2D drawing (the menu, the loading screen, later the HUD), compiled by uam at build time.
// Positions are in the menu's units (1280x720 whatever the screen), from the top left.
#version 460

layout (location = 0) in vec2 inPosition;
layout (location = 1) in vec2 inUv;
layout (location = 2) in vec4 inLocal;   // xy: the point from the rectangle's centre; zw: the rectangle's half size
layout (location = 3) in vec3 inShape;   // x: corner radius; y: outline thickness (0: filled); z: 0 shape, 1 text, 2 image
layout (location = 4) in vec4 inColor;

layout (location = 0) out vec2 outUv;
layout (location = 1) out vec4 outLocal;
layout (location = 2) out vec3 outShape;
layout (location = 3) out vec4 outColor;

layout (std140, binding = 0) uniform Transform
{
	vec4 scale; // x: 2 / width, y: 2 / height, z: +1 or -1 for the way Y points
} transform;

void main()
{
	gl_Position = vec4(inPosition.x * transform.scale.x - 1.0, transform.scale.z * (1.0 - inPosition.y * transform.scale.y), 0.0, 1.0);
	outUv = inUv;
	outLocal = inLocal;
	outShape = inShape;
	outColor = inColor;
}
