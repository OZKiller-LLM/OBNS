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
using Android.Views;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// Touches and mouse input on the scene itself, for the touch overlay (touches that miss its
	/// buttons) and for the game view in the desktop interface (mouse and touch in DeX).
	/// </summary>
	/// <remarks>
	/// A touch or click on one of the cab's touch areas works it (CabTouch). Otherwise one
	/// finger or the mouse looks around (upstream's rotate commands), two fingers move the
	/// camera and pinching zooms; each is held as an analog command whose strength grows with the
	/// distance dragged, the way a joystick axis drives them upstream, and released on lifting.
	/// The mouse wheel zooms.
	/// </remarks>
	public sealed class SceneInput
	{
		private readonly View view;
		private readonly AndroidControls controls;
		private readonly float density;
		private float startX, startY, startSpan;
		private int fingers;
		private bool onCabControl;
		private readonly Translations.Command[] heldCommands = new Translations.Command[8];
		private int heldCount;

		public SceneInput(View view, AndroidControls controls)
		{
			this.view = view;
			this.controls = controls;
			density = view.Context?.Resources?.DisplayMetrics?.Density ?? 2.0f;
		}

		/// <summary>A touch or mouse event on the scene. Always consumed.</summary>
		public bool OnTouch(MotionEvent e)
		{
			switch (e.ActionMasked)
			{
				case MotionEventActions.Down:
				{
					// First, is it on a cab control? A fingertip may land a little off it; a mouse pointer should not.
					bool mouse = e.GetToolType(0) == MotionEventToolType.Mouse;
					int element = controls.Cab.HitTest(e.GetX(), e.GetY(), mouse ? 2.0f : 10.0f * density);
					if (element >= 0)
					{
						onCabControl = true;
						controls.Post(session => controls.Cab.Press(session, controls, element));
						return true;
					}

					goto case MotionEventActions.PointerDown;
				}
				case MotionEventActions.PointerDown:
				case MotionEventActions.PointerUp:
				{
					if (onCabControl)
					{
						return true;
					}

					// The finger lifting is still in the event; leave it out of the new baseline.
					int skip = e.ActionMasked == MotionEventActions.PointerUp ? e.ActionIndex : -1;
					ReleaseHeld();
					fingers = e.PointerCount - (skip >= 0 ? 1 : 0);
					Centroid(e, out startX, out startY, skip);
					startSpan = Span(e, skip);
					return true;
				}
				case MotionEventActions.Move:
				{
					if (onCabControl)
					{
						return true;
					}

					Centroid(e, out float x, out float y, -1);
					float full = 120.0f * density;
					double dx = Math.Max(-1.0, Math.Min(1.0, (x - startX) / full));
					double dy = Math.Max(-1.0, Math.Min(1.0, (y - startY) / full));
					ReleaseHeld();
					if (fingers >= 2)
					{
						double pinch = Math.Max(-1.0, Math.Min(1.0, (Span(e, -1) - startSpan) / full));
						if (Math.Abs(pinch) > Math.Max(Math.Abs(dx), Math.Abs(dy)))
						{
							Hold(pinch > 0 ? Translations.Command.CameraZoomIn : Translations.Command.CameraZoomOut, Math.Abs(pinch));
						}
						else
						{
							Hold(dx > 0 ? Translations.Command.CameraMoveRight : Translations.Command.CameraMoveLeft, Math.Abs(dx));
							Hold(dy > 0 ? Translations.Command.CameraMoveDown : Translations.Command.CameraMoveUp, Math.Abs(dy));
						}
					}
					else
					{
						Hold(dx > 0 ? Translations.Command.CameraRotateRight : Translations.Command.CameraRotateLeft, Math.Abs(dx));
						Hold(dy > 0 ? Translations.Command.CameraRotateDown : Translations.Command.CameraRotateUp, Math.Abs(dy));
					}

					return true;
				}
				case MotionEventActions.Up:
				case MotionEventActions.Cancel:
					if (onCabControl)
					{
						onCabControl = false;
						controls.Post(session => controls.Cab.Release(session, controls));
					}

					ReleaseHeld();
					fingers = 0;
					return true;
			}

			return true;
		}

		private Action stopZoom;

		/// <summary>A mouse wheel turn zooms, a short burst of the zoom command per notch. Returns whether it was one.</summary>
		public bool OnGenericMotion(MotionEvent e)
		{
			if ((e.Source & InputSourceType.ClassPointer) != InputSourceType.ClassPointer || e.ActionMasked != MotionEventActions.Scroll)
			{
				return false;
			}

			float wheel = e.GetAxisValue(Axis.Vscroll);
			if (wheel == 0.0f)
			{
				return false;
			}

			Translations.Command zoom = wheel > 0 ? Translations.Command.CameraZoomIn : Translations.Command.CameraZoomOut;
			controls.Analog(Translations.Command.CameraZoomIn, 0.0);
			controls.Analog(Translations.Command.CameraZoomOut, 0.0);
			controls.Analog(zoom, Math.Min(1.0, Math.Abs(wheel)));
			stopZoom ??= () =>
			{
				controls.Analog(Translations.Command.CameraZoomIn, 0.0);
				controls.Analog(Translations.Command.CameraZoomOut, 0.0);
			};
			view.RemoveCallbacks(stopZoom);
			view.PostDelayed(stopZoom, 150);
			return true;
		}

		private void Hold(Translations.Command command, double strength)
		{
			// A small dead zone, so that a tap does not nudge the view.
			if (strength < 0.08 || heldCount == heldCommands.Length)
			{
				return;
			}

			controls.Analog(command, strength);
			heldCommands[heldCount++] = command;
		}

		private void ReleaseHeld()
		{
			for (int i = 0; i < heldCount; i++)
			{
				controls.Analog(heldCommands[i], 0.0);
			}

			heldCount = 0;
		}

		private static void Centroid(MotionEvent e, out float x, out float y, int skip)
		{
			x = 0.0f;
			y = 0.0f;
			int n = 0;
			for (int i = 0; i < e.PointerCount; i++)
			{
				if (i != skip)
				{
					x += e.GetX(i);
					y += e.GetY(i);
					n++;
				}
			}

			if (n > 0)
			{
				x /= n;
				y /= n;
			}
		}

		/// <summary>The fingers' average distance from their centre, doubled: zero for one finger.</summary>
		private static float Span(MotionEvent e, int skip)
		{
			Centroid(e, out float cx, out float cy, skip);
			float total = 0.0f;
			int n = 0;
			for (int i = 0; i < e.PointerCount; i++)
			{
				if (i != skip)
				{
					float dx = e.GetX(i) - cx;
					float dy = e.GetY(i) - cy;
					total += (float)Math.Sqrt(dx * dx + dy * dy);
					n++;
				}
			}

			return n > 1 ? total / n * 2.0f : 0.0f;
		}
	}
}
