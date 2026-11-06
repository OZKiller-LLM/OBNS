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
using OpenBveApi.Colors;

namespace OpenBve.Switch
{
	/// <summary>
	/// The driving information as Android's DriverInfoBar shows it (DriverHud.cs), drawn over the scene by deko3d: a card at
	/// the bottom of the screen. Its top line: the speed (red above the limit), the limit as a round sign (red for a signal
	/// at danger), the handles coloured by what they do (neutral, power, brake, emergency), the reverser and the clock.
	/// Below: the next stop, how far, and its timetabled times. Above both, only while it happens, what the safety system
	/// is doing to the handles; above the card, the in-game messages. Android's sizes scaled for 720p, none under 18 px.
	/// </summary>
	public static class DriverCard
	{
		// Android's colours (DriverHud.cs).
		private const uint Text = 0xFFF0F3F8, Dim = 0xFFA0AAB9, Over = 0xFFFF5F5A;
		private const uint CardFill = 0xCD0E121B, CardBorder = 0x46FFFFFF;
		private const uint WarningFill = 0xFFFFBE3C, WarningText = 0xFF1E1600;
		private const uint SignRed = 0xFFD22323;
		private const uint PowerChip = 0xFF1E69D7, BrakeChip = 0xFFD77D19, EmergencyChip = 0xFFCD2323, NeutralChip = 0xFF485060;
		private const uint ForwardChip = 0xFF289655, ReverseChip = 0xFF9646AF;

		// Sizes: Android's sp at about 1.5 px each at 720p.
		private const float SpeedSize = 36, UnitSize = 18, ChipSize = 18, ClockSize = 22, NextSize = 19, WarningSize = 18;
		private const float Padding = 18, Radius = 20, Gap = 14, SignSize = 44, ChipPadding = 12, ChipHeight = 30;

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		private static float ChipWidth(IMenuCanvas canvas, string text) => canvas.TextWidth(text, ChipSize, true) + 2 * ChipPadding;

		/// <summary>Draws the card for the status, centred at the bottom of the 1280x720 screen.</summary>
		public static void Draw(IMenuCanvas canvas, DriverStatus status)
		{
			if (status == null)
			{
				return;
			}

			bool over = status.Limit > 0.0 && status.Speed > status.Limit.Value + 1.0;
			string speed = status.Speed.ToString("0");
			string unit = " km/h";
			string handles = status.Handles ?? string.Empty;
			string reverser = status.Reverser ?? string.Empty;
			string clock = status.Clock ?? string.Empty;

			// The top line's width, then the next-stop line's: the card as wide as the wider, within the safe area.
			float topWidth = canvas.TextWidth(speed, SpeedSize, true) + canvas.TextWidth(unit, UnitSize) + Gap +
			                 (status.Limit != null ? SignSize + Gap : 0) + ChipWidth(canvas, handles) + 8 + ChipWidth(canvas, reverser) + 2 * Gap +
			                 canvas.TextWidth(clock, ClockSize, true);
			List<(string Text, uint Color, bool Bold)> next = NextLine(status);
			float nextWidth = 0;
			foreach ((string text, uint _, bool bold) in next)
			{
				nextWidth += canvas.TextWidth(text, NextSize, bold);
			}

			float most = MenuStyle.Width - 2 * MenuStyle.SafeX;
			float width = Math.Min(most, Math.Max(480, Math.Max(topWidth, nextWidth) + 2 * Padding));
			float warningHeight = status.Warning != null ? WarningSize + 10 + 8 : 0;
			float height = Padding + warningHeight + SignSize + 6 + NextSize + Padding - 4;
			float x = (MenuStyle.Width - width) / 2, y = MenuStyle.Height - MenuStyle.SafeY - height;

			DrawMessages(canvas, status.Messages, x, y - 10);

			canvas.Fill(x, y, width, height, CardFill, Radius);
			canvas.Outline(x, y, width, height, CardBorder, 1.5f, Radius);
			float lineY = y + Padding - 4;
			float left = x + Padding, right = x + width - Padding;

			if (status.Warning != null)
			{
				string warning = MenuStyle.Fit(canvas, "⚠  " + status.Warning, WarningSize, width - 2 * Padding - 16, true);
				canvas.Fill(left, lineY, width - 2 * Padding, WarningSize + 10, WarningFill, 9);
				canvas.Text(warning, left + 8, lineY + 5, WarningSize, WarningText, true);
				lineY += warningHeight;
			}

			// The top line, its parts centred on the sign's height.
			float middle = lineY + SignSize / 2;
			float pen = left;
			canvas.Text(speed, pen, middle - SpeedSize / 2 - 2, SpeedSize, over ? Over : Text, true);
			pen += canvas.TextWidth(speed, SpeedSize, true);
			canvas.Text(unit, pen, middle - UnitSize / 2 + 4, UnitSize, Dim);
			pen += canvas.TextWidth(unit, UnitSize) + Gap;

			if (status.Limit != null)
			{
				bool danger = status.Limit <= 0.0;
				canvas.Fill(pen, middle - SignSize / 2, SignSize, SignSize, danger ? SignRed : 0xFFFFFFFF, SignSize / 2);
				canvas.Outline(pen, middle - SignSize / 2, SignSize, SignSize, SignRed, 4.5f, SignSize / 2);
				string limit = status.Limit.Value.ToString("0");
				float limitSize = status.Limit >= 100.0 ? 16 : 18;
				canvas.Text(limit, pen + (SignSize - canvas.TextWidth(limit, limitSize, true)) / 2, middle - limitSize / 2 - 1, limitSize, danger ? 0xFFFFFFFF : 0xFF000000, true);
				pen += SignSize + Gap;
			}

			uint handleColor = status.HandleState switch
			{
				HandleState.Power => PowerChip,
				HandleState.Brake => BrakeChip,
				HandleState.Emergency => EmergencyChip,
				_ => NeutralChip
			};
			pen += Chip(canvas, handles, pen, middle, handleColor) + 8;
			Chip(canvas, reverser, pen, middle, reverser == "F" ? ForwardChip : reverser == "R" ? ReverseChip : NeutralChip);
			if (topWidth + 2 * Padding <= width + 0.5f)
			{
				canvas.Text(clock, right - canvas.TextWidth(clock, ClockSize, true), middle - ClockSize / 2 - 1, ClockSize, Text, true);
			}

			// The next stop, cut short if the card can't hold it.
			float lineX = left, nextY = lineY + SignSize + 6;
			canvas.Clip(left, nextY - 2, width - 2 * Padding, NextSize + 8);
			foreach ((string text, uint color, bool bold) in next)
			{
				canvas.Text(text, lineX, nextY, NextSize, color, bold);
				lineX += canvas.TextWidth(text, NextSize, bold);
			}

			canvas.Unclip();
		}

		private static float Chip(IMenuCanvas canvas, string text, float x, float middle, uint color)
		{
			float width = ChipWidth(canvas, text);
			canvas.Fill(x, middle - ChipHeight / 2, width, ChipHeight, color, 10);
			canvas.Text(text, x + ChipPadding, middle - ChipSize / 2 - 1, ChipSize, 0xFFFFFFFF, true);
			return width;
		}

		/// <summary>"▸ Lo Wu · 350 m · Arr 10:04:00 · Dep 10:05:00", the station and distance brightest (as Android's).</summary>
		private static List<(string Text, uint Color, bool Bold)> NextLine(DriverStatus status)
		{
			List<(string, uint, bool)> parts = new List<(string, uint, bool)>();
			if (status.NextName == null)
			{
				parts.Add((T("android", "timetable_end", "End of the line"), Dim, false));
				return parts;
			}

			parts.Add(("▸ ", Dim, false));
			parts.Add((status.NextName, Text, true));
			parts.Add(("  ·  ", Dim, false));
			parts.Add((status.NextDistance ?? string.Empty, Text, false));
			if (status.NextArrival != null)
			{
				parts.Add(("  ·  " + T("android", "timetable_arrival", "Arr") + " " + status.NextArrival, Dim, false));
			}

			if (status.NextDeparture != null)
			{
				parts.Add(("  ·  " + T("android", "timetable_departure", "Dep") + " " + status.NextDeparture, Dim, false));
			}

			return parts;
		}

		/// <summary>The in-game messages (at most three), above the card, each on a dark strip in its colour.</summary>
		private static void DrawMessages(IMenuCanvas canvas, IReadOnlyList<(string Text, MessageColor Color)> messages, float x, float bottom)
		{
			if (messages == null || messages.Count == 0)
			{
				return;
			}

			const float size = 20, rowHeight = 34;
			float y = bottom - messages.Count * (rowHeight + 6);
			foreach ((string text, MessageColor color) in messages)
			{
				string shown = MenuStyle.Fit(canvas, text, size, MenuStyle.Width - 2 * MenuStyle.SafeX - 32);
				float width = canvas.TextWidth(shown, size) + 28;
				float left = (MenuStyle.Width - width) / 2;
				canvas.Fill(left, y, width, rowHeight, 0xB40E121B, 10);
				canvas.Text(shown, left + 14, y + (rowHeight - size) / 2 - 1, size, MessageArgb(color));
				y += rowHeight + 6;
			}
		}

		/// <summary>Upstream's message colours, brightened for a dark strip.</summary>
		private static uint MessageArgb(MessageColor color)
		{
			switch (color)
			{
				case MessageColor.Red: return 0xFFFF6E6E;
				case MessageColor.Orange: return 0xFFFFB45A;
				case MessageColor.Green: return 0xFF78E68C;
				case MessageColor.Blue: return 0xFF82B4FF;
				case MessageColor.Magenta: return 0xFFF08CF0;
				case MessageColor.Gray: return 0xFFB4B9C3;
				case MessageColor.White: return 0xFFFFFFFF;
				default: return 0xFFF0F3F8;
			}
		}
	}
}
