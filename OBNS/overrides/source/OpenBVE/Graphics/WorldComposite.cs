//Simplified BSD License (BSD-2-Clause)
//
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

		// The depth is read from .r: a depth image (Vulkan) or a single-channel colour image holding it (Direct3D, Metal).
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

		// The same for images bound as rectangles (IOSurfaces, Metal's), read by their texels.
		private const string RectangleFragmentSource = @"#version 330 core
uniform sampler2DRect uColor;
uniform sampler2DRect uDepth;
in vec2 uv;
out vec4 fragColor;
void main()
{
	fragColor = texture(uColor, uv * vec2(textureSize(uColor)));
	gl_FragDepth = texture(uDepth, uv * vec2(textureSize(uDepth))).r;
}";

		private int program, vao;
		private readonly TextureTarget target;

		/// <summary>The composite of 2D images, or (rectangle) of rectangle textures.</summary>
		internal WorldComposite(bool rectangle = false)
		{
			target = rectangle ? TextureTarget.TextureRectangle : TextureTarget.Texture2D;
			int vertex = CompileShader(ShaderType.VertexShader, VertexSource);
			int fragment = CompileShader(ShaderType.FragmentShader, rectangle ? RectangleFragmentSource : FragmentSource);
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
			GL.BindTexture(target, depthTexture);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(target, colorTexture);
			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
			GL.Disable(EnableCap.CullFace);
		}

		private void Unbind()
		{
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(target, 0);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(target, 0);
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
