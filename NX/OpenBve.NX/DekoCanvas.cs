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

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenBve.Switch;

namespace OpenBve.NX
{
	/// <summary>
	/// The menu's canvas on deko3d (NX/runner/source/obns_dk2d.inc): rectangles, outlines, text from the console's
	/// shared fonts and images, in the menu's 1280x720 units. A frame is <see cref="Begin"/>, the drawing, <see cref="End"/>.
	/// </summary>
	internal sealed unsafe class DekoCanvas : IMenuCanvas
	{
		[DllImport("__Internal", EntryPoint = "obns_dk_open")]
		private static extern int OpenNative(int width, int height);

		[DllImport("__Internal", EntryPoint = "obns_docked")]
		private static extern int Docked();

		/// <summary>The framebuffer's size, fixed at the first open: 1920x1080 docked, 1280x720 handheld (OBNS/SWITCH.md).
		/// The 2D layer scales its 1280x720 units to it.</summary>
		internal static int Width { get; private set; } = 1280;

		internal static int Height { get; private set; } = 720;

		private static bool sized;

		/// <summary>Opens deko3d (once) at the console's size: docked or handheld. 0 when it worked.</summary>
		internal static int Open()
		{
			if (!sized)
			{
				sized = true;
				bool docked = Docked() != 0;
				Width = docked ? 1920 : 1280;
				Height = docked ? 1080 : 720;
			}

			return OpenNative(Width, Height);
		}

		[DllImport("__Internal", EntryPoint = "obns_dk_close")]
		internal static extern void Close();

		[DllImport("__Internal", EntryPoint = "obns_dk2d_begin")]
		private static extern int BeginFrame(uint background);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_end")]
		private static extern int EndFrame();

		[DllImport("__Internal", EntryPoint = "obns_dk2d_fill")]
		private static extern void DkFill(float x, float y, float width, float height, uint color, float radius);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_outline")]
		private static extern void DkOutline(float x, float y, float width, float height, uint color, float thickness, float radius);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_text")]
		private static extern void DkText(char* text, int length, float x, float y, float size, uint color, int bold);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_text_width")]
		private static extern float DkTextWidth(char* text, int length, float size, int bold);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_image_ex")]
		private static extern int DkImage(byte* path, float x, float y, float width, float height, int cover, uint tint);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_clip")]
		private static extern void DkClip(float x, float y, float width, float height);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_unclip")]
		private static extern void DkUnclip();

		/// <summary>Widths measured, by size, boldness and text: the menu measures the same labels every frame.</summary>
		private readonly Dictionary<(float, bool, string), float> widths = new Dictionary<(float, bool, string), float>();

		/// <summary>Starts a frame cleared to the colour (0xAARRGGBB); false if deko3d can't draw.</summary>
		public bool Begin(uint background) => BeginFrame(background) == 0;

		/// <summary>Records, presents and waits for the frame; false if the GPU's queue failed.</summary>
		public bool End() => EndFrame() == 0;

		[DllImport("__Internal", EntryPoint = "obns_dk2d_overlay_end")]
		private static extern int OverlayEnd();

		/// <summary>Starts the in-game interface's drawing over the 3D frame DekoGL is recording (Begin's colour is unused).</summary>
		public bool BeginOverlay() => BeginFrame(0) == 0;

		/// <summary>Records the interface's quads into the 3D frame, after the scene; DekoGL's EndFrame presents both.</summary>
		public bool EndOverlay() => OverlayEnd() == 0;

		public void Fill(float x, float y, float width, float height, uint color, float radius = 0) => DkFill(x, y, width, height, color, radius);

		[DllImport("__Internal", EntryPoint = "obns_dk2d_line")]
		private static extern void DkLine(float x1, float y1, float x2, float y2, uint color, float thickness);

		public void Line(float x1, float y1, float x2, float y2, uint color, float thickness) => DkLine(x1, y1, x2, y2, color, thickness);

		public void Outline(float x, float y, float width, float height, uint color, float thickness, float radius = 0) =>
			DkOutline(x, y, width, height, color, thickness, radius);

		public void Text(string text, float x, float y, float size, uint color, bool bold = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			fixed (char* characters = text)
			{
				DkText(characters, text.Length, x, y, size, color, bold ? 1 : 0);
			}
		}

		public float TextWidth(string text, float size, bool bold = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}

			if (widths.TryGetValue((size, bold, text), out float width))
			{
				return width;
			}

			fixed (char* characters = text)
			{
				width = DkTextWidth(characters, text.Length, size, bold ? 1 : 0);
			}

			if (widths.Count > 20000)
			{
				widths.Clear();
			}

			widths[(size, bold, text)] = width;
			return width;
		}

		public bool Image(string file, float x, float y, float width, float height) => Image(file, x, y, width, height, false, 0xFFFFFFFF);

		/// <summary>An image fitted inside the rectangle, or (<paramref name="cover"/>) filling it with the overflow cropped
		/// (Android's CenterCrop), multiplied by <paramref name="tint"/> (its alpha: the opacity).</summary>
		public bool Image(string file, float x, float y, float width, float height, bool cover, uint tint)
		{
			if (string.IsNullOrEmpty(file))
			{
				return false;
			}

			byte[] path = System.Text.Encoding.UTF8.GetBytes(file + "\0");
			fixed (byte* bytes = path)
			{
				return DkImage(bytes, x, y, width, height, cover ? 1 : 0, tint) != 0;
			}
		}

		public void Clip(float x, float y, float width, float height) => DkClip(x, y, width, height);

		public void Unclip() => DkUnclip();
	}

	/// <summary>
	/// The Switch's menu at 60 frames a second until a route and a train are started (the route file and train
	/// folder returned) or − is pressed (null).
	/// </summary>
	internal static class MenuLoop
	{
		[DllImport("__Internal", EntryPoint = "obns_input_read")]
		internal static extern unsafe uint ReadInput(int* touching, float* x, float* y, int* docked);

		internal static unsafe SwitchLaunch Run(System.Action<string> log)
		{
			int opened = DekoCanvas.Open();
			if (opened != 0)
			{
				log("deko3d did not open (" + opened + "): no menu");
				return null;
			}

			DekoCanvas canvas = new DekoCanvas();
			SwitchMenu menu = new SwitchMenu();
			MenuInput input = new MenuInput();
			Stopwatch clock = Stopwatch.StartNew();
			double last = 0, worst = 0;
			long frames = 0;
			while (true)
			{
				// The console's applet messages (the HOME menu, sleep): taken each frame; the time away isn't animated.
				AppletEvents applet = Applet.Poll();
				string consoleMessages = Applet.TakeMessages();
				if (consoleMessages != null)
				{
					log("menu: " + consoleMessages);
				}

				if (applet != AppletEvents.None)
				{
					log("menu: " + Applet.Describe(applet));
					if ((applet & AppletEvents.Exit) != 0)
					{
						return null;
					}

					if ((applet & (AppletEvents.FocusBack | AppletEvents.Resumed)) != 0)
					{
						last = clock.Elapsed.TotalSeconds;
					}
				}

				if (!Applet.InFocus)
				{
					// Not suspended by the system: the menu waits, drawing nothing, until the game is back in focus.
					if ((Applet.WaitForFocus(log, "menu: ") & AppletEvents.Exit) != 0)
					{
						return null;
					}

					last = clock.Elapsed.TotalSeconds;
				}

				double now = clock.Elapsed.TotalSeconds, seconds = now - last;
				last = now;
				int touching, docked;
				float x, y;
				uint buttons = ReadInput(&touching, &x, &y, &docked);
				input.Update((MenuButtons)buttons, touching != 0, x, y, seconds);
				menu.Docked = docked != 0;
				if (input.Is(MenuButtons.Minus))
				{
					log($"menu: left with −, {frames} frames, the slowest {worst * 1000:F1} ms");
					return null;
				}

				menu.Update(input, seconds);
				if (menu.Launch != null)
				{
					log($"menu: started {menu.Launch.RouteFile} with {menu.Launch.TrainFolder}; {frames} frames, the slowest {worst * 1000:F1} ms");
					return menu.Launch;
				}

				if (!canvas.Begin(MenuStyle.Background))
				{
					log("deko3d: the 2D drawing did not start");
					return null;
				}

				menu.Draw(canvas, seconds);
				if (!canvas.End())
				{
					log("deko3d: the GPU's queue failed in the menu");
					return null;
				}

				frames++;
				double took = clock.Elapsed.TotalSeconds - now;
				if (frames > 2 && took > worst)
				{
					worst = took;
				}

				if (frames == 1)
				{
					log($"menu: the first frame in {took * 1000:F1} ms");
				}
			}
		}
	}
}
