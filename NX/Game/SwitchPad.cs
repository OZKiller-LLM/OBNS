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
using System.Runtime.InteropServices;
using OpenBve.Android;
using OpenBveApi.Interface;

namespace OpenBve.Switch
{
	/// <summary>
	/// The console's controllers driving the train (OBNS/SWITCH.md: controls as the Android branch's): Android's built-in
	/// controller layout (ControlBindings.DefaultPad) by the buttons' names - ZR and ZL notch power and brake up, R and L
	/// notch them back, the right stick's click is the emergency brake; A the safety system's acknowledge, B the horn, X
	/// and Y the doors; the D-pad's up and down the reverser, left and right the views; the left stick's click the cab;
	/// + pauses and − opens the timetable. Presses and releases go to AndroidControls, as Android's controller input does.
	/// </summary>
	public sealed class SwitchPad
	{
		[DllImport("__Internal", EntryPoint = "obns_input_pad")]
		private static extern unsafe ulong ReadPad(float* leftX, float* leftY, float* rightX, float* rightY);

		// libnx's HidNpadButton bits.
		public const ulong A = 1UL << 0, B = 1UL << 1, X = 1UL << 2, Y = 1UL << 3, StickL = 1UL << 4, StickR = 1UL << 5;
		public const ulong L = 1UL << 6, R = 1UL << 7, ZL = 1UL << 8, ZR = 1UL << 9, Plus = 1UL << 10, Minus = 1UL << 11;
		public const ulong Left = 1UL << 12, Up = 1UL << 13, Right = 1UL << 14, Down = 1UL << 15;

		/// <summary>Android's DefaultPad, by button: power and brake name both the two-handle and the single-handle
		/// commands, and the train's handles take the ones that apply.</summary>
		private static readonly (ulong Button, Translations.Command Command)[] Bindings =
		{
			(ZR, Translations.Command.PowerIncrease), (ZR, Translations.Command.SinglePower),
			(R, Translations.Command.PowerDecrease), (R, Translations.Command.SingleNeutral),
			(ZL, Translations.Command.BrakeIncrease), (ZL, Translations.Command.SingleBrake),
			(L, Translations.Command.BrakeDecrease), (L, Translations.Command.SingleNeutral),
			(StickR, Translations.Command.BrakeEmergency), (StickR, Translations.Command.SingleEmergency),
			(A, Translations.Command.SecurityS),
			(B, Translations.Command.HornPrimary),
			(X, Translations.Command.DoorsLeft),
			(Y, Translations.Command.DoorsRight),
			(StickL, Translations.Command.CameraInterior),
			(Plus, Translations.Command.MiscPause),
			(Minus, Translations.Command.TimetableToggle),
			(Up, Translations.Command.ReverserForward),
			(Down, Translations.Command.ReverserBackward),
			(Left, Translations.Command.CameraPOIPrevious),
			(Right, Translations.Command.CameraPOINext)
		};

		private ulong held;

		/// <summary>The buttons down at the last <see cref="Update"/>.</summary>
		public ulong Held => held;

		/// <summary>The buttons that went down at the last <see cref="Update"/>.</summary>
		public ulong Pressed { get; private set; }

		/// <summary>Both sticks at the last <see cref="Update"/>, -1 to 1 (up positive).</summary>
		public float LeftX, LeftY, RightX, RightY;

		/// <summary>Reads the controllers and sends what changed to the controls (pause and the timetable to the game,
		/// which decides: <paramref name="commands"/> false sends nothing, for a menu over the game).</summary>
		public unsafe void Update(AndroidControls controls, bool commands = true)
		{
			float lx, ly, rx, ry;
			ulong now = ReadPad(&lx, &ly, &rx, &ry);
			LeftX = lx;
			LeftY = ly;
			RightX = rx;
			RightY = ry;
			ulong changed = now ^ held;
			Pressed = now & ~held;
			held = now;
			if (!commands || changed == 0 || controls == null)
			{
				return;
			}

			foreach ((ulong button, Translations.Command command) in Bindings)
			{
				if ((changed & button) == 0 || command == Translations.Command.MiscPause || command == Translations.Command.TimetableToggle)
				{
					continue;
				}

				if ((now & button) != 0)
				{
					controls.Press(command, exact: true);
				}
				else
				{
					controls.Release(command, exact: true);
				}
			}
		}

		/// <summary>Whether the button went down at the last update.</summary>
		public bool Is(ulong button) => (Pressed & button) != 0;

		private readonly Dictionary<Translations.Command, double> analogHeld = new Dictionary<Translations.Command, double>();

		/// <summary>
		/// The sticks move the camera, as Android's controller input does (KeyboardInput.cs): the left stick moves it
		/// (up forward), the right stick turns it - unless <paramref name="rightStickFree"/> is false (the timetable scrolls
		/// with it then). Call after <see cref="Update"/>.
		/// </summary>
		public void Camera(AndroidControls controls, bool rightStickFree)
		{
			if (controls == null)
			{
				return;
			}

			Axis(controls, LeftX, Translations.Command.CameraMoveRight, Translations.Command.CameraMoveLeft);
			Axis(controls, LeftY, Translations.Command.CameraMoveForward, Translations.Command.CameraMoveBackward);
			Axis(controls, rightStickFree ? RightX : 0, Translations.Command.CameraRotateRight, Translations.Command.CameraRotateLeft);
			Axis(controls, rightStickFree ? RightY : 0, Translations.Command.CameraRotateUp, Translations.Command.CameraRotateDown);
		}

		private void Axis(AndroidControls controls, float value, Translations.Command positive, Translations.Command negative)
		{
			// A resting stick rarely reads exactly zero.
			const float deadZone = 0.15f;
			double strength = Math.Abs(value) < deadZone ? 0.0 : (Math.Abs(value) - deadZone) / (1.0 - deadZone);
			Set(controls, positive, value > 0 ? strength : 0.0);
			Set(controls, negative, value < 0 ? strength : 0.0);
		}

		private void Set(AndroidControls controls, Translations.Command command, double strength)
		{
			analogHeld.TryGetValue(command, out double previous);
			// Small changes are skipped so a hand's tremor doesn't flood the queue - but never the return to zero.
			if (strength < 0.03)
			{
				strength = 0.0;
			}

			if (strength == previous || (strength != 0.0 && previous != 0.0 && Math.Abs(previous - strength) < 0.02))
			{
				return;
			}

			analogHeld[command] = strength;
			controls.Analog(command, strength);
		}
	}
}
