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

namespace OpenBve.Switch
{
	/// <summary>What the menu's items can ask of the menu: a list to pick from, a short message.</summary>
	public interface IMenuHost
	{
		/// <summary>Shows a list over the menu; <paramref name="picked"/> is called with the choice (not if B closes it).</summary>
		void Pick(string title, string[] options, int selected, Action<int> picked);

		/// <summary>A message shown for a few seconds at the bottom of the screen.</summary>
		void Notify(string message);
	}

	/// <summary>A line of the menu: a heading, a note, an option, a button or an entry in a list.</summary>
	public abstract class MenuItem
	{
		/// <summary>Whether the D-pad stops on it.</summary>
		public virtual bool Focusable => true;

		/// <summary>Its height at this width.</summary>
		public abstract float Measure(IMenuCanvas canvas, float width);

		public abstract void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused);

		/// <summary>The frame's buttons while it has the focus (A, left and right; up and down move the focus);
		/// true if it used them.</summary>
		public virtual bool Handle(MenuInput input, IMenuHost host) => false;

		/// <summary>A tap on it, <paramref name="x"/> from its left edge.</summary>
		public virtual void Tap(float x, float width, IMenuHost host)
		{
		}

		/// <summary>The focused row's background and the focus ring, as Android's (Ui.EnableFocusRing).</summary>
		protected static void Row(IMenuCanvas canvas, float x, float y, float width, float height, bool focused)
		{
			if (focused)
			{
				canvas.Fill(x, y, width, height, MenuStyle.SurfaceRaised, MenuStyle.Radius);
				canvas.Outline(x, y, width, height, MenuStyle.FocusRing, 3, MenuStyle.Radius);
			}
		}

		/// <summary>The text's top for it to sit in the middle of a row.</summary>
		protected static float Middle(float y, float height, float size) => y + (height - size) / 2;
	}

	/// <summary>A section's heading, in the accent colour.</summary>
	public sealed class HeadingItem : MenuItem
	{
		private readonly string text;

		public HeadingItem(string text) => this.text = text;

		public override bool Focusable => false;

		public override float Measure(IMenuCanvas canvas, float width) => MenuStyle.HeadingSize + 28;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused) =>
			canvas.Text(MenuStyle.Fit(canvas, text, MenuStyle.HeadingSize, width, true), x + 4, y + 20, MenuStyle.HeadingSize, MenuStyle.Accent, true);
	}

	/// <summary>A note under an option, in the secondary colour, wrapped.</summary>
	public sealed class InfoItem : MenuItem
	{
		private readonly Func<string> text;
		private string wrappedText;
		private float wrappedWidth;
		private List<string> lines;

		public InfoItem(string text) : this(() => text)
		{
		}

		/// <summary>A note whose text can change (a status).</summary>
		public InfoItem(Func<string> text) => this.text = text;

		public override bool Focusable => false;

		private List<string> Lines(IMenuCanvas canvas, float width)
		{
			string now = text() ?? string.Empty;
			if (lines == null || now != wrappedText || width != wrappedWidth)
			{
				lines = MenuStyle.Wrap(canvas, now, MenuStyle.SmallSize, width - 8);
				wrappedText = now;
				wrappedWidth = width;
			}

			return lines;
		}

		public override float Measure(IMenuCanvas canvas, float width) => Lines(canvas, width).Count * (MenuStyle.SmallSize + 6) + 10;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			float top = y + 4;
			foreach (string line in Lines(canvas, width))
			{
				canvas.Text(line, x + 4, top, MenuStyle.SmallSize, MenuStyle.Secondary);
				top += MenuStyle.SmallSize + 6;
			}
		}
	}

	/// <summary>
	/// An option with a few values (Android's dropdown): left and right step through them, A opens the list. Its label
	/// on the left, the value on the right between arrows.
	/// </summary>
	public sealed class ChoiceItem : MenuItem
	{
		private readonly string label;
		private readonly string[] options;
		private readonly Action<int> changed;
		private readonly Func<bool> enabled;

		public int Selected { get; private set; }

		public ChoiceItem(string label, string[] options, int selected, Action<int> changed, Func<bool> enabled = null)
		{
			this.label = label;
			this.options = options;
			Selected = Math.Clamp(selected, 0, options.Length - 1);
			this.changed = changed;
			this.enabled = enabled;
		}

		private bool Enabled => enabled == null || enabled();

		// The value column: as wide as the widest value with its arrows, at most half the row.
		private float laidWidth = -1, valueWidth;
		private List<string> labelLines;

		private void Layout(IMenuCanvas canvas, float width)
		{
			if (width == laidWidth)
			{
				return;
			}

			laidWidth = width;
			float widest = 0;
			foreach (string option in options)
			{
				widest = Math.Max(widest, canvas.TextWidth(option, MenuStyle.BodySize));
			}

			// Wide pages (Options) line their values up in one column, as Android's rows; a narrow panel fits the values.
			valueWidth = width > 600 ? MenuStyle.ValueColumn(width) : Math.Min(width * 0.5f, widest + 80);
			// A label too long for its side takes two lines (Android's rows wrap), the second cut if it must be.
			List<string> lines = MenuStyle.Wrap(canvas, label, MenuStyle.BodySize, width - valueWidth - 32);
			if (lines.Count > 2)
			{
				lines[1] = MenuStyle.Fit(canvas, lines[1] + " " + string.Join(" ", lines.GetRange(2, lines.Count - 2)), MenuStyle.BodySize, width - valueWidth - 32);
				lines.RemoveRange(2, lines.Count - 2);
			}

			labelLines = lines;
		}

		public override float Measure(IMenuCanvas canvas, float width)
		{
			Layout(canvas, width);
			return labelLines.Count > 1 ? MenuStyle.RowHeight + MenuStyle.BodySize + 4 : MenuStyle.RowHeight;
		}

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			float height = Measure(canvas, width);
			Row(canvas, x, y, width, height, focused);
			uint color = Enabled ? MenuStyle.Primary : MenuStyle.Secondary;
			float lineHeight = MenuStyle.BodySize + 4;
			float top = Middle(y, height, labelLines.Count * lineHeight - 4);
			for (int i = 0; i < labelLines.Count; i++)
			{
				canvas.Text(labelLines[i], x + 16, top + i * lineHeight, MenuStyle.BodySize, color);
			}

			float valueX = x + width - valueWidth - 8;
			string value = MenuStyle.Fit(canvas, options[Selected], MenuStyle.BodySize, valueWidth - 64);
			float textWidth = canvas.TextWidth(value, MenuStyle.BodySize);
			float textY = Middle(y, height, MenuStyle.BodySize);
			canvas.Text(value, valueX + (valueWidth - textWidth) / 2, textY, MenuStyle.BodySize, color);
			if (focused && Enabled)
			{
				canvas.Text("‹", valueX + 8, textY, MenuStyle.BodySize, MenuStyle.Accent, true);
				canvas.Text("›", valueX + valueWidth - 20, textY, MenuStyle.BodySize, MenuStyle.Accent, true);
			}
		}

		private void Set(int index)
		{
			index = Math.Clamp(index, 0, options.Length - 1);
			if (index != Selected)
			{
				Selected = index;
				changed(index);
			}
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			if (!Enabled)
			{
				return false;
			}

			if (input.Is(MenuButtons.Left))
			{
				Set(Selected - 1);
				return true;
			}

			if (input.Is(MenuButtons.Right))
			{
				Set(Selected + 1);
				return true;
			}

			if (input.Is(MenuButtons.A))
			{
				host.Pick(label, options, Selected, Set);
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host)
		{
			if (Enabled)
			{
				host.Pick(label, options, Selected, Set);
			}
		}
	}

	/// <summary>A number in steps (Android's seek bar): left and right step, the bar drawn on the right; a tap on the
	/// bar sets the value there.</summary>
	public sealed class SliderItem : MenuItem
	{
		private readonly string label;
		private readonly int min, max, step;
		private readonly Func<int, string> format;
		private readonly Action<int> changed;
		private int value;

		public SliderItem(string label, int min, int max, int step, int value, Func<int, string> format, Action<int> changed)
		{
			this.label = label;
			this.min = min;
			this.max = max;
			this.step = step;
			this.value = Math.Clamp(value, min, max);
			this.format = format;
			this.changed = changed;
		}

		public override float Measure(IMenuCanvas canvas, float width) => MenuStyle.RowHeight;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			Row(canvas, x, y, width, MenuStyle.RowHeight, focused);
			float labelWidth = width - MenuStyle.ValueColumn(width) - 24;
			canvas.Text(MenuStyle.Fit(canvas, label, MenuStyle.BodySize, labelWidth), x + 16, Middle(y, MenuStyle.RowHeight, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Primary);
			float column = MenuStyle.ValueColumn(width);
			float barX = x + width - column + 8, barWidth = column - 130, barY = y + MenuStyle.RowHeight / 2 - 3;
			float fraction = max > min ? (value - min) / (float)(max - min) : 0;
			canvas.Fill(barX, barY, barWidth, 6, MenuStyle.Surface, 3);
			canvas.Fill(barX, barY, barWidth * fraction, 6, MenuStyle.Accent, 3);
			canvas.Fill(barX + barWidth * fraction - 9, barY - 6, 18, 18, focused ? MenuStyle.FocusRing : MenuStyle.Primary, 9);
			canvas.Text(format(value), barX + barWidth + 18, Middle(y, MenuStyle.RowHeight, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Primary);
		}

		private void Set(int newValue)
		{
			newValue = Math.Clamp(min + (int)Math.Round((newValue - min) / (double)step) * step, min, max);
			if (newValue != value)
			{
				value = newValue;
				changed(value);
			}
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			// ZL and ZR take bigger steps, for the long ranges (viewing distance: 100 to 2,000 m in 50s).
			int stride = (input.Held & (MenuButtons.ZL | MenuButtons.ZR)) != 0 ? step * 5 : step;
			if (input.Is(MenuButtons.Left))
			{
				Set(value - stride);
				return true;
			}

			if (input.Is(MenuButtons.Right))
			{
				Set(value + stride);
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host)
		{
			float column = MenuStyle.ValueColumn(width);
			float barX = width - column + 8, barWidth = column - 130;
			if (x >= barX - 20 && x <= barX + barWidth + 20)
			{
				Set(min + (int)Math.Round(Math.Clamp((x - barX) / barWidth, 0, 1) * (max - min)));
			}
		}
	}

	/// <summary>A button: A or a tap runs it. Prominent ones (Start) are filled with the accent colour.</summary>
	public sealed class ButtonItem : MenuItem
	{
		private readonly Func<string> label;
		private readonly Action action;
		private readonly Func<bool> enabled;
		private readonly bool prominent;

		public ButtonItem(string label, Action action, bool prominent = false, Func<bool> enabled = null) : this(() => label, action, prominent, enabled)
		{
		}

		public ButtonItem(Func<string> label, Action action, bool prominent = false, Func<bool> enabled = null)
		{
			this.label = label;
			this.action = action;
			this.prominent = prominent;
			this.enabled = enabled;
		}

		private bool Enabled => enabled == null || enabled();

		public override float Measure(IMenuCanvas canvas, float width) => MenuStyle.RowHeight + 8;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			float height = MenuStyle.RowHeight;
			uint fill = !Enabled ? MenuStyle.Surface : prominent ? MenuStyle.Accent : MenuStyle.SurfaceRaised;
			canvas.Fill(x, y + 4, width, height, fill, MenuStyle.Radius);
			if (focused)
			{
				canvas.Outline(x, y + 4, width, height, MenuStyle.FocusRing, 3, MenuStyle.Radius);
			}

			string text = MenuStyle.Fit(canvas, label(), MenuStyle.BodySize, width - 32, true);
			float textWidth = canvas.TextWidth(text, MenuStyle.BodySize, true);
			canvas.Text(text, x + (width - textWidth) / 2, Middle(y + 4, height, MenuStyle.BodySize), MenuStyle.BodySize,
				!Enabled ? MenuStyle.Secondary : prominent ? 0xFFFFFFFF : MenuStyle.Primary, true);
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			if (input.Is(MenuButtons.A) && Enabled)
			{
				action();
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host)
		{
			if (Enabled)
			{
				action();
			}
		}
	}

	/// <summary>An entry in a list of files or folders (the route and train browsers, the recent lists): a title and a
	/// second line; A or a tap opens it.</summary>
	public sealed class EntryItem : MenuItem
	{
		public readonly string Title, Detail, Mark;

		/// <summary>The file or folder it stands for (to find it again in the list), or null.</summary>
		public readonly string Path;

		private readonly Action open;

		/// <summary>Whether it is the one chosen (drawn with the selection colour).</summary>
		public bool Chosen;

		public EntryItem(string mark, string title, string detail, Action open, string path = null)
		{
			Mark = mark;
			Title = title;
			Detail = detail;
			this.open = open;
			Path = path;
		}

		public override float Measure(IMenuCanvas canvas, float width) => string.IsNullOrEmpty(Detail) ? MenuStyle.RowHeight : MenuStyle.RowHeight + 8;

		public override void Draw(IMenuCanvas canvas, float x, float y, float width, bool focused)
		{
			float height = Measure(canvas, width);
			if (Chosen)
			{
				canvas.Fill(x, y, width, height, MenuStyle.Selection, MenuStyle.Radius);
			}

			Row(canvas, x, y, width, height, focused);
			float textX = x + 16;
			if (!string.IsNullOrEmpty(Mark))
			{
				canvas.Text(Mark, textX, Middle(y, height, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Accent, true);
				textX += 30;
			}

			if (string.IsNullOrEmpty(Detail))
			{
				canvas.Text(MenuStyle.Fit(canvas, Title, MenuStyle.BodySize, x + width - textX - 12), textX, Middle(y, height, MenuStyle.BodySize), MenuStyle.BodySize, MenuStyle.Primary);
			}
			else
			{
				canvas.Text(MenuStyle.Fit(canvas, Title, MenuStyle.BodySize, x + width - textX - 12), textX, y + 10, MenuStyle.BodySize, MenuStyle.Primary);
				canvas.Text(MenuStyle.Fit(canvas, Detail, MenuStyle.SmallSize, x + width - textX - 12), textX, y + 14 + MenuStyle.BodySize, MenuStyle.SmallSize, MenuStyle.Secondary);
			}
		}

		public override bool Handle(MenuInput input, IMenuHost host)
		{
			if (input.Is(MenuButtons.A))
			{
				open();
				return true;
			}

			return false;
		}

		public override void Tap(float x, float width, IMenuHost host) => open();
	}
}
