// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): the base of OBNS's controls, with the part of
// System.Windows.Forms.Control that upstream's forms use. A control paints itself with System.Drawing into a bitmap of
// its own, kept until something about it changes; the host (Host.cs) draws those bitmaps with OpenGL.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Threading;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	public class Control : Component, ISupportInitialize
	{
		public static readonly Font DefaultFont = new Font("Microsoft Sans Serif", 8.25f);

		/// <summary>The default colours, as Windows' classic ones.</summary>
		internal static readonly Color ControlColor = Color.FromArgb(240, 240, 240);
		internal static readonly Color BorderColor = Color.FromArgb(173, 173, 173);
		internal static readonly Color HighlightColor = Color.FromArgb(0, 120, 215);
		internal static readonly Color GrayTextColor = Color.FromArgb(109, 109, 109);

		private int left, top, width, height;
		private string text = string.Empty;
		private Font font;
		private Color foreColor = Color.Empty, backColor = Color.Empty;
		private bool visible = true, enabled = true;
		private Control parent;

		public Control()
		{
			Controls = CreateControlsInstance();
			Size defaultSize = DefaultSize;
			width = defaultSize.Width;
			height = defaultSize.Height;
		}

		protected virtual Size DefaultSize => new Size(100, 23);

		protected virtual ControlCollection CreateControlsInstance() => new ControlCollection(this);

		// --- hierarchy ---

		public ControlCollection Controls { get; }

		public Control Parent
		{
			get { return parent; }
			set
			{
				if (parent == value)
				{
					return;
				}

				parent?.Controls.Remove(this);
				value?.Controls.Add(this);
			}
		}

		internal void SetParentInternal(Control value)
		{
			parent = value;
		}

		public string Name { get; set; } = string.Empty;

		public object Tag { get; set; }

		public int TabIndex { get; set; }

		public bool TabStop { get; set; } = true;

		public bool CausesValidation { get; set; } = true;

		public bool UseWaitCursor { get; set; }

		public bool AllowDrop { get; set; }

		/// <summary>Every control here is drawn from a bitmap of its own, so already without flicker.</summary>
		protected bool DoubleBuffered { get; set; }

		public ImeMode ImeMode { get; set; }

		public RightToLeft RightToLeft { get; set; }

		public Padding Margin { get; set; } = new Padding(3);

		private Padding padding;

		public Padding Padding
		{
			get { return padding; }
			set
			{
				padding = value;
				PerformLayout();
			}
		}

		public ContextMenuStrip ContextMenuStrip { get; set; }

		public Cursor Cursor { get; set; } = Cursors.Default;

		public Form FindForm()
		{
			Control control = this;
			while (control != null && !(control is Form))
			{
				control = control.parent;
			}

			return (Form)control;
		}

		public Form TopLevelControl => FindForm();

		public bool IsHandleCreated => true;

		public IntPtr Handle => IntPtr.Zero;

		public bool IsDisposed { get; private set; }

		public bool Disposing { get; private set; }

		protected override void Dispose(bool disposing)
		{
			if (disposing && !IsDisposed)
			{
				Disposing = true;
				for (int i = Controls.Count - 1; i >= 0; i--)
				{
					Controls[i].Dispose();
				}

				surface?.Dispose();
				surface = null;
				IsDisposed = true;
			}

			base.Dispose(disposing);
		}

		// --- geometry ---

		public int Left
		{
			get { return left; }
			set { SetBounds(value, top, width, height); }
		}

		public int Top
		{
			get { return top; }
			set { SetBounds(left, value, width, height); }
		}

		public int Width
		{
			get { return width; }
			set { SetBounds(left, top, value, height); }
		}

		public int Height
		{
			get { return height; }
			set { SetBounds(left, top, width, value); }
		}

		public int Right => left + width;

		public int Bottom => top + height;

		public Point Location
		{
			get { return new Point(left, top); }
			set { SetBounds(value.X, value.Y, width, height); }
		}

		public Size Size
		{
			get { return new Size(width, height); }
			set { SetBounds(left, top, value.Width, value.Height); }
		}

		public Rectangle Bounds
		{
			get { return new Rectangle(left, top, width, height); }
			set { SetBounds(value.X, value.Y, value.Width, value.Height); }
		}

		public Size ClientSize
		{
			get { return ClientRectangle.Size; }
			set { SetClientSizeCore(value.Width, value.Height); }
		}

		protected virtual void SetClientSizeCore(int x, int y)
		{
			Size = new Size(x, y);
		}

		public Rectangle ClientRectangle => new Rectangle(0, 0, width, height);

		/// <summary>Where the children are, in this control's coordinates.</summary>
		public virtual Rectangle DisplayRectangle => ClientRectangle;

		private Size minimumSize, maximumSize;

		public virtual Size MinimumSize
		{
			get { return minimumSize; }
			set
			{
				minimumSize = value;
				SetBounds(left, top, width, height);
			}
		}

		public virtual Size MaximumSize
		{
			get { return maximumSize; }
			set
			{
				maximumSize = value;
				SetBounds(left, top, width, height);
			}
		}

		private AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left;

		public AnchorStyles Anchor
		{
			get { return anchor; }
			set
			{
				anchor = value;
				dock = DockStyle.None;
				RecordAnchor();
			}
		}

		private DockStyle dock;

		public DockStyle Dock
		{
			get { return dock; }
			set
			{
				dock = value;
				parent?.PerformLayout();
			}
		}

		private bool autoSize;

		public virtual bool AutoSize
		{
			get { return autoSize; }
			set
			{
				autoSize = value;
				ApplyAutoSize();
			}
		}

		/// <summary>The size the control would take for its content (its text), for AutoSize.</summary>
		public virtual Size GetPreferredSize(Size proposedSize) => Size;

		protected void ApplyAutoSize()
		{
			if (autoSize && !(this is Form))
			{
				Size preferred = GetPreferredSize(Size.Empty);
				if (preferred != Size)
				{
					Size = preferred;
				}
			}
		}

		public void SetBounds(int x, int y, int w, int h)
		{
			if (minimumSize.Width > 0) w = Math.Max(w, minimumSize.Width);
			if (minimumSize.Height > 0) h = Math.Max(h, minimumSize.Height);
			if (maximumSize.Width > 0) w = Math.Min(w, maximumSize.Width);
			if (maximumSize.Height > 0) h = Math.Min(h, maximumSize.Height);
			w = Math.Max(0, w);
			h = Math.Max(0, h);
			bool moved = x != left || y != top;
			bool resized = w != width || h != height;
			if (!moved && !resized)
			{
				return;
			}

			left = x;
			top = y;
			width = w;
			height = h;
			if (!inParentLayout)
			{
				RecordAnchor();
			}

			if (moved)
			{
				OnLocationChanged(EventArgs.Empty);
			}

			if (resized)
			{
				Invalidate();
				PerformLayout();
				OnSizeChanged(EventArgs.Empty);
				OnResize(EventArgs.Empty);
			}

			Host.RequestRender();
		}

		// --- anchoring: as Windows Forms, a control keeps its distance to the sides of its parent it is anchored to ---

		private bool inParentLayout;
		private bool anchorRecorded;
		private int anchorRight, anchorBottom;
		private float anchorCenterX, anchorCenterY;

		/// <summary>Whether the controls' anchors are fixed yet: they are when the form is first shown, from the layout the designer gave.</summary>
		internal static bool AnchorsLive;

		internal void RecordAnchor()
		{
			if (parent == null || !AnchorsLive)
			{
				anchorRecorded = false;
				return;
			}

			Rectangle area = parent.DisplayRectangle;
			anchorRight = area.Right - Right;
			anchorBottom = area.Bottom - Bottom;
			anchorCenterX = area.Width > 0 ? (left - area.Left + width / 2.0f) / area.Width : 0.5f;
			anchorCenterY = area.Height > 0 ? (top - area.Top + height / 2.0f) / area.Height : 0.5f;
			anchorRecorded = true;
		}

		/// <summary>Fixes every control's anchor from the layout as it is now.</summary>
		internal void RecordAnchorsRecursive()
		{
			RecordAnchor();
			foreach (Control child in Controls)
			{
				child.RecordAnchorsRecursive();
			}
		}

		private int layoutSuspended;

		public void SuspendLayout()
		{
			layoutSuspended++;
		}

		public void ResumeLayout()
		{
			ResumeLayout(true);
		}

		public void ResumeLayout(bool performLayout)
		{
			if (layoutSuspended > 0)
			{
				layoutSuspended--;
			}

			if (performLayout)
			{
				PerformLayout();
			}
		}

		public void PerformLayout()
		{
			if (layoutSuspended > 0 || !AnchorsLive)
			{
				return;
			}

			OnLayout(EventArgs.Empty);
		}

		public event EventHandler Layout;

		/// <summary>Places the children: docked ones in the remaining space, the others by their anchors.</summary>
		protected virtual void OnLayout(EventArgs e)
		{
			Rectangle area = DisplayRectangle;
			Rectangle remaining = area;
			for (int i = Controls.Count - 1; i >= 0; i--)
			{
				Control child = Controls[i];
				if (!child.visible && child.dock != DockStyle.None)
				{
					continue;
				}

				child.inParentLayout = true;
				try
				{
					switch (child.dock)
					{
						case DockStyle.Top:
							child.SetBounds(remaining.Left, remaining.Top, remaining.Width, child.height);
							remaining.Y += child.height;
							remaining.Height -= child.height;
							break;
						case DockStyle.Bottom:
							child.SetBounds(remaining.Left, remaining.Bottom - child.height, remaining.Width, child.height);
							remaining.Height -= child.height;
							break;
						case DockStyle.Left:
							child.SetBounds(remaining.Left, remaining.Top, child.width, remaining.Height);
							remaining.X += child.width;
							remaining.Width -= child.width;
							break;
						case DockStyle.Right:
							child.SetBounds(remaining.Right - child.width, remaining.Top, child.width, remaining.Height);
							remaining.Width -= child.width;
							break;
						case DockStyle.Fill:
							break;
						default:
							child.ApplyAnchor(area);
							break;
					}
				}
				finally
				{
					child.inParentLayout = false;
				}
			}

			foreach (Control child in Controls)
			{
				if (child.dock == DockStyle.Fill)
				{
					child.inParentLayout = true;
					child.SetBounds(remaining.Left, remaining.Top, remaining.Width, remaining.Height);
					child.inParentLayout = false;
				}
			}

			Layout?.Invoke(this, e);
		}

		private void ApplyAnchor(Rectangle area)
		{
			if (!anchorRecorded)
			{
				RecordAnchor();
				return;
			}

			int x = left, y = top, w = width, h = height;
			bool l = (anchor & AnchorStyles.Left) != 0, r = (anchor & AnchorStyles.Right) != 0;
			bool t = (anchor & AnchorStyles.Top) != 0, b = (anchor & AnchorStyles.Bottom) != 0;
			if (l && r)
			{
				w = area.Right - anchorRight - x;
			}
			else if (r)
			{
				x = area.Right - anchorRight - w;
			}
			else if (!l)
			{
				x = (int)Math.Round(area.Left + anchorCenterX * area.Width - w / 2.0f);
			}

			if (t && b)
			{
				h = area.Bottom - anchorBottom - y;
			}
			else if (b)
			{
				y = area.Bottom - anchorBottom - h;
			}
			else if (!t)
			{
				y = (int)Math.Round(area.Top + anchorCenterY * area.Height - h / 2.0f);
			}

			SetBounds(x, y, w, h);
		}

		// --- appearance ---

		public virtual string Text
		{
			get { return text; }
			set
			{
				value = value ?? string.Empty;
				if (text == value)
				{
					return;
				}

				text = value;
				ApplyAutoSize();
				Invalidate();
				OnTextChanged(EventArgs.Empty);
			}
		}

		public virtual Font Font
		{
			get { return font ?? parent?.Font ?? DefaultFont; }
			set
			{
				font = value;
				InvalidateTree();
				ApplyAutoSize();
				OnFontChanged(EventArgs.Empty);
			}
		}

		public virtual Color ForeColor
		{
			get { return foreColor.IsEmpty ? parent?.ForeColor ?? Color.Black : foreColor; }
			set
			{
				foreColor = value;
				InvalidateTree();
			}
		}

		/// <summary>The control's own background; empty means its parent's shows through.</summary>
		protected virtual Color DefaultBackColor => Color.Empty;

		public virtual Color BackColor
		{
			get
			{
				if (!backColor.IsEmpty)
				{
					return backColor;
				}

				Color own = DefaultBackColor;
				return !own.IsEmpty ? own : parent?.BackColor ?? ControlColor;
			}
			set
			{
				backColor = value;
				InvalidateTree();
			}
		}

		/// <summary>The colour this control fills its own bitmap with, or none.</summary>
		internal Color OwnBackColor => !backColor.IsEmpty ? backColor : DefaultBackColor;

		public Image BackgroundImage { get; set; }

		public ImageLayout BackgroundImageLayout { get; set; }

		public bool Visible
		{
			get { return visible && (parent == null || parent.Visible); }
			set
			{
				if (visible == value)
				{
					return;
				}

				visible = value;
				if (!value && Host.ContainsFocus(this))
				{
					Host.SetFocus(null);
				}

				parent?.PerformLayout();
				OnVisibleChanged(EventArgs.Empty);
				Host.RequestRender();
			}
		}

		/// <summary>The control's own visibility, regardless of its parents'.</summary>
		internal bool OwnVisible => visible;

		public bool Enabled
		{
			get { return enabled && (parent == null || parent.Enabled); }
			set
			{
				if (enabled == value)
				{
					return;
				}

				enabled = value;
				InvalidateTree();
				OnEnabledChanged(EventArgs.Empty);
			}
		}

		public void Show()
		{
			Visible = true;
		}

		public void Hide()
		{
			Visible = false;
		}

		public void BringToFront()
		{
			if (parent != null)
			{
				parent.Controls.SetChildIndex(this, 0);
			}
		}

		public void SendToBack()
		{
			if (parent != null)
			{
				parent.Controls.SetChildIndex(this, parent.Controls.Count - 1);
			}
		}

		// --- painting ---

		private bool dirty = true;
		internal Surface surface;

		public void Invalidate()
		{
			dirty = true;
			Host.RequestRender();
		}

		public void Invalidate(bool invalidateChildren)
		{
			if (invalidateChildren)
			{
				InvalidateTree();
			}
			else
			{
				Invalidate();
			}
		}

		public void Invalidate(Rectangle rc)
		{
			Invalidate();
		}

		internal void InvalidateTree()
		{
			Invalidate();
			foreach (Control child in Controls)
			{
				child.InvalidateTree();
			}
		}

		public void Refresh()
		{
			Invalidate();
		}

		public void Update()
		{
		}

		public event PaintEventHandler Paint;

		/// <summary>Draws the control in its own coordinates (logical units).</summary>
		protected virtual void OnPaint(PaintEventArgs e)
		{
			Paint?.Invoke(this, e);
		}

		/// <summary>Whether a form paints on this control itself (the native host shows such a control as a picture).</summary>
		internal bool HasPaintHandler => Paint != null;

		/// <summary>Whether the control draws anything itself (a bare container need not have a bitmap).</summary>
		internal virtual bool DrawsItself => !OwnBackColor.IsEmpty || Paint != null || BackgroundImage != null;

		/// <summary>The control's bitmap, painted again if anything changed.</summary>
		internal Surface GetSurface(float scale)
		{
			int pw = Math.Max(1, (int)Math.Ceiling(width * scale));
			int ph = Math.Max(1, (int)Math.Ceiling(height * scale));
			if (surface != null && !dirty && surface.Width == pw && surface.Height == ph)
			{
				return surface;
			}

			if (surface == null || surface.Width != pw || surface.Height != ph)
			{
				surface?.Dispose();
				surface = new Surface(pw, ph);
			}

			using (Graphics g = Graphics.FromImage(surface.Bitmap))
			{
				g.Clear(Color.Transparent);
				g.ScaleTransform(scale, scale);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
				g.PixelOffsetMode = PixelOffsetMode.Half;
				Color back = OwnBackColor;
				if (!back.IsEmpty && back.A > 0)
				{
					using (SolidBrush brush = new SolidBrush(back))
					{
						g.FillRectangle(brush, 0, 0, width, height);
					}
				}

				if (BackgroundImage != null)
				{
					g.DrawImage(BackgroundImage, new Rectangle(0, 0, width, height));
				}

				g.SmoothingMode = SmoothingMode.None;
				g.PixelOffsetMode = PixelOffsetMode.Default;
				try
				{
					OnPaint(new PaintEventArgs(g, ClientRectangle));
				}
				catch (Exception ex)
				{
					Host.Log("painting " + Name + " failed: " + ex.Message);
				}
			}

			surface.Changed = true;
			dirty = false;
			return surface;
		}

		/// <summary>Where the children are drawn from, in this control's coordinates (a scrolled panel moves them).</summary>
		internal virtual Point ChildOffset => Point.Empty;

		/// <summary>Controls drawn above the children (a scroll bar, a group's frame): none by default.</summary>
		internal virtual void PaintOverChildren(Graphics g)
		{
		}

		internal virtual bool PaintsOverChildren => false;

		internal Surface overSurface;
		private bool overDirty = true;

		internal Surface GetOverSurface(float scale)
		{
			int pw = Math.Max(1, (int)Math.Ceiling(width * scale));
			int ph = Math.Max(1, (int)Math.Ceiling(height * scale));
			if (overSurface != null && !dirty && !overDirty && overSurface.Width == pw && overSurface.Height == ph)
			{
				return overSurface;
			}

			if (overSurface == null || overSurface.Width != pw || overSurface.Height != ph)
			{
				overSurface?.Dispose();
				overSurface = new Surface(pw, ph);
			}

			using (Graphics g = Graphics.FromImage(overSurface.Bitmap))
			{
				g.Clear(Color.Transparent);
				g.ScaleTransform(scale, scale);
				g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
				PaintOverChildren(g);
			}

			overSurface.Changed = true;
			overDirty = false;
			return overSurface;
		}

		// --- text helpers ---

		private static readonly Graphics measureGraphics = Graphics.FromImage(new Bitmap(1, 1));

		/// <summary>The size of a text in a font, as drawn here.</summary>
		internal static SizeF MeasureText(string value, Font f, int wrapWidth = 0)
		{
			lock (measureGraphics)
			{
				if (string.IsNullOrEmpty(value))
				{
					SizeF line = measureGraphics.MeasureString("Ag", f, 1000, StringFormat.GenericTypographic);
					return new SizeF(0, line.Height);
				}

				return wrapWidth > 0
					? measureGraphics.MeasureString(value, f, wrapWidth, StringFormat.GenericTypographic)
					: measureGraphics.MeasureString(value, f, new PointF(0, 0), StringFormat.GenericTypographic);
			}
		}

		internal static StringFormat Format(ContentAlignment alignment, bool wrap, bool ellipsis)
		{
			StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();
			format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
			if (!wrap)
			{
				format.FormatFlags |= StringFormatFlags.NoWrap;
			}

			format.Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;
			switch (alignment)
			{
				case ContentAlignment.TopLeft: case ContentAlignment.MiddleLeft: case ContentAlignment.BottomLeft:
					format.Alignment = StringAlignment.Near;
					break;
				case ContentAlignment.TopCenter: case ContentAlignment.MiddleCenter: case ContentAlignment.BottomCenter:
					format.Alignment = StringAlignment.Center;
					break;
				default:
					format.Alignment = StringAlignment.Far;
					break;
			}

			switch (alignment)
			{
				case ContentAlignment.TopLeft: case ContentAlignment.TopCenter: case ContentAlignment.TopRight:
					format.LineAlignment = StringAlignment.Near;
					break;
				case ContentAlignment.MiddleLeft: case ContentAlignment.MiddleCenter: case ContentAlignment.MiddleRight:
					format.LineAlignment = StringAlignment.Center;
					break;
				default:
					format.LineAlignment = StringAlignment.Far;
					break;
			}

			return format;
		}

		internal static void DrawText(Graphics g, string value, Font f, Color color, RectangleF area, ContentAlignment alignment, bool wrap = false, bool ellipsis = false)
		{
			if (string.IsNullOrEmpty(value))
			{
				return;
			}

			using (StringFormat format = Format(alignment, wrap, ellipsis))
			using (SolidBrush brush = new SolidBrush(color))
			{
				g.DrawString(value, f, brush, area, format);
			}
		}

		internal static void DrawBorder(Graphics g, Rectangle r, Color color)
		{
			using (Pen pen = new Pen(color))
			{
				g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
			}
		}

		/// <summary>The text colour for this control's state.</summary>
		internal Color TextColor => Enabled ? ForeColor : GrayTextColor;

		// --- focus and input ---

		public bool Focused => Host.FocusedControl == this;

		public bool ContainsFocus => Host.ContainsFocus(this);

		/// <summary>Whether a second click raises DoubleClick instead of Click (a button takes both as clicks).</summary>
		internal virtual bool StandardDoubleClick => true;

		/// <summary>Whether the control takes the keyboard focus when clicked.</summary>
		internal virtual bool CanTakeFocus => false;

		public bool CanFocus => Visible && Enabled;

		public bool Focus()
		{
			if (!CanFocus)
			{
				return false;
			}

			Host.SetFocus(this);
			return true;
		}

		public void Select()
		{
			Focus();
		}

		internal bool IsMouseOver => Host.HoveredControl == this;

		internal bool IsPressed => Host.CapturedControl == this && Host.CaptureInside;

		public event EventHandler Click;
		public event EventHandler DoubleClick;
		public event MouseEventHandler MouseClick;
		public event MouseEventHandler MouseDoubleClick;
		public event MouseEventHandler MouseDown;
		public event MouseEventHandler MouseUp;
		public event MouseEventHandler MouseMove;
		public event MouseEventHandler MouseWheel;
		public event EventHandler MouseEnter;
		public event EventHandler MouseLeave;
		public event KeyEventHandler KeyDown;
		public event KeyEventHandler KeyUp;
		public event KeyPressEventHandler KeyPress;
		public event EventHandler TextChanged;
		public event EventHandler FontChanged;
		public event EventHandler Enter;
		public event EventHandler Leave;
		public event EventHandler GotFocus;
		public event EventHandler LostFocus;
		public event EventHandler Resize;
		public event EventHandler SizeChanged;
		public event EventHandler LocationChanged;
		public event EventHandler VisibleChanged;
		public event EventHandler EnabledChanged;
		public event DragEventHandler DragEnter;
		public event DragEventHandler DragOver;
		public event DragEventHandler DragDrop;
		public event EventHandler DragLeave;
		public event CancelEventHandler Validating;
		public event EventHandler Validated;

		protected virtual void OnClick(EventArgs e) => Click?.Invoke(this, e);
		protected virtual void OnDoubleClick(EventArgs e) => DoubleClick?.Invoke(this, e);
		protected virtual void OnMouseClick(MouseEventArgs e) => MouseClick?.Invoke(this, e);
		protected virtual void OnMouseDoubleClick(MouseEventArgs e) => MouseDoubleClick?.Invoke(this, e);
		protected virtual void OnMouseDown(MouseEventArgs e) => MouseDown?.Invoke(this, e);
		protected virtual void OnMouseUp(MouseEventArgs e) => MouseUp?.Invoke(this, e);
		protected virtual void OnMouseMove(MouseEventArgs e) => MouseMove?.Invoke(this, e);
		protected virtual void OnMouseEnter(EventArgs e) => MouseEnter?.Invoke(this, e);
		protected virtual void OnMouseLeave(EventArgs e) => MouseLeave?.Invoke(this, e);
		protected virtual void OnKeyDown(KeyEventArgs e) => KeyDown?.Invoke(this, e);
		protected virtual void OnKeyUp(KeyEventArgs e) => KeyUp?.Invoke(this, e);
		protected virtual void OnKeyPress(KeyPressEventArgs e) => KeyPress?.Invoke(this, e);
		protected virtual void OnTextChanged(EventArgs e) => TextChanged?.Invoke(this, e);
		protected virtual void OnFontChanged(EventArgs e) => FontChanged?.Invoke(this, e);
		protected virtual void OnEnter(EventArgs e) => Enter?.Invoke(this, e);
		protected virtual void OnLeave(EventArgs e) => Leave?.Invoke(this, e);
		protected virtual void OnGotFocus(EventArgs e) => GotFocus?.Invoke(this, e);
		protected virtual void OnLostFocus(EventArgs e) => LostFocus?.Invoke(this, e);
		protected virtual void OnResize(EventArgs e) => Resize?.Invoke(this, e);
		protected virtual void OnSizeChanged(EventArgs e) => SizeChanged?.Invoke(this, e);
		protected virtual void OnLocationChanged(EventArgs e) => LocationChanged?.Invoke(this, e);
		protected virtual void OnVisibleChanged(EventArgs e) => VisibleChanged?.Invoke(this, e);
		protected virtual void OnEnabledChanged(EventArgs e) => EnabledChanged?.Invoke(this, e);
		protected virtual void OnDragEnter(DragEventArgs e) => DragEnter?.Invoke(this, e);
		protected virtual void OnDragOver(DragEventArgs e) => DragOver?.Invoke(this, e);
		protected virtual void OnDragDrop(DragEventArgs e) => DragDrop?.Invoke(this, e);
		protected virtual void OnDragLeave(EventArgs e) => DragLeave?.Invoke(this, e);
		protected virtual void OnValidating(CancelEventArgs e) => Validating?.Invoke(this, e);
		protected virtual void OnValidated(EventArgs e) => Validated?.Invoke(this, e);

		/// <summary>Scrolls by a wheel step; whether the control used it (else its parent may).</summary>
		internal virtual bool HandleWheel(int delta, Point location) => false;

		// The host's way in (the On... methods are protected, as in Windows Forms).
		internal void RaiseClick() => OnClick(EventArgs.Empty);
		internal void RaiseDoubleClick() => OnDoubleClick(EventArgs.Empty);
		internal void RaiseMouseClick(MouseEventArgs e) => OnMouseClick(e);
		internal void RaiseMouseDoubleClick(MouseEventArgs e) => OnMouseDoubleClick(e);
		internal void RaiseMouseDown(MouseEventArgs e) => OnMouseDown(e);
		internal void RaiseMouseUp(MouseEventArgs e) => OnMouseUp(e);
		internal void RaiseMouseMove(MouseEventArgs e) => OnMouseMove(e);
		internal void RaiseMouseEnter() => OnMouseEnter(EventArgs.Empty);
		internal void RaiseMouseLeave() => OnMouseLeave(EventArgs.Empty);
		internal void RaiseMouseWheel(MouseEventArgs e) => MouseWheel?.Invoke(this, e);
		internal void RaiseKeyDown(KeyEventArgs e) => OnKeyDown(e);
		internal void RaiseKeyUp(KeyEventArgs e) => OnKeyUp(e);
		internal void RaiseKeyPress(KeyPressEventArgs e) => OnKeyPress(e);
		internal void RaiseEnter() => OnEnter(EventArgs.Empty);
		internal void RaiseLeave() => OnLeave(EventArgs.Empty);
		internal void RaiseGotFocus() => OnGotFocus(EventArgs.Empty);
		internal void RaiseLostFocus() => OnLostFocus(EventArgs.Empty);
		internal void RaiseDragEnter(DragEventArgs e) => OnDragEnter(e);
		internal void RaiseDragOver(DragEventArgs e) => OnDragOver(e);
		internal void RaiseDragDrop(DragEventArgs e) => OnDragDrop(e);

		/// <summary>Keys the control handles itself before its parent form sees them (arrows in a list, Enter in a multi-line box).</summary>
		internal virtual bool IsInputKey(Keys keyData) => false;

		/// <summary>The deepest visible control under a point given in this control's coordinates.</summary>
		internal Control HitTest(Point p)
		{
			if (!visible || p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height)
			{
				return null;
			}

			Point offset = ChildOffset;
			Rectangle area = DisplayRectangle;
			if (area.Contains(p))
			{
				foreach (Control child in Controls)
				{
					Control hit = child.HitTest(new Point(p.X - child.left - offset.X, p.Y - child.top - offset.Y));
					if (hit != null)
					{
						return hit;
					}
				}
			}

			return this;
		}

		/// <summary>This control's position in its form's client coordinates.</summary>
		internal Point PositionInForm()
		{
			int x = 0, y = 0;
			Control control = this;
			while (control != null && !(control is Form))
			{
				x += control.left;
				y += control.top;
				if (control.parent != null)
				{
					Point offset = control.parent.ChildOffset;
					x += offset.X;
					y += offset.Y;
				}

				control = control.parent;
			}

			return new Point(x, y);
		}

		public Point PointToClient(Point p)
		{
			Point screen = Host.FormOrigin(FindForm());
			Point own = PositionInForm();
			return new Point(p.X - screen.X - own.X, p.Y - screen.Y - own.Y);
		}

		public Point PointToScreen(Point p)
		{
			Point screen = Host.FormOrigin(FindForm());
			Point own = PositionInForm();
			return new Point(p.X + screen.X + own.X, p.Y + screen.Y + own.Y);
		}

		public Rectangle RectangleToScreen(Rectangle r)
		{
			Point p = PointToScreen(r.Location);
			return new Rectangle(p, r.Size);
		}

		public Graphics CreateGraphics()
		{
			return Graphics.FromImage(new Bitmap(Math.Max(1, width), Math.Max(1, height)));
		}

		// --- threads: as Windows Forms, a control is used on the thread that runs the menu ---

		public bool InvokeRequired => Host.InvokeRequired;

		public object Invoke(Delegate method)
		{
			return Invoke(method, null);
		}

		public object Invoke(Delegate method, params object[] args)
		{
			return Host.Invoke(() => method.DynamicInvoke(args));
		}

		public IAsyncResult BeginInvoke(Delegate method)
		{
			return BeginInvoke(method, null);
		}

		public IAsyncResult BeginInvoke(Delegate method, params object[] args)
		{
			Host.Post(() => method.DynamicInvoke(args));
			return new CompletedResult();
		}

		public object EndInvoke(IAsyncResult result)
		{
			return null;
		}

		private sealed class CompletedResult : IAsyncResult
		{
			public bool IsCompleted => true;

			public WaitHandle AsyncWaitHandle => new ManualResetEvent(true);

			public object AsyncState => null;

			public bool CompletedSynchronously => true;
		}

		public void BeginInit()
		{
		}

		public void EndInit()
		{
		}

		public override string ToString() => GetType().Name + ", Text: " + Text;

		/// <summary>The children of a control, in Windows Forms' order: the first is in front.</summary>
		public class ControlCollection : IList, IEnumerable<Control>
		{
			private readonly List<Control> list = new List<Control>();

			public ControlCollection(Control owner)
			{
				Owner = owner;
			}

			public Control Owner { get; }

			public int Count => list.Count;

			public virtual Control this[int index] => list[index];

			public virtual Control this[string key]
			{
				get
				{
					foreach (Control c in list)
					{
						if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
						{
							return c;
						}
					}

					return null;
				}
			}

			public virtual void Add(Control value)
			{
				if (value == null)
				{
					return;
				}

				if (value.parent != null)
				{
					value.parent.Controls.Remove(value);
				}

				list.Add(value);
				value.SetParentInternal(Owner);
				value.RecordAnchor();
				value.InvalidateTree();
				Owner.OnControlAdded(value);
				Owner.PerformLayout();
				Host.RequestRender();
			}

			public void AddRange(Control[] controls)
			{
				foreach (Control c in controls)
				{
					Add(c);
				}
			}

			public virtual void Remove(Control value)
			{
				if (value == null || !list.Remove(value))
				{
					return;
				}

				if (Host.ContainsFocus(value))
				{
					Host.SetFocus(null);
				}

				value.SetParentInternal(null);
				Owner.OnControlRemoved(value);
				Owner.PerformLayout();
				Host.RequestRender();
			}

			public void RemoveAt(int index)
			{
				Remove(list[index]);
			}

			public virtual void Clear()
			{
				for (int i = list.Count - 1; i >= 0; i--)
				{
					Remove(list[i]);
				}
			}

			public bool Contains(Control control) => list.Contains(control);

			public int IndexOf(Control control) => list.IndexOf(control);

			public int GetChildIndex(Control child) => list.IndexOf(child);

			public void SetChildIndex(Control child, int newIndex)
			{
				if (!list.Remove(child))
				{
					return;
				}

				newIndex = Math.Max(0, Math.Min(newIndex, list.Count));
				list.Insert(newIndex, child);
				Host.RequestRender();
			}

			public Control[] Find(string key, bool searchAllChildren)
			{
				List<Control> found = new List<Control>();
				foreach (Control c in list)
				{
					if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
					{
						found.Add(c);
					}

					if (searchAllChildren)
					{
						found.AddRange(c.Controls.Find(key, true));
					}
				}

				return found.ToArray();
			}

			public IEnumerator<Control> GetEnumerator() => new List<Control>(list).GetEnumerator();

			IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

			int IList.Add(object value)
			{
				Add((Control)value);
				return list.Count - 1;
			}

			bool IList.Contains(object value) => list.Contains(value as Control);

			int IList.IndexOf(object value) => list.IndexOf(value as Control);

			void IList.Insert(int index, object value)
			{
				Add((Control)value);
				SetChildIndex((Control)value, index);
			}

			void IList.Remove(object value) => Remove(value as Control);

			object IList.this[int index]
			{
				get { return list[index]; }
				set { throw new NotSupportedException(); }
			}

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)list).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => list;
		}

		public event ControlEventHandler ControlAdded;
		public event ControlEventHandler ControlRemoved;

		protected virtual void OnControlAdded(Control control) => ControlAdded?.Invoke(this, new ControlEventArgs(control));

		protected virtual void OnControlRemoved(Control control) => ControlRemoved?.Invoke(this, new ControlEventArgs(control));
	}

	public delegate void ControlEventHandler(object sender, ControlEventArgs e);

	public class ControlEventArgs : EventArgs
	{
		public ControlEventArgs(Control control)
		{
			Control = control;
		}

		public Control Control { get; }
	}

}
