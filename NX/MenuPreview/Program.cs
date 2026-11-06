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

extern alias GdiPlus;

using System;
using System.Collections.Generic;
using System.IO;
using OpenBve.Android;
using OpenBve.Switch;
using Gdi = GdiPlus::System.Drawing;
using Gdi2D = GdiPlus::System.Drawing.Drawing2D;
using GdiText = GdiPlus::System.Drawing.Text;

namespace OpenBve.NX.MenuPreview
{
	/// <summary>
	/// Draws the Switch's menu into PNG files: a script of button presses (one per line of the script, e.g. "R", "Down x3",
	/// "A", "shot options-top"), each "shot" writing what the console would show at that point.
	/// Arguments: the data folder (OpenBVE's assets), the content folder (Railway and Train), the output folder, the script.
	/// </summary>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			if (args.Length < 4)
			{
				Console.WriteLine("MenuPreview <data folder> <content folder> <output folder> <script>");
				return 1;
			}

			string data = args[0], content = args[1], output = args[2];
			Directory.CreateDirectory(output);
			SwitchSettings.File = Path.Combine(output, "options-nx.cfg");
			AndroidHost host = new AndroidHost();
			// A file system configuration of its own, not the PC's OpenBVE one (%AppData%\OpenBve).
			string configuration = Path.Combine(output, "filesystem.cfg");
			File.WriteAllText(configuration, "Version=2\nData=" + data + "\n");
			OpenBveApi.FileSystem.FileSystem fileSystem = OpenBveApi.FileSystem.FileSystem.FromCommandLineArgs(new[] { "/filesystem=" + configuration }, host);
			fileSystem.DataFolder = data;
			fileSystem.SettingsFolder = output;
			fileSystem.InitialRouteFolder = Path.Combine(content, "Railway", "Route");
			fileSystem.RouteInstallationDirectory = Path.Combine(content, "Railway");
			fileSystem.InitialTrainFolder = Path.Combine(content, "Train");
			fileSystem.TrainInstallationDirectory = Path.Combine(content, "Train");
			fileSystem.OtherInstallationDirectory = Path.Combine(content, "Other");
			Menu.Init(host, fileSystem, SwitchSettings.GetLanguage(), "1.14.1.2-nx");

			SwitchMenu menu = new SwitchMenu();
			MenuInput input = new MenuInput();
			GdiCanvas canvas = new GdiCanvas();
			int shots = 0;
			foreach (string raw in File.ReadAllLines(args[3]))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}

				string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (parts[0] == "shot")
				{
					// Settle the easing and any preview loading first.
					for (int i = 0; i < 30; i++)
					{
						input.Update(MenuButtons.None, false, 0, 0, 0.1);
						menu.Update(input, 0.1);
						canvas.Begin();
						menu.Draw(canvas, 0.1);
						System.Threading.Thread.Sleep(10);
					}

					string file = Path.Combine(output, (parts.Length > 1 ? parts[1] : "shot" + shots) + ".png");
					canvas.Save(file);
					shots++;
					Console.WriteLine("wrote " + file);
					continue;
				}

				if (parts[0] == "card")
				{
					// The in-game driving card over a stand-in for the scene, with sample statuses.
					string name = parts.Length > 1 ? parts[1] : "card";
					DriverStatus[] samples =
					{
						new DriverStatus(42, 60, "P3", HandleState.Power, "F", "10:03:12", "Alpha", "350 m", "10:04:00", "10:05:00", null,
							new[] { ("Sample message: the doors are closed", OpenBveApi.Colors.MessageColor.White) }, "Cab", false),
						new DriverStatus(68, 60, "B5", HandleState.Brake, "F", "10:03:40", "Beta", "1.2 km", null, "10:09:30",
							"ATS: brakes applied", Array.Empty<(string, OpenBveApi.Colors.MessageColor)>(), "Cab", false),
						new DriverStatus(0, 0, "EB", HandleState.Emergency, "N", "10:04:02", null, null, null, null, null,
							new[] { ("Signal at danger", OpenBveApi.Colors.MessageColor.Red), ("You have passed a red signal", OpenBveApi.Colors.MessageColor.Orange) }, "Cab", false)
					};
					for (int i = 0; i < samples.Length; i++)
					{
						canvas.Begin();
						canvas.Fill(0, 0, 1280, 360, 0xFF6E8FB4);
						canvas.Fill(0, 360, 1280, 360, 0xFF4A4E44);
						DriverCard.Draw(canvas, samples[i]);
						string file = Path.Combine(output, name + "-" + (i + 1) + ".png");
						canvas.Save(file);
						Console.WriteLine("wrote " + file);
					}

					continue;
				}

				if (parts[0] == "tap" && parts.Length >= 3)
				{
					float x = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
					float y = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
					Frame(menu, input, canvas, MenuButtons.None, true, x, y);
					Frame(menu, input, canvas, MenuButtons.None, false, x, y);
					continue;
				}

				if (!Enum.TryParse(parts[0], true, out MenuButtons button))
				{
					Console.WriteLine("unknown: " + line);
					continue;
				}

				int times = parts.Length > 1 && parts[1].StartsWith("x", StringComparison.Ordinal) ? int.Parse(parts[1].Substring(1)) : 1;
				for (int i = 0; i < times; i++)
				{
					Frame(menu, input, canvas, button, false, 0, 0);
					Frame(menu, input, canvas, MenuButtons.None, false, 0, 0);
				}

				if (menu.Launch != null)
				{
					Console.WriteLine("started: " + menu.Launch.RouteFile + " with " + menu.Launch.TrainFolder);
				}
			}

			return 0;
		}

		private static void Frame(SwitchMenu menu, MenuInput input, GdiCanvas canvas, MenuButtons buttons, bool touching, float x, float y)
		{
			input.Update(buttons, touching, x, y, 1.0 / 60);
			menu.Update(input, 1.0 / 60);
			canvas.Begin();
			menu.Draw(canvas, 1.0 / 60);
		}
	}

	/// <summary>The menu's canvas through GDI+, at 1280x720: close enough to deko3d's to judge the layout.</summary>
	internal sealed class GdiCanvas : IMenuCanvas
	{
		private readonly Gdi.Bitmap bitmap = new Gdi.Bitmap(1280, 720);
		private readonly Gdi.Graphics graphics;
		private readonly Dictionary<(float, bool), Gdi.Font> fonts = new Dictionary<(float, bool), Gdi.Font>();
		private readonly Dictionary<string, Gdi.Image> images = new Dictionary<string, Gdi.Image>();
		private readonly Gdi.StringFormat format = (Gdi.StringFormat)Gdi.StringFormat.GenericTypographic.Clone();

		public GdiCanvas()
		{
			graphics = Gdi.Graphics.FromImage(bitmap);
			graphics.SmoothingMode = Gdi2D.SmoothingMode.AntiAlias;
			graphics.TextRenderingHint = GdiText.TextRenderingHint.AntiAliasGridFit;
			graphics.InterpolationMode = Gdi2D.InterpolationMode.HighQualityBicubic;
			format.FormatFlags |= Gdi.StringFormatFlags.MeasureTrailingSpaces | Gdi.StringFormatFlags.NoWrap;
		}

		public void Begin() => graphics.ResetClip();

		public void Save(string file) => bitmap.Save(file, Gdi.Imaging.ImageFormat.Png);

		private static Gdi.Color Color(uint argb) => Gdi.Color.FromArgb(unchecked((int)argb));

		private Gdi.Font Font(float size, bool bold)
		{
			if (!fonts.TryGetValue((size, bold), out Gdi.Font font))
			{
				// The console's standard shared font is a sans serif much like this.
				font = new Gdi.Font("Segoe UI", size * 0.8f, bold ? Gdi.FontStyle.Bold : Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
				fonts[(size, bold)] = font;
			}

			return font;
		}

		private static Gdi2D.GraphicsPath Rounded(float x, float y, float width, float height, float radius)
		{
			Gdi2D.GraphicsPath path = new Gdi2D.GraphicsPath();
			radius = Math.Min(radius, Math.Min(width, height) / 2);
			if (radius <= 0.5f)
			{
				path.AddRectangle(new Gdi.RectangleF(x, y, width, height));
				return path;
			}

			float d = radius * 2;
			path.AddArc(x, y, d, d, 180, 90);
			path.AddArc(x + width - d, y, d, d, 270, 90);
			path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
			path.AddArc(x, y + height - d, d, d, 90, 90);
			path.CloseFigure();
			return path;
		}

		public void Fill(float x, float y, float width, float height, uint color, float radius = 0)
		{
			if (width <= 0 || height <= 0)
			{
				return;
			}

			using Gdi.SolidBrush brush = new Gdi.SolidBrush(Color(color));
			using Gdi2D.GraphicsPath path = Rounded(x, y, width, height, radius);
			graphics.FillPath(brush, path);
		}

		public void Outline(float x, float y, float width, float height, uint color, float thickness, float radius = 0)
		{
			using Gdi.Pen pen = new Gdi.Pen(Color(color), thickness);
			float half = thickness / 2;
			using Gdi2D.GraphicsPath path = Rounded(x + half, y + half, width - thickness, height - thickness, Math.Max(0, radius - half));
			graphics.DrawPath(pen, path);
		}

		public void Text(string text, float x, float y, float size, uint color, bool bold = false)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			using Gdi.SolidBrush brush = new Gdi.SolidBrush(Color(color));
			graphics.DrawString(text, Font(size, bold), brush, x, y, format);
		}

		public float TextWidth(string text, float size, bool bold = false) =>
			string.IsNullOrEmpty(text) ? 0 : graphics.MeasureString(text, Font(size, bold), int.MaxValue, format).Width;

		public bool Image(string file, float x, float y, float width, float height)
		{
			if (!images.TryGetValue(file, out Gdi.Image image))
			{
				try
				{
					image = Gdi.Image.FromFile(file);
				}
				catch (Exception)
				{
					image = null;
				}

				images[file] = image;
			}

			if (image == null)
			{
				return false;
			}

			float scale = Math.Min(width / image.Width, height / image.Height);
			float w = image.Width * scale, h = image.Height * scale;
			graphics.DrawImage(image, x + (width - w) / 2, y + (height - h) / 2, w, h);
			return true;
		}

		public void Clip(float x, float y, float width, float height) => graphics.SetClip(new Gdi.RectangleF(x, y, width, height));

		public void Unclip() => graphics.ResetClip();
	}
}
