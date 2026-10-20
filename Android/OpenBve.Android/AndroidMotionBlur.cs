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
using System;
using LibRender2.MotionBlurs;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Android
{
	/// <summary>
	/// Upstream's motion blur (Options: low, medium, high) for OpenGL ES: each frame the one before
	/// is laid over the scene, fading by speed and frame rate as upstream's MotionBlur.RenderFullscreen
	/// fades it, and the result kept for the next frame.
	/// </summary>
	/// <remarks>
	/// Upstream draws its quad in the fixed-function pipeline, which OpenGL ES does not have (and
	/// upstream skips it on a forward-compatible context too), so the overlay is a small shader of its
	/// own here. The native Vulkan renderer cannot copy the screen yet, and draws none.
	/// </remarks>
	internal sealed class AndroidMotionBlur : IDisposable
	{
		// A screen-filling strip from gl_VertexID alone: no vertex buffer.
		private const string VertexSource =
			"#version 300 es\n" +
			"out vec2 vUv;\n" +
			"void main()\n" +
			"{\n" +
			"\tvec2 p = vec2(float(gl_VertexID & 1), float((gl_VertexID >> 1) & 1));\n" +
			"\tvUv = p;\n" +
			"\tgl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n" +
			"}\n";

		private const string FragmentSource =
			"#version 300 es\n" +
			"precision mediump float;\n" +
			"in vec2 vUv;\n" +
			"uniform sampler2D uTexture;\n" +
			"uniform float uFactor;\n" +
			"out vec4 fragColor;\n" +
			"void main()\n" +
			"{\n" +
			"\tfragColor = vec4(texture(uTexture, vUv).rgb, uFactor);\n" +
			"}\n";

		private int program, factorLocation, textureLocation;
		private int texture, width, height;
		private bool failed, haveFrame;

		/// <summary>Lays the last frame over this one, then keeps this one. Render thread, after the scene's transparent faces.</summary>
		/// <param name="speed">The camera's speed in metres a second.</param>
		public void Render(AndroidRenderer renderer, MotionBlurMode mode, double frameRate, double speed)
		{
			if (failed || mode == MotionBlurMode.None)
			{
				return;
			}

			if (program == 0 && !Build())
			{
				failed = true;
				return;
			}

			int w = renderer.Screen.Width, h = renderer.Screen.Height;
			if (w <= 0 || h <= 0)
			{
				return;
			}

			if (texture == 0 || w != width || h != height)
			{
				// A new size (or the first frame): nothing to lay over until one has been kept.
				if (texture == 0)
				{
					texture = GL.GenTexture();
				}

				GL.BindTexture(TextureTarget.Texture2D, texture);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
				width = w;
				height = h;
				haveFrame = false;
			}

			renderer.LastBoundTexture = null;
			float factor = Factor(mode, frameRate, speed);
			if (haveFrame && factor > 0.0f)
			{
				// Upstream deactivates its shader for the blur; the next Activate sets it up again.
				renderer.CurrentShader?.Deactivate();
				GL.UseProgram(program);
				GL.ActiveTexture(TextureUnit.Texture0);
				GL.BindTexture(TextureTarget.Texture2D, texture);
				GL.Uniform1(textureLocation, 0);
				GL.Uniform1(factorLocation, factor);
				GL.Disable(EnableCap.DepthTest);
				GL.DepthMask(false);
				GL.Disable(EnableCap.CullFace);
				GL.Enable(EnableCap.Blend);
				GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
				renderer.dummyVao.Bind();
				GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
				GL.UseProgram(0);
			}

			// This frame, blurred, for the next one.
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.CopyTexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgb8, 0, 0, w, h, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			haveFrame = true;
			renderer.LastBoundTexture = null;
			renderer.RestoreBlendFunc();
		}

		/// <summary>How much of the last frame shows through: upstream's formula, by mode, frame rate and speed.</summary>
		private static float Factor(MotionBlurMode mode, double frameRate, double speed)
		{
			double strength;
			switch (mode)
			{
				case MotionBlurMode.Low: strength = 0.0025; break;
				case MotionBlurMode.High: strength = 0.0064; break;
				default: strength = 0.0040; break;
			}

			double denominator = strength * frameRate * Math.Sqrt(speed);
			return denominator > 0.001 ? (float)Math.Exp(-1.0 / denominator) : 0.0f;
		}

		private bool Build()
		{
			int vertex = Compile(ShaderType.VertexShader, VertexSource);
			int fragment = Compile(ShaderType.FragmentShader, FragmentSource);
			if (vertex == 0 || fragment == 0)
			{
				return false;
			}

			program = GL.CreateProgram();
			GL.AttachShader(program, vertex);
			GL.AttachShader(program, fragment);
			GL.LinkProgram(program);
			GL.DeleteShader(vertex);
			GL.DeleteShader(fragment);
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				global::Android.Util.Log.Warn("OpenBVE", "motion blur: the shader did not link; no motion blur");
				GL.DeleteProgram(program);
				program = 0;
				return false;
			}

			factorLocation = GL.GetUniformLocation(program, "uFactor");
			textureLocation = GL.GetUniformLocation(program, "uTexture");
			return true;
		}

		private static int Compile(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled != 0)
			{
				return shader;
			}

			global::Android.Util.Log.Warn("OpenBVE", "motion blur: the " + type + " did not compile: " + GL.GetShaderInfoLog(shader));
			GL.DeleteShader(shader);
			return 0;
		}

		/// <summary>Lets the texture and shader go. Render thread, with the context current.</summary>
		public void Dispose()
		{
			if (texture != 0)
			{
				GL.DeleteTexture(texture);
				texture = 0;
			}

			if (program != 0)
			{
				GL.DeleteProgram(program);
				program = 0;
			}
		}
	}
}
