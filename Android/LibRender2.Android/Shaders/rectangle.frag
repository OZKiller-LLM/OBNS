#version 150 core
// ANDROID PORT: a copy of upstream's assets/Shaders/rectangle.frag (OpenBVE 1.14.1.2), used in its place (LibRender2.Android.csproj): float literals in the alpha-test comparisons (GLSL ES has no implicit int to float conversion).
// On an upstream update: copy the file again and reapply the change (Android/UpstreamPatches).
in vec2 textureCoord;
uniform vec2 uAlphaTest;
uniform bool uRectangleHasColour;
uniform vec4 uColor;
uniform sampler2D uTexture;
out vec4 fragColor;

void main(void)
{
	vec4 textureColour = texture(uTexture, textureCoord);
	
	vec4 finalColor = textureColour * uColor;

	/*
	 * NOTES:
	 * Unused alpha functions must not be added to the shader
	 * This has a nasty affect on framerates
	 *
	 * A switch case block is also ~30% slower than the else-if
	 *
	 * Numbers used are those from the GL.AlphaFunction enum to allow
	 * for direct casts
	 */
	if(uAlphaTest.x == 513.0) // Less
	{
		if(finalColor.a > uAlphaTest.y)
		{
			discard;
		}
	}
	else if(uAlphaTest.x == 514.0) // Equal
	{
		if(!(abs(finalColor.a - 1.0) < 0.00001))
		{
			discard;
		}
	}
	else if(uAlphaTest.x == 516.0) // Greater
	{
		if(finalColor.a <= uAlphaTest.y)
		{
			discard;
		}
	}
	
	fragColor = finalColor;
	
}
