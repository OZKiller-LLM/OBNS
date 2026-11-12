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

namespace OpenBve.Switch
{
	/// <summary>The console's buttons, as the menu reads them (libnx's HidNpadButton, folded: either stick's directions
	/// count as the D-pad's).</summary>
	[Flags]
	public enum MenuButtons
	{
		None = 0,
		Up = 1 << 0,
		Down = 1 << 1,
		Left = 1 << 2,
		Right = 1 << 3,
		A = 1 << 4,
		B = 1 << 5,
		X = 1 << 6,
		Y = 1 << 7,
		L = 1 << 8,
		R = 1 << 9,
		ZL = 1 << 10,
		ZR = 1 << 11,
		Plus = 1 << 12,
		Minus = 1 << 13,
		/// <summary>The sticks' clicks: only the Controls page's button capture uses them.</summary>
		StickL = 1 << 14,
		StickR = 1 << 15
	}

	/// <summary>
	/// The buttons pressed this frame, held directions repeating as the console's own menus do (after 0.4 s, then 12 a
	/// second), and the touch screen in handheld mode, in the menu's 1280x720 coordinates.
	/// </summary>
	public sealed class MenuInput
	{
		private const double RepeatDelay = 0.4, RepeatInterval = 1.0 / 12.0;
		private const MenuButtons Repeating = MenuButtons.Up | MenuButtons.Down | MenuButtons.Left | MenuButtons.Right;

		private MenuButtons held;
		private double heldFor, nextRepeat;

		/// <summary>The buttons down now.</summary>
		public MenuButtons Held => held;

		/// <summary>The buttons that went down this frame, and held directions as they repeat.</summary>
		public MenuButtons Pressed { get; private set; }

		/// <summary>Whether a finger is on the screen.</summary>
		public bool Touching { get; private set; }

		/// <summary>Where the finger is, or was last.</summary>
		public float TouchX { get; private set; }

		public float TouchY { get; private set; }

		/// <summary>The finger came down this frame.</summary>
		public bool TouchStarted { get; private set; }

		/// <summary>The finger came up this frame without having dragged: a tap, at <see cref="TouchX"/>, <see cref="TouchY"/>.</summary>
		public bool Tapped { get; private set; }

		/// <summary>How far the finger moved up this frame while dragging (for scrolling).</summary>
		public float DragY { get; private set; }

		private float startX, startY, lastY;
		private bool dragging;

		/// <summary>The frame's state: the buttons held, the touch screen (x, y in the menu's coordinates), the time since the last.</summary>
		public void Update(MenuButtons now, bool touching, float x, float y, double seconds)
		{
			MenuButtons pressed = now & ~held;
			MenuButtons direction = now & Repeating;
			if ((pressed & Repeating) != 0 || direction == MenuButtons.None)
			{
				heldFor = 0;
				nextRepeat = RepeatDelay;
			}
			else
			{
				heldFor += seconds;
				if (heldFor >= nextRepeat)
				{
					pressed |= direction;
					nextRepeat += RepeatInterval;
				}
			}

			held = now;
			Pressed = pressed;

			TouchStarted = touching && !Touching;
			Tapped = false;
			DragY = 0;
			if (TouchStarted)
			{
				startX = x;
				startY = lastY = y;
				dragging = false;
			}
			else if (touching)
			{
				if (!dragging && (Math.Abs(x - startX) > 16 || Math.Abs(y - startY) > 16))
				{
					dragging = true;
				}

				if (dragging)
				{
					DragY = lastY - y;
				}

				lastY = y;
			}
			else if (Touching && !dragging)
			{
				Tapped = true;
			}

			Touching = touching;
			if (touching)
			{
				TouchX = x;
				TouchY = y;
			}
		}

		/// <summary>Whether the button went down this frame (or repeated).</summary>
		public bool Is(MenuButtons button) => (Pressed & button) != 0;

		/// <summary>Marks the frame's presses as used, so a page opened by one doesn't take it too.</summary>
		public void Consume()
		{
			Pressed = MenuButtons.None;
			Tapped = false;
			TouchStarted = false;
		}

		/// <summary>Whether the tap was inside the rectangle.</summary>
		public bool TappedIn(float x, float y, float width, float height) =>
			Tapped && TouchX >= x && TouchX < x + width && TouchY >= y && TouchY < y + height;
	}
}
