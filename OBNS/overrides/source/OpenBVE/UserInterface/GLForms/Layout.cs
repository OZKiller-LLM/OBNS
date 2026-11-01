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
using System.Drawing;

namespace OpenBve.GLForms
{
	public enum SizeType
	{
		AutoSize,
		Absolute,
		Percent
	}

	public enum FlowDirection
	{
		LeftToRight,
		TopDown,
		RightToLeft,
		BottomUp
	}

	public enum AutoSizeMode
	{
		GrowAndShrink,
		GrowOnly
	}

	/// <summary>Places its children one after another, each with its margin, wrapping at the panel's edge (a ZUIKI settings page).</summary>
	public class FlowLayoutPanel : Panel
	{
		public FlowDirection FlowDirection { get; set; } = FlowDirection.LeftToRight;

		public bool WrapContents { get; set; } = true;

		protected override void OnLayout(EventArgs e)
		{
			Rectangle area = DisplayRectangle;
			area.Inflate(-Padding.Left, -Padding.Top);
			bool vertical = FlowDirection == FlowDirection.TopDown || FlowDirection == FlowDirection.BottomUp;
			// A scroll bar takes its width from the area, once the content is taller than the panel.
			int limit = vertical ? area.Bottom : area.Right - (AutoScroll ? 17 : 0);
			int x = area.Left, y = area.Top, lineExtent = 0;
			foreach (Control child in Controls)
			{
				if (!child.OwnVisible)
				{
					continue;
				}

				Size size = child.AutoSize ? child.GetPreferredSize(Size.Empty) : child.Size;
				int outerWidth = size.Width + child.Margin.Left + child.Margin.Right;
				int outerHeight = size.Height + child.Margin.Top + child.Margin.Bottom;
				if (WrapContents && lineExtent > 0)
				{
					if (!vertical && x + outerWidth > limit)
					{
						x = area.Left;
						y += lineExtent;
						lineExtent = 0;
					}
					else if (vertical && y + outerHeight > limit)
					{
						y = area.Top;
						x += lineExtent;
						lineExtent = 0;
					}
				}

				child.SetBounds(x + child.Margin.Left, y + child.Margin.Top, size.Width, size.Height);
				if (vertical)
				{
					y += outerHeight;
					lineExtent = Math.Max(lineExtent, outerWidth);
				}
				else
				{
					x += outerWidth;
					lineExtent = Math.Max(lineExtent, outerHeight);
				}
			}
		}
	}

	public class TableLayoutStyle
	{
		public SizeType SizeType { get; set; }
	}

	public class ColumnStyle : TableLayoutStyle
	{
		public ColumnStyle()
		{
		}

		public ColumnStyle(SizeType sizeType)
		{
			SizeType = sizeType;
		}

		public ColumnStyle(SizeType sizeType, float width)
		{
			SizeType = sizeType;
			Width = width;
		}

		public float Width { get; set; }
	}

	public class RowStyle : TableLayoutStyle
	{
		public RowStyle()
		{
		}

		public RowStyle(SizeType sizeType)
		{
			SizeType = sizeType;
		}

		public RowStyle(SizeType sizeType, float height)
		{
			SizeType = sizeType;
			Height = height;
		}

		public float Height { get; set; }
	}

	/// <summary>A grid of cells sized by its column and row styles, each child in the cell it was added to.</summary>
	public class TableLayoutPanel : Panel
	{
		public sealed class TableLayoutControlCollection : ControlCollection
		{
			private readonly TableLayoutPanel table;

			internal TableLayoutControlCollection(TableLayoutPanel owner) : base(owner)
			{
				table = owner;
			}

			public void Add(Control control, int column, int row)
			{
				table.cells[control] = new Point(column, row);
				Add(control);
			}
		}

		private readonly Dictionary<Control, Point> cells = new Dictionary<Control, Point>();

		protected override ControlCollection CreateControlsInstance() => new TableLayoutControlCollection(this);

		public new TableLayoutControlCollection Controls => (TableLayoutControlCollection)base.Controls;

		public int ColumnCount { get; set; } = 1;

		public int RowCount { get; set; } = 1;

		public List<ColumnStyle> ColumnStyles { get; } = new List<ColumnStyle>();

		public List<RowStyle> RowStyles { get; } = new List<RowStyle>();

		/// <summary>Sizes along one axis: absolute ones first, then the percents share what's left; unstyled tracks share equally.</summary>
		private static int[] Tracks(int count, int total, Func<int, SizeType?> type, Func<int, float> value)
		{
			count = Math.Max(count, 1);
			int[] sizes = new int[count];
			int remaining = total;
			float percentTotal = 0;
			for (int i = 0; i < count; i++)
			{
				SizeType? kind = type(i);
				if (kind == SizeType.Absolute)
				{
					sizes[i] = (int)value(i);
					remaining -= sizes[i];
				}
				else if (kind == SizeType.Percent)
				{
					percentTotal += value(i);
				}
				else
				{
					percentTotal += 100f / count;
				}
			}

			remaining = Math.Max(0, remaining);
			for (int i = 0; i < count; i++)
			{
				SizeType? kind = type(i);
				if (kind != SizeType.Absolute)
				{
					float share = kind == SizeType.Percent ? value(i) : 100f / count;
					sizes[i] = percentTotal > 0 ? (int)(remaining * share / percentTotal) : 0;
				}
			}

			return sizes;
		}

		protected override void OnLayout(EventArgs e)
		{
			Rectangle area = DisplayRectangle;
			int[] columns = Tracks(ColumnCount, area.Width, i => i < ColumnStyles.Count ? ColumnStyles[i].SizeType : (SizeType?)null, i => i < ColumnStyles.Count ? ColumnStyles[i].Width : 0);
			int[] rows = Tracks(RowCount, area.Height, i => i < RowStyles.Count ? RowStyles[i].SizeType : (SizeType?)null, i => i < RowStyles.Count ? RowStyles[i].Height : 0);
			foreach (Control child in base.Controls)
			{
				if (!cells.TryGetValue(child, out Point cell))
				{
					continue;
				}

				int x = area.Left, y = area.Top;
				for (int i = 0; i < cell.X && i < columns.Length; i++) x += columns[i];
				for (int i = 0; i < cell.Y && i < rows.Length; i++) y += rows[i];
				int width = cell.X < columns.Length ? columns[cell.X] : 0, height = cell.Y < rows.Length ? rows[cell.Y] : 0;
				if (child.Dock == DockStyle.Fill)
				{
					child.SetBounds(x + child.Margin.Left, y + child.Margin.Top, width - child.Margin.Left - child.Margin.Right, height - child.Margin.Top - child.Margin.Bottom);
				}
				else
				{
					// Anchored to all or some of the cell's sides, or left as it is at the top left.
					int w = (child.Anchor & (AnchorStyles.Left | AnchorStyles.Right)) == (AnchorStyles.Left | AnchorStyles.Right) ? width - child.Margin.Left - child.Margin.Right : child.Width;
					int h = (child.Anchor & (AnchorStyles.Top | AnchorStyles.Bottom)) == (AnchorStyles.Top | AnchorStyles.Bottom) ? height - child.Margin.Top - child.Margin.Bottom : child.Height;
					child.SetBounds(x + child.Margin.Left, y + child.Margin.Top, w, h);
				}
			}
		}
	}
}
