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
using System.Collections.Generic;
using System.IO;
using System.Text;
using Android.Content.Res;
using Android.Opengl;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.SmokeTest
{
	/// <summary>
	/// Compiles OpenBVE's shaders on a real GLES driver, through the same shim and translator
	/// the renderer uses. This exercises three things at once: that the P/Invokes resolve
	/// against libGLESv3.so, that the desktop GLSL survives translation to GLSL ES 3.00, and
	/// that the resulting programs link.
	/// </summary>
	public static class ShaderTest
	{
		/// <summary>The shader pairs the renderer builds, as named in LibRender2.</summary>
		private static readonly string[][] Programs =
		{
			new[] { "default", "default" },
			new[] { "default", "picking" },
			new[] { "rectangle", "rectangle" },
			new[] { "text", "rectangle" },
			new[] { "shadow_depth", "shadow_depth" }
		};

		/// <summary>Creates an offscreen GLES 3 context, compiles and links every shader program.</summary>
		/// <param name="assets">The asset manager holding the shader sources.</param>
		/// <returns>A one line summary.</returns>
		public static string Run(AssetManager assets)
		{
			EglContext context = EglContext.CreateOffscreen();
			try
			{
				string renderer = GL.GetString(StringName.Renderer);
				string version = GL.GetString(StringName.Version);

				List<string> failures = new List<string>();
				foreach (string[] program in Programs)
				{
					string error = BuildProgram(assets, program[0], program[1]);
					if (error != null)
					{
						failures.Add(program[0] + "+" + program[1] + ": " + error);
					}
				}

				if (failures.Count > 0)
				{
					throw new Exception(string.Join(" | ", failures));
				}

				return Programs.Length + " programs compiled and linked on " + renderer + " (" + version + ")";
			}
			finally
			{
				context.Dispose();
			}
		}

		private static string BuildProgram(AssetManager assets, string vertexName, string fragmentName)
		{
			int vertexShader = 0;
			int fragmentShader = 0;
			int program = 0;

			try
			{
				vertexShader = GL.CreateShader(ShaderType.VertexShader);
				GL.ShaderSource(vertexShader, ReadAsset(assets, "Shaders/" + vertexName + ".vert"));
				GL.CompileShader(vertexShader);
				GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int vertexStatus);
				if (vertexStatus == 0)
				{
					return vertexName + ".vert did not compile: " + Trim(GL.GetShaderInfoLog(vertexShader));
				}

				fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
				GL.ShaderSource(fragmentShader, ReadAsset(assets, "Shaders/" + fragmentName + ".frag"));
				GL.CompileShader(fragmentShader);
				GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out int fragmentStatus);
				if (fragmentStatus == 0)
				{
					return fragmentName + ".frag did not compile: " + Trim(GL.GetShaderInfoLog(fragmentShader));
				}

				program = GL.CreateProgram();
				GL.AttachShader(program, vertexShader);
				GL.AttachShader(program, fragmentShader);
				GL.LinkProgram(program);
				GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkStatus);
				if (linkStatus == 0)
				{
					return "did not link: " + Trim(GL.GetProgramInfoLog(program));
				}

				return null;
			}
			finally
			{
				if (program != 0)
				{
					GL.DeleteProgram(program);
				}

				if (vertexShader != 0)
				{
					GL.DeleteShader(vertexShader);
				}

				if (fragmentShader != 0)
				{
					GL.DeleteShader(fragmentShader);
				}
			}
		}

		private static string Trim(string log)
		{
			if (string.IsNullOrWhiteSpace(log))
			{
				return "(no info log)";
			}

			string collapsed = log.Replace("\n", " ").Replace("\r", " ").Trim();
			return collapsed.Length > 300 ? collapsed.Substring(0, 300) + "..." : collapsed;
		}

		private static string ReadAsset(AssetManager assets, string name)
		{
			using (Stream stream = assets.Open(name))
			using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
			{
				return reader.ReadToEnd();
			}
		}
	}

	/// <summary>A throwaway offscreen GLES 3 context, so GL calls can be made with no surface on screen.</summary>
	public sealed class EglContext : IDisposable
	{
		private readonly EGLDisplay display;
		private readonly EGLSurface surface;
		private readonly EGLContext context;

		private EglContext(EGLDisplay display, EGLSurface surface, EGLContext context)
		{
			this.display = display;
			this.surface = surface;
			this.context = context;
		}

		/// <summary>Creates and binds a 64x64 pbuffer context.</summary>
		public static EglContext CreateOffscreen()
		{
			EGLDisplay display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
			if (display == EGL14.EglNoDisplay)
			{
				throw new Exception("no EGL display");
			}

			int[] version = new int[2];
			if (!EGL14.EglInitialize(display, version, 0, version, 1))
			{
				throw new Exception("eglInitialize failed");
			}

			int[] configAttributes =
			{
				EGL14.EglRenderableType, EGLExt.EglOpenglEs3BitKhr,
				EGL14.EglSurfaceType, EGL14.EglPbufferBit,
				EGL14.EglRedSize, 8,
				EGL14.EglGreenSize, 8,
				EGL14.EglBlueSize, 8,
				EGL14.EglAlphaSize, 8,
				EGL14.EglDepthSize, 16,
				EGL14.EglNone
			};

			EGLConfig[] configs = new EGLConfig[1];
			int[] configCount = new int[1];
			if (!EGL14.EglChooseConfig(display, configAttributes, 0, configs, 0, 1, configCount, 0) || configCount[0] < 1)
			{
				throw new Exception("no GLES 3 capable EGL config");
			}

			int[] contextAttributes = { EGL14.EglContextClientVersion, 3, EGL14.EglNone };
			EGLContext context = EGL14.EglCreateContext(display, configs[0], EGL14.EglNoContext, contextAttributes, 0);
			if (context == EGL14.EglNoContext)
			{
				throw new Exception("eglCreateContext failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			int[] surfaceAttributes = { EGL14.EglWidth, 64, EGL14.EglHeight, 64, EGL14.EglNone };
			EGLSurface surface = EGL14.EglCreatePbufferSurface(display, configs[0], surfaceAttributes, 0);
			if (surface == EGL14.EglNoSurface)
			{
				throw new Exception("eglCreatePbufferSurface failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			if (!EGL14.EglMakeCurrent(display, surface, surface, context))
			{
				throw new Exception("eglMakeCurrent failed (0x" + EGL14.EglGetError().ToString("x") + ")");
			}

			// Tell the shim which thread owns the context, so render-thread checks answer correctly.
			OpenTK.AndroidGraphicsContext.RenderThreadId = Environment.CurrentManagedThreadId;
			// And that its cached driver state belongs to a previous context.
			GL.ResetStateCache();
			return new EglContext(display, surface, context);
		}

		/// <inheritdoc />
		public void Dispose()
		{
			EGL14.EglMakeCurrent(display, EGL14.EglNoSurface, EGL14.EglNoSurface, EGL14.EglNoContext);
			EGL14.EglDestroySurface(display, surface);
			EGL14.EglDestroyContext(display, context);
			EGL14.EglTerminate(display);
			OpenTK.AndroidGraphicsContext.RenderThreadId = -1;
		}
	}
}
