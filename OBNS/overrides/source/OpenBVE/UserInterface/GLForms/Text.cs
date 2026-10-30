// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): text boxes (one line or several, editable,
// with a caret and a selection), the rich text box that shows route and train descriptions (its links can be clicked),
// and the numeric up-down.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	public enum CharacterCasing { Normal, Upper, Lower }

	public abstract class TextBoxBase : Control
	{
		/// <summary>The text copied or cut, for pasting within the menu.</summary>
		internal static string Clipboard = string.Empty;

		private int caret, anchor;
		private int scrollX, scrollY;
		private bool multiline, readOnly, wordWrap = true;
		private List<Line> lines;
		private int layoutWidth = -1;

		protected TextBoxBase()
		{
			Cursor = Cursors.IBeam;
		}

		protected override Size DefaultSize => new Size(100, 20);

		protected override Color DefaultBackColor => readOnly && !(this is RichTextBox) ? ControlColor : Color.White;

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public virtual bool Multiline
		{
			get { return multiline; }
			set
			{
				multiline = value;
				Relayout();
			}
		}

		public bool WordWrap
		{
			get { return wordWrap; }
			set
			{
				wordWrap = value;
				Relayout();
			}
		}

		public bool ReadOnly
		{
			get { return readOnly; }
			set
			{
				readOnly = value;
				Invalidate();
			}
		}

		public BorderStyle BorderStyle { get; set; } = BorderStyle.Fixed3D;

		public bool HideSelection { get; set; } = true;

		public bool AcceptsTab { get; set; }

		public int MaxLength { get; set; } = 32767;

		public bool Modified { get; set; }

		public bool ShortcutsEnabled { get; set; } = true;

		public int TextLength => Text.Length;

		public override string Text
		{
			get { return base.Text; }
			set
			{
				base.Text = value;
				caret = anchor = Math.Min(caret, base.Text.Length);
				Relayout();
			}
		}

		public string[] Lines
		{
			get { return Text.Replace("\r\n", "\n").Split('\n'); }
			set { Text = string.Join(Environment.NewLine, value ?? new string[0]); }
		}

		public int SelectionStart
		{
			get { return Math.Min(caret, anchor); }
			set
			{
				caret = anchor = Math.Max(0, Math.Min(Text.Length, value));
				Invalidate();
			}
		}

		public int SelectionLength
		{
			get { return Math.Abs(caret - anchor); }
			set
			{
				int start = SelectionStart;
				anchor = start;
				caret = Math.Max(start, Math.Min(Text.Length, start + value));
				Invalidate();
			}
		}

		public string SelectedText
		{
			get { return Text.Substring(SelectionStart, SelectionLength); }
			set { Insert(value ?? string.Empty); }
		}

		public void Select(int start, int length)
		{
			anchor = Math.Max(0, Math.Min(Text.Length, start));
			caret = Math.Max(0, Math.Min(Text.Length, start + length));
			ScrollToCaret();
			Invalidate();
		}

		public void SelectAll()
		{
			anchor = 0;
			caret = Text.Length;
			Invalidate();
		}

		public void DeselectAll()
		{
			anchor = caret;
			Invalidate();
		}

		public void Clear()
		{
			Text = string.Empty;
		}

		public void AppendText(string value)
		{
			base.Text = Text + value;
			caret = anchor = Text.Length;
			Relayout();
			ScrollToCaret();
		}

		public void Copy()
		{
			if (SelectionLength > 0)
			{
				Clipboard = SelectedText;
			}
		}

		public void Cut()
		{
			if (!readOnly && SelectionLength > 0)
			{
				Copy();
				Insert(string.Empty);
			}
		}

		public void Paste()
		{
			if (!readOnly)
			{
				Insert(Clipboard);
			}
		}

		public void Undo()
		{
		}

		public void ScrollToCaret()
		{
			EnsureLayout();
			Point p = CaretPoint(caret);
			Rectangle area = TextArea;
			if (multiline)
			{
				int lineHeight = LineHeight;
				if (p.Y < scrollY)
				{
					scrollY = p.Y;
				}
				else if (p.Y + lineHeight > scrollY + area.Height)
				{
					scrollY = p.Y + lineHeight - area.Height;
				}
			}
			else
			{
				if (p.X < scrollX)
				{
					scrollX = Math.Max(0, p.X - 20);
				}
				else if (p.X > scrollX + area.Width - 2)
				{
					scrollX = p.X - area.Width + 20;
				}
			}

			Invalidate();
		}

		/// <summary>Replaces the selection with a text, as typing or pasting does.</summary>
		private void Insert(string value)
		{
			string text = Text;
			int start = SelectionStart, length = SelectionLength;
			if (!multiline)
			{
				value = value.Replace("\r", string.Empty).Replace("\n", string.Empty);
			}

			if (text.Length - length + value.Length > MaxLength)
			{
				value = value.Substring(0, Math.Max(0, MaxLength - (text.Length - length)));
			}

			base.Text = text.Substring(0, start) + value + text.Substring(start + length);
			caret = anchor = start + value.Length;
			Modified = true;
			Relayout();
			ScrollToCaret();
		}

		// --- layout: the text in lines, wrapped to the box when it has several ---

		private struct Line
		{
			internal int Start, Length, Y;
		}

		internal virtual string DisplayText => Text;

		internal int LineHeight => (int)Math.Ceiling(MeasureText(string.Empty, Font).Height) + 1;

		private bool HasVerticalBar => multiline && VerticalScrollBars && ContentHeight > TextArea.Height;

		internal virtual bool VerticalScrollBars => false;

		private Rectangle TextArea
		{
			get
			{
				int inset = BorderStyle == BorderStyle.None ? 0 : 2;
				int bar = multiline && VerticalScrollBars ? 17 : 0;
				return new Rectangle(inset + 1, inset + (multiline ? 1 : 0), Math.Max(1, Width - 2 * inset - 2 - bar), Math.Max(1, Height - 2 * inset - (multiline ? 2 : 0)));
			}
		}

		private int ContentHeight
		{
			get
			{
				EnsureLayout();
				return lines.Count * LineHeight;
			}
		}

		private void Relayout()
		{
			lines = null;
			Invalidate();
		}

		protected override void OnResize(EventArgs e)
		{
			Relayout();
			base.OnResize(e);
		}

		protected override void OnFontChanged(EventArgs e)
		{
			Relayout();
			base.OnFontChanged(e);
		}

		private void EnsureLayout()
		{
			int width = TextArea.Width;
			if (lines != null && layoutWidth == width)
			{
				return;
			}

			layoutWidth = width;
			lines = new List<Line>();
			string text = DisplayText;
			int lineHeight = LineHeight;
			if (!multiline)
			{
				lines.Add(new Line { Start = 0, Length = text.Length, Y = 0 });
				return;
			}

			int y = 0;
			int start = 0;
			while (true)
			{
				int end = text.IndexOf('\n', start);
				int paragraphEnd = end < 0 ? text.Length : end;
				// A paragraph, wrapped at word boundaries.
				int lineStart = start;
				while (true)
				{
					int length = paragraphEnd - lineStart;
					if (wordWrap && length > 0 && Measure(text, lineStart, length) > width)
					{
						int fit = Fit(text, lineStart, paragraphEnd, width);
						lines.Add(new Line { Start = lineStart, Length = fit, Y = y });
						y += lineHeight;
						lineStart += fit;
						continue;
					}

					lines.Add(new Line { Start = lineStart, Length = length, Y = y });
					y += lineHeight;
					break;
				}

				if (end < 0)
				{
					break;
				}

				start = end + 1;
			}
		}

		private float Measure(string text, int start, int length)
		{
			return length <= 0 ? 0 : MeasureText(text.Substring(start, length).Replace("\r", string.Empty), Font).Width;
		}

		/// <summary>How many characters from start fit in the width, breaking after a space where there is one.</summary>
		private int Fit(string text, int start, int end, int width)
		{
			int low = 1, high = end - start;
			while (low < high)
			{
				int mid = (low + high + 1) / 2;
				if (Measure(text, start, mid) <= width)
				{
					low = mid;
				}
				else
				{
					high = mid - 1;
				}
			}

			int space = text.LastIndexOf(' ', start + low - 1, low);
			if (space > start && start + low < end)
			{
				return space - start + 1;
			}

			return Math.Max(1, low);
		}

		private int LineOf(int index)
		{
			EnsureLayout();
			for (int i = lines.Count - 1; i >= 0; i--)
			{
				if (index >= lines[i].Start)
				{
					return i;
				}
			}

			return 0;
		}

		/// <summary>Where a character position is, in the text's own coordinates (unscrolled).</summary>
		private Point CaretPoint(int index)
		{
			int line = LineOf(index);
			Line l = lines[line];
			int x = (int)Math.Round(Measure(DisplayText, l.Start, Math.Max(0, Math.Min(index, l.Start + l.Length) - l.Start)));
			return new Point(x, l.Y);
		}

		/// <summary>The character position at a point in the control.</summary>
		private int IndexAt(Point p)
		{
			EnsureLayout();
			Rectangle area = TextArea;
			int lineHeight = LineHeight;
			int yText = multiline ? p.Y - area.Y + scrollY : 0;
			int line = Math.Max(0, Math.Min(lines.Count - 1, yText / lineHeight));
			Line l = lines[line];
			int xText = p.X - area.X + scrollX - AlignOffset(l);
			string text = DisplayText;
			int best = l.Start;
			float bestDistance = float.MaxValue;
			int lineEnd = l.Start + l.Length;
			if (lineEnd > l.Start && lineEnd <= text.Length && text[lineEnd - 1] == '\r')
			{
				lineEnd--;
			}

			for (int i = l.Start; i <= lineEnd; i++)
			{
				float x = Measure(text, l.Start, i - l.Start);
				float distance = Math.Abs(x - xText);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best = i;
				}
				else
				{
					break;
				}
			}

			return best;
		}

		public HorizontalAlignment TextAlign { get; set; } = HorizontalAlignment.Left;

		private int AlignOffset(Line l)
		{
			if (TextAlign == HorizontalAlignment.Left || multiline)
			{
				return 0;
			}

			int width = (int)Measure(DisplayText, l.Start, l.Length);
			return TextAlign == HorizontalAlignment.Right ? Math.Max(0, TextArea.Width - width - 2) : Math.Max(0, (TextArea.Width - width) / 2);
		}

		// --- painting ---

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			EnsureLayout();
			Rectangle area = TextArea;
			string text = DisplayText;
			int lineHeight = LineHeight;
			int maxScroll = Math.Max(0, ContentHeight - area.Height);
			scrollY = Math.Max(0, Math.Min(scrollY, maxScroll));
			bool showSelection = (Focused || !HideSelection) && caret != anchor;
			int selStart = SelectionStart, selEnd = SelectionStart + SelectionLength;
			Region clip = g.Clip;
			g.SetClip(area);
			foreach (Line l in lines)
			{
				int y = multiline ? area.Y + l.Y - scrollY : area.Y + (area.Height - lineHeight) / 2;
				if (y + lineHeight < area.Top || y > area.Bottom)
				{
					continue;
				}

				int x0 = area.X - scrollX + AlignOffset(l);
				string lineText = text.Substring(l.Start, l.Length).Replace("\r", string.Empty).Replace("\n", string.Empty);
				if (showSelection && selEnd > l.Start && selStart < l.Start + l.Length + 1)
				{
					int a = Math.Max(selStart, l.Start), b = Math.Min(selEnd, l.Start + l.Length);
					float xa = Measure(text, l.Start, a - l.Start), xb = Measure(text, l.Start, b - l.Start);
					using (SolidBrush brush = new SolidBrush(Focused ? HighlightColor : Color.FromArgb(204, 204, 204)))
					{
						g.FillRectangle(brush, x0 + xa, y, Math.Max(2, xb - xa), lineHeight);
					}
				}

				PaintLine(g, lineText, l.Start, x0, y, lineHeight);
				if (showSelection && selEnd > l.Start && selStart < l.Start + l.Length)
				{
					// The selected part drawn again over the highlight, in white.
					int a = Math.Max(selStart, l.Start), b = Math.Min(selEnd, l.Start + l.Length);
					float xa = Measure(text, l.Start, a - l.Start);
					string part = text.Substring(a, b - a).Replace("\r", string.Empty).Replace("\n", string.Empty);
					if (Focused)
					{
						DrawText(g, part, Font, Color.White, new RectangleF(x0 + xa, y, 4000, lineHeight), ContentAlignment.MiddleLeft);
					}
				}
			}

			if (Focused && Enabled && caret == anchor || Focused && Enabled && !readOnly)
			{
				Point p = CaretPoint(caret);
				int y = multiline ? area.Y + p.Y - scrollY : area.Y + (area.Height - lineHeight) / 2;
				int x = area.X - scrollX + p.X + AlignOffset(lines[LineOf(caret)]);
				if (!readOnly || this is TextBox)
				{
					g.DrawLine(Pens.Black, x, y + 1, x, y + lineHeight - 1);
				}
			}

			g.Clip = clip;
			if (HasVerticalBar)
			{
				int content = ContentHeight;
				Rectangle track = new Rectangle(Width - 17 - (BorderStyle == BorderStyle.None ? 0 : 1), BorderStyle == BorderStyle.None ? 0 : 1, 17, Height - (BorderStyle == BorderStyle.None ? 0 : 2));
				g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)), track);
				int thumb = Math.Max(20, track.Height * area.Height / Math.Max(1, content));
				int ty = track.Y + (track.Height - thumb) * scrollY / Math.Max(1, content - area.Height);
				g.FillRectangle(new SolidBrush(Color.FromArgb(193, 193, 193)), track.X + 3, ty + 1, 11, thumb - 2);
			}

			if (BorderStyle != BorderStyle.None)
			{
				DrawBorder(g, ClientRectangle, Focused ? HighlightColor : IsMouseOver ? Color.FromArgb(23, 23, 23) : Color.FromArgb(122, 122, 122));
			}

			base.OnPaint(e);
		}

		/// <summary>Draws one line of text; a rich text box draws its links differently.</summary>
		internal virtual void PaintLine(Graphics g, string lineText, int start, int x, int y, int lineHeight)
		{
			DrawText(g, lineText, Font, TextColor, new RectangleF(x, y, 4000, lineHeight), ContentAlignment.MiddleLeft);
		}

		// --- input ---

		private bool selecting;

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button != MouseButtons.Left)
			{
				return;
			}

			if (HasVerticalBar && e.X >= Width - 18)
			{
				ScrollFromBar(e.Y);
				draggingBar = true;
				return;
			}

			int index = IndexAt(e.Location);
			caret = index;
			if ((Host.ModifierKeys & Keys.Shift) == 0)
			{
				anchor = index;
			}

			selecting = true;
			Invalidate();
		}

		private bool draggingBar;

		private void ScrollFromBar(int y)
		{
			int content = ContentHeight;
			scrollY = (int)((long)Math.Max(0, content - TextArea.Height) * Math.Max(0, Math.Min(Height, y)) / Math.Max(1, Height));
			Invalidate();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (draggingBar && (e.Button & MouseButtons.Left) != 0)
			{
				ScrollFromBar(e.Y);
				return;
			}

			if (selecting && (e.Button & MouseButtons.Left) != 0)
			{
				caret = IndexAt(e.Location);
				ScrollToCaret();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			selecting = false;
			draggingBar = false;
		}

		protected override void OnDoubleClick(EventArgs e)
		{
			base.OnDoubleClick(e);
			// The word under the caret is selected.
			string text = Text;
			int start = Math.Min(caret, text.Length), end = start;
			while (start > 0 && char.IsLetterOrDigit(text[start - 1])) start--;
			while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
			anchor = start;
			caret = end;
			Invalidate();
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			if (!multiline)
			{
				return false;
			}

			int max = Math.Max(0, ContentHeight - TextArea.Height);
			int old = scrollY;
			scrollY = Math.Max(0, Math.Min(max, scrollY - delta / 120 * 3 * LineHeight));
			Invalidate();
			return old != scrollY || max > 0;
		}

		internal override bool IsInputKey(Keys keyData)
		{
			switch (keyData & Keys.KeyCode)
			{
				case Keys.Left: case Keys.Right: case Keys.Home: case Keys.End: case Keys.Back: case Keys.Delete:
					return true;
				case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown:
					return multiline;
				case Keys.Return:
					return multiline && AcceptsReturnKey;
				case Keys.Tab:
					return AcceptsTab;
				default:
					return false;
			}
		}

		internal virtual bool AcceptsReturnKey => false;

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled)
			{
				return;
			}

			bool shift = e.Shift;
			int line = LineOf(caret);
			switch (e.KeyCode)
			{
				case Keys.Left:
					Move(SelectionLength > 0 && !shift ? SelectionStart : Math.Max(0, caret - 1), shift);
					break;
				case Keys.Right:
					Move(SelectionLength > 0 && !shift ? SelectionStart + SelectionLength : Math.Min(Text.Length, caret + 1), shift);
					break;
				case Keys.Home:
					Move(e.Control ? 0 : lines[line].Start, shift);
					break;
				case Keys.End:
					Move(e.Control ? Text.Length : Math.Min(Text.Length, lines[line].Start + lines[line].Length - (EndsWithBreak(lines[line]) ? 1 : 0)), shift);
					break;
				case Keys.Up:
				case Keys.Down:
				case Keys.PageUp:
				case Keys.PageDown:
					if (!multiline)
					{
						return;
					}

					int step = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : e.KeyCode == Keys.PageUp ? -Math.Max(1, TextArea.Height / LineHeight) : Math.Max(1, TextArea.Height / LineHeight);
					int target = Math.Max(0, Math.Min(lines.Count - 1, line + step));
					Point p = CaretPoint(caret);
					Rectangle area = TextArea;
					Move(IndexAt(new Point(area.X + p.X - scrollX, area.Y + lines[target].Y - scrollY + 1)), shift);
					break;
				case Keys.Back:
					if (!readOnly)
					{
						if (SelectionLength == 0 && caret > 0)
						{
							anchor = caret - 1;
							if (anchor > 0 && Text[anchor] == '\n' && Text[anchor - 1] == '\r')
							{
								anchor--;
							}
						}

						Insert(string.Empty);
					}

					break;
				case Keys.Delete:
					if (!readOnly)
					{
						if (SelectionLength == 0 && caret < Text.Length)
						{
							anchor = caret + (Text[caret] == '\r' && caret + 1 < Text.Length ? 2 : 1);
						}

						Insert(string.Empty);
					}

					break;
				case Keys.Return:
					if (multiline && AcceptsReturnKey && !readOnly)
					{
						Insert("\r\n");
						break;
					}

					return;
				case Keys.Tab:
					if (AcceptsTab && !readOnly)
					{
						Insert("\t");
						break;
					}

					return;
				case Keys.A:
					if (e.Control)
					{
						SelectAll();
						break;
					}

					return;
				case Keys.C:
					if (e.Control)
					{
						Copy();
						break;
					}

					return;
				case Keys.X:
					if (e.Control)
					{
						Cut();
						break;
					}

					return;
				case Keys.V:
					if (e.Control)
					{
						Paste();
						break;
					}

					return;
				default:
					return;
			}

			e.Handled = true;
			e.SuppressKeyPress = true;
		}

		private bool EndsWithBreak(Line l)
		{
			int end = l.Start + l.Length;
			return end > 0 && end <= Text.Length && end - 1 >= l.Start && (Text[end - 1] == '\r' || Text[end - 1] == '\n');
		}

		private void Move(int index, bool extend)
		{
			caret = Math.Max(0, Math.Min(Text.Length, index));
			if (!extend)
			{
				anchor = caret;
			}

			ScrollToCaret();
		}

		protected override void OnKeyPress(KeyPressEventArgs e)
		{
			base.OnKeyPress(e);
			if (e.Handled || readOnly || char.IsControl(e.KeyChar))
			{
				return;
			}

			char c = e.KeyChar;
			if (this is TextBox box)
			{
				if (box.CharacterCasing == CharacterCasing.Upper) c = char.ToUpperInvariant(c);
				else if (box.CharacterCasing == CharacterCasing.Lower) c = char.ToLowerInvariant(c);
			}

			Insert(c.ToString());
			e.Handled = true;
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}
	}

	public class TextBox : TextBoxBase
	{
		public bool AcceptsReturn { get; set; }

		public ScrollBars ScrollBars { get; set; }

		public char PasswordChar { get; set; }

		public bool UseSystemPasswordChar { get; set; }

		public CharacterCasing CharacterCasing { get; set; }

		public AutoCompleteMode AutoCompleteMode { get; set; }

		public AutoCompleteSource AutoCompleteSource { get; set; }

		internal override bool AcceptsReturnKey => AcceptsReturn;

		internal override bool VerticalScrollBars => ScrollBars == ScrollBars.Vertical || ScrollBars == ScrollBars.Both;

		internal override string DisplayText => PasswordChar != '\0' || UseSystemPasswordChar ? new string(UseSystemPasswordChar ? '\u25CF' : PasswordChar, Text.Length) : Text;

		public override Size GetPreferredSize(Size proposedSize)
		{
			return new Size(Width, LineHeight + 6);
		}
	}

	public enum AutoCompleteMode { None, Suggest, Append, SuggestAppend }

	public enum AutoCompleteSource { FileSystem = 1, HistoryList = 2, RecentlyUsedList = 4, AllUrl = 6, AllSystemSources = 7, FileSystemDirectories = 32, CustomSource = 64, None = 128, ListItems = 256 }

	/// <summary>A read-mostly box of several lines whose web addresses are links.</summary>
	public class RichTextBox : TextBoxBase
	{
		private static readonly Regex UrlPattern = new Regex(@"(https?://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private List<KeyValuePair<int, int>> links = new List<KeyValuePair<int, int>>();

		public RichTextBox()
		{
			base.Multiline = true;
			BorderStyle = BorderStyle.Fixed3D;
		}

		protected override Size DefaultSize => new Size(100, 96);

		public bool DetectUrls { get; set; } = true;

		/// <summary>The native host's way in: the user clicked a link.</summary>
		internal void NativeLink(string link) => LinkClicked?.Invoke(this, new LinkClickedEventArgs(link));

		public RichTextBoxScrollBars ScrollBars { get; set; } = RichTextBoxScrollBars.Both;

		public float ZoomFactor { get; set; } = 1.0f;

		public bool AutoWordSelection { get; set; }

		public int BulletIndent { get; set; }

		public Color SelectionColor { get; set; }

		public Font SelectionFont { get; set; }

		public HorizontalAlignment SelectionAlignment { get; set; }

		internal override bool VerticalScrollBars => ScrollBars != RichTextBoxScrollBars.None && ScrollBars != RichTextBoxScrollBars.Horizontal;

		internal override bool AcceptsReturnKey => true;

		/// <summary>The text in RTF: only plain text is kept here, so RTF given is reduced to its text.</summary>
		public string Rtf
		{
			get { return @"{\rtf1\ansi " + Text.Replace(@"\", @"\\").Replace("{", @"\{").Replace("}", @"\}").Replace("\r\n", @"\par ").Replace("\n", @"\par ") + "}"; }
			set { Text = StripRtf(value ?? string.Empty); }
		}

		private static string StripRtf(string rtf)
		{
			string text = Regex.Replace(rtf, @"\\par[d]?\s?", "\n");
			text = Regex.Replace(text, @"\\'([0-9a-fA-F]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
			text = Regex.Replace(text, @"\\[a-z]+-?\d*\s?", string.Empty);
			return text.Replace("{", string.Empty).Replace("}", string.Empty).Trim();
		}

		public event LinkClickedEventHandler LinkClicked;

		protected override void OnTextChanged(EventArgs e)
		{
			links = new List<KeyValuePair<int, int>>();
			if (DetectUrls)
			{
				foreach (Match match in UrlPattern.Matches(Text))
				{
					links.Add(new KeyValuePair<int, int>(match.Index, match.Length));
				}
			}

			base.OnTextChanged(e);
		}

		internal override void PaintLine(Graphics g, string lineText, int start, int x, int y, int lineHeight)
		{
			if (links.Count == 0)
			{
				base.PaintLine(g, lineText, start, x, y, lineHeight);
				return;
			}

			// The line in runs: plain text, and links in blue, underlined.
			int position = 0;
			float cursor = x;
			using (Font underlined = new Font(Font, Font.Style | FontStyle.Underline))
			{
				while (position < lineText.Length)
				{
					int linkStart = -1, linkLength = 0;
					foreach (KeyValuePair<int, int> link in links)
					{
						int a = link.Key - start, b = link.Key + link.Value - start;
						if (b > position && a < lineText.Length && (linkStart < 0 || Math.Max(a, position) < linkStart))
						{
							linkStart = Math.Max(a, position);
							linkLength = Math.Min(b, lineText.Length) - linkStart;
						}
					}

					int plainEnd = linkStart < 0 ? lineText.Length : linkStart;
					if (plainEnd > position)
					{
						string plain = lineText.Substring(position, plainEnd - position);
						DrawText(g, plain, Font, TextColor, new RectangleF(cursor, y, 4000, lineHeight), ContentAlignment.MiddleLeft);
						cursor += MeasureText(plain, Font).Width;
						position = plainEnd;
					}

					if (linkStart >= 0 && linkLength > 0)
					{
						string link = lineText.Substring(linkStart, linkLength);
						DrawText(g, link, underlined, Color.FromArgb(0, 102, 204), new RectangleF(cursor, y, 4000, lineHeight), ContentAlignment.MiddleLeft);
						cursor += MeasureText(link, Font).Width;
						position = linkStart + linkLength;
					}
				}
			}
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (SelectionLength > 0)
			{
				return;
			}

			int index = SelectionStart;
			foreach (KeyValuePair<int, int> link in links)
			{
				if (index >= link.Key && index < link.Key + link.Value)
				{
					LinkClicked?.Invoke(this, new LinkClickedEventArgs(Text.Substring(link.Key, link.Value)));
					return;
				}
			}
		}
	}

	public class NumericUpDown : Control, ISupportInitialize
	{
		private decimal value, minimum, maximum = 100, increment = 1;
		private string editing;
		private const int ButtonWidth = 17;

		protected override Size DefaultSize => new Size(120, 20);

		protected override Color DefaultBackColor => Color.White;

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public decimal Value
		{
			get { return value; }
			set
			{
				if (value < minimum || value > maximum)
				{
					throw new ArgumentOutOfRangeException(nameof(value), "Value of '" + value + "' is not valid for 'Value'. 'Value' should be between 'Minimum' and 'Maximum'.");
				}

				SetValue(value);
			}
		}

		private void SetValue(decimal v)
		{
			v = Math.Max(minimum, Math.Min(maximum, v));
			editing = null;
			if (v == value)
			{
				Invalidate();
				return;
			}

			value = v;
			base.Text = Format(v);
			Invalidate();
			OnValueChanged(EventArgs.Empty);
		}

		public decimal Minimum
		{
			get { return minimum; }
			set
			{
				minimum = value;
				if (maximum < value) maximum = value;
				if (this.value < value) SetValue(value);
			}
		}

		public decimal Maximum
		{
			get { return maximum; }
			set
			{
				maximum = value;
				if (minimum > value) minimum = value;
				if (this.value > value) SetValue(value);
			}
		}

		public decimal Increment
		{
			get { return increment; }
			set { increment = value; }
		}

		public int DecimalPlaces { get; set; }

		public bool Hexadecimal { get; set; }

		public bool ThousandsSeparator { get; set; }

		public bool ReadOnly { get; set; }

		public bool InterceptArrowKeys { get; set; } = true;

		public HorizontalAlignment TextAlign { get; set; } = HorizontalAlignment.Left;

		public LeftRightAlignment UpDownAlign { get; set; } = LeftRightAlignment.Right;

		public BorderStyle BorderStyle { get; set; } = BorderStyle.Fixed3D;

		public event EventHandler ValueChanged;

		protected virtual void OnValueChanged(EventArgs e) => ValueChanged?.Invoke(this, e);

		public override string Text
		{
			get { return editing ?? Format(value); }
			set
			{
				if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal parsed))
				{
					SetValue(parsed);
				}
			}
		}

		private string Format(decimal v)
		{
			if (Hexadecimal)
			{
				return ((long)v).ToString("X", CultureInfo.CurrentCulture);
			}

			return v.ToString((ThousandsSeparator ? "N" : "F") + DecimalPlaces, CultureInfo.CurrentCulture);
		}

		public void UpButton()
		{
			Commit();
			SetValue(value + increment);
		}

		public void DownButton()
		{
			Commit();
			SetValue(value - increment);
		}

		/// <summary>Takes what was typed, as Windows does when the box loses focus or Enter is pressed.</summary>
		private void Commit()
		{
			if (editing == null)
			{
				return;
			}

			string text = editing;
			editing = null;
			if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal parsed) || decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
			{
				SetValue(parsed);
			}

			Invalidate();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			if (!Enabled)
			{
				g.FillRectangle(new SolidBrush(ControlColor), ClientRectangle);
			}

			Rectangle text = new Rectangle(3, 0, Width - ButtonWidth - 5, Height);
			string shown = Text;
			ContentAlignment align = TextAlign == HorizontalAlignment.Right ? ContentAlignment.MiddleRight : TextAlign == HorizontalAlignment.Center ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
			DrawText(g, shown, Font, TextColor, text, align);
			if (Focused && Enabled)
			{
				float x = 3 + MeasureText(shown, Font).Width;
				if (align == ContentAlignment.MiddleLeft)
				{
					g.DrawLine(Pens.Black, x, 3, x, Height - 4);
				}
			}

			int bx = Width - ButtonWidth - 1;
			int half = (Height - 2) / 2;
			Rectangle up = new Rectangle(bx, 1, ButtonWidth, half), down = new Rectangle(bx, 1 + half, ButtonWidth, Height - 2 - half);
			bool hot = IsMouseOver && Enabled;
			g.FillRectangle(new SolidBrush(hot && hoverPart == 1 ? Color.FromArgb(229, 241, 251) : Color.FromArgb(240, 240, 240)), up);
			g.FillRectangle(new SolidBrush(hot && hoverPart == 2 ? Color.FromArgb(229, 241, 251) : Color.FromArgb(240, 240, 240)), down);
			g.DrawLine(Pens.LightGray, bx, 1 + half, bx + ButtonWidth, 1 + half);
			Brush arrow = Enabled ? Brushes.DimGray : Brushes.LightGray;
			float cx = bx + ButtonWidth / 2f;
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
			g.FillPolygon(arrow, new[] { new PointF(cx - 3.5f, up.Bottom - 3), new PointF(cx + 3.5f, up.Bottom - 3), new PointF(cx, up.Top + 3) });
			g.FillPolygon(arrow, new[] { new PointF(cx - 3.5f, down.Top + 3), new PointF(cx + 3.5f, down.Top + 3), new PointF(cx, down.Bottom - 3) });
			g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
			if (BorderStyle != BorderStyle.None)
			{
				DrawBorder(g, ClientRectangle, Focused ? HighlightColor : Color.FromArgb(122, 122, 122));
			}

			base.OnPaint(e);
		}

		private int hoverPart;

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			int part = e.X >= Width - ButtonWidth - 1 ? (e.Y < Height / 2 ? 1 : 2) : 0;
			if (part != hoverPart)
			{
				hoverPart = part;
				Invalidate();
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button != MouseButtons.Left || !Enabled)
			{
				return;
			}

			if (e.X >= Width - ButtonWidth - 1)
			{
				if (e.Y < Height / 2)
				{
					UpButton();
				}
				else
				{
					DownButton();
				}
			}
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			if (!Focused)
			{
				return false;
			}

			if (delta > 0)
			{
				UpButton();
			}
			else
			{
				DownButton();
			}

			return true;
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Up || key == Keys.Down || key == Keys.Back || key == Keys.Delete;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled)
			{
				return;
			}

			switch (e.KeyCode)
			{
				case Keys.Up:
					UpButton();
					break;
				case Keys.Down:
					DownButton();
					break;
				case Keys.Return:
					Commit();
					return;
				case Keys.Back:
					if (!ReadOnly)
					{
						string text = editing ?? Format(value);
						editing = text.Length > 0 ? text.Substring(0, text.Length - 1) : text;
						Invalidate();
					}

					break;
				case Keys.Delete:
					if (!ReadOnly)
					{
						editing = string.Empty;
						Invalidate();
					}

					break;
				default:
					return;
			}

			e.Handled = true;
		}

		protected override void OnKeyPress(KeyPressEventArgs e)
		{
			base.OnKeyPress(e);
			if (e.Handled || ReadOnly)
			{
				return;
			}

			char c = e.KeyChar;
			string separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
			if (char.IsDigit(c) || c == '-' || separator.IndexOf(c) >= 0 || Hexadecimal && Uri.IsHexDigit(c))
			{
				editing = (editing ?? string.Empty) + c;
				Invalidate();
				e.Handled = true;
			}
		}

		protected override void OnLostFocus(EventArgs e)
		{
			Commit();
			base.OnLostFocus(e);
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}
	}

	public enum LeftRightAlignment { Left, Right }
}
