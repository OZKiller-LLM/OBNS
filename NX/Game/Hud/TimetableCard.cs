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
using OpenBve.Android;

namespace OpenBve.Switch
{
	/// <summary>
	/// The timetable as Android's route card shows it on its Timetable tab (TimetablePanel.cs), drawn over the scene by
	/// deko3d: the next stop at the top - "→" ahead, "●" at the station - with its distance and how the train stands
	/// against the timetable (late in orange); below it the stations as a timeline, the line travelled in the accent colour,
	/// the next stop ringed, the train's station green, each row with its arrival and departure. − shows and hides it; it
	/// keeps the current station in view, and the right stick scrolls it. (Android's Map and Gradient tabs, and the route's
	/// own timetable picture, are still to come.)
	/// </summary>
	public sealed partial class TimetableCard
	{
		// Android's colours (TimetablePanel.cs).
		private const uint Card = 0xF010141E, CardBorder = 0x3CFFFFFF, Muted = 0x96FFFFFF, Line = 0x5AFFFFFF;
		private const uint Current = 0xFF5CA0FF, Here = 0xFF50C878, Late = 0xFFFFAA3C, CurrentRow = 0x285CA0FF, White = 0xFFFFFFFF;

		private const float Width = 460, Top = MenuStyle.SafeY, Bottom = 560, Padding = 18;
		private const float NameSize = 21, DetailSize = 18, TimeSize = 19, NextSize = 26, RowHeight = 54, MarkerWidth = 30, TimeWidth = 92;

		/// <summary>Whether it is shown.</summary>
		public bool Shown;

		private float scroll, target;
		private bool following = true;

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		/// <summary>The right stick's vertical position scrolls it (up: earlier stations); left alone, it follows the train.</summary>
		public void Update(float stickY, double seconds)
		{
			if (Math.Abs(stickY) > 0.2f)
			{
				following = false;
				target -= stickY * (float)seconds * 900.0f;
			}
		}

		/// <summary>Shows or hides it; shown again, it follows the train.</summary>
		public void Toggle()
		{
			Shown = !Shown;
			following = true;
		}

		/// <summary>
		/// − on the console: hidden, then each of Android's tabs in turn - Timetable, Map, Gradient - then hidden again (the
		/// card is open while driving, so the driving buttons stay the train's).
		/// </summary>
		public void Cycle()
		{
			if (!Shown)
			{
				Shown = true;
				CurrentTab = Tab.Timetable;
				following = true;
				return;
			}

			if (CurrentTab == Tab.Gradient)
			{
				Shown = false;
				CurrentTab = Tab.Timetable;
				return;
			}

			CurrentTab++;
			RouteShown();
		}

		public void Draw(IMenuCanvas canvas, TimetableSnapshot snapshot, double seconds)
		{
			if (!Shown)
			{
				return;
			}

			float x = MenuStyle.Width - MenuStyle.SafeX - Width, y = Top, height = Bottom - Top;
			canvas.Fill(x, y, Width, height, Card, 22);
			canvas.Outline(x, y, Width, height, CardBorder, 1.5f, 22);
			float left = x + Padding, inner = Width - 2 * Padding;

			// The heading: the tabs (Android's: Timetable, Map, Gradient; − goes on to the next) and the clock.
			DrawTabs(canvas, left, y + 14, inner, snapshot?.Clock);
			if (CurrentTab != Tab.Timetable)
			{
				DrawRoute(canvas, left, y + 44, inner, height - 44 - Padding, seconds);
				return;
			}

			if (snapshot == null)
			{
				return;
			}

			// The next stop.
			IReadOnlyList<TimetableRow> rows = snapshot.Rows;
			int currentIndex = -1;
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].State == TimetableRowState.Next || rows[i].State == TimetableRowState.AtStation)
				{
					currentIndex = i;
					break;
				}
			}

			TimetableRow next = currentIndex >= 0 ? rows[currentIndex] : null;
			float nextY = y + 44;
			canvas.Fill(left - 6, nextY, inner + 12, 76, 0x325CA0FF, 14);
			if (next == null)
			{
				canvas.Text(T("android", "timetable_end", "End of the line"), left + 8, nextY + 24, NextSize, White, true);
			}
			else
			{
				string name = (next.State == TimetableRowState.AtStation ? "● " : "→ ") + next.Name;
				canvas.Text(MenuStyle.Fit(canvas, name, NextSize, inner - 16, true), left + 8, nextY + 8, NextSize, White, true);
				(string detail, bool late) = NextDetail(next);
				canvas.Text(MenuStyle.Fit(canvas, detail, DetailSize, inner - 16), left + 8, nextY + 8 + NextSize + 8, DetailSize, late ? Late : Muted);
			}

			// The columns' headings.
			float listTop = nextY + 76 + 12;
			float timesX = left + inner - 2 * TimeWidth;
			canvas.Text(T("android", "timetable_arrival", "Arr"), timesX, listTop, 16, Muted);
			canvas.Text(T("android", "timetable_departure", "Dep"), timesX + TimeWidth, listTop, 16, Muted);
			listTop += 26;
			float listHeight = y + height - Padding - listTop;

			// Following the train: the current row a row below the top.
			float total = rows.Count * RowHeight;
			if (following && currentIndex >= 0)
			{
				target = (currentIndex - 1) * RowHeight;
			}

			target = Math.Clamp(target, 0, Math.Max(0, total - listHeight));
			scroll += (target - scroll) * (float)Math.Min(1.0, seconds * 10.0);

			canvas.Clip(x, listTop, Width, listHeight);
			for (int i = 0; i < rows.Count; i++)
			{
				float rowY = listTop + i * RowHeight - scroll;
				if (rowY + RowHeight < listTop || rowY > listTop + listHeight)
				{
					continue;
				}

				DrawRow(canvas, rows[i], i == 0, i == rows.Count - 1, left, rowY, inner, timesX);
			}

			canvas.Unclip();
		}

		private static (string Text, bool Late) NextDetail(TimetableRow row)
		{
			List<string> parts = new List<string>();
			if (row.State == TimetableRowState.AtStation)
			{
				parts.Add(row.Departure != null
					? T("android", "timetable_departs", "departs") + " " + row.Departure + Relative(row.ToDeparture, true)
					: T("android", "timetable_stopped", "stopped"));
			}
			else
			{
				parts.Add(Distance(row.Distance));
				if (row.Arrival != null)
				{
					parts.Add(T("android", "timetable_arrives", "arr") + " " + row.Arrival + Relative(row.ToArrival, false));
				}
			}

			bool late = row.State == TimetableRowState.AtStation ? row.ToDeparture < 0.0 : row.ToArrival < 0.0;
			return (string.Join("  ·  ", parts), late);
		}

		private static void DrawRow(IMenuCanvas canvas, TimetableRow row, bool first, bool last, float left, float y, float inner, float timesX)
		{
			bool current = row.State == TimetableRowState.Next || row.State == TimetableRowState.AtStation;
			bool done = row.State == TimetableRowState.Done;
			if (current)
			{
				canvas.Fill(left - 6, y, inner + 12, RowHeight, CurrentRow, 10);
			}

			// The timeline: the line above travelled once the train reaches this station, below once past it.
			float lineX = left + MarkerWidth / 2, middle = y + RowHeight / 2;
			bool reached = row.State != TimetableRowState.Ahead;
			if (!first)
			{
				canvas.Fill(lineX - 1.5f, y, 3, RowHeight / 2, reached ? Current : Line);
			}

			if (!last)
			{
				canvas.Fill(lineX - 1.5f, middle, 3, RowHeight / 2, done ? Current : Line);
			}

			float radius = row.Pass ? 4.5f : 7.5f;
			switch (row.State)
			{
				case TimetableRowState.AtStation:
					Dot(canvas, lineX, middle, radius * 2.0f, 0x4650C878);
					Dot(canvas, lineX, middle, radius * 1.3f, Here);
					break;
				case TimetableRowState.Next:
					Dot(canvas, lineX, middle, radius * 2.0f, 0x465CA0FF);
					canvas.Outline(lineX - radius * 1.2f, middle - radius * 1.2f, radius * 2.4f, radius * 2.4f, Current, 3, radius * 1.2f);
					break;
				case TimetableRowState.Done:
					Dot(canvas, lineX, middle, radius, Current);
					break;
				default:
					Dot(canvas, lineX, middle, radius, Card);
					canvas.Outline(lineX - radius, middle - radius, radius * 2, radius * 2, White, 2.5f, radius);
					break;
			}

			// The name and its detail.
			string detail;
			if (row.Pass)
			{
				detail = T("android", "timetable_pass", "pass");
			}
			else if (done)
			{
				detail = row.Terminal ? T("android", "timetable_terminal", "terminus") : string.Empty;
			}
			else if (row.State == TimetableRowState.AtStation)
			{
				detail = row.Departure != null ? T("android", "timetable_departs", "departs") + Relative(row.ToDeparture, true) : T("android", "timetable_stopped", "stopped");
			}
			else
			{
				detail = Distance(row.Distance) + (current && row.Arrival != null ? "  ·" + Relative(row.ToArrival, false) : string.Empty) +
				         (row.Terminal ? "  ·  " + T("android", "timetable_terminal", "terminus") : string.Empty);
			}

			bool late = row.State == TimetableRowState.AtStation ? row.ToDeparture < 0.0 : current && row.ToArrival < 0.0;
			uint text = done ? Muted : White;
			float nameX = left + MarkerWidth + 8, nameWidth = timesX - nameX - 8;
			if (detail.Length == 0)
			{
				canvas.Text(MenuStyle.Fit(canvas, row.Name, NameSize, nameWidth, current), nameX, middle - NameSize / 2 - 1, NameSize, current ? White : text, current);
			}
			else
			{
				canvas.Text(MenuStyle.Fit(canvas, row.Name, NameSize, nameWidth, current), nameX, y + 5, NameSize, current ? White : text, current);
				canvas.Text(MenuStyle.Fit(canvas, detail, DetailSize - 2, nameWidth), nameX, y + 7 + NameSize, DetailSize - 2, late ? Late : current ? 0xD2FFFFFF : Muted);
			}

			string arrival = row.Arrival ?? (row.Pass ? string.Empty : "—");
			canvas.Text(arrival, timesX, middle - TimeSize / 2 - 1, TimeSize, text);
			canvas.Text(row.Departure ?? string.Empty, timesX + TimeWidth, middle - TimeSize / 2 - 1, TimeSize, text);
		}

		private static void Dot(IMenuCanvas canvas, float x, float y, float radius, uint color) =>
			canvas.Fill(x - radius, y - radius, radius * 2, radius * 2, color, radius);

		/// <summary>" (in 2:15)", or " (late 0:45)" once the time has gone (as Android's).</summary>
		private static string Relative(double seconds, bool departing)
		{
			if (double.IsNaN(seconds))
			{
				return string.Empty;
			}

			// Past midnight the timetable's times wrap; take the nearer way round.
			seconds %= 86400.0;
			if (seconds > 43200.0)
			{
				seconds -= 86400.0;
			}
			else if (seconds < -43200.0)
			{
				seconds += 86400.0;
			}

			TimeSpan span = TimeSpan.FromSeconds(Math.Abs(Math.Round(seconds)));
			string text = span.TotalHours >= 1.0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
			if (seconds >= 0.0)
			{
				return " (" + string.Format(T("android", "timetable_in", "in {0}"), text) + ")";
			}

			return departing
				? " (" + string.Format(T("android", "timetable_overdue", "overdue {0}"), text) + ")"
				: " (" + string.Format(T("android", "timetable_late", "late {0}"), text) + ")";
		}

		private static string Distance(double metres) => Math.Abs(metres) < 1000.0 ? metres.ToString("0") + " m" : (metres / 1000.0).ToString("0.00") + " km";
	}
}
