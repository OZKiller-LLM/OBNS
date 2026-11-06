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

namespace OpenBve.Switch
{
	/// <summary>
	/// What the Switch's menu draws with: rectangles, text and images in the menu's own coordinates, 1280x720 (scaled
	/// by the renderer to the screen: 1080p docked). The menu knows nothing of the GPU; deko3d's 2D drawing implements
	/// this (step 3), and a test can implement it to check the layout.
	/// </summary>
	public interface IMenuCanvas
	{
		/// <summary>A filled rectangle, its corners rounded by <paramref name="radius"/>. Colours are 0xAARRGGBB.</summary>
		void Fill(float x, float y, float width, float height, uint color, float radius = 0);

		/// <summary>A rectangle's outline, <paramref name="thickness"/> wide, inside its bounds.</summary>
		void Outline(float x, float y, float width, float height, uint color, float thickness, float radius = 0);

		/// <summary>One line of text, its top at <paramref name="y"/>, <paramref name="size"/> pixels high.</summary>
		void Text(string text, float x, float y, float size, uint color, bool bold = false);

		/// <summary>How wide a line of text is drawn.</summary>
		float TextWidth(string text, float size, bool bold = false);

		/// <summary>An image file fitted inside the rectangle, its proportions kept; false while it isn't ready (loading,
		/// or unreadable), when the caller draws a placeholder.</summary>
		bool Image(string file, float x, float y, float width, float height);

		/// <summary>Draws only inside the rectangle until <see cref="Unclip"/>.</summary>
		void Clip(float x, float y, float width, float height);

		void Unclip();
	}

	/// <summary>The menu's look: Android's menu (Ui.cs) laid out for the console (OBNS/SWITCH.md, step 6).</summary>
	public static class MenuStyle
	{
		// Android's palette.
		public const uint Background = 0xFF12151C;
		public const uint Surface = 0xFF1C202A;
		public const uint SurfaceRaised = 0xFF262C3A;
		public const uint Primary = 0xFFE8ECF4;
		public const uint Secondary = 0xFF96A0B2;
		public const uint Accent = 0xFF5CA0FF;
		public const uint Selection = 0x5A5CA0FF;
		public const uint FocusRing = 0xFF8CC8FF;
		public const uint Danger = 0xFFE65A50;

		/// <summary>The menu's own size; the renderer scales it to the screen.</summary>
		public const float Width = 1280, Height = 720;

		/// <summary>About 5% kept clear of each edge, for televisions that cut the picture's edges.</summary>
		public const float SafeX = 64, SafeY = 36;

		/// <summary>Text: readable on the Lite's 5.5" screen at 720p (18 px at the least).</summary>
		public const float TitleSize = 30, HeadingSize = 24, BodySize = 20, SmallSize = 18;

		/// <summary>A row: a touch target of 60 to 80 px.</summary>
		public const float RowHeight = 64;

		public const float Gap = 12, Radius = 10;

		/// <summary>The width of a row's value (a choice's value, a slider's bar) on the right: the same on every row of a
		/// page, so they line up.</summary>
		public static float ValueColumn(float rowWidth) => rowWidth > 600 ? rowWidth * 0.4f : rowWidth * 0.5f;

		/// <summary>Breaks text into lines no wider than <paramref name="width"/>, at spaces where it can (anywhere in
		/// CJK text, which has none), keeping the text's own line breaks.</summary>
		public static List<string> Wrap(IMenuCanvas canvas, string text, float size, float width)
		{
			List<string> lines = new List<string>();
			foreach (string paragraph in (text ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
			{
				string rest = paragraph;
				while (rest.Length != 0)
				{
					if (canvas.TextWidth(rest, size) <= width)
					{
						lines.Add(rest);
						break;
					}

					// The longest start that fits, by halving.
					int low = 1, high = rest.Length;
					while (low < high)
					{
						int middle = (low + high + 1) / 2;
						if (canvas.TextWidth(rest.Substring(0, middle), size) <= width)
						{
							low = middle;
						}
						else
						{
							high = middle - 1;
						}
					}

					int cut = low;
					int space = rest.LastIndexOf(' ', cut - 1, cut);
					if (space > 0 && cut < rest.Length)
					{
						cut = space;
					}

					lines.Add(rest.Substring(0, cut).TrimEnd());
					rest = rest.Substring(cut).TrimStart();
				}

				if (paragraph.Length == 0)
				{
					lines.Add(string.Empty);
				}
			}

			return lines;
		}

		/// <summary>Text cut to fit <paramref name="width"/>, ending in an ellipsis if it was cut.</summary>
		public static string Fit(IMenuCanvas canvas, string text, float size, float width, bool bold = false)
		{
			if (string.IsNullOrEmpty(text) || canvas.TextWidth(text, size, bold) <= width)
			{
				return text ?? string.Empty;
			}

			int length = text.Length;
			while (length > 1 && canvas.TextWidth(text.Substring(0, length) + "…", size, bold) > width)
			{
				length--;
			}

			return text.Substring(0, length) + "…";
		}
	}
}
