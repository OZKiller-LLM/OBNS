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

// ReSharper disable InconsistentNaming
// ReSharper disable once CheckNamespace
namespace OpenTK.Graphics
{
	/// <summary>
	/// Direct EGL bindings. Android's Java EGL14 classes always talk to the system driver, so the
	/// ANGLE (Vulkan) backend has to create its display, context and surface through these, which
	/// <see cref="GraphicsLibraries"/> points at ANGLE's own libEGL.
	/// </summary>
	public static class NativeEgl
	{
		private const string Library = "libEGL.so";

		public const int EGL_NONE = 0x3038;
		public const int EGL_RED_SIZE = 0x3024;
		public const int EGL_GREEN_SIZE = 0x3023;
		public const int EGL_BLUE_SIZE = 0x3022;
		public const int EGL_ALPHA_SIZE = 0x3021;
		public const int EGL_DEPTH_SIZE = 0x3025;
		public const int EGL_SURFACE_TYPE = 0x3033;
		public const int EGL_WINDOW_BIT = 0x0004;
		public const int EGL_RENDERABLE_TYPE = 0x3040;
		public const int EGL_OPENGL_ES3_BIT = 0x0040;
		public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
		public const int EGL_VENDOR = 0x3053;
		public const int EGL_VERSION = 0x3054;

		// EGL_ANGLE_platform_angle and EGL_ANGLE_platform_angle_vulkan
		public const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
		public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
		public const int EGL_PLATFORM_ANGLE_TYPE_VULKAN_ANGLE = 0x3450;

		[DllImport(Library)] public static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
		[DllImport(Library)] public static extern IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr[] attributes);
		[DllImport(Library)] public static extern bool eglInitialize(IntPtr display, out int major, out int minor);
		[DllImport(Library)] public static extern bool eglChooseConfig(IntPtr display, int[] attributes, IntPtr[] configs, int configSize, out int count);
		[DllImport(Library)] public static extern IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr shareContext, int[] attributes);
		[DllImport(Library)] public static extern IntPtr eglCreateWindowSurface(IntPtr display, IntPtr config, IntPtr window, int[] attributes);
		[DllImport(Library)] public static extern bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
		[DllImport(Library)] public static extern bool eglSwapBuffers(IntPtr display, IntPtr surface);
		[DllImport(Library)] public static extern bool eglSwapInterval(IntPtr display, int interval);
		[DllImport(Library)] public static extern bool eglDestroySurface(IntPtr display, IntPtr surface);
		[DllImport(Library)] public static extern bool eglDestroyContext(IntPtr display, IntPtr context);
		[DllImport(Library)] public static extern bool eglTerminate(IntPtr display);
		[DllImport(Library)] public static extern int eglGetError();
		[DllImport(Library, EntryPoint = "eglQueryString")] private static extern IntPtr eglQueryStringNative(IntPtr display, int name);

		/// <summary>Reads an EGL string such as the vendor or version.</summary>
		public static string QueryString(IntPtr display, int name)
		{
			return Marshal.PtrToStringAnsi(eglQueryStringNative(display, name)) ?? string.Empty;
		}
	}
}
