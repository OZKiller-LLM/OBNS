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
using System.Globalization;
using OpenBve.Android;

namespace OpenBve.Switch
{
	/// <summary>
	/// The switch map (upstream's SWITCH_MENU), as Android's SwitchMapPanel for the console: the line around the train with
	/// every track and the train's way ahead highlighted, its switches marked; beside it the switches nearby, the chosen
	/// one's setting and what A does to it. Up and down choose a switch, A changes it, L and R zoom, Y follows the train
	/// again, B or + closes. The game runs on while it is open, as upstream's and Android's; it refreshes twice a second.
	/// </summary>
	public sealed class SwitchMapCard
	{
		// Android's colours (SwitchMapPanel.cs, RouteViews.cs).
		private const uint Dim = 0x6E000000, Card = 0xF010141E, CardBorder = 0x3CFFFFFF, MapBackground = 0xFF0B0E15;
		private const uint Muted = 0x96FFFFFF, White = 0xFFFFFFFF, RowSelected = 0x465CA0FF, Track = 0x78BEC8DC;
		private const uint WayGlow = 0x465CA0FF, Here = 0xFF50C878, Chip = 0xD7161B26;
		private const uint RunThrough = 0xFFEB5050, Fixed = 0xFF78808E, Changeable = 0xFFFFB84D;

		/// <summary>How far either side of the train the map reaches, in metres (Android's).</summary>
		public const double Radius = 2000.0;

		private const float Left = MenuStyle.SafeX, Top = MenuStyle.SafeY, Right = MenuStyle.Width - MenuStyle.SafeX, Bottom = MenuStyle.Height - MenuStyle.SafeY;
		private const float MapWidth = 760, ListRowHeight = 52, Padding = 16;

		private readonly Func<SwitchMapSnapshot> source;
		private SwitchMapSnapshot snapshot;
		private double refresh;
		private int chosenIndex;
		private Guid? chosen;
		// Pixels per metre: about 700 m across to start, as Android's.
		private double scale = -1;

		/// <summary>Raised when the chosen switch is to go to its next setting.</summary>
		public event Action<Guid> ChangeRequested;

		/// <summary>Whether it is shown.</summary>
		public bool Shown { get; private set; }

		public SwitchMapCard(Func<SwitchMapSnapshot> source)
		{
			this.source = source;
		}

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		/// <summary>Upstream's switch menu's own words, without their colons.</summary>
		private static string Upstream(string key, string fallback) => T("switchmenu", key, fallback).Trim().TrimEnd(':', '：').Trim();

		public void Show()
		{
			Shown = true;
			scale = -1;
			refresh = 0;
			snapshot = source();
			chosenIndex = 0;
			// The first switch on the way ahead, as the one to start from.
			if (snapshot != null)
			{
				for (int i = 0; i < snapshot.Switches.Count; i++)
				{
					if (snapshot.Switches[i].OnWay && snapshot.Switches[i].Distance >= 0)
					{
						chosenIndex = i;
						break;
					}
				}
			}

			Choose(chosenIndex);
		}

		public void Hide()
		{
			Shown = false;
		}

		private void Choose(int index)
		{
			IReadOnlyList<SwitchMark> switches = snapshot?.Switches;
			if (switches == null || switches.Count == 0)
			{
				chosen = null;
				chosenIndex = 0;
				return;
			}

			chosenIndex = Math.Clamp(index, 0, switches.Count - 1);
			chosen = switches[chosenIndex].Id;
		}

		/// <summary>The buttons, and the refresh. Call each frame while it is shown.</summary>
		public void Update(MenuInput input, double seconds)
		{
			if (!Shown)
			{
				return;
			}

			refresh -= seconds;
			if (refresh <= 0)
			{
				refresh = 0.5;
				snapshot = source();
				// The chosen switch kept by its id as the list changes around it.
				if (snapshot != null && chosen != null)
				{
					int found = -1;
					for (int i = 0; i < snapshot.Switches.Count; i++)
					{
						if (snapshot.Switches[i].Id == chosen)
						{
							found = i;
							break;
						}
					}

					Choose(found >= 0 ? found : chosenIndex);
				}
				else
				{
					Choose(chosenIndex);
				}
			}

			if (input.Is(MenuButtons.B) || input.Is(MenuButtons.Plus))
			{
				Hide();
				return;
			}

			if (input.Is(MenuButtons.Down))
			{
				Choose(chosenIndex + 1);
			}
			else if (input.Is(MenuButtons.Up))
			{
				Choose(chosenIndex - 1);
			}

			if (input.Is(MenuButtons.R) && scale > 0)
			{
				scale = Math.Min(scale * 1.6, 6.0);
			}
			else if (input.Is(MenuButtons.L) && scale > 0)
			{
				scale = Math.Max(scale / 1.6, MinScale);
			}
			else if (input.Is(MenuButtons.Y))
			{
				scale = -1;
			}

			if (input.Is(MenuButtons.A) && chosen != null && Chosen() is SwitchMark mark && !mark.Fixed)
			{
				ChangeRequested?.Invoke(mark.Id);
				refresh = 0.15;
			}
		}

		private SwitchMark Chosen()
		{
			IReadOnlyList<SwitchMark> switches = snapshot?.Switches;
			return switches != null && chosenIndex < switches.Count ? switches[chosenIndex] : null;
		}

		private double MinScale => Math.Min(MapWidth, Bottom - Top - 100) / (2.2 * (snapshot?.Radius ?? Radius));

		internal static uint ColourOf(SwitchMark mark) => mark.RunThrough ? RunThrough : mark.Fixed ? Fixed : Changeable;

		public void Draw(IMenuCanvas canvas)
		{
			if (!Shown)
			{
				return;
			}

			canvas.Fill(0, 0, MenuStyle.Width, MenuStyle.Height, Dim);
			canvas.Fill(Left, Top, Right - Left, Bottom - Top, Card, 22);
			canvas.Outline(Left, Top, Right - Left, Bottom - Top, CardBorder, 1.5f, 22);
			canvas.Text(Upstream("title", "Select a Switch To Change"), Left + Padding + 4, Top + 14, 24, White, true);

			float mapX = Left + Padding, mapY = Top + 56, mapHeight = Bottom - Top - 56 - 58;
			DrawMap(canvas, mapX, mapY, MapWidth, mapHeight);
			canvas.Text(T("android", "switch_way", "Your train's way ahead is highlighted.").Split('.')[0] + ".", mapX + 4, mapY + mapHeight + 10, 16, Muted);

			float listX = mapX + MapWidth + Padding, listWidth = Right - Padding - listX;
			DrawList(canvas, listX, mapY, listWidth, mapHeight);

			// The buttons, along the foot.
			string hints = "Ⓐ " + T("android", "switch_change", "Change setting") + "   Ⓛ Ⓡ " + T("switch", "zoom", "Zoom") + "   Ⓨ " +
			               T("switch", "follow_train", "Follow the train") + "   Ⓑ " + T("menu", "back", "Back");
			canvas.Text(hints, Right - Padding - canvas.TextWidth(hints, 16), Bottom - 36, 16, Muted);
		}

		private void DrawMap(IMenuCanvas canvas, float x, float y, float width, float height)
		{
			canvas.Fill(x, y, width, height, MapBackground, 12);
			if (snapshot == null)
			{
				return;
			}

			if (scale <= 0)
			{
				scale = Math.Max(MinScale, Math.Min(width, height) / 700.0);
			}

			canvas.Clip(x, y, width, height);
			double centreX = snapshot.X, centreZ = snapshot.Z;
			float Sx(double wx) => x + width / 2 + (float)((wx - centreX) * scale);
			float Sy(double wz) => y + height / 2 - (float)((wz - centreZ) * scale);

			void Polyline(float[] points, uint colour, float thickness)
			{
				if (points == null || points.Length < 4)
				{
					return;
				}

				for (int i = 2; i + 1 < points.Length; i += 2)
				{
					canvas.Line(Sx(points[i - 2]), Sy(points[i - 1]), Sx(points[i]), Sy(points[i + 1]), colour, thickness);
				}
			}

			// Every track, then the way ahead over them.
			foreach (float[] track in snapshot.Tracks)
			{
				Polyline(track, Track, 3);
			}

			Polyline(snapshot.Way, WayGlow, 9);
			Polyline(snapshot.Way, MenuStyle.Accent, 4);

			// Stations, a dot and the name.
			foreach ((string name, double sx, double sz) in snapshot.Stations)
			{
				float px = Sx(sx), py = Sy(sz);
				Dot(canvas, px, py, 4, Muted);
				ChipLabel(canvas, name, px + 8, py - 14, Muted);
			}

			// The train, then the switches - the chosen one last, ringed, with its setting.
			Dot(canvas, Sx(snapshot.X), Sy(snapshot.Z), 7, Here);
			SwitchMark mark = Chosen();
			foreach (SwitchMark other in snapshot.Switches)
			{
				if (other != mark)
				{
					Dot(canvas, Sx(other.X), Sy(other.Z), 7, ColourOf(other));
				}
			}

			if (mark != null)
			{
				float px = Sx(mark.X), py = Sy(mark.Z);
				canvas.Outline(px - 15, py - 15, 30, 30, White, 3, 15);
				Dot(canvas, px, py, 9, ColourOf(mark));
				ChipLabel(canvas, mark.Setting, px + 20, py - 12, White);
			}

			canvas.Unclip();
		}

		private void DrawList(IMenuCanvas canvas, float x, float y, float width, float height)
		{
			canvas.Text(T("android", "switches_nearby", "Nearby switches"), x, y, 17, Muted, true);
			IReadOnlyList<SwitchMark> switches = snapshot?.Switches ?? Array.Empty<SwitchMark>();
			float detailHeight = 150, rowsTop = y + 30, rowsHeight = height - 30 - detailHeight - 10;
			if (switches.Count == 0)
			{
				string none = string.Format(CultureInfo.CurrentCulture, T("android", "switches_none", "No switches within {0} m of the train."), (int)(snapshot?.Radius ?? Radius));
				canvas.Text(MenuStyle.Fit(canvas, none, 17, width), x, rowsTop + 8, 17, Muted);
				return;
			}

			// The rows, scrolled to keep the chosen one in view.
			int visible = Math.Max(1, (int)(rowsHeight / ListRowHeight));
			int first = Math.Clamp(chosenIndex - visible / 2, 0, Math.Max(0, switches.Count - visible));
			canvas.Clip(x, rowsTop, width, rowsHeight);
			for (int i = first; i < switches.Count && i < first + visible; i++)
			{
				SwitchMark mark = switches[i];
				float rowY = rowsTop + (i - first) * ListRowHeight;
				if (i == chosenIndex)
				{
					canvas.Fill(x, rowY + 2, width, ListRowHeight - 4, RowSelected, 10);
					canvas.Outline(x, rowY + 2, width, ListRowHeight - 4, MenuStyle.FocusRing, 2, 10);
				}

				Dot(canvas, x + 18, rowY + ListRowHeight / 2, 7, ColourOf(mark));
				string name = string.IsNullOrEmpty(mark.Name) ? string.Format(CultureInfo.CurrentCulture, T("android", "switch_unnamed", "Switch {0}"), i + 1) : mark.Name;
				canvas.Text(MenuStyle.Fit(canvas, name, 18, width - 40, true), x + 34, rowY + 6, 18, White, true);
				canvas.Text(MenuStyle.Fit(canvas, mark.Setting + " · " + DistanceText(mark.Distance), 15, width - 40), x + 34, rowY + 28, 15, Muted);
			}

			canvas.Unclip();

			// The chosen switch: its setting, where it is, and what can be done.
			SwitchMark chosenMark = Chosen();
			if (chosenMark == null)
			{
				return;
			}

			float detailY = y + height - detailHeight;
			canvas.Fill(x, detailY, width, detailHeight, 0x285CA0FF, 14);
			canvas.Text(MenuStyle.Fit(canvas, Upstream("current", "Current Setting") + ": " + chosenMark.Setting, 18, width - 24, true), x + 12, detailY + 12, 18, White, true);
			canvas.Text(MenuStyle.Fit(canvas, Upstream("distance", "Distance from Player") + ": " + DistanceText(chosenMark.Distance), 16, width - 24), x + 12, detailY + 42, 16, Muted);
			string note = chosenMark.Fixed ? T("android", "switch_fixed", "Fixed route: this switch cannot be changed.") :
				chosenMark.RunThrough ? T("android", "switch_run_through", "A train has run through this switch: change it to reset it.") :
				"Ⓐ " + T("android", "switch_change", "Change setting");
			float noteY = detailY + 72;
			foreach (string line in MenuStyle.Wrap(canvas, note, 16, width - 24))
			{
				canvas.Text(line, x + 12, noteY, 16, chosenMark.Fixed ? Muted : White);
				noteY += 22;
			}
		}

		private static string DistanceText(double distance)
		{
			int amount = (int)Math.Round(Math.Abs(distance));
			return distance >= 0
				? string.Format(CultureInfo.CurrentCulture, T("android", "switch_ahead", "{0} m ahead"), amount)
				: string.Format(CultureInfo.CurrentCulture, T("android", "switch_behind", "{0} m behind"), amount);
		}

		private static void Dot(IMenuCanvas canvas, float x, float y, float radius, uint colour) =>
			canvas.Fill(x - radius, y - radius, 2 * radius, 2 * radius, colour, radius);

		private static void ChipLabel(IMenuCanvas canvas, string text, float x, float y, uint colour)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			float width = canvas.TextWidth(text, 15) + 16;
			canvas.Fill(x, y, width, 24, Chip, 8);
			canvas.Text(text, x + 8, y + 3, 15, colour);
		}
	}
}
