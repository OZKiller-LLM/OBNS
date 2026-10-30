// OBNS: not an upstream file. OpenGL's composite of a world layer drawn by another API (OBNS/VULKAN.md, OBNS/DIRECT3D.md):
// its colour laid over the frame as premultiplied colour, its alpha the world's coverage, and its depth written into
// OpenGL's depth buffer; the cab layer's colour laid over later, colour only.

using System;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	internal sealed class WorldComposite : IDisposable
	{
		private const string VertexSource = @"#version 330 core
out vec2 uv;
void main()
{
	// One triangle covering the viewport.
	vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	uv = p;
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

		// The depth is read from .r: a depth image (Vulkan) or a single-channel colour image holding it (Direct3D 11).
		private const string FragmentSource = @"#version 330 core
uniform sampler2D uColor;
uniform sampler2D uDepth;
in vec2 uv;
out vec4 fragColor;
void main()
{
	// Premultiplied colour, its alpha the world's coverage; the depth as the layer wrote it.
	fragColor = texture(uColor, uv);
	gl_FragDepth = texture(uDepth, uv).r;
}";

		private int program, vao;

		internal WorldComposite()
		{
			int vertex = CompileShader(ShaderType.VertexShader, VertexSource);
			int fragment = CompileShader(ShaderType.FragmentShader, FragmentSource);
			program = GL.CreateProgram();
			GL.AttachShader(program, vertex);
			GL.AttachShader(program, fragment);
			GL.LinkProgram(program);
			GL.DeleteShader(vertex);
			GL.DeleteShader(fragment);
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				throw new InvalidOperationException("the composite program did not link: " + GL.GetProgramInfoLog(program));
			}

			GL.ProgramUniform1(program, GL.GetUniformLocation(program, "uColor"), 0);
			GL.ProgramUniform1(program, GL.GetUniformLocation(program, "uDepth"), 1);
			vao = GL.GenVertexArray();
		}

		private static int CompileShader(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled == 0)
			{
				throw new InvalidOperationException("a composite shader did not compile: " + GL.GetShaderInfoLog(shader));
			}

			return shader;
		}

		/// <summary>Lays the world image over the framebuffer and writes its depth. Leaves upstream's renderer to set its own state again.</summary>
		internal void World(int colorTexture, int depthTexture)
		{
			Bind(colorTexture, depthTexture);
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Always);
			GL.DepthMask(true);
			GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
			GL.DepthFunc(DepthFunction.Lequal);
			Unbind();
		}

		/// <summary>
		/// Lays the cab layer over the framebuffer, where upstream draws it (after the particles): colour only, as nothing
		/// after it reads the depth. Leaves upstream's renderer to set its own state again.
		/// </summary>
		internal void Cab(int cabColorTexture, int depthTexture)
		{
			Bind(cabColorTexture, depthTexture);
			GL.Disable(EnableCap.DepthTest);
			GL.DepthMask(false);
			GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
			Unbind();
		}

		private void Bind(int colorTexture, int depthTexture)
		{
			GL.UseProgram(program);
			GL.BindVertexArray(vao);
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(TextureTarget.Texture2D, depthTexture);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, colorTexture);
			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
			GL.Disable(EnableCap.CullFace);
		}

		private static void Unbind()
		{
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.BindVertexArray(0);
			GL.UseProgram(0);
		}

		public void Dispose()
		{
			if (program != 0)
			{
				GL.DeleteProgram(program);
				GL.DeleteVertexArray(vao);
				program = 0;
			}
		}
	}
}
