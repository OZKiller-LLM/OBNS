// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): combo boxes (and their drop-down lists), list
// views in Windows' details view with their image lists, and the data grid of the package pages.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Reflection;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	/// <summary>A vertical scroll bar drawn inside a list, and what it does with the mouse.</summary>
	internal sealed class ListScroller
	{
		internal const int Width = 17;
		internal int Top;
		internal bool Dragging;

		internal static void Paint(Graphics g, Rectangle track, int first, int visible, int total)
		{
			g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)), track);
			if (total <= visible)
			{
				return;
			}

			int thumb = Math.Max(16, track.Height * visible / total);
			int y = track.Y + (track.Height - thumb) * first / Math.Max(1, total - visible);
			g.FillRectangle(new SolidBrush(Color.FromArgb(193, 193, 193)), track.X + 3, y + 1, track.Width - 6, thumb - 2);
		}

		/// <summary>The first row shown when the bar is dragged to a height in the track.</summary>
		internal static int FirstAt(Rectangle track, int y, int visible, int total)
		{
			int max = Math.Max(0, total - visible);
			return (int)Math.Round(max * Math.Max(0.0, Math.Min(1.0, (y - track.Y) / (double)Math.Max(1, track.Height))));
		}
	}

	public class ComboBox : Control
	{
		private int selectedIndex = -1;
		private ComboBoxStyle style = ComboBoxStyle.DropDown;
		private object dataSource;

		public ComboBox()
		{
			Items = new ObjectCollection(this);
		}

		protected override Size DefaultSize => new Size(121, 21);

		protected override Color DefaultBackColor => Color.White;

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public ObjectCollection Items { get; }

		public ComboBoxStyle DropDownStyle
		{
			get { return style; }
			set
			{
				style = value;
				Invalidate();
			}
		}

		public DrawMode DrawMode { get; set; }

		public int ItemHeight { get; set; } = 15;

		public int MaxDropDownItems { get; set; } = 8;

		public int DropDownHeight { get; set; } = 106;

		public int DropDownWidth { get; set; }

		public bool FormattingEnabled { get; set; }

		public bool IntegralHeight { get; set; } = true;

		public bool Sorted { get; set; }

		public FlatStyle FlatStyle { get; set; } = FlatStyle.Standard;

		public AutoCompleteMode AutoCompleteMode { get; set; }

		public AutoCompleteSource AutoCompleteSource { get; set; }

		public bool DroppedDown
		{
			get { return Host.ActivePopup is ComboPopup popup && popup.Owner == this; }
			set
			{
				if (value && !DroppedDown)
				{
					OpenList();
				}
				else if (!value && DroppedDown)
				{
					Host.ClosePopup();
				}
			}
		}

		public string DisplayMember { get; set; } = string.Empty;

		public string ValueMember { get; set; } = string.Empty;

		/// <summary>The list the items come from: they are taken from it when it is set.</summary>
		public object DataSource
		{
			get { return dataSource; }
			set
			{
				dataSource = value;
				Items.ClearInternal();
				if (value is IEnumerable list)
				{
					foreach (object item in list)
					{
						Items.AddInternal(item);
					}
				}

				selectedIndex = -1;
				SelectedIndex = Items.Count > 0 ? 0 : -1;
			}
		}

		public event EventHandler SelectedIndexChanged;
		public event EventHandler SelectedValueChanged;
		public event EventHandler SelectionChangeCommitted;
		public event EventHandler DropDown;
		public event EventHandler DropDownClosed;
		public event DrawItemEventHandler DrawItem;

		protected virtual void OnSelectedIndexChanged(EventArgs e)
		{
			SelectedIndexChanged?.Invoke(this, e);
			SelectedValueChanged?.Invoke(this, e);
		}

		protected virtual void OnSelectionChangeCommitted(EventArgs e) => SelectionChangeCommitted?.Invoke(this, e);

		protected virtual void OnDrawItem(DrawItemEventArgs e) => DrawItem?.Invoke(this, e);

		public int SelectedIndex
		{
			get { return selectedIndex; }
			set
			{
				if (value < -1 || value >= Items.Count)
				{
					throw new ArgumentOutOfRangeException(nameof(value), "InvalidArgument=Value of '" + value + "' is not valid for 'SelectedIndex'.");
				}

				if (value == selectedIndex)
				{
					return;
				}

				selectedIndex = value;
				if (style != ComboBoxStyle.DropDownList)
				{
					typed = null;
				}

				Invalidate();
				OnSelectedIndexChanged(EventArgs.Empty);
				OnTextChanged(EventArgs.Empty);
			}
		}

		public object SelectedItem
		{
			get { return selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null; }
			set { SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
		}

		public object SelectedValue
		{
			get { return Member(SelectedItem, ValueMember); }
			set
			{
				for (int i = 0; i < Items.Count; i++)
				{
					if (Equals(Member(Items[i], ValueMember), value))
					{
						SelectedIndex = i;
						return;
					}
				}
			}
		}

		private string typed;

		public override string Text
		{
			get { return typed ?? (SelectedItem != null ? GetItemText(SelectedItem) : string.Empty); }
			set
			{
				int index = FindStringExact(value);
				if (index >= 0)
				{
					typed = null;
					SelectedIndex = index;
				}
				else if (style != ComboBoxStyle.DropDownList)
				{
					typed = value ?? string.Empty;
					selectedIndex = -1;
					Invalidate();
					OnTextChanged(EventArgs.Empty);
				}
			}
		}

		private static object Member(object item, string member)
		{
			if (item == null || string.IsNullOrEmpty(member))
			{
				return item;
			}

			PropertyInfo property = item.GetType().GetProperty(member);
			if (property != null)
			{
				return property.GetValue(item, null);
			}

			FieldInfo field = item.GetType().GetField(member);
			return field != null ? field.GetValue(item) : item;
		}

		public string GetItemText(object item)
		{
			object shown = Member(item, DisplayMember);
			return shown?.ToString() ?? string.Empty;
		}

		public int FindStringExact(string s)
		{
			for (int i = 0; i < Items.Count; i++)
			{
				if (string.Equals(GetItemText(Items[i]), s, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		public int FindString(string s)
		{
			for (int i = 0; i < Items.Count; i++)
			{
				if (GetItemText(Items[i]).StartsWith(s ?? string.Empty, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		public void BeginUpdate()
		{
		}

		public void EndUpdate()
		{
			Invalidate();
		}

		public void SelectAll()
		{
		}

		internal void ItemsChanged()
		{
			if (selectedIndex >= Items.Count)
			{
				selectedIndex = Items.Count - 1;
			}

			Invalidate();
		}

		/// <summary>Draws an item, the owner's way when it draws them.</summary>
		internal void PaintItem(Graphics g, int index, Rectangle bounds, bool selected, bool edit)
		{
			Color back = selected ? HighlightColor : Color.White;
			Color fore = selected ? Color.White : TextColor;
			if (DrawMode != DrawMode.Normal && DrawItem != null)
			{
				DrawItemState state = (selected ? DrawItemState.Selected | DrawItemState.Focus : DrawItemState.None) | (edit ? DrawItemState.ComboBoxEdit : DrawItemState.None) | (Enabled ? DrawItemState.None : DrawItemState.Disabled);
				OnDrawItem(new DrawItemEventArgs(g, Font, bounds, index, state, fore, back));
				return;
			}

			using (SolidBrush brush = new SolidBrush(back))
			{
				g.FillRectangle(brush, bounds);
			}

			if (index >= 0 && index < Items.Count)
			{
				DrawText(g, GetItemText(Items[index]), Font, fore, new RectangleF(bounds.X + 2, bounds.Y, bounds.Width - 4, bounds.Height), ContentAlignment.MiddleLeft, false, true);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			bool list = style == ComboBoxStyle.DropDownList;
			Color fill = !Enabled ? Color.FromArgb(240, 240, 240) : list ? (IsMouseOver || DroppedDown ? Color.FromArgb(229, 241, 251) : Color.FromArgb(240, 240, 240)) : Color.White;
			g.FillRectangle(new SolidBrush(fill), ClientRectangle);
			Rectangle content = new Rectangle(2, 2, Width - 21, Height - 4);
			if (typed != null || selectedIndex < 0 || !list && Focused)
			{
				string shown = Text;
				bool editing = !list && Focused && Enabled;
				if (editing && selectAll && shown.Length > 0)
				{
					SizeF size = MeasureText(shown, Font);
					g.FillRectangle(new SolidBrush(HighlightColor), content.X + 1, content.Y + 1, Math.Min(content.Width, size.Width + 1), content.Height - 2);
				}

				// A long path shows its end, where typing happens.
				float width = MeasureText(shown, Font).Width;
				float offset = editing && width > content.Width - 4 ? width - content.Width + 4 : 0;
				Region clip = g.Clip;
				g.SetClip(content);
				DrawText(g, shown, Font, editing && selectAll ? Color.White : TextColor, new RectangleF(content.X + 1 - offset, content.Y, content.Width + offset, content.Height), ContentAlignment.MiddleLeft, false, !editing);
				if (editing && !selectAll)
				{
					float x = content.X + 1 - offset + width;
					g.DrawLine(Pens.Black, x, content.Y + 2, x, content.Bottom - 2);
				}

				g.Clip = clip;
			}
			else
			{
				bool focused = Focused && list && !DroppedDown;
				if (DrawMode != DrawMode.Normal && DrawItem != null)
				{
					PaintItem(g, selectedIndex, content, focused, true);
				}
				else
				{
					if (focused)
					{
						g.FillRectangle(new SolidBrush(HighlightColor), content);
					}

					DrawText(g, GetItemText(SelectedItem), Font, focused ? Color.White : TextColor, new RectangleF(content.X + 1, content.Y, content.Width, content.Height), ContentAlignment.MiddleLeft, false, true);
				}
			}

			float cx = Width - 10, cy = Height / 2f;
			using (Pen pen = new Pen(Enabled ? Color.FromArgb(96, 96, 96) : Color.LightGray, 1.2f))
			{
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
				g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
				g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
			}

			DrawBorder(g, ClientRectangle, !Enabled ? Color.FromArgb(204, 204, 204) : Focused || DroppedDown ? HighlightColor : IsMouseOver ? Color.FromArgb(23, 23, 23) : Color.FromArgb(122, 122, 122));
			base.OnPaint(e);
		}

		private void OpenList()
		{
			if (Items.Count == 0)
			{
				return;
			}

			DropDown?.Invoke(this, EventArgs.Empty);
			Form form = FindForm();
			Point origin = Host.FormOrigin(form);
			Point own = PositionInForm();
			int rows = Math.Min(Items.Count, Math.Max(1, MaxDropDownItems));
			int rowHeight = Math.Max(ItemHeight, (int)Math.Ceiling(MeasureText(string.Empty, Font).Height) + 3);
			ComboPopup popup = new ComboPopup(this, rowHeight)
			{
				Location = new Point(origin.X + own.X, origin.Y + own.Y + Height),
				Size = new Size(Math.Max(Width, DropDownWidth), rows * rowHeight + 2),
				FlipTop = origin.Y + own.Y
			};
			popup.Closed += (s, e) => DropDownClosed?.Invoke(this, EventArgs.Empty);
			Host.ShowPopup(popup);
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button != MouseButtons.Left || !Enabled)
			{
				return;
			}

			// An editable box takes typing where it is clicked; its arrow, and a list box anywhere, open the list.
			if (style != ComboBoxStyle.DropDownList && e.X < Width - 20)
			{
				selectAll = false;
				Invalidate();
				return;
			}

			OpenList();
		}

		/// <summary>Whether the next key typed replaces the whole text (an editable box just entered by Tab, or after Ctrl+A).</summary>
		private bool selectAll;

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			selectAll = style != ComboBoxStyle.DropDownList && Host.MouseButtonsDown == MouseButtons.None;
			Invalidate();
		}

		protected override void OnLostFocus(EventArgs e)
		{
			selectAll = false;
			base.OnLostFocus(e);
			Invalidate();
		}

		/// <summary>Typing in an editable box: the text changes, the selection is left.</summary>
		private void Edit(string value)
		{
			typed = value;
			selectedIndex = -1;
			selectAll = false;
			Invalidate();
			OnTextChanged(EventArgs.Empty);
		}

		/// <summary>The user's choice: the selection changes and SelectionChangeCommitted follows, as with the mouse in Windows.</summary>
		internal void Choose(int index)
		{
			if (index < 0 || index >= Items.Count)
			{
				return;
			}

			bool changed = index != selectedIndex || typed != null;
			typed = null;
			SelectedIndex = index;
			if (changed)
			{
				OnSelectionChangeCommitted(EventArgs.Empty);
			}

			Invalidate();
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.F4 ||
			       (style == ComboBoxStyle.DropDownList ? key == Keys.Home || key == Keys.End : key == Keys.Back || key == Keys.Delete);
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled)
			{
				return;
			}

			if (style != ComboBoxStyle.DropDownList)
			{
				string text = Text;
				switch (e.KeyCode)
				{
					case Keys.Back:
						Edit(selectAll ? string.Empty : text.Length > 0 ? text.Substring(0, text.Length - 1) : text);
						e.Handled = true;
						return;
					case Keys.Delete:
						if (selectAll)
						{
							Edit(string.Empty);
						}

						e.Handled = true;
						return;
					case Keys.A:
						if (e.Control)
						{
							selectAll = true;
							Invalidate();
							e.SuppressKeyPress = true;
						}

						return;
					case Keys.C:
						if (e.Control)
						{
							TextBoxBase.Clipboard = text;
							e.SuppressKeyPress = true;
						}

						return;
					case Keys.V:
						if (e.Control)
						{
							Edit((selectAll ? string.Empty : text) + TextBoxBase.Clipboard);
							e.Handled = true;
							e.SuppressKeyPress = true;
						}

						return;
				}
			}

			if (Items.Count == 0)
			{
				return;
			}

			switch (e.KeyCode)
			{
				case Keys.Down:
					if (e.Alt)
					{
						OpenList();
					}
					else
					{
						Choose(Math.Min(Items.Count - 1, selectedIndex + 1));
					}

					break;
				case Keys.Up:
					Choose(Math.Max(0, selectedIndex - 1));
					break;
				case Keys.Home:
					Choose(0);
					break;
				case Keys.End:
					Choose(Items.Count - 1);
					break;
				case Keys.PageDown:
					Choose(Math.Min(Items.Count - 1, selectedIndex + MaxDropDownItems));
					break;
				case Keys.PageUp:
					Choose(Math.Max(0, selectedIndex - MaxDropDownItems));
					break;
				case Keys.F4:
					OpenList();
					break;
				default:
					return;
			}

			e.Handled = true;
		}

		protected override void OnKeyPress(KeyPressEventArgs e)
		{
			base.OnKeyPress(e);
			if (e.Handled)
			{
				return;
			}

			if (style != ComboBoxStyle.DropDownList)
			{
				Edit((selectAll ? string.Empty : Text) + e.KeyChar);
				e.Handled = true;
				return;
			}

			if (Items.Count == 0)
			{
				return;
			}

			// A letter jumps to the next item starting with it.
			string letter = e.KeyChar.ToString();
			for (int step = 1; step <= Items.Count; step++)
			{
				int i = (Math.Max(0, selectedIndex) + step) % Items.Count;
				if (GetItemText(Items[i]).StartsWith(letter, StringComparison.CurrentCultureIgnoreCase))
				{
					Choose(i);
					break;
				}
			}

			e.Handled = true;
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			if (!Focused || Items.Count == 0)
			{
				return false;
			}

			Choose(Math.Max(0, Math.Min(Items.Count - 1, selectedIndex + (delta > 0 ? -1 : 1))));
			return true;
		}

		public class ObjectCollection : IList
		{
			private readonly ComboBox owner;
			private readonly List<object> items = new List<object>();

			public ObjectCollection(ComboBox owner)
			{
				this.owner = owner;
			}

			public int Count => items.Count;

			public object this[int index]
			{
				get { return items[index]; }
				set
				{
					items[index] = value;
					owner.ItemsChanged();
				}
			}

			internal void AddInternal(object item) => items.Add(item);

			internal void ClearInternal() => items.Clear();

			public int Add(object item)
			{
				if (owner.Sorted)
				{
					string text = owner.GetItemText(item);
					int at = items.FindIndex(i => string.Compare(owner.GetItemText(i), text, StringComparison.CurrentCultureIgnoreCase) > 0);
					if (at >= 0)
					{
						items.Insert(at, item);
						owner.ItemsChanged();
						return at;
					}
				}

				items.Add(item);
				owner.ItemsChanged();
				return items.Count - 1;
			}

			public void AddRange(object[] range)
			{
				foreach (object item in range)
				{
					Add(item);
				}
			}

			public void Insert(int index, object item)
			{
				items.Insert(index, item);
				if (owner.selectedIndex >= index)
				{
					owner.selectedIndex++;
				}

				owner.ItemsChanged();
			}

			public void Remove(object item)
			{
				int index = items.IndexOf(item);
				if (index >= 0)
				{
					RemoveAt(index);
				}
			}

			public void RemoveAt(int index)
			{
				items.RemoveAt(index);
				if (owner.selectedIndex == index)
				{
					owner.selectedIndex = -1;
					owner.OnSelectedIndexChanged(EventArgs.Empty);
				}
				else if (owner.selectedIndex > index)
				{
					owner.selectedIndex--;
				}

				owner.ItemsChanged();
			}

			public void Clear()
			{
				items.Clear();
				if (owner.selectedIndex != -1)
				{
					owner.selectedIndex = -1;
					owner.OnSelectedIndexChanged(EventArgs.Empty);
				}

				owner.ItemsChanged();
			}

			public bool Contains(object item) => items.Contains(item);

			public int IndexOf(object item) => items.IndexOf(item);

			public IEnumerator GetEnumerator() => items.ToArray().GetEnumerator();

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);

			public void CopyTo(object[] destination, int arrayIndex) => items.CopyTo(destination, arrayIndex);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => items;
		}
	}

	/// <summary>A combo box's open list.</summary>
	internal sealed class ComboPopup : Popup
	{
		internal readonly ComboBox Owner;
		private readonly int rowHeight;
		private int first;
		private int hot;

		internal ComboPopup(ComboBox owner, int rowHeight)
		{
			Owner = owner;
			this.rowHeight = rowHeight;
			hot = owner.SelectedIndex;
			int visible = Math.Max(1, owner.MaxDropDownItems);
			first = Math.Max(0, Math.Min(owner.Items.Count - visible, hot - visible / 2));
		}

		private int Visible => Math.Max(1, (Height - 2) / rowHeight);

		private bool Scrolls => Owner.Items.Count > Visible;

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			g.FillRectangle(Brushes.White, ClientRectangle);
			int width = Width - 2 - (Scrolls ? ListScroller.Width : 0);
			for (int i = 0; i < Visible && first + i < Owner.Items.Count; i++)
			{
				Rectangle row = new Rectangle(1, 1 + i * rowHeight, width, rowHeight);
				Owner.PaintItem(g, first + i, row, first + i == hot, false);
			}

			if (Scrolls)
			{
				ListScroller.Paint(g, new Rectangle(Width - 1 - ListScroller.Width, 1, ListScroller.Width, Height - 2), first, Visible, Owner.Items.Count);
			}

			DrawBorder(g, ClientRectangle, Color.FromArgb(100, 100, 100));
		}

		private bool dragging;

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (Scrolls && e.X >= Width - 1 - ListScroller.Width)
			{
				dragging = true;
				Scroll(e.Y);
			}
		}

		private void Scroll(int y)
		{
			first = ListScroller.FirstAt(new Rectangle(0, 1, ListScroller.Width, Height - 2), y, Visible, Owner.Items.Count);
			Invalidate();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			if (dragging)
			{
				if ((e.Button & MouseButtons.Left) != 0)
				{
					Scroll(e.Y);
				}

				return;
			}

			if (Scrolls && e.X >= Width - 1 - ListScroller.Width)
			{
				return;
			}

			int item = first + Math.Max(0, (e.Y - 1) / rowHeight);
			if (item != hot && item < Owner.Items.Count)
			{
				hot = item;
				Invalidate();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			if (dragging)
			{
				dragging = false;
				return;
			}

			if (e.X >= 0 && e.X < Width - (Scrolls ? ListScroller.Width : 0) && e.Y >= 0 && e.Y < Height && hot >= 0)
			{
				int chosen = hot;
				Host.ClosePopup();
				Owner.Choose(chosen);
			}
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			first = Math.Max(0, Math.Min(Owner.Items.Count - Visible, first - delta / 120 * 3));
			Invalidate();
			return true;
		}

		internal override bool HandleKey(KeyEventArgs e)
		{
			switch (e.KeyCode)
			{
				case Keys.Escape:
					Host.ClosePopup();
					return true;
				case Keys.Down:
					hot = Math.Min(Owner.Items.Count - 1, hot + 1);
					break;
				case Keys.Up:
					hot = Math.Max(0, hot - 1);
					break;
				case Keys.PageDown:
					hot = Math.Min(Owner.Items.Count - 1, hot + Visible);
					break;
				case Keys.PageUp:
					hot = Math.Max(0, hot - Visible);
					break;
				case Keys.Return:
				case Keys.Tab:
				case Keys.F4:
				{
					int chosen = hot;
					Host.ClosePopup();
					Owner.Choose(chosen);
					return true;
				}

				default:
					return false;
			}

			if (hot < first)
			{
				first = hot;
			}
			else if (hot >= first + Visible)
			{
				first = hot - Visible + 1;
			}

			Invalidate();
			return true;
		}
	}

	// --- list views ---

	public class ImageList : Component
	{
		public ImageList()
		{
			Images = new ImageCollection();
		}

		public ImageList(IContainer container) : this()
		{
			container?.Add(this);
		}

		public ImageCollection Images { get; }

		public Size ImageSize { get; set; } = new Size(16, 16);

		public Color TransparentColor { get; set; } = Color.Transparent;

		public ColorDepth ColorDepth { get; set; } = ColorDepth.Depth8Bit;

		public object Tag { get; set; }

		public sealed class ImageCollection : IEnumerable
		{
			private readonly List<KeyValuePair<string, Image>> images = new List<KeyValuePair<string, Image>>();

			public int Count => images.Count;

			public bool Empty => images.Count == 0;

			public Image this[int index]
			{
				get { return images[index].Value; }
				set { images[index] = new KeyValuePair<string, Image>(images[index].Key, value); }
			}

			public Image this[string key]
			{
				get
				{
					int index = IndexOfKey(key);
					return index >= 0 ? images[index].Value : null;
				}
			}

			public StringCollectionView Keys => new StringCollectionView(images.Select(i => i.Key ?? string.Empty).ToList());

			public void Add(Image value) => images.Add(new KeyValuePair<string, Image>(string.Empty, value));

			public void Add(string key, Image image) => images.Add(new KeyValuePair<string, Image>(key, image));

			public void Add(string key, Icon icon) => images.Add(new KeyValuePair<string, Image>(key, icon.ToBitmap()));

			public void Add(Icon icon) => images.Add(new KeyValuePair<string, Image>(string.Empty, icon.ToBitmap()));

			public void AddRange(Image[] range)
			{
				foreach (Image image in range)
				{
					Add(image);
				}
			}

			public bool ContainsKey(string key) => IndexOfKey(key) >= 0;

			public int IndexOfKey(string key) => images.FindIndex(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

			public void SetKeyName(int index, string name) => images[index] = new KeyValuePair<string, Image>(name, images[index].Value);

			public void Clear() => images.Clear();

			public void RemoveByKey(string key)
			{
				int index = IndexOfKey(key);
				if (index >= 0)
				{
					images.RemoveAt(index);
				}
			}

			public void RemoveAt(int index) => images.RemoveAt(index);

			public IEnumerator GetEnumerator() => images.Select(i => i.Value).GetEnumerator();
		}
	}

	public sealed class StringCollectionView : IEnumerable<string>
	{
		private readonly List<string> list;

		internal StringCollectionView(List<string> list)
		{
			this.list = list;
		}

		public int Count => list.Count;

		public string this[int index] => list[index];

		public bool Contains(string value) => list.Contains(value);

		public IEnumerator<string> GetEnumerator() => list.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public enum ColorDepth { Depth4Bit = 4, Depth8Bit = 8, Depth16Bit = 16, Depth24Bit = 24, Depth32Bit = 32 }

	public class ColumnHeader : Component, ICloneable
	{
		private int width = 60;

		public ColumnHeader()
		{
		}

		public ColumnHeader(string imageKey)
		{
			ImageKey = imageKey;
		}

		public string Text { get; set; } = string.Empty;

		public string Name { get; set; } = string.Empty;

		public int Width
		{
			get { return width; }
			set
			{
				width = value;
				ListView?.ColumnsChanged();
			}
		}

		public HorizontalAlignment TextAlign { get; set; }

		public string ImageKey { get; set; }

		public int ImageIndex { get; set; } = -1;

		public int DisplayIndex { get; set; }

		public object Tag { get; set; }

		public ListView ListView { get; internal set; }

		public int Index => ListView?.Columns.IndexOf(this) ?? -1;

		public void AutoResize(ColumnHeaderAutoResizeStyle headerAutoResize) => ListView?.AutoResizeColumn(Index, headerAutoResize);

		public object Clone() => new ColumnHeader { Text = Text, Name = Name, width = width, TextAlign = TextAlign };
	}

	public class ListViewItem : ICloneable
	{
		private bool selected;

		public ListViewItem()
		{
			SubItems = new ListViewSubItemCollection(this);
			SubItems.Add(string.Empty);
		}

		public ListViewItem(string text) : this()
		{
			Text = text;
		}

		public ListViewItem(string text, int imageIndex) : this(text)
		{
			ImageIndex = imageIndex;
		}

		public ListViewItem(string text, string imageKey) : this(text)
		{
			ImageKey = imageKey;
		}

		public ListViewItem(string[] items) : this()
		{
			if (items != null && items.Length > 0)
			{
				Text = items[0];
				for (int i = 1; i < items.Length; i++)
				{
					SubItems.Add(items[i]);
				}
			}
		}

		public ListViewItem(string[] items, int imageIndex) : this(items)
		{
			ImageIndex = imageIndex;
		}

		public ListViewItem(string[] items, string imageKey) : this(items)
		{
			ImageKey = imageKey;
		}

		public string Text
		{
			get { return SubItems[0].Text; }
			set
			{
				SubItems[0].Text = value;
				ListView?.Invalidate();
			}
		}

		public string Name { get; set; } = string.Empty;

		public ListViewSubItemCollection SubItems { get; }

		public string ImageKey { get; set; }

		public int ImageIndex { get; set; } = -1;

		public object Tag { get; set; }

		public Color ForeColor { get; set; } = Color.Empty;

		public Color BackColor { get; set; } = Color.Empty;

		public Font Font { get; set; }

		public bool Checked { get; set; }

		public bool UseItemStyleForSubItems { get; set; } = true;

		public string ToolTipText { get; set; }

		public ListViewGroup Group { get; set; }

		public ListView ListView { get; internal set; }

		public int Index => ListView?.Items.IndexOf(this) ?? -1;

		public bool Selected
		{
			get { return selected; }
			set
			{
				if (selected == value)
				{
					return;
				}

				selected = value;
				ListView?.SelectionChangedByItem(this);
			}
		}

		internal void SetSelectedSilently(bool value) => selected = value;

		public bool Focused
		{
			get { return ListView != null && ListView.FocusedItem == this; }
			set
			{
				if (ListView != null && value)
				{
					ListView.FocusedItem = this;
				}
			}
		}

		public void EnsureVisible() => ListView?.EnsureVisible(Index);

		public void Remove() => ListView?.Items.Remove(this);

		public object Clone()
		{
			ListViewItem copy = new ListViewItem(SubItems.Cast<ListViewSubItem>().Select(s => s.Text).ToArray()) { ImageKey = ImageKey, ImageIndex = ImageIndex, Tag = Tag, ForeColor = ForeColor, BackColor = BackColor };
			return copy;
		}

		public override string ToString() => "ListViewItem: {" + Text + "}";

		public class ListViewSubItem
		{
			private string text = string.Empty;

			public ListViewSubItem()
			{
			}

			public ListViewSubItem(ListViewItem owner, string text)
			{
				Text = text;
			}

			public string Text
			{
				get { return text; }
				set { text = value ?? string.Empty; }
			}

			public string Name { get; set; } = string.Empty;

			public Color ForeColor { get; set; } = Color.Empty;

			public Color BackColor { get; set; } = Color.Empty;

			public Font Font { get; set; }

			public object Tag { get; set; }
		}

		public class ListViewSubItemCollection : IList
		{
			private readonly ListViewItem owner;
			private readonly List<ListViewSubItem> items = new List<ListViewSubItem>();

			internal ListViewSubItemCollection(ListViewItem owner)
			{
				this.owner = owner;
			}

			public int Count => items.Count;

			public ListViewSubItem this[int index]
			{
				get
				{
					// Windows gives empty sub-items up to the columns' count.
					while (index >= items.Count && index < 256)
					{
						items.Add(new ListViewSubItem());
					}

					return items[index];
				}
				set { items[index] = value; }
			}

			public ListViewSubItem Add(string text)
			{
				ListViewSubItem item = new ListViewSubItem(owner, text);
				items.Add(item);
				owner.ListView?.Invalidate();
				return item;
			}

			public ListViewSubItem Add(ListViewSubItem item)
			{
				items.Add(item);
				owner.ListView?.Invalidate();
				return item;
			}

			public void AddRange(string[] texts)
			{
				foreach (string text in texts)
				{
					Add(text);
				}
			}

			public void Clear() => items.Clear();

			public void RemoveAt(int index) => items.RemoveAt(index);

			public IEnumerator GetEnumerator() => items.GetEnumerator();

			int IList.Add(object value)
			{
				items.Add((ListViewSubItem)value);
				return items.Count - 1;
			}

			bool IList.Contains(object value) => items.Contains(value as ListViewSubItem);

			int IList.IndexOf(object value) => items.IndexOf(value as ListViewSubItem);

			void IList.Insert(int index, object value) => items.Insert(index, (ListViewSubItem)value);

			void IList.Remove(object value) => items.Remove(value as ListViewSubItem);

			object IList.this[int index]
			{
				get { return this[index]; }
				set { items[index] = (ListViewSubItem)value; }
			}

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => items;
		}
	}

	public class ListViewGroup
	{
		public string Header { get; set; }

		public string Name { get; set; }
	}

	public class ListView : Control
	{
		private int first;
		private ListViewItem focusedItem;
		private bool updating;

		public ListView()
		{
			Columns = new ColumnHeaderCollection(this);
			Items = new ListViewItemCollection(this);
			SelectedItems = new SelectedListViewItemCollection(this);
			SelectedIndices = new SelectedIndexCollection(this);
		}

		protected override Size DefaultSize => new Size(121, 97);

		protected override Color DefaultBackColor => Color.White;

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public ColumnHeaderCollection Columns { get; }

		public ListViewItemCollection Items { get; }

		public SelectedListViewItemCollection SelectedItems { get; }

		public SelectedIndexCollection SelectedIndices { get; }

		public ImageList SmallImageList { get; set; }

		public ImageList LargeImageList { get; set; }

		public ImageList StateImageList { get; set; }

		public View View { get; set; } = View.LargeIcon;

		public bool MultiSelect { get; set; } = true;

		public bool FullRowSelect { get; set; }

		public bool GridLines { get; set; }

		public bool HideSelection { get; set; } = true;

		public bool LabelWrap { get; set; } = true;

		public bool LabelEdit { get; set; }

		public bool ShowGroups { get; set; } = true;

		public bool ShowItemToolTips { get; set; }

		public bool CheckBoxes { get; set; }

		public bool Scrollable { get; set; } = true;

		public bool HoverSelection { get; set; }

		public bool UseCompatibleStateImageBehavior { get; set; } = true;

		public SortOrder Sorting { get; set; }

		public ColumnHeaderStyle HeaderStyle { get; set; } = ColumnHeaderStyle.Clickable;

		public BorderStyle BorderStyle { get; set; } = BorderStyle.Fixed3D;

		public ListViewGroupCollection Groups { get; } = new ListViewGroupCollection();

		public IComparer ListViewItemSorter { get; set; }

		public event EventHandler SelectedIndexChanged;
		public event EventHandler ItemActivate;
		public event ColumnClickEventHandler ColumnClick;
		public event ListViewItemSelectionChangedEventHandler ItemSelectionChanged;

		protected virtual void OnSelectedIndexChanged(EventArgs e) => SelectedIndexChanged?.Invoke(this, e);

		public ListViewItem FocusedItem
		{
			get { return focusedItem; }
			set
			{
				focusedItem = value;
				Invalidate();
			}
		}

		public ListViewItem TopItem
		{
			get { return first < Items.Count ? Items[first] : null; }
			set
			{
				int index = Items.IndexOf(value);
				if (index >= 0)
				{
					first = index;
					Invalidate();
				}
			}
		}

		public void BeginUpdate()
		{
			updating = true;
		}

		public void EndUpdate()
		{
			updating = false;
			ClampScroll();
			Invalidate();
		}

		public void Clear()
		{
			Items.Clear();
			Columns.Clear();
		}

		public void Sort()
		{
		}

		internal bool Updating => updating;

		internal void ColumnsChanged() => Invalidate();

		internal void SelectionChangedByItem(ListViewItem item)
		{
			if (item.Selected && !MultiSelect)
			{
				foreach (ListViewItem other in Items)
				{
					if (other != item && other.Selected)
					{
						other.SetSelectedSilently(false);
					}
				}
			}

			Invalidate();
			ItemSelectionChanged?.Invoke(this, new ListViewItemSelectionChangedEventArgs(item, item.Index, item.Selected));
			OnSelectedIndexChanged(EventArgs.Empty);
		}

		// --- geometry ---

		private int HeaderHeight => View == View.Details && HeaderStyle != ColumnHeaderStyle.None ? 24 : 0;

		internal int RowHeight
		{
			get
			{
				int text = (int)Math.Ceiling(MeasureText(string.Empty, Font).Height) + 4;
				int image = SmallImageList != null ? SmallImageList.ImageSize.Height + 2 : 0;
				return Math.Max(17, Math.Max(text, image));
			}
		}

		private int Inset => BorderStyle == BorderStyle.None ? 0 : 1;

		private int VisibleRows => Math.Max(1, (Height - 2 * Inset - HeaderHeight - (HScrolls ? ListScroller.Width : 0)) / RowHeight);

		private bool Scrolls => Items.Count > VisibleRows;

		/// <summary>The columns' widths added up, as set (a width to fit counts as its header's).</summary>
		private int ColumnsTotal
		{
			get
			{
				if (View != View.Details)
				{
					return 0;
				}

				int total = 0;
				for (int i = 0; i < Columns.Count; i++)
				{
					total += Columns[i].Width > 0 ? Columns[i].Width : HeaderWidth(i);
				}

				return total;
			}
		}

		/// <summary>Whether the columns are wider than the list, which then scrolls sideways as in Windows.</summary>
		private bool HScrolls
		{
			get
			{
				bool vertical = Items.Count * RowHeight > Height - 2 * Inset - HeaderHeight;
				return ColumnsTotal > Width - 2 * Inset - (vertical ? ListScroller.Width : 0);
			}
		}

		private int scrollX;

		private int MaxScrollX => Math.Max(0, ColumnsTotal - RowsArea.Width);

		private Rectangle RowsArea => new Rectangle(Inset, Inset + HeaderHeight, Width - 2 * Inset - (Scrolls ? ListScroller.Width : 0), Height - 2 * Inset - HeaderHeight - (HScrolls ? ListScroller.Width : 0));

		private Rectangle Track => new Rectangle(Width - Inset - ListScroller.Width, Inset + HeaderHeight, ListScroller.Width, Height - 2 * Inset - HeaderHeight - (HScrolls ? ListScroller.Width : 0));

		private Rectangle HTrack => new Rectangle(Inset, Height - Inset - ListScroller.Width, Width - 2 * Inset - (Scrolls ? ListScroller.Width : 0), ListScroller.Width);

		private void ClampScroll()
		{
			first = Math.Max(0, Math.Min(first, Items.Count - VisibleRows));
		}

		public void EnsureVisible(int index)
		{
			if (index < 0)
			{
				return;
			}

			if (index < first)
			{
				first = index;
			}
			else if (index >= first + VisibleRows)
			{
				first = index - VisibleRows + 1;
			}

			ClampScroll();
			Invalidate();
		}

		/// <summary>The columns' widths, as shown: in a view without columns, one as wide as the list.</summary>
		private int[] ColumnWidths()
		{
			if (View != View.Details || Columns.Count == 0)
			{
				return new[] { RowsArea.Width };
			}

			int[] widths = new int[Columns.Count];
			for (int i = 0; i < Columns.Count; i++)
			{
				int w = Columns[i].Width;
				if (w == -1)
				{
					w = ContentWidth(i);
				}
				else if (w == -2)
				{
					w = i == Columns.Count - 1 ? Math.Max(HeaderWidth(i), RowsArea.Width - widths.Take(i).Sum()) : HeaderWidth(i);
				}

				widths[i] = Math.Max(0, w);
			}

			return widths;
		}

		private int HeaderWidth(int column) => (int)Math.Ceiling(MeasureText(Columns[column].Text, Font).Width) + 14;

		private int ContentWidth(int column)
		{
			int width = 0;
			int image = column == 0 && SmallImageList != null ? SmallImageList.ImageSize.Width + 4 : 0;
			foreach (ListViewItem item in Items)
			{
				width = Math.Max(width, (int)Math.Ceiling(MeasureText(item.SubItems[column].Text, Font).Width) + image + 12);
			}

			return width;
		}

		public void AutoResizeColumns(ColumnHeaderAutoResizeStyle headerAutoResize)
		{
			for (int i = 0; i < Columns.Count; i++)
			{
				AutoResizeColumn(i, headerAutoResize);
			}
		}

		public void AutoResizeColumn(int columnIndex, ColumnHeaderAutoResizeStyle headerAutoResize)
		{
			if (columnIndex < 0 || columnIndex >= Columns.Count || headerAutoResize == ColumnHeaderAutoResizeStyle.None)
			{
				return;
			}

			int width = headerAutoResize == ColumnHeaderAutoResizeStyle.HeaderSize ? HeaderWidth(columnIndex) : Math.Max(ContentWidth(columnIndex), Items.Count == 0 ? HeaderWidth(columnIndex) : 0);
			if (headerAutoResize == ColumnHeaderAutoResizeStyle.HeaderSize && columnIndex == Columns.Count - 1)
			{
				// Windows lets the last column fill the rest of the list.
				int others = 0;
				for (int i = 0; i < columnIndex; i++)
				{
					others += Columns[i].Width;
				}

				width = Math.Max(width, RowsArea.Width - others);
			}

			Columns[columnIndex].Width = width;
		}

		public ListViewItem GetItemAt(int x, int y)
		{
			Rectangle rows = RowsArea;
			if (y < rows.Top || y >= rows.Bottom)
			{
				return null;
			}

			int index = first + (y - rows.Top) / RowHeight;
			return index >= 0 && index < Items.Count ? Items[index] : null;
		}

		public ListViewHitTestInfo HitTest(int x, int y) => new ListViewHitTestInfo(GetItemAt(x, y));

		public ListViewHitTestInfo HitTest(Point point) => HitTest(point.X, point.Y);

		// --- painting ---

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			ClampScroll();
			int[] widths = ColumnWidths();
			Rectangle rows = RowsArea;
			int rowHeight = RowHeight;
			scrollX = Math.Max(0, Math.Min(scrollX, MaxScrollX));
			Region outer = g.Clip;
			if (HeaderHeight > 0)
			{
				int x = Inset - scrollX;
				Rectangle header = new Rectangle(Inset, Inset, Width - 2 * Inset, HeaderHeight);
				g.FillRectangle(Brushes.White, header);
				g.SetClip(new Rectangle(Inset, Inset, rows.Width, HeaderHeight));
				for (int i = 0; i < widths.Length && i < Columns.Count; i++)
				{
					DrawText(g, Columns[i].Text, Font, Color.FromArgb(76, 96, 122), new RectangleF(x + 6, Inset, widths[i] - 8, HeaderHeight), Columns[i].TextAlign == HorizontalAlignment.Right ? ContentAlignment.MiddleRight : Columns[i].TextAlign == HorizontalAlignment.Center ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft, false, true);
					x += widths[i];
					g.DrawLine(new Pen(Color.FromArgb(229, 229, 229)), x - 1, Inset + 2, x - 1, Inset + HeaderHeight - 2);
				}

				g.DrawLine(new Pen(Color.FromArgb(229, 229, 229)), Inset, Inset + HeaderHeight - 1, Width - Inset, Inset + HeaderHeight - 1);
				g.Clip = outer;
			}

			Region clip = g.Clip;
			g.SetClip(rows);
			for (int r = 0; r <= VisibleRows && first + r < Items.Count; r++)
			{
				ListViewItem item = Items[first + r];
				int y = rows.Top + r * rowHeight;
				bool selected = item.Selected && (Focused || !HideSelection);
				int x = rows.Left - scrollX;
				int rowWidth = FullRowSelect || View != View.Details ? rows.Width : widths[0];
				if (selected)
				{
					using (SolidBrush brush = new SolidBrush(Focused ? Color.FromArgb(204, 232, 255) : Color.FromArgb(217, 217, 217)))
					{
						g.FillRectangle(brush, x, y, Math.Min(rowWidth, rows.Width), rowHeight);
					}
				}
				else if (!item.BackColor.IsEmpty)
				{
					using (SolidBrush brush = new SolidBrush(item.BackColor))
					{
						g.FillRectangle(brush, x, y, rows.Width, rowHeight);
					}
				}
				else if (IsMouseOver && hotRow == first + r)
				{
					using (SolidBrush brush = new SolidBrush(Color.FromArgb(229, 243, 255)))
					{
						g.FillRectangle(brush, x, y, Math.Min(rowWidth, rows.Width), rowHeight);
					}
				}

				for (int c = 0; c < widths.Length; c++)
				{
					int cx = x + 4;
					int cw = widths[c] - 6;
					if (c == 0)
					{
						Image image = ItemImage(item);
						if (image != null)
						{
							Size size = SmallImageList.ImageSize;
							g.DrawImage(image, cx, y + (rowHeight - size.Height) / 2, size.Width, size.Height);
							cx += size.Width + 3;
							cw -= size.Width + 3;
						}
					}

					string text = c < item.SubItems.Count ? item.SubItems[c].Text : string.Empty;
					Color fore = !Enabled ? GrayTextColor : !item.ForeColor.IsEmpty ? item.ForeColor : ForeColor;
					HorizontalAlignment align = c < Columns.Count && View == View.Details ? Columns[c].TextAlign : HorizontalAlignment.Left;
					DrawText(g, text, item.Font ?? Font, fore, new RectangleF(cx, y, Math.Max(0, cw), rowHeight), align == HorizontalAlignment.Right ? ContentAlignment.MiddleRight : align == HorizontalAlignment.Center ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft, false, true);
					x += widths[c];
					if (View != View.Details)
					{
						break;
					}
				}

				if (GridLines)
				{
					g.DrawLine(Pens.Gainsboro, rows.Left, y + rowHeight - 1, rows.Right, y + rowHeight - 1);
				}

				if (Focused && item == focusedItem && !selected)
				{
					using (Pen pen = new Pen(Color.FromArgb(153, 209, 255)))
					{
						g.DrawRectangle(pen, rows.Left, y, Math.Min(rowWidth, rows.Width) - 1, rowHeight - 1);
					}
				}
			}

			g.Clip = clip;
			if (Scrolls)
			{
				ListScroller.Paint(g, Track, first, VisibleRows, Items.Count);
			}

			if (HScrolls)
			{
				Rectangle track = HTrack;
				g.FillRectangle(new SolidBrush(Color.FromArgb(240, 240, 240)), track);
				int total = ColumnsTotal;
				int thumb = Math.Max(16, track.Width * rows.Width / Math.Max(1, total));
				int tx = track.X + (track.Width - thumb) * scrollX / Math.Max(1, MaxScrollX);
				g.FillRectangle(new SolidBrush(Color.FromArgb(193, 193, 193)), tx + 1, track.Y + 3, thumb - 2, track.Height - 6);
			}

			if (BorderStyle != BorderStyle.None)
			{
				DrawBorder(g, ClientRectangle, Color.FromArgb(130, 135, 144));
			}

			base.OnPaint(e);
		}

		private Image ItemImage(ListViewItem item)
		{
			if (SmallImageList == null)
			{
				return null;
			}

			if (!string.IsNullOrEmpty(item.ImageKey))
			{
				return SmallImageList.Images[item.ImageKey];
			}

			return item.ImageIndex >= 0 && item.ImageIndex < SmallImageList.Images.Count ? SmallImageList.Images[item.ImageIndex] : null;
		}

		// --- input ---

		private int hotRow = -1;
		private int anchorIndex = -1;
		private bool draggingBar;

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (draggingBar)
			{
				if ((e.Button & MouseButtons.Left) != 0)
				{
					first = ListScroller.FirstAt(Track, e.Y, VisibleRows, Items.Count);
					Invalidate();
				}

				return;
			}

			if (draggingHBar)
			{
				if ((e.Button & MouseButtons.Left) != 0)
				{
					ScrollXTo(e.X);
				}

				return;
			}

			ListViewItem item = GetItemAt(e.X, e.Y);
			int row = item == null ? -1 : item.Index;
			if (row != hotRow)
			{
				hotRow = row;
				Invalidate();
			}
		}

		protected override void OnMouseLeave(EventArgs e)
		{
			hotRow = -1;
			base.OnMouseLeave(e);
		}

		private bool draggingHBar;

		private void ScrollXTo(int x)
		{
			Rectangle track = HTrack;
			scrollX = (int)Math.Round(MaxScrollX * Math.Max(0.0, Math.Min(1.0, (x - track.X) / (double)Math.Max(1, track.Width))));
			Invalidate();
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (Scrolls && Track.Contains(e.Location))
			{
				draggingBar = true;
				first = ListScroller.FirstAt(Track, e.Y, VisibleRows, Items.Count);
				Invalidate();
				base.OnMouseDown(e);
				return;
			}

			if (HScrolls && HTrack.Contains(e.Location))
			{
				draggingHBar = true;
				ScrollXTo(e.X);
				base.OnMouseDown(e);
				return;
			}

			ListViewItem item = GetItemAt(e.X, e.Y);
			if (item != null)
			{
				Keys modifiers = Host.ModifierKeys;
				if (e.Button == MouseButtons.Right && item.Selected)
				{
					// A right click on the selection keeps it, for a context menu.
				}
				else if (MultiSelect && (modifiers & Keys.Control) != 0)
				{
					item.Selected = !item.Selected;
					anchorIndex = item.Index;
				}
				else if (MultiSelect && (modifiers & Keys.Shift) != 0 && anchorIndex >= 0)
				{
					SelectRange(anchorIndex, item.Index);
				}
				else
				{
					SelectOnly(item);
					anchorIndex = item.Index;
				}

				focusedItem = item;
			}
			else if (e.Y >= RowsArea.Top && MultiSelect)
			{
				// A click below the items clears the selection, as in Windows.
				SelectOnly(null);
			}

			Invalidate();
			base.OnMouseDown(e);
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			draggingBar = false;
			draggingHBar = false;
			base.OnMouseUp(e);
		}

		protected override void OnDoubleClick(EventArgs e)
		{
			base.OnDoubleClick(e);
			if (SelectedItems.Count > 0)
			{
				ItemActivate?.Invoke(this, EventArgs.Empty);
			}
		}

		/// <summary>Selects one item (or none), raising one change for all of it.</summary>
		private void SelectOnly(ListViewItem only)
		{
			bool changed = false;
			foreach (ListViewItem item in Items)
			{
				bool want = item == only;
				if (item.Selected != want)
				{
					item.SetSelectedSilently(want);
					changed = true;
				}
			}

			if (changed)
			{
				if (only != null)
				{
					ItemSelectionChanged?.Invoke(this, new ListViewItemSelectionChangedEventArgs(only, only.Index, true));
				}

				OnSelectedIndexChanged(EventArgs.Empty);
			}

			Invalidate();
		}

		private void SelectRange(int a, int b)
		{
			int low = Math.Min(a, b), high = Math.Max(a, b);
			for (int i = 0; i < Items.Count; i++)
			{
				Items[i].SetSelectedSilently(i >= low && i <= high);
			}

			OnSelectedIndexChanged(EventArgs.Empty);
			Invalidate();
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			if (HScrolls && ((Host.ModifierKeys & Keys.Shift) != 0 || !Scrolls))
			{
				int oldX = scrollX;
				scrollX = Math.Max(0, Math.Min(MaxScrollX, scrollX - delta / 120 * 40));
				Invalidate();
				return oldX != scrollX || !Scrolls;
			}

			int old = first;
			first -= delta / 120 * 3;
			ClampScroll();
			Invalidate();
			return old != first || Scrolls;
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Left || key == Keys.Right;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled || Items.Count == 0)
			{
				return;
			}

			int current = focusedItem?.Index ?? (SelectedIndices.Count > 0 ? SelectedIndices[0] : -1);
			int target;
			switch (e.KeyCode)
			{
				case Keys.Down: target = current + 1; break;
				case Keys.Up: target = current < 0 ? 0 : current - 1; break;
				case Keys.Home: target = 0; break;
				case Keys.End: target = Items.Count - 1; break;
				case Keys.PageDown: target = current + VisibleRows - 1; break;
				case Keys.PageUp: target = current - VisibleRows + 1; break;
				case Keys.Return:
					if (SelectedItems.Count > 0)
					{
						ItemActivate?.Invoke(this, EventArgs.Empty);
					}

					return;
				case Keys.A:
					if (e.Control && MultiSelect)
					{
						SelectRange(0, Items.Count - 1);
						e.Handled = true;
					}

					return;
				default:
					return;
			}

			target = Math.Max(0, Math.Min(Items.Count - 1, target));
			ListViewItem item = Items[target];
			if (e.Shift && MultiSelect && anchorIndex >= 0)
			{
				SelectRange(anchorIndex, target);
			}
			else
			{
				SelectOnly(item);
				anchorIndex = target;
			}

			focusedItem = item;
			EnsureVisible(target);
			e.Handled = true;
		}

		protected override void OnGotFocus(EventArgs e)
		{
			base.OnGotFocus(e);
			Invalidate();
		}

		protected override void OnLostFocus(EventArgs e)
		{
			base.OnLostFocus(e);
			Invalidate();
		}

		// --- collections ---

		public class ColumnHeaderCollection : IList
		{
			private readonly ListView owner;
			private readonly List<ColumnHeader> columns = new List<ColumnHeader>();

			public ColumnHeaderCollection(ListView owner)
			{
				this.owner = owner;
			}

			public int Count => columns.Count;

			public ColumnHeader this[int index] => columns[index];

			public ColumnHeader this[string key] => columns.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));

			public int Add(ColumnHeader value)
			{
				value.ListView = owner;
				columns.Add(value);
				owner.ColumnsChanged();
				return columns.Count - 1;
			}

			public ColumnHeader Add(string text)
			{
				return Add(text, 60, HorizontalAlignment.Left);
			}

			public ColumnHeader Add(string text, int width)
			{
				return Add(text, width, HorizontalAlignment.Left);
			}

			public ColumnHeader Add(string text, int width, HorizontalAlignment textAlign)
			{
				ColumnHeader column = new ColumnHeader { Text = text, TextAlign = textAlign };
				column.Width = width;
				Add(column);
				return column;
			}

			public ColumnHeader Add(string key, string text, int width)
			{
				ColumnHeader column = Add(text, width, HorizontalAlignment.Left);
				column.Name = key;
				return column;
			}

			public void AddRange(ColumnHeader[] values)
			{
				foreach (ColumnHeader column in values)
				{
					Add(column);
				}
			}

			public void Clear()
			{
				columns.Clear();
				owner.ColumnsChanged();
			}

			public void RemoveAt(int index)
			{
				columns.RemoveAt(index);
				owner.ColumnsChanged();
			}

			public void Remove(ColumnHeader column)
			{
				columns.Remove(column);
				owner.ColumnsChanged();
			}

			public int IndexOf(ColumnHeader column) => columns.IndexOf(column);

			public bool Contains(ColumnHeader column) => columns.Contains(column);

			public IEnumerator GetEnumerator() => columns.ToArray().GetEnumerator();

			int IList.Add(object value) => Add((ColumnHeader)value);

			bool IList.Contains(object value) => columns.Contains(value as ColumnHeader);

			int IList.IndexOf(object value) => columns.IndexOf(value as ColumnHeader);

			void IList.Insert(int index, object value)
			{
				((ColumnHeader)value).ListView = owner;
				columns.Insert(index, (ColumnHeader)value);
			}

			void IList.Remove(object value) => Remove(value as ColumnHeader);

			object IList.this[int index]
			{
				get { return columns[index]; }
				set { columns[index] = (ColumnHeader)value; }
			}

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)columns).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => columns;
		}

		public class ListViewItemCollection : IList
		{
			private readonly ListView owner;
			private readonly List<ListViewItem> items = new List<ListViewItem>();

			public ListViewItemCollection(ListView owner)
			{
				this.owner = owner;
			}

			public int Count => items.Count;

			public ListViewItem this[int index] => items[index];

			public ListViewItem this[string key] => items.FirstOrDefault(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase));

			public ListViewItem Add(ListViewItem value)
			{
				value.ListView = owner;
				items.Add(value);
				Changed(value.Selected);
				return value;
			}

			public ListViewItem Add(string text) => Add(new ListViewItem(text));

			public ListViewItem Add(string text, int imageIndex) => Add(new ListViewItem(text, imageIndex));

			public ListViewItem Add(string text, string imageKey) => Add(new ListViewItem(text, imageKey));

			public ListViewItem Add(string key, string text, string imageKey)
			{
				ListViewItem item = Add(new ListViewItem(text, imageKey));
				item.Name = key;
				return item;
			}

			public void AddRange(ListViewItem[] values)
			{
				foreach (ListViewItem item in values)
				{
					item.ListView = owner;
					items.Add(item);
				}

				Changed(values.Any(v => v.Selected));
			}

			public ListViewItem Insert(int index, ListViewItem item)
			{
				item.ListView = owner;
				items.Insert(index, item);
				Changed(item.Selected);
				return item;
			}

			public void Remove(ListViewItem item)
			{
				if (items.Remove(item))
				{
					item.ListView = null;
					Changed(item.Selected);
				}
			}

			public void RemoveAt(int index) => Remove(items[index]);

			public void RemoveByKey(string key)
			{
				ListViewItem item = this[key];
				if (item != null)
				{
					Remove(item);
				}
			}

			public void Clear()
			{
				bool hadSelection = items.Any(i => i.Selected);
				foreach (ListViewItem item in items)
				{
					item.ListView = null;
				}

				items.Clear();
				owner.focusedItem = null;
				owner.first = 0;
				Changed(hadSelection);
			}

			private void Changed(bool selectionChanged)
			{
				owner.ClampScroll();
				owner.Invalidate();
				if (selectionChanged)
				{
					owner.OnSelectedIndexChanged(EventArgs.Empty);
				}
			}

			public bool ContainsKey(string key) => this[key] != null;

			public bool Contains(ListViewItem item) => items.Contains(item);

			public int IndexOf(ListViewItem item) => items.IndexOf(item);

			public ListViewItem[] Find(string key, bool searchAllSubItems) => items.Where(i => string.Equals(i.Name, key, StringComparison.OrdinalIgnoreCase)).ToArray();

			public IEnumerator GetEnumerator() => items.ToArray().GetEnumerator();

			int IList.Add(object value)
			{
				Add((ListViewItem)value);
				return items.Count - 1;
			}

			bool IList.Contains(object value) => items.Contains(value as ListViewItem);

			int IList.IndexOf(object value) => items.IndexOf(value as ListViewItem);

			void IList.Insert(int index, object value) => Insert(index, (ListViewItem)value);

			void IList.Remove(object value) => Remove(value as ListViewItem);

			object IList.this[int index]
			{
				get { return items[index]; }
				set { items[index] = (ListViewItem)value; }
			}

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)items).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => items;
		}

		public class SelectedListViewItemCollection : IList
		{
			private readonly ListView owner;

			public SelectedListViewItemCollection(ListView owner)
			{
				this.owner = owner;
			}

			private List<ListViewItem> Selected
			{
				get
				{
					List<ListViewItem> list = new List<ListViewItem>();
					foreach (ListViewItem item in owner.Items)
					{
						if (item.Selected)
						{
							list.Add(item);
						}
					}

					return list;
				}
			}

			public int Count => Selected.Count;

			public ListViewItem this[int index] => Selected[index];

			public void Clear()
			{
				foreach (ListViewItem item in Selected)
				{
					item.Selected = false;
				}
			}

			public bool Contains(ListViewItem item) => Selected.Contains(item);

			public int IndexOf(ListViewItem item) => Selected.IndexOf(item);

			public IEnumerator GetEnumerator() => Selected.GetEnumerator();

			int IList.Add(object value) => throw new NotSupportedException();

			bool IList.Contains(object value) => Selected.Contains(value as ListViewItem);

			int IList.IndexOf(object value) => Selected.IndexOf(value as ListViewItem);

			void IList.Insert(int index, object value) => throw new NotSupportedException();

			void IList.Remove(object value) => throw new NotSupportedException();

			void IList.RemoveAt(int index) => throw new NotSupportedException();

			void IList.Clear() => Clear();

			object IList.this[int index]
			{
				get { return Selected[index]; }
				set { throw new NotSupportedException(); }
			}

			bool IList.IsReadOnly => true;

			bool IList.IsFixedSize => true;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)Selected).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => this;
		}

		public class SelectedIndexCollection : IEnumerable<int>
		{
			private readonly ListView owner;

			public SelectedIndexCollection(ListView owner)
			{
				this.owner = owner;
			}

			private List<int> Indices
			{
				get
				{
					List<int> list = new List<int>();
					for (int i = 0; i < owner.Items.Count; i++)
					{
						if (owner.Items[i].Selected)
						{
							list.Add(i);
						}
					}

					return list;
				}
			}

			public int Count => Indices.Count;

			public int this[int index] => Indices[index];

			public int Add(int itemIndex)
			{
				owner.Items[itemIndex].Selected = true;
				return Count;
			}

			public void Remove(int itemIndex)
			{
				owner.Items[itemIndex].Selected = false;
			}

			public void Clear()
			{
				foreach (ListViewItem item in owner.Items)
				{
					item.Selected = false;
				}
			}

			public bool Contains(int selectedIndex) => Indices.Contains(selectedIndex);

			public int IndexOf(int selectedIndex) => Indices.IndexOf(selectedIndex);

			public IEnumerator<int> GetEnumerator() => Indices.GetEnumerator();

			IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
		}

		public class ListViewGroupCollection : List<ListViewGroup>
		{
		}
	}

	public delegate void ColumnClickEventHandler(object sender, ColumnClickEventArgs e);

	public class ColumnClickEventArgs : EventArgs
	{
		public ColumnClickEventArgs(int column)
		{
			Column = column;
		}

		public int Column { get; }
	}

	public delegate void ListViewItemSelectionChangedEventHandler(object sender, ListViewItemSelectionChangedEventArgs e);

	public class ListViewItemSelectionChangedEventArgs : EventArgs
	{
		public ListViewItemSelectionChangedEventArgs(ListViewItem item, int itemIndex, bool isSelected)
		{
			Item = item;
			ItemIndex = itemIndex;
			IsSelected = isSelected;
		}

		public ListViewItem Item { get; }

		public int ItemIndex { get; }

		public bool IsSelected { get; }
	}

	public class ListViewHitTestInfo
	{
		public ListViewHitTestInfo(ListViewItem item)
		{
			Item = item;
		}

		public ListViewItem Item { get; }
	}

	// --- the data grid ---

	public class DataGridViewColumn : Component
	{
		public string Name { get; set; } = string.Empty;

		public string HeaderText { get; set; } = string.Empty;

		public bool ReadOnly { get; set; }

		public bool Visible { get; set; } = true;

		public int Width { get; set; } = 100;

		public int MinimumWidth { get; set; } = 5;

		public float FillWeight { get; set; } = 100;

		public string DataPropertyName { get; set; } = string.Empty;

		public DataGridViewColumnSortMode SortMode { get; set; }

		public DataGridViewAutoSizeColumnMode AutoSizeMode { get; set; }

		public bool Frozen { get; set; }

		public object Tag { get; set; }

		public string ToolTipText { get; set; }

		public DataGridView DataGridView { get; internal set; }

		public int Index => DataGridView?.Columns.IndexOf(this) ?? -1;
	}

	public class DataGridViewTextBoxColumn : DataGridViewColumn
	{
		public int MaxInputLength { get; set; } = 32767;
	}

	public class DataGridViewCell
	{
		public object Value { get; set; }

		public object FormattedValue => Value;

		public DataGridViewRow OwningRow { get; internal set; }

		public int RowIndex => OwningRow?.Index ?? -1;

		public int ColumnIndex { get; internal set; }

		public bool Selected
		{
			get { return OwningRow != null && OwningRow.Selected; }
			set
			{
				if (OwningRow != null)
				{
					OwningRow.Selected = value;
				}
			}
		}

		public bool ReadOnly { get; set; }

		public string ToolTipText { get; set; }

		public object Tag { get; set; }
	}

	public class DataGridViewRow
	{
		private bool selected;

		public DataGridViewRow()
		{
			Cells = new DataGridViewCellCollection(this);
		}

		public DataGridViewCellCollection Cells { get; }

		public DataGridView DataGridView { get; internal set; }

		public int Index => DataGridView?.Rows.IndexOf(this) ?? -1;

		public bool Visible { get; set; } = true;

		public object Tag { get; set; }

		public bool ReadOnly { get; set; }

		public int Height { get; set; } = 22;

		public bool Selected
		{
			get { return selected; }
			set
			{
				if (selected == value)
				{
					return;
				}

				selected = value;
				DataGridView?.RowSelectionChanged(this);
			}
		}

		internal void SetSelectedSilently(bool value) => selected = value;

		public void CreateCells(DataGridView grid, params object[] values)
		{
			Cells.Clear();
			for (int i = 0; i < grid.Columns.Count; i++)
			{
				Cells.Add(new DataGridViewCell { Value = values != null && i < values.Length ? values[i] : null });
			}
		}

		public void SetValues(params object[] values)
		{
			for (int i = 0; i < values.Length; i++)
			{
				Cells[i].Value = values[i];
			}
		}
	}

	public class DataGridViewCellCollection : IEnumerable
	{
		private readonly DataGridViewRow owner;
		private readonly List<DataGridViewCell> cells = new List<DataGridViewCell>();

		internal DataGridViewCellCollection(DataGridViewRow owner)
		{
			this.owner = owner;
		}

		public int Count => cells.Count;

		public DataGridViewCell this[int index]
		{
			get
			{
				while (index >= cells.Count)
				{
					Add(new DataGridViewCell());
				}

				return cells[index];
			}
		}

		public DataGridViewCell this[string columnName]
		{
			get
			{
				int index = owner.DataGridView?.Columns.IndexOf(columnName) ?? -1;
				return index >= 0 ? this[index] : null;
			}
		}

		public int Add(DataGridViewCell cell)
		{
			cell.OwningRow = owner;
			cell.ColumnIndex = cells.Count;
			cells.Add(cell);
			return cells.Count - 1;
		}

		public void Clear() => cells.Clear();

		public IEnumerator GetEnumerator() => cells.GetEnumerator();
	}

	public class DataGridView : Control, ISupportInitialize
	{
		private int first;

		public DataGridView()
		{
			Columns = new DataGridViewColumnCollection(this);
			Rows = new DataGridViewRowCollection(this);
		}

		protected override Size DefaultSize => new Size(240, 150);

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public DataGridViewColumnCollection Columns { get; }

		public DataGridViewRowCollection Rows { get; }

		public int ColumnCount
		{
			get { return Columns.Count; }
			set
			{
				while (Columns.Count < value)
				{
					Columns.Add(new DataGridViewTextBoxColumn());
				}

				while (Columns.Count > value)
				{
					Columns.RemoveAt(Columns.Count - 1);
				}
			}
		}

		public int RowCount => Rows.Count;

		public bool AllowUserToAddRows { get; set; } = true;

		public bool AllowUserToDeleteRows { get; set; } = true;

		public bool AllowUserToResizeColumns { get; set; } = true;

		public bool AllowUserToResizeRows { get; set; } = true;

		public bool AllowUserToOrderColumns { get; set; }

		public bool MultiSelect { get; set; } = true;

		public bool ReadOnly { get; set; }

		public bool RowHeadersVisible { get; set; } = true;

		public int RowHeadersWidth { get; set; } = 41;

		public bool ColumnHeadersVisible { get; set; } = true;

		public int ColumnHeadersHeight { get; set; } = 23;

		public DataGridViewColumnHeadersHeightSizeMode ColumnHeadersHeightSizeMode { get; set; }

		public DataGridViewAutoSizeColumnsMode AutoSizeColumnsMode { get; set; } = DataGridViewAutoSizeColumnsMode.None;

		public DataGridViewSelectionMode SelectionMode { get; set; } = DataGridViewSelectionMode.RowHeaderSelect;

		public Color BackgroundColor { get; set; } = Color.FromArgb(171, 171, 171);

		public BorderStyle BorderStyle { get; set; } = BorderStyle.FixedSingle;

		public Color GridColor { get; set; } = Color.FromArgb(160, 160, 160);

		public bool EnableHeadersVisualStyles { get; set; } = true;

		public event EventHandler SelectionChanged;
		public event DataGridViewCellEventHandler CellContentClick;
		public event DataGridViewCellEventHandler CellClick;
		public event DataGridViewCellEventHandler CellDoubleClick;
		public event DataGridViewCellEventHandler CellContentDoubleClick;

		protected virtual void OnSelectionChanged(EventArgs e) => SelectionChanged?.Invoke(this, e);

		public DataGridViewSelectedRowCollection SelectedRows
		{
			get
			{
				List<DataGridViewRow> rows = new List<DataGridViewRow>();
				// Windows lists them from the last selected; here, from the bottom.
				for (int i = Rows.Count - 1; i >= 0; i--)
				{
					if (Rows[i].Selected)
					{
						rows.Add(Rows[i]);
					}
				}

				return new DataGridViewSelectedRowCollection(rows);
			}
		}

		public DataGridViewSelectedCellCollection SelectedCells
		{
			get
			{
				List<DataGridViewCell> cells = new List<DataGridViewCell>();
				for (int i = Rows.Count - 1; i >= 0; i--)
				{
					if (Rows[i].Selected)
					{
						for (int c = Columns.Count - 1; c >= 0; c--)
						{
							cells.Add(Rows[i].Cells[c]);
						}
					}
				}

				return new DataGridViewSelectedCellCollection(cells);
			}
		}

		public DataGridViewRow CurrentRow => SelectedRows.Count > 0 ? SelectedRows[0] : null;

		public DataGridViewCell CurrentCell
		{
			get { return CurrentRow?.Cells[0]; }
			set
			{
				if (value?.OwningRow != null)
				{
					SelectOnly(value.OwningRow);
				}
			}
		}

		public void ClearSelection()
		{
			bool changed = false;
			foreach (DataGridViewRow row in Rows)
			{
				if (row.Selected)
				{
					row.SetSelectedSilently(false);
					changed = true;
				}
			}

			Invalidate();
			if (changed)
			{
				OnSelectionChanged(EventArgs.Empty);
			}
		}

		public void BeginInit()
		{
		}

		public void EndInit()
		{
		}

		internal void RowSelectionChanged(DataGridViewRow row)
		{
			if (row.Selected && !MultiSelect)
			{
				foreach (DataGridViewRow other in Rows)
				{
					if (other != row)
					{
						other.SetSelectedSilently(false);
					}
				}
			}

			Invalidate();
			OnSelectionChanged(EventArgs.Empty);
		}

		internal void RowsChanged()
		{
			ClampScroll();
			Invalidate();
		}

		private void SelectOnly(DataGridViewRow only)
		{
			bool changed = false;
			foreach (DataGridViewRow row in Rows)
			{
				bool want = row == only;
				if (row.Selected != want)
				{
					row.SetSelectedSilently(want);
					changed = true;
				}
			}

			Invalidate();
			if (changed)
			{
				OnSelectionChanged(EventArgs.Empty);
			}
		}

		// --- geometry ---

		private const int RowHeight = 22;

		private int HeaderHeight => ColumnHeadersVisible ? ColumnHeadersHeight : 0;

		private int RowHeaderWidth => RowHeadersVisible ? RowHeadersWidth : 0;

		private int VisibleRows => Math.Max(1, (Height - 2 - HeaderHeight) / RowHeight);

		private bool Scrolls => Rows.Count > VisibleRows;

		private Rectangle Track => new Rectangle(Width - 1 - ListScroller.Width, 1 + HeaderHeight, ListScroller.Width, Height - 2 - HeaderHeight);

		private void ClampScroll()
		{
			first = Math.Max(0, Math.Min(first, Rows.Count - VisibleRows));
		}

		private int[] ColumnWidths(int available)
		{
			List<DataGridViewColumn> visible = Columns.Cast<DataGridViewColumn>().ToList();
			int[] widths = new int[visible.Count];
			if (AutoSizeColumnsMode == DataGridViewAutoSizeColumnsMode.Fill)
			{
				float weights = visible.Where(c => c.Visible).Sum(c => c.FillWeight);
				for (int i = 0; i < visible.Count; i++)
				{
					widths[i] = visible[i].Visible ? (int)(available * visible[i].FillWeight / Math.Max(1, weights)) : 0;
				}
			}
			else
			{
				for (int i = 0; i < visible.Count; i++)
				{
					widths[i] = visible[i].Visible ? (visible[i].AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? Math.Max(visible[i].MinimumWidth, available - visible.Where((c, j) => j != i && c.Visible).Sum(c => c.Width)) : visible[i].Width) : 0;
				}
			}

			return widths;
		}

		// --- painting ---

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			ClampScroll();
			g.FillRectangle(new SolidBrush(BackgroundColor), ClientRectangle);
			int available = Width - 2 - RowHeaderWidth - (Scrolls ? ListScroller.Width : 0);
			int[] widths = ColumnWidths(available);
			int left = 1 + RowHeaderWidth;
			Pen grid = new Pen(GridColor);
			if (HeaderHeight > 0)
			{
				g.FillRectangle(Brushes.White, 1, 1, Width - 2, HeaderHeight);
				int x = left;
				for (int c = 0; c < widths.Length; c++)
				{
					if (widths[c] <= 0)
					{
						continue;
					}

					DrawText(g, Columns[c].HeaderText, Font, Color.Black, new RectangleF(x + 4, 1, widths[c] - 8, HeaderHeight), ContentAlignment.MiddleLeft, false, true);
					x += widths[c];
					g.DrawLine(grid, x - 1, 1, x - 1, HeaderHeight);
				}

				g.DrawLine(grid, 1, HeaderHeight, Width - 1, HeaderHeight);
			}

			int rowsTop = 1 + HeaderHeight;
			Region clip = g.Clip;
			g.SetClip(new Rectangle(1, rowsTop, Width - 2 - (Scrolls ? ListScroller.Width : 0), Height - 1 - rowsTop));
			for (int r = 0; r <= VisibleRows && first + r < Rows.Count; r++)
			{
				DataGridViewRow row = Rows[first + r];
				int y = rowsTop + r * RowHeight;
				bool selected = row.Selected;
				g.FillRectangle(selected ? new SolidBrush(HighlightColor) : Brushes.White, left, y, widths.Sum(), RowHeight);
				if (RowHeaderWidth > 0)
				{
					g.FillRectangle(Brushes.White, 1, y, RowHeaderWidth, RowHeight);
					if (selected)
					{
						g.FillPolygon(Brushes.Black, new[] { new PointF(RowHeaderWidth / 2f - 2, y + 7), new PointF(RowHeaderWidth / 2f + 3, y + 11), new PointF(RowHeaderWidth / 2f - 2, y + 15) });
					}

					g.DrawLine(grid, RowHeaderWidth, y, RowHeaderWidth, y + RowHeight);
				}

				int x = left;
				for (int c = 0; c < widths.Length; c++)
				{
					if (widths[c] <= 0)
					{
						continue;
					}

					string text = row.Cells[c].Value?.ToString() ?? string.Empty;
					DrawText(g, text, Font, selected ? Color.White : Color.Black, new RectangleF(x + 3, y, widths[c] - 6, RowHeight), ContentAlignment.MiddleLeft, false, true);
					x += widths[c];
					g.DrawLine(grid, x - 1, y, x - 1, y + RowHeight - 1);
				}

				g.DrawLine(grid, 1, y + RowHeight - 1, x, y + RowHeight - 1);
			}

			g.Clip = clip;
			grid.Dispose();
			if (Scrolls)
			{
				ListScroller.Paint(g, Track, first, VisibleRows, Rows.Count);
			}

			DrawBorder(g, ClientRectangle, Focused ? HighlightColor : Color.FromArgb(130, 135, 144));
			base.OnPaint(e);
		}

		// --- input ---

		private bool draggingBar;
		private int anchorRow = -1;

		private int RowAt(int y)
		{
			int rowsTop = 1 + HeaderHeight;
			if (y < rowsTop)
			{
				return -1;
			}

			int index = first + (y - rowsTop) / RowHeight;
			return index < Rows.Count ? index : -1;
		}

		private int ColumnAt(int x)
		{
			int available = Width - 2 - RowHeaderWidth - (Scrolls ? ListScroller.Width : 0);
			int[] widths = ColumnWidths(available);
			int left = 1 + RowHeaderWidth;
			for (int c = 0; c < widths.Length; c++)
			{
				if (x >= left && x < left + widths[c])
				{
					return c;
				}

				left += widths[c];
			}

			return -1;
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			if (Scrolls && Track.Contains(e.Location))
			{
				draggingBar = true;
				first = ListScroller.FirstAt(Track, e.Y, VisibleRows, Rows.Count);
				Invalidate();
				base.OnMouseDown(e);
				return;
			}

			int row = RowAt(e.Y);
			if (row >= 0)
			{
				Keys modifiers = Host.ModifierKeys;
				if (MultiSelect && (modifiers & Keys.Control) != 0)
				{
					Rows[row].Selected = !Rows[row].Selected;
				}
				else if (MultiSelect && (modifiers & Keys.Shift) != 0 && anchorRow >= 0)
				{
					int low = Math.Min(anchorRow, row), high = Math.Max(anchorRow, row);
					for (int i = 0; i < Rows.Count; i++)
					{
						Rows[i].SetSelectedSilently(i >= low && i <= high);
					}

					OnSelectionChanged(EventArgs.Empty);
				}
				else
				{
					SelectOnly(Rows[row]);
					anchorRow = row;
				}
			}

			base.OnMouseDown(e);
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			if (draggingBar && (e.Button & MouseButtons.Left) != 0)
			{
				first = ListScroller.FirstAt(Track, e.Y, VisibleRows, Rows.Count);
				Invalidate();
			}
		}

		protected override void OnMouseUp(MouseEventArgs e)
		{
			draggingBar = false;
			base.OnMouseUp(e);
		}

		protected override void OnMouseClick(MouseEventArgs e)
		{
			base.OnMouseClick(e);
			int row = RowAt(e.Y), column = ColumnAt(e.X);
			if (row >= 0 && column >= 0)
			{
				DataGridViewCellEventArgs args = new DataGridViewCellEventArgs(column, row);
				CellClick?.Invoke(this, args);
				CellContentClick?.Invoke(this, args);
			}
		}

		protected override void OnMouseDoubleClick(MouseEventArgs e)
		{
			base.OnMouseDoubleClick(e);
			int row = RowAt(e.Y), column = ColumnAt(e.X);
			if (row >= 0 && column >= 0)
			{
				DataGridViewCellEventArgs args = new DataGridViewCellEventArgs(column, row);
				CellDoubleClick?.Invoke(this, args);
				CellContentDoubleClick?.Invoke(this, args);
			}
		}

		internal override bool HandleWheel(int delta, Point location)
		{
			int old = first;
			first -= delta / 120 * 3;
			ClampScroll();
			Invalidate();
			return old != first || Scrolls;
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled || Rows.Count == 0)
			{
				return;
			}

			int current = CurrentRow?.Index ?? -1;
			int target;
			switch (e.KeyCode)
			{
				case Keys.Down: target = current + 1; break;
				case Keys.Up: target = current - 1; break;
				case Keys.Home: target = 0; break;
				case Keys.End: target = Rows.Count - 1; break;
				case Keys.PageDown: target = current + VisibleRows - 1; break;
				case Keys.PageUp: target = current - VisibleRows + 1; break;
				default: return;
			}

			target = Math.Max(0, Math.Min(Rows.Count - 1, target));
			SelectOnly(Rows[target]);
			anchorRow = target;
			if (target < first)
			{
				first = target;
			}
			else if (target >= first + VisibleRows)
			{
				first = target - VisibleRows + 1;
			}

			Invalidate();
			e.Handled = true;
		}
	}

	public class DataGridViewColumnCollection : IEnumerable
	{
		private readonly DataGridView owner;
		private readonly List<DataGridViewColumn> columns = new List<DataGridViewColumn>();

		internal DataGridViewColumnCollection(DataGridView owner)
		{
			this.owner = owner;
		}

		public int Count => columns.Count;

		public DataGridViewColumn this[int index] => columns[index];

		public DataGridViewColumn this[string name] => columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

		public int Add(DataGridViewColumn column)
		{
			column.DataGridView = owner;
			columns.Add(column);
			owner.Invalidate();
			return columns.Count - 1;
		}

		public int Add(string columnName, string headerText)
		{
			return Add(new DataGridViewTextBoxColumn { Name = columnName, HeaderText = headerText });
		}

		public void AddRange(params DataGridViewColumn[] range)
		{
			foreach (DataGridViewColumn column in range)
			{
				Add(column);
			}
		}

		public void RemoveAt(int index)
		{
			columns.RemoveAt(index);
			owner.Invalidate();
		}

		public void Clear()
		{
			columns.Clear();
			owner.Invalidate();
		}

		public int IndexOf(DataGridViewColumn column) => columns.IndexOf(column);

		internal int IndexOf(string name) => columns.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

		public bool Contains(string name) => IndexOf(name) >= 0;

		public IEnumerator GetEnumerator() => columns.GetEnumerator();
	}

	public class DataGridViewRowCollection : IEnumerable
	{
		private readonly DataGridView owner;
		private readonly List<DataGridViewRow> rows = new List<DataGridViewRow>();

		internal DataGridViewRowCollection(DataGridView owner)
		{
			this.owner = owner;
		}

		public int Count => rows.Count;

		public DataGridViewRow this[int index] => rows[index];

		public int Add()
		{
			return Add(new object[0]);
		}

		public int Add(params object[] values)
		{
			DataGridViewRow row = new DataGridViewRow();
			row.CreateCells(owner, values);
			return Add(row);
		}

		public int Add(DataGridViewRow row)
		{
			row.DataGridView = owner;
			while (row.Cells.Count < owner.Columns.Count)
			{
				row.Cells.Add(new DataGridViewCell());
			}

			rows.Add(row);
			owner.RowsChanged();
			return rows.Count - 1;
		}

		public void AddRange(params DataGridViewRow[] range)
		{
			foreach (DataGridViewRow row in range)
			{
				Add(row);
			}
		}

		public void Insert(int index, params object[] values)
		{
			DataGridViewRow row = new DataGridViewRow();
			row.CreateCells(owner, values);
			row.DataGridView = owner;
			rows.Insert(index, row);
			owner.RowsChanged();
		}

		public void RemoveAt(int index)
		{
			bool selected = rows[index].Selected;
			rows.RemoveAt(index);
			owner.RowsChanged();
			if (selected)
			{
				owner.RowSelectionChanged(new DataGridViewRow());
			}
		}

		public void Remove(DataGridViewRow row)
		{
			int index = rows.IndexOf(row);
			if (index >= 0)
			{
				RemoveAt(index);
			}
		}

		public void Clear()
		{
			bool selected = rows.Any(r => r.Selected);
			rows.Clear();
			owner.RowsChanged();
			if (selected)
			{
				owner.RowSelectionChanged(new DataGridViewRow());
			}
		}

		public int IndexOf(DataGridViewRow row) => rows.IndexOf(row);

		public IEnumerator GetEnumerator() => rows.ToArray().GetEnumerator();
	}

	public class DataGridViewSelectedRowCollection : IEnumerable
	{
		private readonly List<DataGridViewRow> rows;

		internal DataGridViewSelectedRowCollection(List<DataGridViewRow> rows)
		{
			this.rows = rows;
		}

		public int Count => rows.Count;

		public DataGridViewRow this[int index] => rows[index];

		public IEnumerator GetEnumerator() => rows.GetEnumerator();
	}

	public class DataGridViewSelectedCellCollection : IEnumerable
	{
		private readonly List<DataGridViewCell> cells;

		internal DataGridViewSelectedCellCollection(List<DataGridViewCell> cells)
		{
			this.cells = cells;
		}

		public int Count => cells.Count;

		public DataGridViewCell this[int index] => cells[index];

		public IEnumerator GetEnumerator() => cells.GetEnumerator();
	}
}
