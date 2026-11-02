// Derived from upstream's rectangle.vert and text.vert, under their licence (BSD-2-Clause, The OpenBVE Project).
// OBNS: the 2D part of a frame drawn by Vulkan (OBNS/RENDERERS.md). The six vertices upstream's shaders make from gl_VertexID,
// as a list: upstream draws them as a strip, but with culling on, which leaves each half of a rectangle covered once.
// Compiled with: glslang -V --target-env vulkan1.3 -o frame2d.vert.spv frame2d.vert
#version 450

layout(push_constant) uniform Quad
{
	vec4 position;   // left, top, width, height, in clip space
	vec4 coordinates; // left, top, width, height, in the texture
	vec4 color;
	vec2 alphaTest;
	int textureIndex;
	int samplerIndex;
} quad;

layout(location = 0) out vec2 textureCoord;

void main()
{
	// Which corner each of the six is: x and y, 0 or 1.
	const vec2 corners[6] = vec2[6](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(0, 1), vec2(1, 1));
	vec2 corner = corners[gl_VertexIndex];
	textureCoord = quad.coordinates.xy + corner * quad.coordinates.zw;
	gl_Position = vec4(quad.position.xy + corner * quad.position.zw, 0.0, 1.0);
}
