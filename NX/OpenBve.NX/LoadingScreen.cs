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
using System.Runtime.InteropServices;
using System.Threading;
using OpenBve.Android;

namespace OpenBve.NX
{
	/// <summary>
	/// OBNS 1.31N, step 3's first part: deko3d's first frames on the console - the loading screen's progress bar
	/// (LoadProgress: the route the first 80%, the train the rest, as upstream's), drawn from rectangles by its own thread
	/// at 30 frames a second while the loading thread works. A small block moves along under the bar so that a long
	/// stage still shows the game is alive. Text and the route's image come with the 2D drawing's shaders.
	/// </summary>
	internal static class LoadingScreen
	{
		[DllImport("__Internal", EntryPoint = "obns_dk_open")]
		private static extern int Open(int width, int height);

		[DllImport("__Internal", EntryPoint = "obns_dk_frame_rects")]
		private static extern unsafe int FrameRects(float red, float green, float blue, float* rects, int count);

		[DllImport("__Internal", EntryPoint = "obns_dk_close")]
		private static extern void Close();

		private const int Width = 1280, Height = 720;

		private static Thread thread;
		private static volatile bool running;
		private static volatile bool finished;

		/// <summary>Whether deko3d opened (else the screen stays black, as before).</summary>
		internal static bool Opened { get; private set; }

		/// <summary>Opens deko3d and starts drawing; logs what failed.</summary>
		internal static void Start(Action<string> log)
		{
			int result = Open(Width, Height);
			if (result != 0)
			{
				log("deko3d did not open (" + result + "): the screen stays black");
				return;
			}

			Opened = true;
			running = true;
			thread = new Thread(() => Loop(log)) { IsBackground = true, Name = "OBNS loading screen" };
			thread.Start();
			log("deko3d open: the loading screen draws at 30 frames a second");
		}

		/// <summary>The bar full, in the accent colour, until <see cref="Stop"/>.</summary>
		internal static void Finish() => finished = true;

		/// <summary>Stops drawing and closes deko3d.</summary>
		internal static void Stop()
		{
			if (!Opened)
			{
				return;
			}

			running = false;
			thread?.Join();
			Close();
			Opened = false;
		}

		// The menu's palette (MenuStyle), as 0 to 1.
		private static readonly float[] Background = Rgb(0x12151C), Track = Rgb(0x262C3A), Accent = Rgb(0x5CA0FF), Secondary = Rgb(0x96A0B2), Done = Rgb(0x8CC8FF);

		private static float[] Rgb(int rgb) => new[] { ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f };

		private static unsafe void Loop(Action<string> log)
		{
			Stopwatch clock = Stopwatch.StartNew();
			float* rects = stackalloc float[7 * 8];
			long frames = 0;
			double worst = 0;
			while (running)
			{
				double start = clock.Elapsed.TotalSeconds;
				double progress = finished ? 1.0 : Math.Clamp(LoadProgress.Value, 0.0, 1.0);
				int count = 0;
				const float barX = 240, barY = 520, barWidth = 800, barHeight = 14;
				Rect(rects, ref count, barX, barY, barWidth, barHeight, Track);
				Rect(rects, ref count, barX, barY, (float)(barWidth * progress), barHeight, finished ? Done : Accent);
				// Where the route's part ends and the train's starts (80%).
				Rect(rects, ref count, barX + barWidth * 0.8f - 1, barY - 6, 2, barHeight + 12, Secondary);
				if (!finished)
				{
					// Alive: a block going to and fro under the bar, once every 2 s.
					double phase = start % 2.0 / 2.0;
					double along = phase < 0.5 ? phase * 2 : 2 - phase * 2;
					Rect(rects, ref count, barX + (float)(along * (barWidth - 60)), barY + barHeight + 14, 60, 4, Secondary);
				}

				int result = FrameRects(Background[0], Background[1], Background[2], rects, count);
				if (result != 0)
				{
					log("deko3d: the queue is in an error state after " + frames + " frames; the loading screen stops");
					running = false;
					break;
				}

				frames++;
				double took = clock.Elapsed.TotalSeconds - start;
				worst = Math.Max(worst, took);
				int rest = (int)((1.0 / 30 - took) * 1000);
				if (rest > 0)
				{
					Thread.Sleep(rest);
				}
			}

			log($"loading screen: {frames} frames in {clock.Elapsed.TotalSeconds:F0} s, the slowest {worst * 1000:F1} ms");
		}

		private static unsafe void Rect(float* rects, ref int count, float x, float y, float width, float height, float[] color)
		{
			float* r = rects + count * 7;
			r[0] = x;
			r[1] = y;
			r[2] = width;
			r[3] = height;
			r[4] = color[0];
			r[5] = color[1];
			r[6] = color[2];
			count++;
		}
	}
}
