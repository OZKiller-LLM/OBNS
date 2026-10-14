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

// ReSharper disable once CheckNamespace
namespace OpenTK
{
	/*
	 * Android has no desktop window, no resolution list to choose from and no mouse cursor.
	 * These types exist so the upstream renderer compiles; the Android front end owns the real
	 * surface and drives the render loop itself.
	 */

	/// <summary>The state of a window.</summary>
	public enum WindowState
	{
		Normal = 0,
		Minimized = 1,
		Maximized = 2,
		Fullscreen = 3
	}

	/// <summary>The border style of a window.</summary>
	public enum WindowBorder
	{
		Resizable = 0,
		Fixed = 1,
		Hidden = 2
	}

	/// <summary>Vertical synchronisation modes.</summary>
	public enum VSyncMode
	{
		Off = 0,
		On = 1,
		Adaptive = 2
	}

	/// <summary>A mouse cursor. Retained only so cursor-loading code compiles; never displayed.</summary>
	public class MouseCursor
	{
		/// <summary>The default cursor.</summary>
		public static readonly MouseCursor Default = new MouseCursor();

		/// <summary>An empty cursor.</summary>
		public static readonly MouseCursor Empty = new MouseCursor();

		/// <summary>Creates a cursor from raw BGRA image data.</summary>
		public MouseCursor(int hotspotX, int hotspotY, int width, int height, IntPtr data)
		{
			HotspotX = hotspotX;
			HotspotY = hotspotY;
			Width = width;
			Height = height;
		}

		/// <summary>Creates a cursor from raw BGRA image data.</summary>
		public MouseCursor(int hotspotX, int hotspotY, int width, int height, byte[] data)
		{
			HotspotX = hotspotX;
			HotspotY = hotspotY;
			Width = width;
			Height = height;
		}

		private MouseCursor()
		{
		}

		/// <summary>The X coordinate of the cursor hotspot.</summary>
		public int HotspotX { get; }

		/// <summary>The Y coordinate of the cursor hotspot.</summary>
		public int HotspotY { get; }

		/// <summary>The width in pixels.</summary>
		public int Width { get; }

		/// <summary>The height in pixels.</summary>
		public int Height { get; }
	}

	/// <summary>A display resolution.</summary>
	public class DisplayResolution
	{
		/// <summary>Creates a resolution.</summary>
		public DisplayResolution(int width, int height, int bitsPerPixel, float refreshRate)
		{
			Width = width;
			Height = height;
			BitsPerPixel = bitsPerPixel;
			RefreshRate = refreshRate;
		}

		/// <summary>The width in pixels.</summary>
		public int Width { get; }

		/// <summary>The height in pixels.</summary>
		public int Height { get; }

		/// <summary>The colour depth.</summary>
		public int BitsPerPixel { get; }

		/// <summary>The refresh rate in Hz.</summary>
		public float RefreshRate { get; }
	}

	/// <summary>
	/// The display. On Android the size is whatever the app's surface is, so the Android front
	/// end sets <see cref="Default"/> up at startup and resolution changes are ignored.
	/// </summary>
	public class DisplayDevice
	{
		/// <summary>The primary display.</summary>
		public static DisplayDevice Default { get; set; } = new DisplayDevice(1280, 720, 1.0f, 1.0f);

		/// <summary>Creates a display.</summary>
		public DisplayDevice(int width, int height, float scaleX, float scaleY)
		{
			Width = width;
			Height = height;
			ScaleFactor = new Vector2(scaleX, scaleY);
			AvailableResolutions = new List<DisplayResolution> { new DisplayResolution(width, height, 32, 60.0f) };
		}

		/// <summary>The width in pixels.</summary>
		public int Width { get; set; }

		/// <summary>The height in pixels.</summary>
		public int Height { get; set; }

		/// <summary>The DPI scale factor.</summary>
		public Vector2 ScaleFactor { get; set; }

		/// <summary>The resolutions this display supports. Always exactly one on Android.</summary>
		public IList<DisplayResolution> AvailableResolutions { get; }

		/// <summary>Changes the resolution. A no-op on Android.</summary>
		public void ChangeResolution(DisplayResolution resolution)
		{
		}

		/// <summary>Changes the resolution. A no-op on Android.</summary>
		public void ChangeResolution(int width, int height, int bitsPerPixel, float refreshRate)
		{
		}

		/// <summary>Restores the original resolution. A no-op on Android.</summary>
		public void RestoreResolution()
		{
		}
	}

	/// <summary>A GL context.</summary>
	public interface IGraphicsContext
	{
		/// <summary>Whether the context is current on the calling thread.</summary>
		bool IsCurrent { get; }

		/// <summary>Presents the back buffer.</summary>
		void SwapBuffers();
	}

	/// <summary>
	/// The GL context backing a <see cref="GameWindow"/>. The Android front end owns the real
	/// EGL context and tells this shim which thread it is current on, so that the renderer's
	/// "am I on the render thread?" checks answer correctly.
	/// </summary>
	public class AndroidGraphicsContext : IGraphicsContext
	{
		/// <summary>The thread the EGL context is current on. Set by the front end when it binds the context.</summary>
		public static int RenderThreadId { get; set; } = -1;

		/// <inheritdoc />
		public bool IsCurrent => RenderThreadId == -1 || Environment.CurrentManagedThreadId == RenderThreadId;

		/// <inheritdoc />
		public virtual void SwapBuffers()
		{
		}
	}

	/// <summary>
	/// Stands in for the OpenTK window. The Android activity owns the real GL surface, so this
	/// only carries the size, state and cursor the renderer reads back.
	/// </summary>
	public class GameWindow : IDisposable
	{
		/// <summary>The GL context of this window.</summary>
		public IGraphicsContext Context { get; set; } = new AndroidGraphicsContext();

		/// <summary>The window title. Unused on Android.</summary>
		public string Title { get; set; } = string.Empty;

		/// <summary>The X position. Unused on Android.</summary>
		public int X { get; set; }

		/// <summary>The Y position. Unused on Android.</summary>
		public int Y { get; set; }

		/// <summary>The width of the drawing surface in pixels.</summary>
		public int Width { get; set; }

		/// <summary>The height of the drawing surface in pixels.</summary>
		public int Height { get; set; }

		/// <summary>The window state. Android apps are always effectively fullscreen.</summary>
		public WindowState WindowState { get; set; } = WindowState.Fullscreen;

		/// <summary>The window border style. Unused on Android.</summary>
		public WindowBorder WindowBorder { get; set; } = WindowBorder.Hidden;

		/// <summary>Whether the window is visible.</summary>
		public bool Visible { get; set; } = true;

		/// <summary>The vertical synchronisation mode. Android composites on vsync regardless.</summary>
		public VSyncMode VSync { get; set; } = VSyncMode.On;

		/// <summary>The mouse cursor. Never displayed on Android.</summary>
		public MouseCursor Cursor { get; set; } = MouseCursor.Default;

		/// <summary>Whether the cursor is visible. Never on Android.</summary>
		public bool CursorVisible { get; set; }

		/// <summary>Presents the back buffer. The Android front end swaps via EGL instead.</summary>
		public virtual void SwapBuffers()
		{
		}

		/// <summary>Requests that the window close.</summary>
		public virtual void Exit()
		{
		}

		/// <inheritdoc />
		public virtual void Dispose()
		{
		}
	}
}

// ReSharper disable once CheckNamespace
namespace OpenTK.Graphics
{
	/// <summary>
	/// Describes the format of a drawing surface. On Android the EGL config is chosen by the
	/// front end, so this only records what was asked for.
	/// </summary>
	public class GraphicsMode
	{
		/// <summary>The default mode.</summary>
		public static GraphicsMode Default { get; } = new GraphicsMode();

		/// <summary>Creates a mode with default settings.</summary>
		public GraphicsMode()
		{
		}

		/// <summary>Creates a mode with the given buffer sizes.</summary>
		public GraphicsMode(int colorBits, int depthBits, int stencilBits, int samples)
		{
			Depth = depthBits;
			Stencil = stencilBits;
			Samples = samples;
		}

		/// <summary>The depth buffer size in bits.</summary>
		public int Depth { get; } = 24;

		/// <summary>The stencil buffer size in bits.</summary>
		public int Stencil { get; } = 8;

		/// <summary>The number of MSAA samples.</summary>
		public int Samples { get; }
	}
}
