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
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using static OpenTK.Graphics.NativeEgl;

namespace OpenBve.Android
{
	/// <summary>
	/// A GLES 3 context from ANGLE running on its Vulkan back end: the renderer's GL calls become
	/// Vulkan work.
	/// </summary>
	/// <remarks>
	/// Created through the native EGL bindings, which <see cref="GraphicsLibraries"/> has pointed at
	/// ANGLE's libEGL. The display is requested with EGL_ANGLE_platform_angle so that ANGLE is told
	/// explicitly to use Vulkan rather than being left to pick a back end.
	/// </remarks>
	public sealed class AngleSurface : IRenderSurface
	{
		private const string Tag = "OpenBVE";

		private readonly IntPtr display;
		private readonly IntPtr config;
		private readonly IntPtr context;
		private IntPtr surface;
		private IntPtr window;

		private AngleSurface(IntPtr display, IntPtr config, IntPtr surface, IntPtr context, IntPtr window)
		{
			this.display = display;
			this.config = config;
			this.surface = surface;
			this.context = context;
			this.window = window;
		}

		/// <inheritdoc />
		public void DetachWindow()
		{
			eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			eglDestroySurface(display, surface);
			surface = IntPtr.Zero;
			ANativeWindow_release(window);
			window = IntPtr.Zero;
		}

		/// <inheritdoc />
		public void AttachWindow(Surface target)
		{
			window = ANativeWindow_fromSurface(JNIEnv.Handle, target.Handle);
			surface = window == IntPtr.Zero ? IntPtr.Zero : eglCreateWindowSurface(display, config, window, new[] { EGL_NONE });
			if (surface == IntPtr.Zero || !eglMakeCurrent(display, surface, surface, context))
			{
				throw new Exception("ANGLE could not attach the context to the new window (0x" + eglGetError().ToString("x") + ")");
			}

			eglSwapInterval(display, 1);
		}

		[DllImport("libandroid.so")] private static extern IntPtr ANativeWindow_fromSurface(IntPtr env, IntPtr surface);
		[DllImport("libandroid.so")] private static extern void ANativeWindow_release(IntPtr window);

		/// <summary>Creates the context on the given surface and makes it current.</summary>
		public static AngleSurface Create(Surface target)
		{
			IntPtr window = ANativeWindow_fromSurface(JNIEnv.Handle, target.Handle);
			if (window == IntPtr.Zero)
			{
				throw new Exception("could not get a native window for the surface");
			}

			IntPtr[] platformAttributes =
			{
				(IntPtr)EGL_PLATFORM_ANGLE_TYPE_ANGLE, (IntPtr)EGL_PLATFORM_ANGLE_TYPE_VULKAN_ANGLE,
				(IntPtr)EGL_NONE
			};
			IntPtr display = eglGetPlatformDisplay(EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero, platformAttributes);
			if (display == IntPtr.Zero)
			{
				throw new Exception("ANGLE has no Vulkan display (0x" + eglGetError().ToString("x") + ")");
			}

			if (!eglInitialize(display, out int major, out int minor))
			{
				throw new Exception("ANGLE eglInitialize failed (0x" + eglGetError().ToString("x") + ") - the device's Vulkan driver may be unusable");
			}

			Log.Info(Tag, "ANGLE EGL " + major + "." + minor + ": " + QueryString(display, EGL_VENDOR) + ", " + QueryString(display, EGL_VERSION));

			int[] configAttributes =
			{
				EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
				EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
				EGL_RED_SIZE, 8,
				EGL_GREEN_SIZE, 8,
				EGL_BLUE_SIZE, 8,
				EGL_ALPHA_SIZE, 8,
				EGL_DEPTH_SIZE, 24,
				EGL_NONE
			};
			IntPtr[] configs = new IntPtr[1];
			if (!eglChooseConfig(display, configAttributes, configs, 1, out int count) || count < 1)
			{
				throw new Exception("ANGLE has no GLES 3 config with a 24 bit depth buffer");
			}

			IntPtr context = eglCreateContext(display, configs[0], IntPtr.Zero, new[] { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE });
			if (context == IntPtr.Zero)
			{
				throw new Exception("ANGLE eglCreateContext failed (0x" + eglGetError().ToString("x") + ")");
			}

			IntPtr surface = eglCreateWindowSurface(display, configs[0], window, new[] { EGL_NONE });
			if (surface == IntPtr.Zero)
			{
				throw new Exception("ANGLE eglCreateWindowSurface failed (0x" + eglGetError().ToString("x") + ")");
			}

			if (!eglMakeCurrent(display, surface, surface, context))
			{
				throw new Exception("ANGLE eglMakeCurrent failed (0x" + eglGetError().ToString("x") + ")");
			}

			// Match the system path, which presents at the display's refresh rate.
			eglSwapInterval(display, 1);
			GL.ResetStateCache();
			return new AngleSurface(display, configs[0], surface, context, window);
		}

		/// <inheritdoc />
		public void SwapBuffers()
		{
			eglSwapBuffers(display, surface);
		}

		/// <inheritdoc />
		public void Dispose()
		{
			eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			if (surface != IntPtr.Zero)
			{
				eglDestroySurface(display, surface);
			}

			eglDestroyContext(display, context);
			eglTerminate(display);
			if (window != IntPtr.Zero)
			{
				ANativeWindow_release(window);
			}
		}
	}
}
