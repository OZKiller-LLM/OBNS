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
using System.Runtime.InteropServices;
using OpenBve.Android;
using OpenBveApi.Interface;

namespace OpenBve.Switch
{
	/// <summary>
	/// A USB mouse (OBNS/SWITCH.md, "Keyboard and mouse stay supported"; the user, 2026-11-25), docked or handheld, as
	/// Android's mouse on the scene (SceneInput.cs): a click on one of the cab's touch areas works it (CabTouch); dragging
	/// with the left button elsewhere looks around and with the right moves the camera, each as an analog command whose
	/// strength grows with the distance dragged; the wheel zooms. In the menus its left button is a touch where the pointer
	/// is (runner/source/obns_input.c). The pointer is drawn while the mouse is in use.
	/// </summary>
	public sealed class SwitchMouse
	{
		[DllImport("__Internal", EntryPoint = "obns_input_mouse")]
		private static extern unsafe int ReadMouse(int* x, int* y, int* dx, int* dy, int* wheel, uint* buttons);

		private const uint Left = 1, Right = 2;

		/// <summary>Seconds the pointer stays drawn after the mouse last moved or clicked.</summary>
		private const double PointerSeconds = 3.0;

		/// <summary>The drag, in screen units (of 1280x720), that gives an analog command its full strength.</summary>
		private const float FullDrag = 180.0f;

		private static float pointerX, pointerY;
		private static double usedAt = double.NegativeInfinity;
		private static bool connected;

		private uint buttons;
		private float startX, startY;
		private bool onCab, onCard;
		private float lastX, lastY;
		private double zoomUntil;
		private readonly Translations.Command[] heldCommands = new Translations.Command[4];
		private int heldCount;

		/// <summary>Whether a mouse is plugged in (as of the last read).</summary>
		public static bool Connected => connected;

		/// <summary>Reads the mouse: its place, and whether it was used just now. Once a frame, by whoever draws.</summary>
		public static unsafe uint Read(double now, out int wheel)
		{
			int x, y, dx, dy, w;
			uint held;
			connected = ReadMouse(&x, &y, &dx, &dy, &w, &held) != 0;
			wheel = w;
			if (!connected)
			{
				return 0;
			}

			if (dx != 0 || dy != 0 || w != 0 || held != 0 || x != (int)pointerX || y != (int)pointerY)
			{
				usedAt = now;
			}

			pointerX = x;
			pointerY = y;
			return held;
		}

		/// <summary>Draws the pointer (in the menus' 1280x720 units) while the mouse is in use.</summary>
		public static void DrawPointer(IMenuCanvas canvas, double now)
		{
			if (!connected || now - usedAt > PointerSeconds)
			{
				return;
			}

			// A dot with a dark rim, seen on any background.
			canvas.Fill(pointerX - 7, pointerY - 7, 14, 14, 0xE0202020, 7);
			canvas.Fill(pointerX - 5, pointerY - 5, 10, 10, 0xFFFFFFFF, 5);
		}

		/// <summary>
		/// The mouse on the scene, once a frame while driving (no menu open): the cab's controls, the camera, the zoom.
		/// <paramref name="sceneWidth"/> is the scene's width in pixels (the cab's touch areas are in them).
		/// </summary>
		public void Update(AndroidTrainSession session, AndroidControls controls, double now, int sceneWidth, TimetableCard card = null)
		{
			uint held = Read(now, out int wheel);
			uint pressed = held & ~buttons, released = buttons & ~held;
			buttons = held;
			float moveX = pointerX - lastX, moveY = pointerY - lastY;
			lastX = pointerX;
			lastY = pointerY;
			if (controls == null || session == null)
			{
				return;
			}

			// Over the route card's map or gradient: dragged with the left button it pans, the wheel zooms (the camera stays).
			if ((pressed & Left) != 0 && card != null && card.RouteContains(pointerX, pointerY))
			{
				onCard = true;
			}

			if (onCard)
			{
				if ((held & Left) == 0)
				{
					onCard = false;
				}
				else
				{
					card?.RouteDrag(moveX, moveY);
				}

				return;
			}

			if (wheel != 0 && card != null && card.RouteContains(pointerX, pointerY))
			{
				card.RouteZoom(wheel);
				wheel = 0;
			}

			float scale = sceneWidth / 1280.0f;
			if ((pressed & Left) != 0)
			{
				// First, is it on a cab control? A pointer should land on it, unlike a fingertip.
				int element = controls.Cab.HitTest(pointerX * scale, pointerY * scale, 2.0f * scale);
				if (element >= 0)
				{
					onCab = true;
					controls.Cab.Press(session, controls, element);
				}
			}

			if ((pressed & (Left | Right)) != 0)
			{
				ReleaseHeld(controls);
				startX = pointerX;
				startY = pointerY;
			}

			if ((released & Left) != 0 && onCab)
			{
				onCab = false;
				controls.Cab.Release(session, controls);
			}

			ReleaseHeld(controls);
			if ((held & (Left | Right)) != 0 && !onCab)
			{
				double dx = Math.Max(-1.0, Math.Min(1.0, (pointerX - startX) / FullDrag));
				double dy = Math.Max(-1.0, Math.Min(1.0, (pointerY - startY) / FullDrag));
				if ((held & Left) != 0)
				{
					Hold(controls, dx > 0 ? Translations.Command.CameraRotateRight : Translations.Command.CameraRotateLeft, Math.Abs(dx));
					Hold(controls, dy > 0 ? Translations.Command.CameraRotateDown : Translations.Command.CameraRotateUp, Math.Abs(dy));
				}
				else
				{
					Hold(controls, dx > 0 ? Translations.Command.CameraMoveRight : Translations.Command.CameraMoveLeft, Math.Abs(dx));
					Hold(controls, dy > 0 ? Translations.Command.CameraMoveDown : Translations.Command.CameraMoveUp, Math.Abs(dy));
				}
			}

			// The wheel zooms while it turns, and a little after (its notches come a few frames apart).
			if (wheel != 0)
			{
				controls.Analog(wheel > 0 ? Translations.Command.CameraZoomOut : Translations.Command.CameraZoomIn, 0.0);
				controls.Analog(wheel > 0 ? Translations.Command.CameraZoomIn : Translations.Command.CameraZoomOut, 0.6);
				zoomUntil = now + 0.15;
			}
			else if (zoomUntil > 0 && now > zoomUntil)
			{
				zoomUntil = 0;
				controls.Analog(Translations.Command.CameraZoomIn, 0.0);
				controls.Analog(Translations.Command.CameraZoomOut, 0.0);
			}
		}

		/// <summary>Lets go of everything the mouse holds (a menu opened over the game).</summary>
		public void ReleaseAll(AndroidTrainSession session, AndroidControls controls)
		{
			ReleaseHeld(controls);
			if (onCab && session != null && controls != null)
			{
				controls.Cab.Release(session, controls);
			}

			onCab = false;
			onCard = false;
			if (zoomUntil > 0 && controls != null)
			{
				controls.Analog(Translations.Command.CameraZoomIn, 0.0);
				controls.Analog(Translations.Command.CameraZoomOut, 0.0);
			}

			zoomUntil = 0;
			buttons = 0;
		}

		private void Hold(AndroidControls controls, Translations.Command command, double strength)
		{
			// A small dead zone, so that a click does not nudge the view.
			if (strength < 0.08 || heldCount == heldCommands.Length)
			{
				return;
			}

			controls.Analog(command, strength);
			heldCommands[heldCount++] = command;
		}

		private void ReleaseHeld(AndroidControls controls)
		{
			for (int i = 0; i < heldCount; i++)
			{
				controls?.Analog(heldCommands[i], 0.0);
			}

			heldCount = 0;
		}
	}
}
