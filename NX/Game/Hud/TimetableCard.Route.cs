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
using System.Globalization;
using OpenBve.Android;

namespace OpenBve.Switch
{
	/*
	 * The route card's Map and Gradient tabs (Android's TimetablePanel and RouteViews: RouteMapView, GradientChartView),
	 * drawn by the console's 2D layer and following the train until moved (the user, 2026-11-25: panning and zooming - the
	 * right stick, which the card has while it is open, and a USB mouse dragged and wheeled over it): the map
	 * the line's plan around the train, travelled in blue and ahead in white, the stations and the other trains; the
	 * gradient the line's height 4 km at a time from just behind the train, with the gradients in per mille.
	 */
	public sealed partial class TimetableCard
	{
		/// <summary>Android's tabs.</summary>
		public enum Tab
		{
			Timetable,
			Map,
			Gradient
		}

		private const uint Faint = 0x28FFFFFF, MapBackground = 0xFF0B0E15, Chip = 0xD7161B26, Other = 0xFFFFB84D;

		/// <summary>The tab shown.</summary>
		public Tab CurrentTab { get; private set; }

		/// <summary>The route's plan and elevation (the session's, worked out once).</summary>
		public Func<RouteGeometry> Geometry;

		/// <summary>Where the trains are now.</summary>
		public Func<RouteLive> Live;

		private RouteGeometry geometry;
		private RouteLive live;
		private double liveAge = double.MaxValue;

		/// <summary>A route tab shown: the live positions read again at once, and the view on the train again.</summary>
		private void RouteShown()
		{
			liveAge = double.MaxValue;
			routeFollowing = true;
			mapOffsetX = mapOffsetZ = gradientOffset = 0;
		}

		// The view moved away from the train: the map's centre and the gradient's start, from the train's, in metres; the
		// zooms are kept between tabs.
		private bool routeFollowing = true;
		private double mapOffsetX, mapOffsetZ, gradientOffset;
		private double mapSpan = 5000, gradientSpan = 4000;
		private float routeX, routeY, routeWidth, routeHeight;

		/// <summary>Whether a point (1280x720 units) is over the map or the gradient on show.</summary>
		public bool RouteContains(float px, float py) =>
			Shown && CurrentTab != Tab.Timetable && routeWidth > 0 && px >= routeX && px <= routeX + routeWidth && py >= routeY && py <= routeY + routeHeight;

		/// <summary>The right stick on a route tab: the map pans; the gradient pans along the line (left, right) and zooms (up, down).</summary>
		private void RouteStick(float stickX, float stickY, double seconds)
		{
			const float deadZone = 0.2f;
			float sx = Math.Abs(stickX) > deadZone ? stickX : 0, sy = Math.Abs(stickY) > deadZone ? stickY : 0;
			if (sx == 0 && sy == 0)
			{
				return;
			}

			routeFollowing = false;
			if (CurrentTab == Tab.Map)
			{
				// Half the view across a second at full tilt.
				mapOffsetX += sx * mapSpan * 0.5 * seconds;
				mapOffsetZ += sy * mapSpan * 0.5 * seconds;
			}
			else
			{
				gradientOffset += sx * gradientSpan * 0.5 * seconds;
				gradientSpan = Math.Max(500, Math.Min(40000, gradientSpan * Math.Exp(-sy * 1.5 * seconds)));
			}
		}

		/// <summary>A drag over the route tab (1280x720 units): the view follows the pointer.</summary>
		public void RouteDrag(float dx, float dy)
		{
			if (routeWidth <= 0 || dx == 0 && dy == 0)
			{
				return;
			}

			routeFollowing = false;
			if (CurrentTab == Tab.Map)
			{
				double metresPerUnit = mapSpan / routeWidth;
				mapOffsetX -= dx * metresPerUnit;
				mapOffsetZ += dy * metresPerUnit;
			}
			else
			{
				gradientOffset -= dx / routeWidth * gradientSpan;
			}
		}

		/// <summary>The wheel over the route tab: up zooms in.</summary>
		public void RouteZoom(int wheel)
		{
			double factor = wheel > 0 ? 0.8 : 1.25;
			if (CurrentTab == Tab.Map)
			{
				mapSpan = Math.Max(500, Math.Min(40000, mapSpan * factor));
			}
			else
			{
				gradientSpan = Math.Max(500, Math.Min(40000, gradientSpan * factor));
			}
		}

		private void DrawTabs(IMenuCanvas canvas, float left, float y, float inner, string clock)
		{
			string[] names = { T("android", "timetable", "Timetable"), T("android", "route_map", "Map"), T("android", "route_gradient", "Gradient") };
			float x = left;
			for (int i = 0; i < names.Length; i++)
			{
				bool current = (int)CurrentTab == i;
				float width = canvas.TextWidth(names[i], 18, true) + 20;
				if (current)
				{
					canvas.Fill(x - 4, y - 5, width, 28, 0x465CA0FF, 10);
				}

				canvas.Text(names[i], x + 6, y, 18, current ? White : Muted, true);
				x += width + 4;
			}

			// What goes on to the next tab: the − button.
			canvas.Text("(−)", x + 2, y + 1, 16, Muted);
			if (!string.IsNullOrEmpty(clock))
			{
				canvas.Text(clock, left + inner - canvas.TextWidth(clock, 18, true), y, 18, Muted, true);
			}
		}

		private void DrawRoute(IMenuCanvas canvas, float x, float y, float width, float height, double seconds)
		{
			geometry ??= Geometry?.Invoke();
			liveAge += seconds;
			if (live == null || liveAge >= 0.5)
			{
				live = Live?.Invoke() ?? live;
				liveAge = 0;
			}

			canvas.Fill(x - 6, y, width + 12, height, MapBackground, 12);
			if (geometry == null || live == null || geometry.Position.Length < 2)
			{
				canvas.Text(T("android", "route_none", "No route information."), x + 8, y + 12, 17, Muted);
				return;
			}

			routeX = x - 6;
			routeY = y;
			routeWidth = width + 12;
			routeHeight = height;
			canvas.Clip(x - 6, y, width + 12, height);
			if (CurrentTab == Tab.Map)
			{
				DrawMap(canvas, x, y, width, height);
			}
			else
			{
				DrawGradient(canvas, x, y, width, height);
			}

			canvas.Unclip();
		}

		private void DrawMap(IMenuCanvas canvas, float x, float y, float width, float height)
		{
			// About 5 km across, centred on the train (Android's following zoom), unless moved or zoomed.
			double scale = width / mapSpan;
			double centreX = live.X + (routeFollowing ? 0 : mapOffsetX), centreZ = live.Z + (routeFollowing ? 0 : mapOffsetZ);
			float cx = x + width / 2, cy = y + height / 2;
			float Sx(double wx) => cx + (float)((wx - centreX) * scale);
			float Sy(double wz) => cy - (float)((wz - centreZ) * scale);

			double[] px = geometry.X, pz = geometry.Z, pos = geometry.Position;
			// The line: a dark edge under it, then ahead in white and travelled in blue.
			for (int pass = 0; pass < 2; pass++)
			{
				for (int i = 1; i < px.Length; i++)
				{
					float x0 = Sx(px[i - 1]), y0 = Sy(pz[i - 1]), x1 = Sx(px[i]), y1 = Sy(pz[i]);
					if (Math.Max(x0, x1) < x - 20 || Math.Min(x0, x1) > x + width + 20 || Math.Max(y0, y1) < y - 20 || Math.Min(y0, y1) > y + height + 20)
					{
						continue;
					}

					if (pass == 0)
					{
						canvas.Line(x0, y0, x1, y1, 0xA0000000, 7);
					}
					else
					{
						canvas.Line(x0, y0, x1, y1, pos[i] <= live.Position ? MenuStyle.Accent : 0xD2E1E6F0, 4);
					}
				}
			}

			// The stations: stops as dots, the next in blue, passes small; their names where there is room.
			foreach (RouteStationMark station in geometry.Stations)
			{
				float sx = Sx(station.X), sy = Sy(station.Z);
				if (sx < x - 40 || sx > x + width + 40 || sy < y - 20 || sy > y + height + 20)
				{
					continue;
				}

				bool next = station.Index == live.NextStation;
				bool passed = station.Position < live.Position - 1;
				Dot(canvas, sx, sy, station.Stops ? (next ? 6.5f : 5f) : 2.5f, station.Stops ? (next || passed ? Current : White) : White);
				if (station.Stops)
				{
					float labelWidth = canvas.TextWidth(station.Name, 15, next) + 16;
					canvas.Fill(sx + 9, sy - 12, labelWidth, 24, next ? Current : Chip, 8);
					canvas.Text(station.Name, sx + 17, sy - 9, 15, White, next);
				}
			}

			// The other trains, then the player's.
			foreach ((double X, double Z, bool Player) train in live.Trains)
			{
				if (!train.Player)
				{
					Dot(canvas, Sx(train.X), Sy(train.Z), 5, Other);
				}
			}

			float hereX = Sx(live.X), hereY = Sy(live.Z);
			Dot(canvas, hereX, hereY, 14, 0x4650C878);
			Dot(canvas, hereX, hereY, 7, Here);

			// The scale, bottom left.
			double metres = NiceStep(90 / scale);
			float bar = (float)(metres * scale), bx = x + 6, by = y + height - 14;
			canvas.Line(bx, by, bx + bar, by, White, 2);
			canvas.Line(bx, by - 5, bx, by, White, 2);
			canvas.Line(bx + bar, by - 5, bx + bar, by, White, 2);
			canvas.Text(metres >= 1000 ? (metres / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " km" : metres.ToString("0", CultureInfo.InvariantCulture) + " m", bx + bar + 8, by - 10, 14, White);
		}

		private void DrawGradient(IMenuCanvas canvas, float x, float y, float width, float height)
		{
			double span = gradientSpan;
			double start = live.Position - span * 0.25 + (routeFollowing ? 0 : gradientOffset), end = start + span;
			double[] pos = geometry.Position, heights = geometry.Y;
			float top = y + 30, bottom = y + height - 26;
			float Sx(double p) => x + (float)((p - start) / span * width);
			int i0 = Math.Max(0, Array.BinarySearch(pos, start) is int a && a < 0 ? ~a - 1 : a);
			int i1 = Math.Min(pos.Length - 1, Array.BinarySearch(pos, end) is int b && b < 0 ? ~b : b);
			double y0 = double.MaxValue, y1 = double.MinValue;
			for (int i = i0; i <= i1; i++)
			{
				y0 = Math.Min(y0, heights[i]);
				y1 = Math.Max(y1, heights[i]);
			}

			if (y0 > y1)
			{
				y0 = y1 = live.Y;
			}

			double middle = (y0 + y1) / 2, range = Math.Max(20, (y1 - y0) * 1.25);
			y0 = middle - range / 2;
			y1 = middle + range / 2;
			float Sy(double h) => bottom - (float)((h - y0) / (y1 - y0) * (bottom - top));

			// The kilometre lines and their labels.
			double step = NiceStep(span / 5);
			for (double p = Math.Ceiling(start / step) * step; p <= end; p += step)
			{
				float lx = Sx(p);
				canvas.Line(lx, top, lx, bottom, Faint, 1);
				canvas.Text((p / 1000).ToString("0.0#", CultureInfo.InvariantCulture) + " km", lx + 4, bottom + 6, 14, Muted);
			}

			// The profile: filled down to the foot in thin columns (the 2D layer has no polygons), then the line over it.
			for (float column = x; column < x + width; column += 3)
			{
				double p = start + (column - x) / width * span;
				double h = HeightAt(pos, heights, p);
				float sy = Sy(h);
				if (sy < bottom)
				{
					canvas.Fill(column, sy, 3, bottom - sy, 0x325CA0FF);
				}
			}

			for (int i = i0; i < i1; i++)
			{
				canvas.Line(Sx(pos[i]), Sy(heights[i]), Sx(pos[i + 1]), Sy(heights[i + 1]), White, 2.5f);
			}

			// The gradients over each stretch of one pitch wide enough for its label.
			int run = i0;
			for (int i = i0 + 1; i <= i1 + 1; i++)
			{
				if (i <= i1 && Math.Abs(geometry.Pitch[i] - geometry.Pitch[run]) < 1e-6)
				{
					continue;
				}

				int last = Math.Min(i, i1);
				float x0 = Sx(pos[run]), x1 = Sx(pos[last]);
				string text = PerMille(geometry.Pitch[run]);
				if (x1 - x0 > canvas.TextWidth(text, 15) + 12)
				{
					int mid = (run + last) / 2;
					float ty = Math.Max(top + 2, Sy(heights[mid]) - 22);
					canvas.Text(text, (x0 + x1) / 2 - canvas.TextWidth(text, 15) / 2, ty, 15, White);
				}

				run = i;
			}

			// The stations along the foot, and the train.
			foreach (RouteStationMark station in geometry.Stations)
			{
				if (station.Position < start || station.Position > end || !station.Stops)
				{
					continue;
				}

				float sx = Sx(station.Position);
				canvas.Line(sx, top, sx, bottom, 0x50FFFFFF, 1);
				canvas.Text(MenuStyle.Fit(canvas, station.Name, 14, 140), sx + 4, top - 22, 14, station.Index == live.NextStation ? Current : White, station.Index == live.NextStation);
			}

			float tx = Sx(live.Position), ty2 = Sy(live.Y);
			canvas.Line(tx, top, tx, bottom, 0x8C50C878, 2);
			Dot(canvas, tx, ty2, 7, Here);
		}

		/// <summary>A gradient in per mille, as Android's: "level" when flat.</summary>
		private static string PerMille(double pitch)
		{
			double value = Math.Round(pitch * 1000.0, 1);
			if (Math.Abs(value) < 0.05)
			{
				return T("android", "gradient_level", "level");
			}

			return (value > 0 ? "+" : string.Empty) + value.ToString("0.#", CultureInfo.InvariantCulture) + "‰";
		}

		/// <summary>The line's height at a track position, between the geometry's points.</summary>
		private static double HeightAt(double[] pos, double[] heights, double p)
		{
			int i = Array.BinarySearch(pos, p);
			if (i >= 0)
			{
				return heights[i];
			}

			i = ~i;
			if (i <= 0)
			{
				return heights[0];
			}

			if (i >= pos.Length)
			{
				return heights[pos.Length - 1];
			}

			double t = (p - pos[i - 1]) / Math.Max(1e-6, pos[i] - pos[i - 1]);
			return heights[i - 1] + (heights[i] - heights[i - 1]) * t;
		}

		/// <summary>1, 2 or 5 times a power of ten, the nearest to <paramref name="raw"/> (Android's RouteMapView.NiceStep).</summary>
		private static double NiceStep(double raw)
		{
			if (raw <= 0)
			{
				return 1;
			}

			double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
			double unit = raw / power;
			return (unit < 1.5 ? 1 : unit < 3.5 ? 2 : unit < 7.5 ? 5 : 10) * power;
		}
	}
}
