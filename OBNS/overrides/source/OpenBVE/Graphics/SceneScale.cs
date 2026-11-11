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

// OBNS: not an upstream file. Potato mode's scene drawn below the window's resolution on OpenGL (the user's choice,
// 2026-11-11, for a Broadwell MacBook Pro: "draw half of the Retina resolution"). The world, its particles and the cab are
// drawn into an off-screen target of fewer pixels, then stretched over the window with linear filtering; the HUD, text and
// menus are drawn after it at the window's own resolution, so they stay sharp.

using System;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	internal static class SceneScale
	{
		/// <summary>
		/// The most pixels Potato mode draws the scene at when obns.cfg doesn't set a scale: a 13-inch Retina MacBook's
		/// 2560x1600 at half its width and height. Windows of about that size or smaller are drawn at their own resolution.
		/// </summary>
		private const double PixelBudget = 1280.0 * 800.0;

		/// <summary>Scales this close to 1 aren't worth a target of their own.</summary>
		private const double LeastSaving = 0.95;

		private static int framebuffer, colour, depth;
		private static int targetWidth, targetHeight;
		private static int windowWidth, windowHeight;
		private static bool failed;
		private static double lastLogged = -1.0;

		/// <summary>Whether the scene is being drawn into the smaller target now (between Begin and End).</summary>
		internal static bool Active { get; private set; }

		/// <summary>
		/// The scale for a window of this size: obns.cfg's potatoscenescale (a percentage, 25 to 100) where set, else the
		/// pixel budget's (half a Retina screen's width and height, no less than a quarter).
		/// </summary>
		internal static double ScaleFor(int width, int height)
		{
			if (!Potato.Active || width <= 0 || height <= 0)
			{
				return 1.0;
			}

			int percent = ObnsOptions.PotatoSceneScale;
			double scale = percent > 0 ? percent / 100.0 : Math.Sqrt(PixelBudget / ((double)width * height));
			scale = Math.Max(0.25, Math.Min(1.0, scale));
			return scale >= LeastSaving ? 1.0 : scale;
		}

		/// <summary>
		/// Starts the scene in the smaller target, cleared, where Potato mode scales it; false where it is drawn to the window
		/// as before (no scale, or no target could be made). The window's viewport is the target's until End.
		/// </summary>
		internal static bool Begin(int width, int height)
		{
			Active = false;
			double scale = ScaleFor(width, height);
			if (scale >= 1.0 || failed)
			{
				return false;
			}

			int w = Math.Max(1, (int)Math.Round(width * scale));
			int h = Math.Max(1, (int)Math.Round(height * scale));
			if (!Prepare(w, h))
			{
				return false;
			}

			windowWidth = width;
			windowHeight = height;
			if (Math.Abs(scale - lastLogged) > 0.001)
			{
				lastLogged = scale;
				Program.FileSystem.AppendToLogFile("OBNS Potato mode: the scene drawn at " + w + "x" + h + " for a " + width + "x" + height + " window (" + Math.Round(scale * 100.0) + "%), the HUD at the window's resolution");
			}

			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.Viewport(0, 0, targetWidth, targetHeight);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
			Active = true;
			return true;
		}

		/// <summary>
		/// Forgets the target: each game has a window of its own, and the target's names belonged to the last one's context
		/// (Potato.Apply, before the window is made).
		/// </summary>
		internal static void Reset()
		{
			framebuffer = colour = depth = 0;
			targetWidth = targetHeight = 0;
			failed = false;
			Active = false;
			lastLogged = -1.0;
		}

		/// <summary>The target's viewport again, where something set the window's while the scene is in the target.</summary>
		internal static void Viewport()
		{
			if (Active)
			{
				GL.Viewport(0, 0, targetWidth, targetHeight);
			}
		}

		/// <summary>The scene stretched over the window, which is drawn to from here on at its own resolution.</summary>
		internal static void End()
		{
			if (!Active)
			{
				return;
			}

			Active = false;
			GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
			GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
			GL.BlitFramebuffer(0, 0, targetWidth, targetHeight, 0, 0, windowWidth, windowHeight, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			GL.Viewport(0, 0, windowWidth, windowHeight);
		}

		/// <summary>The target at this size: colour and depth-stencil renderbuffers, made again only when the size changes.</summary>
		private static bool Prepare(int w, int h)
		{
			if (framebuffer != 0 && w == targetWidth && h == targetHeight)
			{
				return true;
			}

			if (framebuffer == 0)
			{
				framebuffer = GL.GenFramebuffer();
				colour = GL.GenRenderbuffer();
				depth = GL.GenRenderbuffer();
			}

			GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, colour);
			GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Rgba8, w, h);
			GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
			GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, w, h);
			GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
			GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, colour);
			GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
			FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
			GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
			if (status != FramebufferErrorCode.FramebufferComplete)
			{
				// Drawn at the window's resolution from here on, as without Potato mode's scale.
				failed = true;
				Program.FileSystem.AppendToLogFile("OBNS Potato mode: no smaller target for the scene (" + status + "); drawn at the window's resolution");
				return false;
			}

			targetWidth = w;
			targetHeight = h;
			return true;
		}
	}
}
