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
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Widget;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Hosts;
using OpenBveApi.Textures;

namespace OpenBve.SmokeTest
{
	/// <summary>
	/// Runs OpenBveApi on the device and reports what worked.
	/// This is a smoke test, not a unit test suite: it exercises the parts of the
	/// compatibility layer the simulation depends on before the renderer is stacked on top.
	/// </summary>
	[Activity(Label = "OpenBVE Smoke Test", MainLauncher = true)]
	public class MainActivity : Activity
	{
		private const string Tag = "OPENBVE_SMOKE";

		private readonly List<string> results = new List<string>();
		private int passed;
		private int failed;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);

			// The upstream code asks Windows Forms where the program lives.
			global::System.Windows.Forms.Application.StartupPath = FilesDir.AbsolutePath;

			Run("precise timer", TestTimer);
			Run("bitmap decode and texture conversion", TestTextureRoundTrip);
			Run("text overlay rendering", TestTextOverlay);
			Run("embedded translations", TestTranslations);
			Run("case-insensitive path resolution", TestPathResolution);
			Run("GLES shim and shader translation", () => ShaderTest.Run(Assets));
			Run("OpenAL playback", AudioTest.Run);

			string summary = passed + " passed, " + failed + " failed";
			Log.Info(Tag, "=== " + summary + " ===");

			StringBuilder text = new StringBuilder();
			text.AppendLine(summary);
			text.AppendLine();
			foreach (string line in results)
			{
				text.AppendLine(line);
			}

			ScrollView scroll = new ScrollView(this);
			TextView view = new TextView(this) { Text = text.ToString(), TextSize = 13.0f };
			view.SetPadding(24, 24, 24, 24);
			scroll.AddView(view);
			SetContentView(scroll);
		}

		private void Run(string name, Func<string> test)
		{
			try
			{
				string detail = test();
				passed++;
				string line = "PASS  " + name + (string.IsNullOrEmpty(detail) ? string.Empty : " - " + detail);
				results.Add(line);
				Log.Info(Tag, line);
			}
			catch (Exception ex)
			{
				failed++;
				string line = "FAIL  " + name + " - " + ex.GetType().Name + ": " + ex.Message;
				results.Add(line);
				Log.Error(Tag, line + "\n" + ex.StackTrace);
			}
		}

		/// <summary>The upstream timer P/Invokes kernel32 and falls back to a stopwatch when that throws.</summary>
		private string TestTimer()
		{
			CPreciseTimer.GetElapsedTime();
			global::System.Threading.Thread.Sleep(30);
			double elapsed = CPreciseTimer.GetElapsedTime();
			if (elapsed <= 0.0 || elapsed > 5.0)
			{
				throw new Exception("implausible elapsed time: " + elapsed);
			}

			return elapsed.ToString("0.000") + " s over a 30 ms sleep";
		}

		/// <summary>
		/// Encodes a PNG, decodes it through the Skia-backed Bitmap, and runs it through
		/// BitmapOrigin exactly as the texture pipeline does. Verifies the byte order ends up RGBA.
		/// </summary>
		private string TestTextureRoundTrip()
		{
			Color[] expected =
			{
				Color.FromArgb(255, 255, 0, 0),
				Color.FromArgb(255, 0, 255, 0),
				Color.FromArgb(255, 0, 0, 255),
				Color.FromArgb(128, 255, 255, 255)
			};

			byte[] encoded;
			using (Bitmap source = new Bitmap(2, 2))
			{
				source.SetPixel(0, 0, expected[0]);
				source.SetPixel(1, 0, expected[1]);
				source.SetPixel(0, 1, expected[2]);
				source.SetPixel(1, 1, expected[3]);
				using (MemoryStream stream = new MemoryStream())
				{
					source.Save(stream, ImageFormat.Png);
					encoded = stream.ToArray();
				}
			}

			using (MemoryStream stream = new MemoryStream(encoded))
			using (Bitmap decoded = new Bitmap(stream))
			{
				if (decoded.Width != 2 || decoded.Height != 2)
				{
					throw new Exception("decoded size was " + decoded.Width + "x" + decoded.Height);
				}

				BitmapOrigin origin = new BitmapOrigin(decoded);
				if (!origin.GetTexture(out Texture texture))
				{
					throw new Exception("BitmapOrigin.GetTexture returned false");
				}

				byte[] bytes = texture.Bytes;
				if (bytes.Length != 16)
				{
					throw new Exception("expected 16 bytes, got " + bytes.Length);
				}

				for (int i = 0; i < 4; i++)
				{
					Color want = expected[i];
					byte r = bytes[i * 4], g = bytes[i * 4 + 1], b = bytes[i * 4 + 2], a = bytes[i * 4 + 3];
					if (r != want.R || g != want.G || b != want.B || a != want.A)
					{
						throw new Exception("pixel " + i + " was RGBA(" + r + "," + g + "," + b + "," + a +
						                    "), expected RGBA(" + want.R + "," + want.G + "," + want.B + "," + want.A + ")");
					}
				}
			}

			return "4 pixels survived PNG encode, decode and RGBA conversion, alpha intact";
		}

		/// <summary>Exercises Graphics, Font, MeasureString and DrawString through the Skia shim.</summary>
		private string TestTextOverlay()
		{
			Bitmap bitmap = TextOverlay.AddTextToBitmap(null, "OpenBVE", "sans-serif", 24,
				Color.FromArgb(255, 0, 0, 96), Color.White, new Vector2(4, 4));

			if (bitmap == null)
			{
				throw new Exception("AddTextToBitmap returned null");
			}

			if (bitmap.Width < 8 || bitmap.Height < 8)
			{
				throw new Exception("overlay was " + bitmap.Width + "x" + bitmap.Height);
			}

			// Something other than the background must have been drawn.
			bool drawn = false;
			for (int y = 0; y < bitmap.Height && !drawn; y++)
			{
				for (int x = 0; x < bitmap.Width; x++)
				{
					Color c = bitmap.GetPixel(x, y);
					if (c.R > 128 && c.G > 128 && c.B > 128)
					{
						drawn = true;
						break;
					}
				}
			}

			if (!drawn)
			{
				throw new Exception("no text pixels found in the " + bitmap.Width + "x" + bitmap.Height + " overlay");
			}

			string size = bitmap.Width + "x" + bitmap.Height;
			bitmap.Dispose();
			return "rendered text to a " + size + " bitmap";
		}

		/// <summary>Loads the embedded en-US translation and reads a string out of it.</summary>
		private string TestTranslations()
		{
			// No language folder on device yet, so this falls through to the embedded copy.
			Translations.LoadLanguageFiles(OpenBveApi.Path.CombineDirectory(FilesDir.AbsolutePath, "Languages"));

			string title = Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "program", "title" });
			if (string.IsNullOrWhiteSpace(title))
			{
				throw new Exception("program/title came back empty");
			}

			string power = Translations.GetInterfaceString(HostApplication.OpenBve, new[] { "handles", "power" });
			return "program/title = \"" + title + "\", handles/power = \"" + power + "\"";
		}

		/// <summary>
		/// Android storage is case-sensitive and route content is notoriously inconsistent about
		/// casing. This checks whether the upstream path fixup finds a file whose case does not match.
		/// </summary>
		private string TestPathResolution()
		{
			string root = OpenBveApi.Path.CombineDirectory(FilesDir.AbsolutePath, "PathTest");
			Directory.CreateDirectory(root);
			string actual = OpenBveApi.Path.CombineFile(root, "Texture.PNG");
			File.WriteAllText(actual, "not really a png");

			string resolved = OpenBveApi.Path.CombineFile(root, "texture.png");
			bool found = File.Exists(resolved);

			File.Delete(actual);
			Directory.Delete(root);

			if (!found)
			{
				throw new Exception("a mis-cased filename did not resolve: routes with inconsistent casing will fail to load");
			}

			return "mis-cased filenames resolve";
		}
	}
}

