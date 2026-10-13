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
using System.Runtime.InteropServices;
using Android.Runtime;
using Android.Util;
using Android.Views;
using OpenBve.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Android
{
	/*
	 * The window for the native Vulkan renderer (GraphicsBackend.VulkanNative, `--es graphics
	 * vulkannative`). Vulkan owns the window - device and swapchain (VulkanContext) - and there is
	 * no OpenGL ES context at all: GL.Device is a VulkanGL, which carries out the renderer's GL
	 * calls with Vulkan. A frame begins with the first clear or draw and is presented at the swap.
	 */
	public sealed class VulkanSurface : IRenderSurface
	{
		private readonly VulkanContext vulkan;
		private readonly VulkanGL device;
		private IntPtr window;

		[DllImport("libandroid.so")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
		[DllImport("libandroid.so")] private static extern void ANativeWindow_release(IntPtr window);

		private VulkanSurface(VulkanContext vulkan, VulkanGL device, IntPtr window)
		{
			this.vulkan = vulkan;
			this.device = device;
			this.window = window;
		}

		/// <summary>Whether the frame's draws are recorded on several threads (`--ez vulkan_threads false`: on the render thread only).</summary>
		public static bool RecordOnWorkers = true;

		/// <summary>The Vulkan device and window.</summary>
		public VulkanContext Vulkan => vulkan;

		/// <summary>Creates the Vulkan device on the window and puts GL's calls through it.</summary>
		public static VulkanSurface Create(Surface target)
		{
			IntPtr window = ANativeWindow_fromSurface(JNIEnv.Handle, target.Handle);
			if (window == IntPtr.Zero)
			{
				throw new Exception("no native window for the surface");
			}

			VulkanContext vulkan = null;
			try
			{
				vulkan = new VulkanContext(window);
				VulkanGL.Log = message => Log.Warn("OpenBVE", "vulkan: " + message);
				// The frame's draws are recorded on the frame workers (VulkanGL.Records), unless turned off for comparisons.
				if (RecordOnWorkers)
				{
					VulkanGL.ParallelRun = (count, body) => FrameWorkers.Shared.Run(count, (first, end) =>
					{
						for (int i = first; i < end; i++)
						{
							body(i);
						}
					});
					VulkanGL.ParallelThreads = FrameWorkers.Shared.Threads;
				}
				else
				{
					VulkanGL.ParallelRun = null;
					VulkanGL.ParallelThreads = 1;
				}

				VulkanGL device = new VulkanGL(vulkan, global::Android.App.Application.Context.CacheDir?.AbsolutePath);
				GL.Device = device;
				GL.ResetStateCache();
				Log.Info("OpenBVE", "native Vulkan: " + vulkan.Description + (vulkan.Vulkan13 ? ", Vulkan 1.3 path" + (VulkanGL.UseDynamicState ? " with dynamic state" : string.Empty) : ", Vulkan 1.1 path") + ", swapchain " + vulkan.Width + "x" + vulkan.Height + ", pre-rotation " + vulkan.Rotation + "°; " + device.PipelineCacheReport);
				return new VulkanSurface(vulkan, device, window);
			}
			catch
			{
				GL.Device = null;
				vulkan?.Dispose();
				ANativeWindow_release(window);
				throw;
			}
		}

		/// <summary>The swapchain and the renderer's objects, for the log.</summary>
		public string Describe() => "swapchain " + vulkan.Width + "x" + vulkan.Height + ", pre-rotation " + vulkan.Rotation + "°, rebuilt " + vulkan.Rebuilds + " times; " + device.Describe();

		/// <summary>Presents the frame.</summary>
		public void SwapBuffers()
		{
			device.EndFrame();
		}

		/// <inheritdoc />
		public void DetachWindow()
		{
			// The game may not come back (the app closed from the background): keep what was compiled.
			device.SavePipelineCache();
			vulkan.DetachWindow();
			if (window != IntPtr.Zero)
			{
				ANativeWindow_release(window);
				window = IntPtr.Zero;
			}
		}

		/// <inheritdoc />
		public void AttachWindow(Surface target)
		{
			window = ANativeWindow_fromSurface(JNIEnv.Handle, target.Handle);
			vulkan.AttachWindow(window);
		}

		/// <inheritdoc />
		public void Dispose()
		{
			GL.Device = null;
			device.Dispose();
			vulkan.Dispose();
			if (window != IntPtr.Zero)
			{
				ANativeWindow_release(window);
				window = IntPtr.Zero;
			}
		}
	}
}
