// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): labels, links, buttons, check and radio boxes,
// group boxes, panels, split containers, picture boxes and track bars, drawn after Windows' own look.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	public class Label : Control
	{
		public Label()
		{
			TabStop = false;
		}

		protected override Size DefaultSize => new Size(100, 23);

		public ContentAlignment TextAlign { get; set; } = ContentAlignment.TopLeft;

		public bool AutoEllipsis { get; set; }

		public bool UseMnemonic { get; set; } = true;

		public bool UseCompatibleTextRendering { get; set; }

		public FlatStyle FlatStyle { get; set; } = FlatStyle.Standard;

		public Image Image { get; set; }

		public ContentAlignment ImageAlign { get; set; } = ContentAlignment.MiddleCenter;

		private BorderStyle borderStyle;

		public BorderStyle BorderStyle
		{
			get { return borderStyle; }
			set
			{
				borderStyle = value;
				Invalidate();
			}
		}

		public int PreferredWidth => GetPreferredSize(Size.Empty).Width;

		public int PreferredHeight => GetPreferredSize(Size.Empty).Height;

		internal override bool DrawsItself => true;

		public override Size GetPreferredSize(Size proposedSize)
		{
			SizeF size = MeasureText(Text, Font);
			int border = borderStyle == BorderStyle.None ? 0 : 2;
			return new Size((int)Math.Ceiling(size.Width) + 3 + 2 * border, (int)Math.Ceiling(size.Height) + 2 * border);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			Rectangle area = ClientRectangle;
			if (Image != null)
			{
				g.DrawImage(Image, (area.Width - Image.Width) / 2, (area.Height - Image.Height) / 2, Image.Width, Image.Height);
			}

			string text = UseMnemonic ? Text.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&") : Text;
			int inset = borderStyle == BorderStyle.None ? 0 : 2;
			DrawText(g, text, Font, TextColor, new RectangleF(inset + 1, inset, area.Width - 2 * inset - 1, area.Height - 2 * inset), TextAlign, !AutoSize, AutoEllipsis);
			if (borderStyle != BorderStyle.None)
			{
				DrawBorder(g, area, borderStyle == BorderStyle.Fixed3D ? Color.FromArgb(160, 160, 160) : Color.FromArgb(100, 100, 100));
			}

			base.OnPaint(e);
		}
	}

	public class LinkLabel : Label
	{
		public class Link
		{
			public Link()
			{
			}

			public Link(int start, int length)
			{
				Start = start;
				Length = length;
			}

			public Link(int start, int length, object linkData) : this(start, length)
			{
				LinkData = linkData;
			}

			public int Start { get; set; }

			public int Length { get; set; } = -1;

			public object LinkData { get; set; }

			public bool Visited { get; set; }

			public bool Enabled { get; set; } = true;

			public string Name { get; set; } = string.Empty;

			public object Tag { get; set; }

			public string Description { get; set; }
		}

		public class LinkCollection : IEnumerable<Link>
		{
			private readonly List<Link> links = new List<Link>();

			public int Count => links.Count;

			public Link this[int index] => links[index];

			public Link Add(int start, int length)
			{
				Link link = new Link(start, length);
				links.Add(link);
				return link;
			}

			public Link Add(int start, int length, object linkData)
			{
				Link link = new Link(start, length, linkData);
				links.Add(link);
				return link;
			}

			public int Add(Link value)
			{
				links.Add(value);
				return links.Count - 1;
			}

			public void Clear() => links.Clear();

			public void Remove(Link value) => links.Remove(value);

			public IEnumerator<Link> GetEnumerator() => links.GetEnumerator();

			IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
		}

		public LinkLabel()
		{
			Links.Add(0, -1);
			TabStop = true;
			Cursor = Cursors.Hand;
		}

		public LinkCollection Links { get; } = new LinkCollection();

		public Color LinkColor { get; set; } = Color.FromArgb(0, 0, 255);

		public Color ActiveLinkColor { get; set; } = Color.Red;

		public Color VisitedLinkColor { get; set; } = Color.FromArgb(128, 0, 128);

		public Color DisabledLinkColor { get; set; } = Color.FromArgb(133, 133, 133);

		public LinkBehavior LinkBehavior { get; set; }

		public bool LinkVisited { get; set; }

		public LinkArea LinkArea { get; set; }

		public event LinkLabelLinkClickedEventHandler LinkClicked;

		internal override bool CanTakeFocus => true;

		protected override void OnPaint(PaintEventArgs e)
		{
			bool underline = LinkBehavior != LinkBehavior.NeverUnderline && (LinkBehavior != LinkBehavior.HoverUnderline || IsMouseOver);
			Color color = !Enabled ? DisabledLinkColor : IsPressed ? ActiveLinkColor : LinkVisited ? VisitedLinkColor : LinkColor;
			using (Font font = new Font(Font, underline ? Font.Style | FontStyle.Underline : Font.Style))
			{
				DrawText(e.Graphics, Text, font, color, new RectangleF(1, 0, Width - 1, Height), TextAlign, !AutoSize, AutoEllipsis);
			}

			if (Focused)
			{
				using (Pen pen = new Pen(Color.FromArgb(120, 120, 120)) { DashStyle = DashStyle.Dot })
				{
					SizeF size = MeasureText(Text, Font);
					e.Graphics.DrawRectangle(pen, 0, 0, Math.Min(Width - 1, (int)size.Width + 2), Math.Min(Height - 1, (int)size.Height));
				}
			}
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (Enabled && Links.Count > 0)
			{
				LinkClicked?.Invoke(this, new LinkLabelLinkClickedEventArgs(Links[0]));
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled && (e.KeyCode == Keys.Return || e.KeyCode == Keys.Space))
			{
				OnClick(EventArgs.Empty);
				e.Handled = true;
			}
		}
	}

	public struct LinkArea
	{
		public LinkArea(int start, int length)
		{
			Start = start;
			Length = length;
		}

		public int Start { get; set; }

		public int Length { get; set; }
	}

	/// <summary>Buttons, check boxes and radio buttons.</summary>
	public abstract class ButtonBase : Control
	{
		public ContentAlignment TextAlign { get; set; } = ContentAlignment.MiddleCenter;

		public bool AutoEllipsis { get; set; }

		public bool UseVisualStyleBackColor { get; set; } = true;

		public bool UseCompatibleTextRendering { get; set; }

		public bool UseMnemonic { get; set; } = true;

		public FlatStyle FlatStyle { get; set; } = FlatStyle.Standard;

		public Image Image { get; set; }

		public ContentAlignment ImageAlign { get; set; } = ContentAlignment.MiddleCenter;

		public TextImageRelation TextImageRelation { get; set; }

		public FlatButtonAppearance FlatAppearance { get; } = new FlatButtonAppearance();

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		internal override bool StandardDoubleClick => false;

		public virtual void PerformClick()
		{
			if (Enabled && Visible)
			{
				OnClick(EventArgs.Empty);
			}
		}

		internal string DisplayText => UseMnemonic ? Text.Replace("&&", "\u0001").Replace("&", string.Empty).Replace("\u0001", "&") : Text;

		/// <summary>Draws a push button's face, as Windows 10's.</summary>
		internal void DrawButtonFace(Graphics g, Rectangle r, bool pressed, bool hot)
		{
			Color fill, border;
			if (!Enabled)
			{
				fill = Color.FromArgb(204, 204, 204);
				border = Color.FromArgb(191, 191, 191);
			}
			else if (pressed)
			{
				fill = Color.FromArgb(204, 228, 247);
				border = Color.FromArgb(0, 84, 153);
			}
			else if (hot)
			{
				fill = Color.FromArgb(229, 241, 251);
				border = HighlightColor;
			}
			else
			{
				fill = UseVisualStyleBackColor || OwnBackColor.IsEmpty ? Color.FromArgb(225, 225, 225) : BackColor;
				border = Focused ? HighlightColor : BorderColor;
			}

			using (SolidBrush brush = new SolidBrush(fill))
			{
				g.FillRectangle(brush, r);
			}

			DrawBorder(g, r, border);
			if (Focused && Enabled && !pressed)
			{
				DrawBorder(g, Rectangle.Inflate(r, -1, -1), HighlightColor);
			}
		}

		internal void DrawImageAndText(Graphics g, Rectangle r)
		{
			Rectangle textArea = r;
			if (Image != null)
			{
				int iw = Image.Width, ih = Image.Height;
				if (string.IsNullOrEmpty(Text))
				{
					g.DrawImage(Image, r.X + (r.Width - iw) / 2, r.Y + (r.Height - ih) / 2, iw, ih);
				}
				else
				{
					g.DrawImage(Image, r.X + 4, r.Y + (r.Height - ih) / 2, iw, ih);
					textArea = new Rectangle(r.X + iw + 6, r.Y, r.Width - iw - 6, r.Height);
				}
			}

			DrawText(g, DisplayText, Font, TextColor, new RectangleF(textArea.X + 2, textArea.Y, textArea.Width - 4, textArea.Height), TextAlign, true, AutoEllipsis);
		}
	}

	public enum TextImageRelation { Overlay = 0, ImageAboveText = 1, TextAboveImage = 2, ImageBeforeText = 4, TextBeforeImage = 8 }

	public class FlatButtonAppearance
	{
		public int BorderSize { get; set; } = 1;

		public Color BorderColor { get; set; }

		public Color MouseOverBackColor { get; set; }

		public Color MouseDownBackColor { get; set; }
	}

	public class Button : ButtonBase, IButtonControl
	{
		protected override Size DefaultSize => new Size(75, 23);

		public DialogResult DialogResult { get; set; }

		public void NotifyDefault(bool value)
		{
		}

		public override Size GetPreferredSize(Size proposedSize)
		{
			SizeF size = MeasureText(DisplayText, Font);
			return new Size(Math.Max(75, (int)Math.Ceiling(size.Width) + 16), Math.Max(23, (int)Math.Ceiling(size.Height) + 10));
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			DrawButtonFace(e.Graphics, ClientRectangle, IsPressed || keyPressed, IsMouseOver);
			DrawImageAndText(e.Graphics, ClientRectangle);
			base.OnPaint(e);
		}

		private bool keyPressed;

		protected override void OnClick(EventArgs e)
		{
			if (DialogResult != DialogResult.None)
			{
				Form form = FindForm();
				if (form != null)
				{
					form.DialogResult = DialogResult;
				}
			}

			base.OnClick(e);
		}

		public override void PerformClick()
		{
			keyPressed = false;
			base.PerformClick();
		}
	}

	public class CheckBox : ButtonBase
	{
		public CheckBox()
		{
			TextAlign = ContentAlignment.MiddleLeft;
		}

		protected override Size DefaultSize => new Size(104, 24);

		private CheckState state;

		public CheckState CheckState
		{
			get { return state; }
			set
			{
				if (state == value)
				{
					return;
				}

				bool wasChecked = Checked;
				state = value;
				Invalidate();
				if (wasChecked != Checked)
				{
					OnCheckedChanged(EventArgs.Empty);
				}

				CheckStateChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		public bool Checked
		{
			get { return state != CheckState.Unchecked; }
			set { CheckState = value ? CheckState.Checked : CheckState.Unchecked; }
		}

		public bool AutoCheck { get; set; } = true;

		public bool ThreeState { get; set; }

		public ContentAlignment CheckAlign { get; set; } = ContentAlignment.MiddleLeft;

		public Appearance Appearance { get; set; }

		public event EventHandler CheckedChanged;

		public event EventHandler CheckStateChanged;

		protected virtual void OnCheckedChanged(EventArgs e) => CheckedChanged?.Invoke(this, e);

		public override Size GetPreferredSize(Size proposedSize)
		{
			SizeF size = MeasureText(DisplayText, Font);
			return new Size((int)Math.Ceiling(size.Width) + 21, Math.Max(17, (int)Math.Ceiling(size.Height) + 4));
		}

		protected override void OnClick(EventArgs e)
		{
			if (AutoCheck)
			{
				CheckState = state == CheckState.Unchecked ? CheckState.Checked : ThreeState && state == CheckState.Checked ? CheckState.Indeterminate : CheckState.Unchecked;
			}

			base.OnClick(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			if (Appearance == Appearance.Button)
			{
				DrawButtonFace(g, ClientRectangle, Checked || IsPressed, IsMouseOver);
				DrawImageAndText(g, ClientRectangle);
				base.OnPaint(e);
				return;
			}

			int box = 13;
			int y = (Height - box) / 2;
			Rectangle square = new Rectangle(0, y, box, box);
			Color border = !Enabled ? Color.FromArgb(204, 204, 204) : IsMouseOver || Focused ? HighlightColor : Color.FromArgb(51, 51, 51);
			g.FillRectangle(Enabled ? (IsPressed ? new SolidBrush(Color.FromArgb(204, 228, 247)) : Brushes.White) : Brushes.WhiteSmoke, square);
			DrawBorder(g, square, border);
			if (state == CheckState.Checked)
			{
				g.SmoothingMode = SmoothingMode.AntiAlias;
				using (Pen pen = new Pen(Enabled ? Color.FromArgb(51, 51, 51) : GrayTextColor, 1.6f))
				{
					g.DrawLines(pen, new[] { new PointF(2.5f, y + 6.5f), new PointF(5.2f, y + 9.4f), new PointF(10.5f, y + 3.2f) });
				}

				g.SmoothingMode = SmoothingMode.None;
			}
			else if (state == CheckState.Indeterminate)
			{
				g.FillRectangle(Brushes.DimGray, 3, y + 3, box - 6, box - 6);
			}

			ContentAlignment align = TextAlign == ContentAlignment.MiddleCenter ? ContentAlignment.MiddleLeft : TextAlign;
			DrawText(g, DisplayText, Font, TextColor, new RectangleF(box + 4, 0, Width - box - 4, Height), align, !AutoSize, AutoEllipsis);
			base.OnPaint(e);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled && e.KeyCode == Keys.Space)
			{
				PerformClick();
				e.Handled = true;
			}
		}
	}

	public class RadioButton : ButtonBase
	{
		public RadioButton()
		{
			TextAlign = ContentAlignment.MiddleLeft;
		}

		protected override Size DefaultSize => new Size(104, 24);

		private bool isChecked;

		public bool Checked
		{
			get { return isChecked; }
			set
			{
				if (isChecked == value)
				{
					return;
				}

				isChecked = value;
				if (value && AutoCheck && Parent != null)
				{
					// The others in the same container are unchecked, as Windows does.
					foreach (Control sibling in Parent.Controls)
					{
						if (sibling != this && sibling is RadioButton radio && radio.AutoCheck && radio.Checked)
						{
							radio.Checked = false;
						}
					}
				}

				Invalidate();
				OnCheckedChanged(EventArgs.Empty);
			}
		}

		public bool AutoCheck { get; set; } = true;

		public Appearance Appearance { get; set; }

		public ContentAlignment CheckAlign { get; set; } = ContentAlignment.MiddleLeft;

		public event EventHandler CheckedChanged;

		protected virtual void OnCheckedChanged(EventArgs e) => CheckedChanged?.Invoke(this, e);

		public override Size GetPreferredSize(Size proposedSize)
		{
			SizeF size = MeasureText(DisplayText, Font);
			return new Size((int)Math.Ceiling(size.Width) + 21, Math.Max(17, (int)Math.Ceiling(size.Height) + 4));
		}

		protected override void OnClick(EventArgs e)
		{
			if (AutoCheck)
			{
				Checked = true;
			}

			base.OnClick(e);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			if (Appearance == Appearance.Button)
			{
				DrawButtonFace(g, ClientRectangle, Checked || IsPressed, IsMouseOver);
				DrawImageAndText(g, ClientRectangle);
				base.OnPaint(e);
				return;
			}

			int box = 13;
			int y = (Height - box) / 2;
			g.SmoothingMode = SmoothingMode.AntiAlias;
			Color border = !Enabled ? Color.FromArgb(204, 204, 204) : IsMouseOver || Focused ? HighlightColor : Color.FromArgb(51, 51, 51);
			g.FillEllipse(Brushes.White, 0.5f, y + 0.5f, box - 1, box - 1);
			using (Pen pen = new Pen(border))
			{
				g.DrawEllipse(pen, 0.5f, y + 0.5f, box - 1, box - 1);
			}

			if (isChecked)
			{
				using (SolidBrush brush = new SolidBrush(Enabled ? Color.FromArgb(51, 51, 51) : GrayTextColor))
				{
					g.FillEllipse(brush, 3.5f, y + 3.5f, box - 7, box - 7);
				}
			}

			g.SmoothingMode = SmoothingMode.None;
			ContentAlignment align = TextAlign == ContentAlignment.MiddleCenter ? ContentAlignment.MiddleLeft : TextAlign;
			DrawText(g, DisplayText, Font, TextColor, new RectangleF(box + 4, 0, Width - box - 4, Height), align, !AutoSize, AutoEllipsis);
			base.OnPaint(e);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (!e.Handled && e.KeyCode == Keys.Space)
			{
				PerformClick();
				e.Handled = true;
			}
		}
	}

	public class GroupBox : Control
	{
		public GroupBox()
		{
			TabStop = false;
		}

		protected override Size DefaultSize => new Size(200, 100);

		public FlatStyle FlatStyle { get; set; } = FlatStyle.Standard;

		public bool UseCompatibleTextRendering { get; set; }

		internal override bool DrawsItself => true;

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			SizeF text = MeasureText(Text, Font);
			int top = (int)(text.Height / 2);
			using (Pen pen = new Pen(Color.FromArgb(220, 220, 220)))
			{
				g.DrawRectangle(pen, 0, top, Width - 1, Height - top - 1);
			}

			if (!string.IsNullOrEmpty(Text))
			{
				Color back = BackColor;
				using (SolidBrush brush = new SolidBrush(back.A == 0 ? Color.White : back))
				{
					g.FillRectangle(brush, 6, 0, text.Width + 4, text.Height);
				}

				DrawText(g, Text, Font, TextColor, new RectangleF(8, 0, Math.Min(text.Width + 2, Width - 10), text.Height + 1), ContentAlignment.TopLeft);
			}

			base.OnPaint(e);
		}
	}

	/// <summary>A container whose children can be scrolled when AutoScroll is on.</summary>
	public class ScrollableControl : Control
	{
		private int scrollY;
		private bool autoScroll;
		private const int BarWidth = 17;

		public virtual bool AutoScroll
		{
			get { return autoScroll; }
			set
			{
				autoScroll = value;
				Invalidate();
			}
		}

		public Size AutoScrollMinSize { get; set; }

		public Point AutoScrollPosition
		{
			get { return new Point(0, -scrollY); }
			set { SetScroll(-value.Y); }
		}

		/// <summary>How far the content reaches below the top.</summary>
		private int ContentHeight
		{
			get
			{
				int bottom = AutoScrollMinSize.Height;
				foreach (Control child in Controls)
				{
					if (child.OwnVisible)
					{
						bottom = Math.Max(bottom, child.Bottom);
					}
				}

				return bottom;
			}
		}

		private bool Scrolls => autoScroll && ContentHeight > Height;

		private void SetScroll(int value)
		{
			int max = Math.Max(0, ContentHeight - Height);
			value = Math.Max(0, Math.Min(max, value));
			if (value != scrollY)
			{
				scrollY = value;
				Host.RequestRender();
				Invalidate();
			}
		}

		internal override Point ChildOffset => new Point(0, -scrollY);

		internal override bool HandleWheel(int delta, Point location)
		{
			if (!Scrolls)
			{
				return false;
			}

			SetScroll(scrollY - delta / 120 * 40);
			return true;
		}

		internal override bool PaintsOverChildren => Scrolls;

		internal override void PaintOverChildren(Graphics g)
		{
			int content = ContentHeight;
			Rectangle track = new Rectangle(Width - BarWidth, 0, BarWidth, Height);
			g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)), track);
			int thumb = Math.Max(20, Height * Height / Math.Max(1, content));
			int y = (Height - thumb) * scrollY / Math.Max(1, content - Height);
			g.FillRectangle(new SolidBrush(Color.FromArgb(193, 193, 193)), Width - BarWidth + 3, y + 1, BarWidth - 6, thumb - 2);
		}

		private int dragOffset = -1;

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (Scrolls && e.X >= Width - BarWidth)
			{
				dragOffset = e.Y;
				ScrollTo(e.Y);
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (dragOffset >= 0 && (e.Button & MouseButtons.Left) != 0)
			{
				ScrollTo(e.Y);
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			base.OnMouseUp(e);
			dragOffset = -1;
		}

		private void ScrollTo(int y)
		{
			int content = ContentHeight;
			SetScroll((int)((long)(content - Height) * Math.Max(0, Math.Min(Height, y)) / Math.Max(1, Height)));
		}

		public void ScrollControlIntoView(Control control)
		{
			if (control.Top < scrollY)
			{
				SetScroll(control.Top);
			}
			else if (control.Bottom > scrollY + Height)
			{
				SetScroll(control.Bottom - Height);
			}
		}
	}

	public class ContainerControl : ScrollableControl
	{
	}

	public class Panel : ScrollableControl
	{
		public Panel()
		{
			TabStop = false;
		}

		protected override Size DefaultSize => new Size(200, 100);

		private BorderStyle borderStyle;

		public BorderStyle BorderStyle
		{
			get { return borderStyle; }
			set
			{
				borderStyle = value;
				Invalidate();
			}
		}

		internal override bool DrawsItself => base.DrawsItself || borderStyle != BorderStyle.None;

		public override Rectangle DisplayRectangle
		{
			get
			{
				int inset = borderStyle == BorderStyle.None ? 0 : 1;
				return new Rectangle(inset, inset, Width - 2 * inset, Height - 2 * inset);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			if (borderStyle != BorderStyle.None)
			{
				DrawBorder(e.Graphics, ClientRectangle, borderStyle == BorderStyle.Fixed3D ? Color.FromArgb(160, 160, 160) : Color.FromArgb(100, 100, 100));
			}

			base.OnPaint(e);
		}
	}

	public class SplitterPanel : Panel
	{
		internal SplitterPanel()
		{
		}
	}

	public class SplitContainer : Control, ISupportInitialize
	{
		private int splitterDistance = 50;
		private Orientation orientation = Orientation.Vertical;

		public SplitContainer()
		{
			Controls.Add(Panel1);
			Controls.Add(Panel2);
			Arrange();
		}

		protected override Size DefaultSize => new Size(150, 100);

		public SplitterPanel Panel1 { get; } = new SplitterPanel();

		public SplitterPanel Panel2 { get; } = new SplitterPanel();

		public int SplitterWidth { get; set; } = 4;

		public bool IsSplitterFixed { get; set; }

		public FixedPanel FixedPanel { get; set; }

		public BorderStyle BorderStyle { get; set; }

		public bool Panel1Collapsed { get; set; }

		public bool Panel2Collapsed { get; set; }

		public int Panel1MinSize { get; set; } = 25;

		public int Panel2MinSize { get; set; } = 25;

		public Orientation Orientation
		{
			get { return orientation; }
			set
			{
				orientation = value;
				Arrange();
			}
		}

		public int SplitterDistance
		{
			get { return splitterDistance; }
			set
			{
				splitterDistance = value;
				Arrange();
			}
		}

		private void Arrange()
		{
			if (orientation == Orientation.Vertical)
			{
				Panel1.SetBounds(0, 0, splitterDistance, Height);
				Panel2.SetBounds(splitterDistance + SplitterWidth, 0, Math.Max(0, Width - splitterDistance - SplitterWidth), Height);
			}
			else
			{
				Panel1.SetBounds(0, 0, Width, splitterDistance);
				Panel2.SetBounds(0, splitterDistance + SplitterWidth, Width, Math.Max(0, Height - splitterDistance - SplitterWidth));
			}
		}

		protected override void OnLayout(EventArgs e)
		{
			if (FixedPanel == FixedPanel.Panel2)
			{
				int size = orientation == Orientation.Vertical ? Panel2.Width : Panel2.Height;
				int total = orientation == Orientation.Vertical ? Width : Height;
				splitterDistance = Math.Max(0, total - size - SplitterWidth);
			}

			Arrange();
		}

		protected override void OnResize(EventArgs e)
		{
			Arrange();
			base.OnResize(e);
		}
	}

	public enum FixedPanel { None, Panel1, Panel2 }

	public class PictureBox : Control
	{
		private Image image;
		private PictureBoxSizeMode sizeMode;

		public PictureBox()
		{
			TabStop = false;
		}

		protected override Size DefaultSize => new Size(100, 50);

		public Image Image
		{
			get { return image; }
			set
			{
				image = value;
				if (sizeMode == PictureBoxSizeMode.AutoSize && value != null)
				{
					Size = value.Size;
				}

				Invalidate();
			}
		}

		public Image ErrorImage { get; set; }

		public Image InitialImage { get; set; }

		public string ImageLocation { get; set; }

		public bool WaitOnLoad { get; set; }

		public PictureBoxSizeMode SizeMode
		{
			get { return sizeMode; }
			set
			{
				sizeMode = value;
				if (value == PictureBoxSizeMode.AutoSize && image != null)
				{
					Size = image.Size;
				}

				Invalidate();
			}
		}

		public BorderStyle BorderStyle { get; set; }

		public void Load(string url)
		{
			try
			{
				Image = Image.FromFile(url);
			}
			catch (Exception)
			{
				Image = ErrorImage;
			}
		}

		internal override bool DrawsItself => true;

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			if (image != null)
			{
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.Half;
				try
				{
					lock (image)
					{
						switch (sizeMode)
						{
							case PictureBoxSizeMode.StretchImage:
								g.DrawImage(image, new Rectangle(0, 0, Width, Height));
								break;
							case PictureBoxSizeMode.Zoom:
							{
								float s = Math.Min(Width / (float)image.Width, Height / (float)image.Height);
								float w = image.Width * s, h = image.Height * s;
								g.DrawImage(image, new RectangleF((Width - w) / 2, (Height - h) / 2, w, h));
								break;
							}
							case PictureBoxSizeMode.CenterImage:
								g.DrawImage(image, (Width - image.Width) / 2, (Height - image.Height) / 2, image.Width, image.Height);
								break;
							default:
								g.DrawImage(image, 0, 0, image.Width, image.Height);
								break;
						}
					}
				}
				catch (InvalidOperationException)
				{
					// The image is being changed by another thread (a route preview): drawn next time.
					Invalidate();
				}

				g.PixelOffsetMode = PixelOffsetMode.Default;
			}

			if (BorderStyle != BorderStyle.None)
			{
				DrawBorder(g, ClientRectangle, Color.FromArgb(160, 160, 160));
			}

			base.OnPaint(e);
		}
	}

	public class TrackBar : Control, ISupportInitialize
	{
		private int minimum, maximum = 10, value;

		public TrackBar()
		{
		}

		protected override Size DefaultSize => new Size(104, 45);

		public int Minimum
		{
			get { return minimum; }
			set
			{
				minimum = value;
				if (maximum < value) maximum = value;
				Value = Math.Max(this.value, value);
				Invalidate();
			}
		}

		public int Maximum
		{
			get { return maximum; }
			set
			{
				maximum = value;
				if (minimum > value) minimum = value;
				Value = Math.Min(this.value, value);
				Invalidate();
			}
		}

		public int Value
		{
			get { return value; }
			set
			{
				int clamped = Math.Max(minimum, Math.Min(maximum, value));
				if (clamped == this.value)
				{
					return;
				}

				this.value = clamped;
				Invalidate();
				ValueChanged?.Invoke(this, EventArgs.Empty);
			}
		}

		public int TickFrequency { get; set; } = 1;

		public int SmallChange { get; set; } = 1;

		public int LargeChange { get; set; } = 5;

		public TickStyle TickStyle { get; set; } = TickStyle.BottomRight;

		public Orientation Orientation { get; set; } = Orientation.Horizontal;

		public event EventHandler Scroll;

		public event EventHandler ValueChanged;

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		private float Track0 => 12;

		private float TrackLength => Math.Max(1, Width - 24);

		private float ThumbX => Track0 + (maximum == minimum ? 0 : (value - minimum) * TrackLength / (maximum - minimum));

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			int trackY = 12;
			g.FillRectangle(new SolidBrush(Color.FromArgb(231, 234, 234)), Track0, trackY - 2, TrackLength, 4);
			DrawBorder(g, new Rectangle((int)Track0, trackY - 2, (int)TrackLength, 4), Color.FromArgb(214, 214, 214));
			if (TickStyle != TickStyle.None && TickFrequency > 0 && maximum > minimum)
			{
				for (int t = minimum; t <= maximum; t += TickFrequency)
				{
					float x = Track0 + (t - minimum) * TrackLength / (maximum - minimum);
					if (TickStyle == TickStyle.BottomRight || TickStyle == TickStyle.Both)
					{
						g.DrawLine(Pens.Gray, x, trackY + 11, x, trackY + 14);
					}

					if (TickStyle == TickStyle.TopLeft || TickStyle == TickStyle.Both)
					{
						g.DrawLine(Pens.Gray, x, trackY - 11, x, trackY - 14);
					}
				}
			}

			float tx = ThumbX;
			Color thumb = !Enabled ? Color.FromArgb(204, 204, 204) : IsPressed ? Color.FromArgb(204, 204, 204) : IsMouseOver ? Color.FromArgb(23, 23, 23) : HighlightColor;
			using (SolidBrush brush = new SolidBrush(thumb))
			{
				g.FillRectangle(brush, tx - 5, trackY - 10, 11, 20);
			}

			base.OnPaint(e);
		}

		private void SetFromMouse(int x)
		{
			int v = (int)Math.Round(minimum + (x - Track0) * (maximum - minimum) / TrackLength);
			int old = value;
			Value = v;
			if (value != old)
			{
				Scroll?.Invoke(this, EventArgs.Empty);
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button == MouseButtons.Left)
			{
				SetFromMouse(e.X);
			}
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if ((e.Button & MouseButtons.Left) != 0 && IsPressedOrCaptured)
			{
				SetFromMouse(e.X);
			}
		}

		private bool IsPressedOrCaptured => Host.CapturedControl == this;

		internal override bool HandleWheel(int delta, Point location)
		{
			int old = value;
			Value = value + (delta > 0 ? SmallChange : -SmallChange);
			if (old != value)
			{
				Scroll?.Invoke(this, EventArgs.Empty);
			}

			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled)
			{
				return;
			}

			int old = value;
			switch (e.KeyCode)
			{
				case Keys.Left: case Keys.Down: Value = value - SmallChange; break;
				case Keys.Right: case Keys.Up: Value = value + SmallChange; break;
				case Keys.PageDown: Value = value - LargeChange; break;
				case Keys.PageUp: Value = value + LargeChange; break;
				case Keys.Home: Value = minimum; break;
				case Keys.End: Value = maximum; break;
				default: return;
			}

			e.Handled = true;
			if (old != value)
			{
				Scroll?.Invoke(this, EventArgs.Empty);
			}
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down;
		}
	}
}
