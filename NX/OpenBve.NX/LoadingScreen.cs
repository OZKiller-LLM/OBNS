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
using System.Diagnostics;
using System.Threading;
using OpenBve.Android;

namespace OpenBve.NX
{
	/// <summary>
	/// OBNS 1.32: the loading screen on deko3d - Android's (GameActivity.LoadingScreen) drawn by the 2D layer: the route's
	/// picture across the screen, centre-cropped at 55%, and at the bottom the route's name, "Loading route... NN%" (or
	/// the train) and the bar, from LoadProgress (the route the first 80%, as upstream's). Its own thread draws at 30
	/// frames a second while the loading thread works; an error replaces the bar, in Android's orange-red.
	/// </summary>
	internal static class LoadingScreen
	{
		private static Thread thread;
		private static volatile bool running, finished;
		private static volatile string error;
		private static string title, image;

		/// <summary>Whether deko3d opened (else the screen stays black, as before).</summary>
		internal static bool Opened { get; private set; }

		/// <summary>Opens deko3d (if the menu hasn't) and starts drawing for the route; logs what failed.</summary>
		internal static void Start(Action<string> log, string routeFile, string imageFile)
		{
			int result = DekoCanvas.Open();
			if (result != 0)
			{
				log("deko3d did not open (" + result + "): the screen stays black");
				return;
			}

			title = string.IsNullOrEmpty(routeFile) ? "OpenBVE" : System.IO.Path.GetFileNameWithoutExtension(routeFile);
			image = imageFile;
			if (string.IsNullOrEmpty(image) && !string.IsNullOrEmpty(routeFile))
			{
				try
				{
					image = RouteInfo.FindImageBesideRoute(routeFile);
				}
				catch (Exception)
				{
					image = null;
				}
			}

			Opened = true;
			running = true;
			thread = new Thread(() => Loop(log)) { IsBackground = true, Name = "OBNS loading screen" };
			thread.Start();
			log("loading screen: drawn by deko3d at 30 frames a second" + (string.IsNullOrEmpty(image) ? string.Empty : ", with " + image));
		}

		/// <summary>The bar full, until <see cref="Stop"/>.</summary>
		internal static void Finish() => finished = true;

		/// <summary>The error in place of the bar, as Android's loading screen shows a failed load.</summary>
		internal static void ShowError(string message) => error = message;

		/// <summary>Stops drawing, and closes deko3d unless the game's frames are to follow.</summary>
		internal static void Stop(bool close = true)
		{
			if (!Opened)
			{
				return;
			}

			running = false;
			thread?.Join();
			thread = null;
			if (close)
			{
				DekoCanvas.Close();
			}

			Opened = false;
		}

		private const uint White = 0xFFFFFFFF, Shadow = 0x99000000, OrangeRed = 0xFFFF4500;

		private static void Loop(Action<string> log)
		{
			DekoCanvas canvas = new DekoCanvas();
			Stopwatch clock = Stopwatch.StartNew();
			long frames = 0;
			double worst = 0;
			while (running)
			{
				// The console's applet messages, taken while the route loads too (the HOME menu, sleep); the load itself
				// just goes on once the system lets the game run again.
				AppletEvents applet = Applet.Poll();
				string consoleMessages = Applet.TakeMessages();
				if (consoleMessages != null)
				{
					log("loading screen: " + consoleMessages);
				}

				if (applet != AppletEvents.None)
				{
					log("loading screen: " + Applet.Describe(applet));
				}

				if (!Applet.InFocus)
				{
					// Not suspended by the system: nothing drawn while away; the route goes on loading meanwhile.
					if ((Applet.WaitForFocus(log, "loading screen: ") & AppletEvents.Exit) != 0)
					{
						break;
					}

					continue;
				}

				double start = clock.Elapsed.TotalSeconds;
				if (!canvas.Begin(0xFF0C1018))
				{
					log("loading screen: the 2D drawing did not start");
					break;
				}

				Draw(canvas);
				if (!canvas.End())
				{
					log("deko3d: the GPU's queue failed after " + frames + " frames; the loading screen stops");
					break;
				}

				frames++;
				double took = clock.Elapsed.TotalSeconds - start;
				if (frames > 2)
				{
					worst = Math.Max(worst, took);
				}

				int rest = (int)((1.0 / 30 - took) * 1000);
				if (rest > 0)
				{
					Thread.Sleep(rest);
				}
			}

			running = false;
			log($"loading screen: {frames} frames in {clock.Elapsed.TotalSeconds:F0} s, the slowest {worst * 1000:F1} ms");
		}

		/// <summary>Text with Android's soft black shadow (a 6 px glow there; four offset copies here).</summary>
		private static void ShadowedText(DekoCanvas canvas, string text, float x, float y, float size, uint color, bool bold)
		{
			canvas.Text(text, x - 1.5f, y, size, Shadow, bold);
			canvas.Text(text, x + 1.5f, y, size, Shadow, bold);
			canvas.Text(text, x, y - 1.5f, size, Shadow, bold);
			canvas.Text(text, x, y + 1.5f, size, Shadow, bold);
			canvas.Text(text, x, y, size, color, bold);
		}

		private static void Draw(DekoCanvas canvas)
		{
			const float width = 1280, height = 720, padding = 36;
			if (!string.IsNullOrEmpty(image))
			{
				canvas.Image(image, 0, 0, width, height, true, 0x8CFFFFFF);
			}

			const float titleSize = 34, labelSize = 22, barHeight = 8;
			float barY = height - padding - barHeight;
			float labelY = barY - 14 - labelSize;
			float titleY = labelY - 8 - titleSize;
			ShadowedText(canvas, title, padding, titleY, titleSize, White, false);

			string failure = error;
			if (failure != null)
			{
				ShadowedText(canvas, failure, padding, labelY, labelSize, OrangeRed, false);
				return;
			}

			double value = finished ? 1.0 : Math.Clamp(LoadProgress.Value, 0.0, 1.0);
			string label = LoadProgress.Train != null || finished
				? Menu.T("loading", "loading_train", "Loading train...")
				: Menu.T("loading", "loading_route", "Loading route...") + " " + (value / 0.8 * 100.0).ToString("0") + "%";
			ShadowedText(canvas, label, padding, labelY, labelSize, White, false);
			// Android's horizontal bar: the track, the progress over it.
			float barWidth = width - 2 * padding;
			canvas.Fill(padding, barY, barWidth, barHeight, 0x405CA0FF, barHeight / 2);
			canvas.Fill(padding, barY, (float)(barWidth * value), barHeight, 0xFF5CA0FF, barHeight / 2);
		}
	}
}
