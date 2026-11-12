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
	/// <summary>
	/// A scrolling column of items (Android's ScrollView of rows): up and down move the focus between the items that
	/// take it, the list scrolling to keep the focused one in view; a drag scrolls, a tap focuses and uses an item.
	/// </summary>
	public sealed class MenuList
	{
		public readonly List<MenuItem> Items = new List<MenuItem>();

		/// <summary>The focused item's index, -1 for none.</summary>
		public int Focus = -1;

		private float scroll, target;
		private float contentHeight;
		private float[] tops = Array.Empty<float>(), heights = Array.Empty<float>();
		private float lastX, lastY, lastWidth, lastHeight;

		public void Clear()
		{
			Items.Clear();
			Focus = -1;
			scroll = target = 0;
		}

		public void Add(MenuItem item) => Items.Add(item);

		public MenuItem Focused => Focus >= 0 && Focus < Items.Count ? Items[Focus] : null;

		/// <summary>Focuses the first item that takes the focus, if none has it.</summary>
		public void FocusFirst()
		{
			if (Focused == null || !Focused.Focusable)
			{
				Focus = Next(-1, 1);
			}
		}

		private int Next(int from, int direction)
		{
			for (int i = from + direction; i >= 0 && i < Items.Count; i += direction)
			{
				if (Items[i].Focusable)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>
		/// The frame's input, while the list is the part of the menu in use. False if it didn't use it: up from the first
		/// item, or a button no item took (left from an item that doesn't step, B), for the page to handle.
		/// </summary>
		public bool Update(MenuInput input, IMenuHost host)
		{
			FocusFirst();
			// A binding waiting for its button (Controls) takes every button, the D-pad's too.
			if (BindingItem.Capturing && Focused is BindingItem)
			{
				Focused.Handle(input, host);
				return true;
			}

			if (input.Is(MenuButtons.Down))
			{
				int next = Next(Focus, 1);
				if (next >= 0)
				{
					Focus = next;
				}
				else
				{
					// The last item: show the end of the list (the notes under it).
					target = float.MaxValue;
				}

				return true;
			}

			if (input.Is(MenuButtons.Up))
			{
				int previous = Next(Focus, -1);
				if (previous < 0)
				{
					target = 0;
					return false;
				}

				Focus = previous;
				return true;
			}

			if (Focused != null && Focused.Handle(input, host))
			{
				return true;
			}

			if (input.Touching && input.DragY != 0 && Inside(input.TouchX, input.TouchY))
			{
				target = scroll + input.DragY;
				scroll = target;
				return true;
			}

			if (input.Tapped && Inside(input.TouchX, input.TouchY))
			{
				float y = input.TouchY - lastY + scroll;
				for (int i = 0; i < Items.Count && i < tops.Length; i++)
				{
					if (y >= tops[i] && y < tops[i] + heights[i] && Items[i].Focusable)
					{
						Focus = i;
						Items[i].Tap(input.TouchX - lastX, lastWidth, host);
						return true;
					}
				}
			}

			return false;
		}

		private bool Inside(float x, float y) => x >= lastX && x < lastX + lastWidth && y >= lastY && y < lastY + lastHeight;

		/// <summary>Draws the items in the rectangle, the focused one ringed if the list is in use.</summary>
		public void Draw(IMenuCanvas canvas, float x, float y, float width, float height, bool active, double seconds)
		{
			lastX = x;
			lastY = y;
			lastWidth = width;
			lastHeight = height;
			if (tops.Length != Items.Count)
			{
				tops = new float[Items.Count];
				heights = new float[Items.Count];
			}

			float top = 0;
			for (int i = 0; i < Items.Count; i++)
			{
				tops[i] = top;
				heights[i] = Items[i].Measure(canvas, width);
				top += heights[i] + 4;
			}

			contentHeight = top;
			float most = Math.Max(0, contentHeight - height);
			if (Focus >= 0 && Focus < Items.Count && active && target != float.MaxValue && !float.IsNaN(target))
			{
				// The focused item in view, with a row's margin where there's more beyond.
				float margin = MenuStyle.RowHeight * 0.75f;
				if (tops[Focus] - margin < target)
				{
					target = tops[Focus] - margin;
				}
				else if (tops[Focus] + heights[Focus] + margin > target + height)
				{
					target = tops[Focus] + heights[Focus] + margin - height;
				}
			}

			target = Math.Clamp(target, 0, most);
			// Eased, about 80% of the way in 0.1 s.
			scroll += (target - scroll) * (float)Math.Min(1, seconds * 16);
			if (Math.Abs(target - scroll) < 0.5f)
			{
				scroll = target;
			}

			canvas.Clip(x, y, width, height);
			for (int i = 0; i < Items.Count; i++)
			{
				float itemY = y + tops[i] - scroll;
				if (itemY + heights[i] < y || itemY > y + height)
				{
					continue;
				}

				Items[i].Draw(canvas, x, itemY, width, active && i == Focus);
			}

			canvas.Unclip();
			if (contentHeight > height)
			{
				// A scroll bar at the right edge.
				float barHeight = Math.Max(40, height * height / contentHeight);
				float barY = y + (height - barHeight) * (most > 0 ? scroll / most : 0);
				canvas.Fill(x + width + 6, barY, 4, barHeight, MenuStyle.SurfaceRaised, 2);
			}
		}
	}
}
