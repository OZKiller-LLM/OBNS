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

// OBNS: not an upstream file. A whole game drawn by one API, with OpenGL out of it (OBNS/RENDERERS.md): what is the same whichever
// API it is.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibRender2.Frame;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	/// <summary>What the game asks of the API drawing all of it.</summary>
	internal interface IWholeFrameLayer
	{
		/// <summary>Its 2D part and particles.</summary>
		WholeFrameDrawing FrameDrawing { get; }

		/// <summary>The frame is finished: the 2D part drawn over it, and shown. What SwapBuffers does for OpenGL.</summary>
		void Present();

		/// <summary>Saves the frame about to be shown, when it is.</summary>
		void RequestSnapshot(string file);

		/// <summary>Works out motion blur for the frame about to be made, from the frame rate and the camera's speed, as upstream does.</summary>
		void SetMotionBlur(double frameRate, double speed);
	}

	/// <summary>Whether one API draws the whole game, and which.</summary>
	internal static class WholeGame
	{
		/// <summary>Whether it does: asked for (OBNS_VULKAN_FULL=1, OBNS_D3D11_FULL=1, OBNS_D3D12_FULL=1, OBNS_D3D9_FULL=1), and a device can be had.</summary>
		internal static bool Full => Vulkan.VulkanDevice.Full || Direct3D11.D3D11World.Full || Direct3D12.D3D12World.Full || Direct3D9.D3D9World.Full;

		/// <summary>The API's layer.</summary>
		internal static IWholeFrameLayer Layer
		{
			get
			{
				if (Vulkan.VulkanDevice.Full)
				{
					return Vulkan.VulkanWorld.Instance;
				}

				if (Direct3D11.D3D11World.Full)
				{
					return Direct3D11.D3D11World.Instance;
				}

				if (Direct3D12.D3D12World.Full)
				{
					return Direct3D12.D3D12World.Instance;
				}

				return Direct3D9.D3D9World.Instance;
			}
		}

		/// <summary>Whether the API presents the game now (OpenGL's SwapBuffers must not be called).</summary>
		internal static bool PresentsItself => Full && WorldLayer.Enabled;

		/// <summary>Before the window is made: whether a device can be had, as the window then has no OpenGL to fall back on.</summary>
		internal static void Probe()
		{
			Vulkan.VulkanDevice.ProbeFull();
			Direct3D11.D3D11World.ProbeFull();
			Direct3D12.D3D12World.ProbeFull();
			Direct3D9.D3D9World.ProbeFull();
		}

		/// <summary>A frame's image saved as OpenGL's is: its rows (RGBA, or BGRA) run bottom to top, and it is opaque.</summary>
		internal static unsafe void SaveImage(byte* pixels, long rowPitch, int width, int height, string file, bool bgra = false)
		{
			using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
			{
				System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
				for (int y = 0; y < height; y++)
				{
					byte* from = pixels + (height - 1 - y) * rowPitch;
					byte* to = (byte*)data.Scan0 + (long)y * data.Stride;
					for (int x = 0; x < width; x++)
					{
						to[x * 3] = from[x * 4 + (bgra ? 0 : 2)];
						to[x * 3 + 1] = from[x * 4 + 1];
						to[x * 3 + 2] = from[x * 4 + (bgra ? 2 : 0)];
					}
				}

				bitmap.UnlockBits(data);
				bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
			}
		}

		/// <summary>Motion blur's alpha, upstream's sum.</summary>
		internal static float MotionBlurAlpha(LibRender2.MotionBlurs.MotionBlurMode mode, double frameRate, double speed)
		{
			double strength;
			switch (mode)
			{
				case LibRender2.MotionBlurs.MotionBlurMode.Low: strength = 0.0025; break;
				case LibRender2.MotionBlurs.MotionBlurMode.Medium: strength = 0.0040; break;
				case LibRender2.MotionBlurs.MotionBlurMode.High: strength = 0.0064; break;
				default: strength = 0.0040; break;
			}

			double denominator = strength * frameRate * System.Math.Sqrt(speed);
			return denominator > 0.001 ? (float)System.Math.Exp(-1.0 / denominator) : 0.0f;
		}
	}

	/// <summary>
	/// The frame's rectangles, glyphs and particles kept for the API to draw, in OpenGL's clip space (each API's shader turns that its own way
	/// up). When the API isn't taking them, everything goes to OpenGL, as does the state, which the rest of the frame uses.
	/// </summary>
	internal abstract class WholeFrameDrawing : IFrameDrawing
	{
		/// <summary>The blending of a quad, as upstream's blend functions.</summary>
		protected enum Blend { None = 0, Normal = 1, GlyphMask = 2, GlyphAdd = 3 }

		/// <summary>What the shaders are given for a quad.</summary>
		[StructLayout(LayoutKind.Sequential)]
		protected struct QuadData
		{
			internal float X, Y, Width, Height, U, V, UWidth, VHeight, R, G, B, A, AlphaFunction, AlphaValue;
			internal int Texture, Sampler;
			internal float Depth;
			/// <summary>The fog's colour, and how much of it (a particle's; none for the rest).</summary>
			internal float FogR, FogG, FogB, FogAmount;
		}

		protected struct Quad
		{
			internal QuadData Data;
			internal Blend Blend;
		}

		private readonly IFrameDrawing openGl;
		protected readonly List<Quad> quads = new List<Quad>();
		protected readonly List<Quad> particles = new List<Quad>();
		protected bool active = WholeGame.Full;
		private bool blendEnabled = true;
		private float alphaFunction, alphaValue;

		protected WholeFrameDrawing(IFrameDrawing openGl)
		{
			this.openGl = openGl;
		}

		/// <summary>The textures the quads' places are in.</summary>
		protected abstract WorldTextures Textures { get; }

		/// <summary>The frame has shown its quads.</summary>
		internal void Presented()
		{
			quads.Clear();
			Textures.StartBudget();
		}

		/// <summary>The frame's particles are about to be given.</summary>
		internal void BeginParticles()
		{
			particles.Clear();
		}

		/// <summary>
		/// A particle, in upstream's two passes: its model-view keeps it square to the view, so all of it is at one depth and it can be a
		/// rectangle on the screen.
		/// </summary>
		internal void Particle(int particleIndex, Matrix4D modelView, Texture texture, float opacity)
		{
			int index = texture == null ? Textures.WhiteIndex : TextureIndex(texture, OpenGlTextureWrapMode.ClampClamp);
			if (index == WorldTextures.Later)
			{
				return;
			}

			Matrix4D matrix = modelView * Program.Renderer.CurrentProjectionMatrix;
			// The corners' clip coordinates: (-1,1) and (1,-1) in the particle's own space; the shader's Z is negated, and it is 0 here.
			double w0 = -matrix.Row0.W + matrix.Row1.W + matrix.Row3.W, w1 = matrix.Row0.W - matrix.Row1.W + matrix.Row3.W;
			if (w0 <= 0.0 || w1 <= 0.0)
			{
				return;
			}

			double left = (-matrix.Row0.X + matrix.Row1.X + matrix.Row3.X) / w0, top = (-matrix.Row0.Y + matrix.Row1.Y + matrix.Row3.Y) / w0;
			double right = (matrix.Row0.X - matrix.Row1.X + matrix.Row3.X) / w1, bottom = (matrix.Row0.Y - matrix.Row1.Y + matrix.Row3.Y) / w1;
			double depth = ((-matrix.Row0.Z + matrix.Row1.Z + matrix.Row3.Z) / w0 + 1.0) * 0.5;
			int column = particleIndex / 4, row = particleIndex % 4;
			// Upstream's particles have the world's fog on. They are square to the view, so it is taken at the middle of each.
			float fogAmount = 0.0f;
			if (Program.Renderer.Fog.Enabled)
			{
				float fogFactor;
				if (Program.Renderer.Fog.IsLinear)
				{
					double distance = System.Math.Sqrt(modelView.Row3.X * modelView.Row3.X + modelView.Row3.Y * modelView.Row3.Y + modelView.Row3.Z * modelView.Row3.Z);
					fogFactor = (float)((Program.Renderer.Fog.End - distance) / (Program.Renderer.Fog.End - Program.Renderer.Fog.Start));
					fogFactor = System.Math.Max(0.0f, System.Math.Min(1.0f, fogFactor));
				}
				else
				{
					// The depth, in the window, times w: gl_FragCoord.z / gl_FragCoord.w in upstream's shader.
					double clipDepth = (matrix.Row3.Z / matrix.Row3.W + 1.0) * 0.5 * matrix.Row3.W;
					double fogDepth = Program.Renderer.Fog.Density * clipDepth;
					fogFactor = (float)System.Math.Exp(-fogDepth * fogDepth);
				}

				fogAmount = 1.0f - fogFactor;
			}

			const float inv255 = 1.0f / 255.0f;
			QuadData data = new QuadData
			{
				FogR = Program.Renderer.Fog.Color.R * inv255,
				FogG = Program.Renderer.Fog.Color.G * inv255,
				FogB = Program.Renderer.Fog.Color.B * inv255,
				FogAmount = fogAmount,
				X = (float)left,
				Y = (float)top,
				Width = (float)(right - left),
				Height = (float)(bottom - top),
				U = column * 0.25f,
				V = row * 0.25f,
				UWidth = 0.25f,
				VHeight = 0.25f,
				R = 1.0f,
				G = 1.0f,
				B = 1.0f,
				A = opacity,
				Texture = index,
				Sampler = (int)OpenGlTextureWrapMode.ClampClamp,
				Depth = (float)depth
			};
			// Opaque texels first (alpha exactly 1, writing depth), then blended ones (alpha below 1, not).
			data.AlphaFunction = (float)(int)AlphaFunction.Equal;
			data.AlphaValue = 1.0f;
			particles.Add(new Quad { Data = data, Blend = Blend.None });
			data.AlphaFunction = (float)(int)AlphaFunction.Less;
			particles.Add(new Quad { Data = data, Blend = Blend.Normal });
		}

		private void Add(Blend blend, int texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, double u, double v, double uWidth, double vHeight)
		{
			float scaleX = 2.0f / Program.Renderer.Screen.Width, scaleY = 2.0f / Program.Renderer.Screen.Height;
			quads.Add(new Quad
			{
				Blend = blend,
				Data = new QuadData
				{
					// Row 0 of the images is the bottom of the screen, as in OpenGL; points come from the top.
					X = (float)point.X * scaleX - 1.0f,
					Y = 1.0f - (float)point.Y * scaleY,
					Width = (float)size.X * scaleX,
					Height = -(float)size.Y * scaleY,
					U = (float)u,
					V = (float)v,
					UWidth = (float)uWidth,
					VHeight = (float)vHeight,
					R = color.R,
					G = color.G,
					B = color.B,
					A = color.A,
					AlphaFunction = alphaFunction,
					AlphaValue = alphaValue,
					Texture = texture,
					Sampler = (int)wrapMode
				}
			});
		}

		/// <summary>A texture's place in the layer's textures, white without one or if it can't be made; Later if not made yet.</summary>
		private int TextureIndex(Texture texture, OpenGlTextureWrapMode wrapMode)
		{
			int index = Textures.Prepare(texture, wrapMode, out bool _);
			return index == -1 ? Textures.WhiteIndex : index;
		}

		public void Initialize() => openGl.Initialize();

		public void ScreenChanged() => openGl.ScreenChanged();

		public void Clear() => openGl.Clear();

		public void SetBlend(bool enabled, BlendingFactor source, BlendingFactor destination)
		{
			blendEnabled = enabled;
			openGl.SetBlend(enabled, source, destination);
		}

		public void SetAlphaTest(bool enabled, AlphaFunction function, float value)
		{
			alphaFunction = enabled ? (float)(int)function : 0.0f;
			alphaValue = value;
			openGl.SetAlphaTest(enabled, function, value);
		}

		public void SetDepthWrite(bool enabled) => openGl.SetDepthWrite(enabled);

		public void Rectangle(Texture texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, Vector2 coordinates)
		{
			if (!active)
			{
				openGl.Rectangle(texture, wrapMode, point, size, color, coordinates);
				return;
			}

			int index = TextureIndex(texture, wrapMode);
			if (index != WorldTextures.Later)
			{
				Add(blendEnabled ? Blend.Normal : Blend.None, index, wrapMode, point, size, color, 0.0, 0.0, coordinates.X, coordinates.Y);
			}
		}

		public void BeginText()
		{
			if (!active)
			{
				openGl.BeginText();
			}
		}

		public void Glyph(Texture texture, Vector4 atlas, Vector2 point, Vector2 size, Color128 color)
		{
			if (!active)
			{
				openGl.Glyph(texture, atlas, point, size, color);
				return;
			}

			int index = TextureIndex(texture, OpenGlTextureWrapMode.ClampClamp);
			if (index == WorldTextures.Later)
			{
				return;
			}

			// Upstream's two passes: the background masked off, then the character added.
			Add(blendEnabled ? Blend.GlyphMask : Blend.None, index, OpenGlTextureWrapMode.ClampClamp, point, size, new Color128(color.A, color.A, color.A, 1.0f), atlas.X, atlas.Y, atlas.Z, atlas.W);
			Add(blendEnabled ? Blend.GlyphAdd : Blend.None, index, OpenGlTextureWrapMode.ClampClamp, point, size, color, atlas.X, atlas.Y, atlas.Z, atlas.W);
		}

		public void EndText()
		{
			if (!active)
			{
				openGl.EndText();
			}
		}
	}
}
