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
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * OpenGL ES on Vulkan, for OpenBVE: the native Vulkan renderer's phase 2. Upstream's renderer
	 * (LibRender2) and the port's own drawing talk to OpenGL ES through OpenBve.GLES's GL class;
	 * with GL.Device set to one of these, its native calls arrive here instead of at the driver,
	 * and are carried out with Vulkan. Only what OpenBVE uses is here:
	 *
	 *   - textures by GL name: images in pooled device memory, uploaded and mipmapped by commands
	 *     recorded before the frame's drawing, sampled through a descriptor set per texture;
	 *   - buffers by GL name: pooled host-visible memory written directly, copied on write when
	 *     the GPU may still be reading the old contents; uniform buffers (the animation
	 *     matrices) kept on the CPU and copied into a per-frame ring when drawn with;
	 *   - vertex array objects: the attribute layout and the buffers, as GL keeps them;
	 *   - programs: upstream's three (default, rectangle, text), recognised by their source and
	 *     replaced by GLSL 450 versions compiled ahead (Shaders/), whose uniforms are one block,
	 *     each GL uniform location being a byte offset into it;
	 *   - state (depth, blending, culling, the primitive and the vertex layout) as Vulkan
	 *     pipelines, made the first time each combination is drawn with and kept.
	 *
	 * Frames begin with the first clear or draw after a swap and end at the swap. Everything runs
	 * on the render thread, as GL would.
	 */
	public sealed unsafe partial class VulkanGL : IGLDevice, IDisposable
	{
		// GL enum values used here.
		private const int GL_TEXTURE_2D = 0x0DE1, GL_TEXTURE0 = 0x84C0;
		private const int GL_ARRAY_BUFFER = 0x8892, GL_ELEMENT_ARRAY_BUFFER = 0x8893, GL_UNIFORM_BUFFER = 0x8A11;
		private const int GL_CULL_FACE = 0x0B44, GL_DEPTH_TEST = 0x0B71, GL_BLEND = 0x0BE2;
		private const int GL_FRONT = 0x0404, GL_BACK = 0x0405, GL_FRONT_AND_BACK = 0x0408;
		private const int GL_LESS = 0x0201;
		private const int GL_ONE = 1, GL_ZERO = 0;
		private const int GL_COLOR_BUFFER_BIT = 0x4000, GL_DEPTH_BUFFER_BIT = 0x0100;
		private const int GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02, GL_EXTENSIONS = 0x1F03, GL_SHADING_LANGUAGE_VERSION = 0x8B8C;
		private const int GL_MAX_TEXTURE_SIZE = 0x0D33, GL_VIEWPORT = 0x0BA2, GL_MAX_VERTEX_ATTRIBS = 0x8869, GL_MAX_TEXTURE_IMAGE_UNITS = 0x8872;
		private const int GL_MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF;
		private const int GL_UNPACK_ALIGNMENT = 0x0CF5;
		private const int GL_FRAMEBUFFER_COMPLETE = 0x8CD5;

		private readonly VulkanContext context;
		private readonly Vk vk;
		private readonly Device device;
		private readonly BufferHeap bufferHeap;
		private readonly ImageHeap imageHeap;

		/// <summary>Names for every kind of object, from one counter (GL allows overlaps; nothing here needs them).</summary>
		private int nextName = 1;

		// Fixed-function state, as GL keeps it.
		private float clearRed, clearGreen, clearBlue, clearAlpha = 0.0f;
		private int viewportX, viewportY, viewportWidth, viewportHeight;
		private bool viewportDirty = true;
		private bool depthTest, blend, cullFace;
		private int cullMode = GL_BACK;
		private int blendSource = GL_ONE, blendDestination = GL_ZERO;
		private bool depthMask = true;
		private int depthFunc = GL_LESS;
		private int unpackAlignment = 4;

		/// <summary>A framebuffer object is bound: OpenBVE's shadows and picking, not drawn by this renderer.</summary>
		private bool offscreenBound;

		private readonly Dictionary<int, IntPtr> strings = new Dictionary<int, IntPtr>();
		private readonly HashSet<string> reported = new HashSet<string>();

		/// <summary>Draw calls in the last finished frame, for the log.</summary>
		public int LastFrameDraws { get; private set; }
		private int frameDraws;

		/// <summary>Messages about what is not supported, each once; the host logs them.</summary>
		public static Action<string> Log = message => { };

		public VulkanGL(VulkanContext context)
		{
			this.context = context;
			vk = context.Api;
			device = context.Device;
			bufferHeap = new BufferHeap(context);
			imageHeap = new ImageHeap(context);
			viewportWidth = (int)context.Width;
			viewportHeight = (int)context.Height;
			CreateLayouts();
			CreateShaderModules();
			CreateDefaults();
		}

		/// <summary>The Vulkan device and window this draws to.</summary>
		public VulkanContext Context => context;

		private void ReportOnce(string what)
		{
			if (reported.Add(what))
			{
				Log(what);
			}
		}

		/// <summary>Memory and object counts, for the log.</summary>
		public string Describe()
		{
			return LastFrameDraws + " draws, " + pipelines.Count + " pipelines, " + textures.Count + " textures (" + (imageHeap.UsedBytes >> 20) + " MB, " +
			       (imageHeap.ReservedBytes >> 20) + " MB reserved), " + glBuffers.Count + " buffers (" + (bufferHeap.UsedBytes >> 20) + " MB, " +
			       (bufferHeap.ReservedBytes >> 20) + " MB reserved" + (bufferHeap.DeviceLocal ? ", unified" : ", host") + ")";
		}

		/*
		 * Frames. A frame begins at the first clear or draw after a swap: the swapchain image is
		 * taken then, and the render pass begins with that clear's values (or black). The swap ends
		 * it. Between frames - while loading - texture uploads collect in the next frame's upload
		 * buffer; past a budget they are submitted on their own, so their staging memory comes back.
		 */

		private const ulong StagingBudget = 64UL << 20;
		private ulong stagingPending;

		/// <summary>Ensures a frame is being recorded, inside its render pass. False when there is no window to draw on.</summary>
		private bool EnsureRenderPass()
		{
			if (!context.InFrame)
			{
				if (!context.BeginFrame())
				{
					return false;
				}

				FrameBegun();
			}

			if (!context.InRenderPass)
			{
				context.BeginRenderPass(clearRed, clearGreen, clearBlue, clearAlpha, 1.0f);
				RenderPassBegun();
			}

			return true;
		}

		/// <summary>Ends and presents the frame (the swap).</summary>
		public void EndFrame()
		{
			if (context.InFrame)
			{
				LastFrameDraws = frameDraws;
				frameDraws = 0;
				context.EndFrame();
				stagingPending = 0;
			}
			else if (stagingPending > 0)
			{
				// No frame since the uploads: a loading screen, or the window gone.
				context.FlushUploads();
				stagingPending = 0;
			}
		}

		/// <summary>Counts staging memory waiting for its upload, submitting the uploads alone when there is a lot and no frame to carry them.</summary>
		private void StagingAdded(ulong bytes)
		{
			stagingPending += bytes;
			if (stagingPending > StagingBudget && !context.InFrame)
			{
				context.FlushUploads();
				stagingPending = 0;
			}
		}

		// --- state ---

		public void glEnable(int cap) => SetCapability(cap, true);

		public void glDisable(int cap) => SetCapability(cap, false);

		private void SetCapability(int cap, bool enabled)
		{
			switch (cap)
			{
				case GL_DEPTH_TEST:
					depthTest = enabled;
					break;
				case GL_BLEND:
					blend = enabled;
					break;
				case GL_CULL_FACE:
					cullFace = enabled;
					break;
			}
		}

		public void glClearColor(float r, float g, float b, float a)
		{
			clearRed = r;
			clearGreen = g;
			clearBlue = b;
			clearAlpha = a;
		}

		public void glClear(int mask)
		{
			if (offscreenBound || (mask & (GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT)) == 0)
			{
				return;
			}

			if (!context.InFrame || !context.InRenderPass)
			{
				// The first clear of the frame is the render pass's own (load op clear).
				EnsureRenderPass();
				return;
			}

			ClearAttachment* attachments = stackalloc ClearAttachment[2];
			uint count = 0;
			if ((mask & GL_COLOR_BUFFER_BIT) != 0)
			{
				attachments[count++] = new ClearAttachment(ImageAspectFlags.ColorBit, 0, new ClearValue(new ClearColorValue(clearRed, clearGreen, clearBlue, clearAlpha)));
			}

			if ((mask & GL_DEPTH_BUFFER_BIT) != 0)
			{
				attachments[count++] = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(default, new ClearDepthStencilValue(1.0f, 0)));
			}

			ClearRect rect = new ClearRect(new Rect2D(new Offset2D(0, 0), context.ImageExtent), 0, 1);
			vk.CmdClearAttachments(context.DrawCommands, count, attachments, 1, &rect);
		}

		public void glViewport(int x, int y, int width, int height)
		{
			if (x == viewportX && y == viewportY && width == viewportWidth && height == viewportHeight)
			{
				return;
			}

			viewportX = x;
			viewportY = y;
			viewportWidth = width;
			viewportHeight = height;
			viewportDirty = true;
		}

		public void glBlendFunc(int src, int dst)
		{
			blendSource = src;
			blendDestination = dst;
		}

		public void glDepthMask(byte flag) => depthMask = flag != 0;

		public void glDepthFunc(int func) => depthFunc = func;

		public void glCullFace(int mode) => cullMode = mode;

		public void glLineWidth(float width)
		{
		}

		public void glHint(int target, int mode)
		{
		}

		public void glPixelStorei(int pname, int param)
		{
			if (pname == GL_UNPACK_ALIGNMENT)
			{
				unpackAlignment = Math.Max(1, param);
			}
		}

		public int glGetError() => 0;

		public IntPtr glGetString(int name)
		{
			if (!strings.TryGetValue(name, out IntPtr text))
			{
				string value;
				switch (name)
				{
					case GL_VENDOR:
						value = "OpenBVE";
						break;
					case GL_RENDERER:
						value = context.Description;
						break;
					case GL_VERSION:
						value = "OpenGL ES 3.0 (native Vulkan renderer)";
						break;
					case GL_SHADING_LANGUAGE_VERSION:
						value = "OpenGL ES GLSL ES 3.00 (SPIR-V)";
						break;
					case GL_EXTENSIONS:
						value = context.MaxAnisotropy > 1.0f ? "GL_EXT_texture_filter_anisotropic" : string.Empty;
						break;
					default:
						value = string.Empty;
						break;
				}

				text = Marshal.StringToHGlobalAnsi(value);
				strings[name] = text;
			}

			return text;
		}

		public void glGetFloatv(int pname, float[] parameters)
		{
			if (pname == GL_MAX_TEXTURE_MAX_ANISOTROPY)
			{
				parameters[0] = context.MaxAnisotropy;
				return;
			}

			int[] values = new int[parameters.Length];
			glGetIntegerv(pname, values);
			for (int i = 0; i < parameters.Length; i++)
			{
				parameters[i] = values[i];
			}
		}

		public void glGetIntegerv(int pname, int[] parameters)
		{
			switch (pname)
			{
				case GL_MAX_TEXTURE_SIZE:
					parameters[0] = (int)context.Limits.MaxImageDimension2D;
					break;
				case GL_VIEWPORT:
					parameters[0] = viewportX;
					parameters[1] = viewportY;
					parameters[2] = viewportWidth;
					parameters[3] = viewportHeight;
					break;
				case GL_MAX_VERTEX_ATTRIBS:
					parameters[0] = Attributes;
					break;
				case GL_MAX_TEXTURE_IMAGE_UNITS:
					parameters[0] = Units;
					break;
				default:
					parameters[0] = 0;
					break;
			}
		}

		public void glReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels)
		{
			// Diagnostics only (a full stall even on GL); not read back here.
			ReportOnce("glReadPixels is not supported by the Vulkan renderer: the pixels read as zero");
			Array.Clear(pixels, 0, pixels.Length);
		}

		// --- framebuffer objects: OpenBVE's shadows, picking and motion blur, none drawn by this renderer ---

		public void glGenFramebuffers(int n, int[] framebuffers) => GenNames(n, framebuffers);

		public void glDeleteFramebuffers(int n, int[] framebuffers)
		{
		}

		public void glBindFramebuffer(int target, int framebuffer)
		{
			offscreenBound = framebuffer != 0;
			if (offscreenBound)
			{
				ReportOnce("framebuffer objects are not supported by the Vulkan renderer: drawing into one is skipped");
			}
		}

		public void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
		{
		}

		public void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer)
		{
		}

		public int glCheckFramebufferStatus(int target) => GL_FRAMEBUFFER_COMPLETE;

		public void glGenRenderbuffers(int n, int[] renderbuffers) => GenNames(n, renderbuffers);

		public void glDeleteRenderbuffers(int n, int[] renderbuffers)
		{
		}

		public void glBindRenderbuffer(int target, int renderbuffer)
		{
		}

		public void glRenderbufferStorage(int target, int internalFormat, int width, int height)
		{
		}

		public void glDrawBuffers(int n, int[] buffers)
		{
		}

		public void glReadBuffer(int mode)
		{
		}

		public void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border)
		{
			ReportOnce("glCopyTexImage2D (motion blur) is not supported by the Vulkan renderer");
		}

		private void GenNames(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
			}
		}

		public void Dispose()
		{
			context.WaitIdle();
			DisposeDraw();
			DisposeTextures();
			bufferHeap.Dispose();
			imageHeap.Dispose();
			foreach (IntPtr text in strings.Values)
			{
				Marshal.FreeHGlobal(text);
			}

			strings.Clear();
		}
	}
}
