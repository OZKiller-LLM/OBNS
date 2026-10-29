// OBNS: not an upstream file. The in-window main menu (OBNS/GLFORMS.md): tab controls and their pages.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

namespace OpenBve.GLForms
{
	using Graphics = System.Drawing.Graphics;

	public class TabPage : Panel
	{
		public TabPage()
		{
			base.Visible = false;
		}

		public TabPage(string text) : this()
		{
			Text = text;
		}

		protected override Color DefaultBackColor => UseVisualStyleBackColor ? Color.White : Color.Empty;

		public bool UseVisualStyleBackColor { get; set; }

		public string ToolTipText { get; set; }

		public string ImageKey { get; set; }

		public int ImageIndex { get; set; } = -1;

		public override string Text
		{
			get { return base.Text; }
			set
			{
				base.Text = value;
				(Parent as TabControl)?.Invalidate();
			}
		}
	}

	public class TabControl : Control
	{
		private int selectedIndex = -1;
		private const int TabHeight = 21;

		public TabControl()
		{
			TabPages = new TabPageCollection(this);
		}

		protected override Size DefaultSize => new Size(200, 100);

		internal override bool CanTakeFocus => true;

		internal override bool DrawsItself => true;

		public TabPageCollection TabPages { get; }

		public int TabCount => TabPages.Count;

		public TabAlignment Alignment { get; set; }

		public TabSizeMode SizeMode { get; set; }

		public bool Multiline { get; set; }

		public bool HotTrack { get; set; }

		public Size ItemSize { get; set; }

		public Point Padding { get; set; } = new Point(6, 3);

		public ImageList ImageList { get; set; }

		public event EventHandler SelectedIndexChanged;

		public event TabControlCancelEventHandler Selecting;

		public event TabControlEventHandler Selected;

		protected virtual void OnSelectedIndexChanged(EventArgs e) => SelectedIndexChanged?.Invoke(this, e);

		public override Rectangle DisplayRectangle => new Rectangle(4, TabHeight + 3, Math.Max(0, Width - 8), Math.Max(0, Height - TabHeight - 7));

		public int SelectedIndex
		{
			get { return selectedIndex; }
			set
			{
				if (value < -1 || value >= TabPages.Count || value == selectedIndex)
				{
					return;
				}

				if (value >= 0)
				{
					TabControlCancelEventArgs e = new TabControlCancelEventArgs(TabPages[value], value, false);
					Selecting?.Invoke(this, e);
					if (e.Cancel)
					{
						return;
					}
				}

				selectedIndex = value;
				ShowSelected();
				OnSelectedIndexChanged(EventArgs.Empty);
				if (value >= 0)
				{
					Selected?.Invoke(this, new TabControlEventArgs(TabPages[value], value));
				}
			}
		}

		public TabPage SelectedTab
		{
			get { return selectedIndex >= 0 && selectedIndex < TabPages.Count ? TabPages[selectedIndex] : null; }
			set
			{
				int index = TabPages.IndexOf(value);
				if (index >= 0)
				{
					SelectedIndex = index;
				}
			}
		}

		public void SelectTab(int index) => SelectedIndex = index;

		public void SelectTab(TabPage page) => SelectedTab = page;

		/// <summary>Only the chosen page shows; each fills the area below the tabs.</summary>
		private void ShowSelected()
		{
			for (int i = 0; i < TabPages.Count; i++)
			{
				TabPages[i].Visible = i == selectedIndex;
			}

			Invalidate();
		}

		protected override void OnLayout(EventArgs e)
		{
			Rectangle area = DisplayRectangle;
			foreach (Control page in Controls)
			{
				page.Bounds = area;
			}

			base.OnLayout(e);
		}

		protected override void OnControlAdded(Control control)
		{
			control.Bounds = DisplayRectangle;
			if (selectedIndex < 0 && control is TabPage)
			{
				selectedIndex = 0;
			}

			ShowSelected();
			base.OnControlAdded(control);
		}

		protected override void OnControlRemoved(Control control)
		{
			if (selectedIndex >= TabPages.Count)
			{
				selectedIndex = TabPages.Count - 1;
			}

			ShowSelected();
			base.OnControlRemoved(control);
		}

		protected override void OnResize(EventArgs e)
		{
			Rectangle area = DisplayRectangle;
			foreach (Control page in Controls)
			{
				page.Bounds = area;
			}

			base.OnResize(e);
		}

		/// <summary>Where each tab is: tabs as wide as their text, left to right.</summary>
		private List<Rectangle> TabBounds()
		{
			List<Rectangle> tabs = new List<Rectangle>();
			int x = 2;
			foreach (TabPage page in TabPages)
			{
				int width = (int)Math.Ceiling(MeasureText(page.Text, Font).Width) + 2 * Padding.X + 2;
				if (ItemSize.Width > 0 && SizeMode == TabSizeMode.Fixed)
				{
					width = ItemSize.Width;
				}

				tabs.Add(new Rectangle(x, 2, width, TabHeight));
				x += width;
			}

			return tabs;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics g = e.Graphics;
			Rectangle body = new Rectangle(0, TabHeight + 1, Width, Height - TabHeight - 1);
			g.FillRectangle(Brushes.White, body);
			DrawBorder(g, body, Color.FromArgb(217, 217, 217));
			List<Rectangle> tabs = TabBounds();
			for (int i = 0; i < tabs.Count; i++)
			{
				Rectangle tab = tabs[i];
				bool selected = i == selectedIndex;
				bool hot = IsMouseOver && hotTab == i;
				Rectangle r = selected ? new Rectangle(tab.X - 2, tab.Y - 2, tab.Width + 4, tab.Height + 2) : new Rectangle(tab.X, tab.Y, tab.Width, tab.Height - 1);
				using (SolidBrush brush = new SolidBrush(selected ? Color.White : hot ? Color.FromArgb(216, 234, 249) : Color.FromArgb(240, 240, 240)))
				{
					g.FillRectangle(brush, r);
				}

				using (Pen pen = new Pen(Color.FromArgb(217, 217, 217)))
				{
					g.DrawLine(pen, r.Left, r.Bottom, r.Left, r.Top);
					g.DrawLine(pen, r.Left, r.Top, r.Right - 1, r.Top);
					g.DrawLine(pen, r.Right - 1, r.Top, r.Right - 1, r.Bottom);
				}

				DrawText(g, TabPages[i].Text, Font, Enabled ? Color.Black : GrayTextColor, new RectangleF(r.X, r.Y, r.Width, r.Height), ContentAlignment.MiddleCenter, false, true);
				if (selected && Focused)
				{
					using (Pen pen = new Pen(Color.Gray) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
					{
						g.DrawRectangle(pen, r.X + 3, r.Y + 3, r.Width - 7, r.Height - 6);
					}
				}
			}

			base.OnPaint(e);
		}

		private int hotTab = -1;

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			int hot = TabBounds().FindIndex(t => t.Contains(e.Location));
			if (hot != hotTab)
			{
				hotTab = hot;
				Invalidate();
			}
		}

		protected override void OnMouseDown(MouseEventArgs e)
		{
			base.OnMouseDown(e);
			if (e.Button != MouseButtons.Left)
			{
				return;
			}

			int index = TabBounds().FindIndex(t => t.Contains(e.Location));
			if (index >= 0)
			{
				SelectedIndex = index;
			}
		}

		internal override bool IsInputKey(Keys keyData)
		{
			Keys key = keyData & Keys.KeyCode;
			return key == Keys.Left || key == Keys.Right;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Handled || TabPages.Count == 0)
			{
				return;
			}

			if (e.KeyCode == Keys.Left)
			{
				SelectedIndex = Math.Max(0, selectedIndex - 1);
				e.Handled = true;
			}
			else if (e.KeyCode == Keys.Right)
			{
				SelectedIndex = Math.Min(TabPages.Count - 1, selectedIndex + 1);
				e.Handled = true;
			}
			else if (e.KeyCode == Keys.Tab && e.Control)
			{
				SelectedIndex = (selectedIndex + (e.Shift ? TabPages.Count - 1 : 1)) % TabPages.Count;
				e.Handled = true;
			}
		}

		public class TabPageCollection : IList
		{
			private readonly TabControl owner;

			public TabPageCollection(TabControl owner)
			{
				this.owner = owner;
			}

			private List<TabPage> Pages
			{
				get
				{
					List<TabPage> pages = new List<TabPage>();
					foreach (Control control in owner.Controls)
					{
						if (control is TabPage page)
						{
							pages.Add(page);
						}
					}

					return pages;
				}
			}

			public int Count => Pages.Count;

			public TabPage this[int index] => Pages[index];

			public TabPage this[string key] => Pages.Find(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));

			public void Add(TabPage page) => owner.Controls.Add(page);

			public void Add(string text) => owner.Controls.Add(new TabPage(text));

			public void Add(string key, string text) => owner.Controls.Add(new TabPage(text) { Name = key });

			public void AddRange(TabPage[] pages)
			{
				foreach (TabPage page in pages)
				{
					Add(page);
				}
			}

			public void Insert(int index, TabPage page)
			{
				owner.Controls.Add(page);
				owner.Controls.SetChildIndex(page, index);
				owner.ShowSelected();
			}

			public void Remove(TabPage page) => owner.Controls.Remove(page);

			public void RemoveAt(int index) => owner.Controls.Remove(Pages[index]);

			public void Clear()
			{
				foreach (TabPage page in Pages)
				{
					owner.Controls.Remove(page);
				}

				owner.selectedIndex = -1;
			}

			public bool Contains(TabPage page) => Pages.Contains(page);

			public int IndexOf(TabPage page) => Pages.IndexOf(page);

			public IEnumerator GetEnumerator() => Pages.GetEnumerator();

			int IList.Add(object value)
			{
				Add((TabPage)value);
				return Count - 1;
			}

			bool IList.Contains(object value) => Pages.Contains(value as TabPage);

			int IList.IndexOf(object value) => Pages.IndexOf(value as TabPage);

			void IList.Insert(int index, object value) => Insert(index, (TabPage)value);

			void IList.Remove(object value) => Remove(value as TabPage);

			object IList.this[int index]
			{
				get { return Pages[index]; }
				set { throw new NotSupportedException(); }
			}

			bool IList.IsReadOnly => false;

			bool IList.IsFixedSize => false;

			void ICollection.CopyTo(Array array, int index) => ((ICollection)Pages).CopyTo(array, index);

			bool ICollection.IsSynchronized => false;

			object ICollection.SyncRoot => this;
		}
	}

	public delegate void TabControlCancelEventHandler(object sender, TabControlCancelEventArgs e);

	public class TabControlCancelEventArgs : System.ComponentModel.CancelEventArgs
	{
		public TabControlCancelEventArgs(TabPage tabPage, int tabPageIndex, bool cancel) : base(cancel)
		{
			TabPage = tabPage;
			TabPageIndex = tabPageIndex;
		}

		public TabPage TabPage { get; }

		public int TabPageIndex { get; }
	}

	public delegate void TabControlEventHandler(object sender, TabControlEventArgs e);

	public class TabControlEventArgs : EventArgs
	{
		public TabControlEventArgs(TabPage tabPage, int tabPageIndex)
		{
			TabPage = tabPage;
			TabPageIndex = tabPageIndex;
		}

		public TabPage TabPage { get; }

		public int TabPageIndex { get; }
	}
}
