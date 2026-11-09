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
				vulkan.Log = VulkanGL.Log;
				// The frame's draws are recorded on threads of the renderer's own (VulkanGL.Records): half the
				// cores, at most 4 with the submitting thread - the frame workers update the next frame meanwhile.
				VulkanGL.RecordThreads = RecordOnWorkers ? Math.Max(1, Math.Min(3, Environment.ProcessorCount / 2 - 1)) : 0;

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
		public string Describe() => "swapchain " + vulkan.Width + "x" + vulkan.Height + ", pre-rotation " + vulkan.Rotation + "°, rebuilt " + vulkan.Rebuilds + " times" +
		                            (vulkan.WindowLosses == 0 ? string.Empty : ", window lost " + vulkan.WindowLosses + " times") +
		                            (device.SkippedFrames == 0 ? string.Empty : ", " + device.SkippedFrames + " frames skipped") + "; " + device.Describe();

		/// <summary>Presents the frame.</summary>
		public void SwapBuffers()
		{
			device.EndFrame();
		}

		/// <inheritdoc />
		// Not waiting for the submitter: a loss its present reports is taken up when the next frame begins (VulkanContext.BeginFrame), which is then skipped.
		public bool WindowLost => vulkan.WindowLost;

		/// <inheritdoc />
		public void WindowResized()
		{
			vulkan.WindowResized();
		}

		/// <inheritdoc />
		public void DetachWindow()
		{
			ReleaseWindow();
			// The game may not come back (the app closed from the background): keep what was compiled.
			device.SavePipelineCache();
		}

		/// <summary>Lets go of the window: the frame in progress, the swapchain, the surface and the ANativeWindow.</summary>
		private void ReleaseWindow()
		{
			/*
			 * A frame begun and not yet swapped holds an image of this window's swapchain. Left open,
			 * it would be finished after the swapchain was replaced and present that image's index to
			 * the new one - which never handed it out: VK_ERROR_SURFACE_LOST_KHR, a few seconds into the
			 * game on a Galaxy A36 and a Z Flip5 (2026-11-09; the old swapchain was destroyed with one
			 * image still dequeued). Finish it on this window now; if the window has gone, present only
			 * says so (VulkanContext) and the frame is lost, which is all that is lost.
			 */
			if (vulkan.InFrame)
			{
				Log.Warn("OpenBVE", "vulkan: a frame was open when the window went: presented to the old window first");
				device.EndFrame();
			}

			// The last frame's submission first: it presents to this window.
			device.WaitSubmitter();
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
			ReleaseWindow();
			window = target == null ? IntPtr.Zero : ANativeWindow_fromSurface(JNIEnv.Handle, target.Handle);
			if (window == IntPtr.Zero || !vulkan.AttachWindow(window))
			{
				// Not usable (gone again already): WindowLost says so, and the owner tries again later.
				if (window != IntPtr.Zero)
				{
					ANativeWindow_release(window);
					window = IntPtr.Zero;
				}

				vulkan.MarkWindowLost();
			}
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
