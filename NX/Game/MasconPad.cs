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
using TrainManager.Handles;
using TrainManager.Trains;

namespace OpenBve.Switch
{
	/// <summary>
	/// The Switch's Densha de GO! controllers (OBNS/SWITCH.md, "Upstream's train controllers"; the user's priority): HORI's
	/// One Handle controller, the ZUIKI Mascon and the ZUIKI Mascon Pro, as upstream's ZuikiInput plugin reads them on the
	/// PC - the lever's notches from the same axis thresholds (upstream's MasconController), mapped to the train's notches
	/// as its ConfigureMappings does by default (no conversion: the controller's B1 to B8 and P1 to P5 as the train's, the
	/// emergency position the emergency brake when the train has no more than 8 brake notches; air brakes in three steps).
	/// The console's controller service takes them as wired pads (runner/source/obns_input.c), so the lever is the left
	/// stick's Y; the buttons drive as the console's pad does (SwitchPad's layout by name: A the safety system's acknowledge,
	/// B the horn, X and Y the doors, the D-pad's up and down the reverser, + pauses, − the timetable), and the Mascon Pro's
	/// own controls - the reverser, the horn pedal and the ATS button - as upstream reads them.
	/// </summary>
	public sealed class MasconPad
	{
		[DllImport("__Internal", EntryPoint = "obns_mascon_detect")]
		private static extern int NativeDetect();

		[DllImport("__Internal", EntryPoint = "obns_mascon_attach")]
		private static extern int NativeAttach(int attach);

		[DllImport("__Internal", EntryPoint = "obns_mascon_read")]
		private static extern unsafe int NativeRead(float* leftX, float* leftY, float* rightX, float* rightY, ulong* buttons);

		/// <summary>Which controller is plugged in (obns_mascon_detect).</summary>
		public enum Model
		{
			Unknown = -1,
			None = 0,
			HoriOneHandle = 1,
			ZuikiMascon = 2,
			ZuikiMasconPro = 3
		}

		// Upstream's MasconController thresholds on the lever's axis (OpenTK's axis 1, down positive): pairs of from and to,
		// the first pair neutral; brake B1 to B8 and the emergency position, power P1 to P5.
		private static readonly double[] BrakeValues = { -0.15, 0.15, -0.25, -0.15, -0.35, -0.25, -0.45, -0.35, -0.57, -0.45, -0.7, -0.57, -0.8, -0.7, -0.9, -0.8, -0.97, -0.9, -1, -0.97 };
		private static readonly double[] PowerValues = { -0.15, 0.15, 0.15, 0.32, 0.32, 0.52, 0.52, 0.7, 0.7, 0.87, 0.87, 1 };

		/// <summary>The controller's brake notches (B1 to B8) and power notches (P1 to P5), as upstream's capabilities.</summary>
		private const int ControllerBrakeNotches = 8, ControllerPowerNotches = 5, Emergency = 9;

		private static readonly (ulong Button, Translations.Command Command)[] Buttons =
		{
			(SwitchPad.A, Translations.Command.SecurityS),
			(SwitchPad.B, Translations.Command.HornPrimary),
			// As the console's pad (PadBindings, the user, 2026-11-25): Y, on the left, the left doors; X the right.
			(SwitchPad.Y, Translations.Command.DoorsLeft),
			(SwitchPad.X, Translations.Command.DoorsRight),
			(SwitchPad.Up, Translations.Command.ReverserForward),
			(SwitchPad.Down, Translations.Command.ReverserBackward),
			(SwitchPad.Left, Translations.Command.CameraPOIPrevious),
			(SwitchPad.Right, Translations.Command.CameraPOINext)
		};

		/// <summary>The Mascon Pro's ATS button (its report's button 11: the right stick's click on the console).</summary>
		private const ulong AtsButton = SwitchPad.StickR;

		public Model Detected { get; private set; } = Model.None;

		/// <summary>Whether a controller is in use: the game's pad then leaves it out.</summary>
		public bool Active { get; private set; }

		private ulong held, pressed;
		private int brakeNotch = -1, powerNotch = -1, reverser = int.MinValue;
		private bool horn;

		/// <summary>Looks for a controller on USB and, if there is one, takes its pad for the drive; false if there is none.</summary>
		public bool Attach(Action<string> log)
		{
			int model = NativeDetect();
			Detected = (Model)Math.Max(-1, Math.Min(3, model));
			Active = Detected > Model.None && NativeAttach(1) != 0;
			log?.Invoke("controllers: " + (Detected == Model.Unknown ? "the USB host service can't be used, so no train controller is looked for" :
				Detected == Model.None ? "no train controller on USB" :
				Detected + " on USB, " + (Active ? "its pad found and driving the handles" : "but no pad of it found")));
			brakeNotch = powerNotch = -1;
			reverser = int.MinValue;
			return Active;
		}

		/// <summary>Gives the pad back to the game's own (the drive is over).</summary>
		public void Detach()
		{
			if (Active)
			{
				NativeAttach(0);
				Active = false;
			}
		}

		/// <summary>Whether the button went down at the last update (+ and − are the game's: pause, timetable).</summary>
		public bool Is(ulong button) => (pressed & button) != 0;

		/// <summary>The controller's buttons held, as the menus' (MenuButtons): with only it in the dock, the pause menu is
		/// still reachable (the D-pad, A, B, + and −; not the lever).</summary>
		public MenuButtons MenuHeld()
		{
			if (!Active)
			{
				return MenuButtons.None;
			}

			MenuButtons buttons = MenuButtons.None;
			if ((held & SwitchPad.Up) != 0) buttons |= MenuButtons.Up;
			if ((held & SwitchPad.Down) != 0) buttons |= MenuButtons.Down;
			if ((held & SwitchPad.Left) != 0) buttons |= MenuButtons.Left;
			if ((held & SwitchPad.Right) != 0) buttons |= MenuButtons.Right;
			if ((held & SwitchPad.A) != 0) buttons |= MenuButtons.A;
			if ((held & SwitchPad.B) != 0) buttons |= MenuButtons.B;
			if ((held & SwitchPad.X) != 0) buttons |= MenuButtons.X;
			if ((held & SwitchPad.Y) != 0) buttons |= MenuButtons.Y;
			if ((held & SwitchPad.Plus) != 0) buttons |= MenuButtons.Plus;
			if ((held & SwitchPad.Minus) != 0) buttons |= MenuButtons.Minus;
			return buttons;
		}

		/// <summary>Reads the controller and sends what changed: the notches on the render thread, the buttons as presses.</summary>
		public unsafe void Update(AndroidControls controls, bool commands = true)
		{
			pressed = 0;
			if (!Active)
			{
				return;
			}

			float lx, ly, rx, ry;
			ulong now;
			if (NativeRead(&lx, &ly, &rx, &ry, &now) == 0)
			{
				return;
			}

			ulong changed = now ^ held;
			pressed = now & ~held;
			held = now;
			if (!commands || controls == null)
			{
				return;
			}

			// The lever: upstream's axis 1 is the report's Y, down positive; libnx's stick Y is up positive.
			double axis = Math.Round(-ly, 4);
			int brake = 0, power = 0;
			for (int i = 0; i < BrakeValues.Length; i += 2)
			{
				if (axis >= BrakeValues[i] && axis <= BrakeValues[i + 1])
				{
					brake = i / 2;
					break;
				}
			}

			for (int i = 0; i < PowerValues.Length; i += 2)
			{
				if (axis >= PowerValues[i] && axis <= PowerValues[i + 1])
				{
					power = i / 2;
					break;
				}
			}

			if (brake != brakeNotch)
			{
				brakeNotch = brake;
				controls.Post(s => Brake(s.Train, brake));
			}

			if (power != powerNotch)
			{
				powerNotch = power;
				controls.Post(s => Notch(s.Train, Translations.Command.PowerAnyNotch, power));
			}

			foreach ((ulong button, Translations.Command command) in Buttons)
			{
				if ((changed & button) != 0)
				{
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

			if (Detected == Model.ZuikiMasconPro)
			{
				// The reverser: upstream's axis 3 (the right stick's Y on the console, down positive there too).
				double reverserAxis = -ry;
				int position = reverserAxis >= 0.5 ? -1 : reverserAxis <= -0.5 ? 0 : 1;
				if (position != reverser)
				{
					reverser = position;
					controls.Post(s => Notch(s.Train, Translations.Command.ReverserAnyPosition, position));
				}

				// The horn pedal (upstream's axis 0 at 0.75 and over) and the ATS button.
				bool hornNow = lx >= 0.75f;
				if (hornNow != horn)
				{
					horn = hornNow;
					if (hornNow)
					{
						controls.Press(Translations.Command.HornPrimary, exact: true);
					}
					else
					{
						controls.Release(Translations.Command.HornPrimary, exact: true);
					}
				}

				if ((changed & AtsButton) != 0)
				{
					if ((now & AtsButton) != 0)
					{
						controls.Press(Translations.Command.SecurityS, exact: true);
					}
					else
					{
						controls.Release(Translations.Command.SecurityS, exact: true);
					}
				}
			}
		}

		/// <summary>The brake lever's position on the train, as upstream's default mapping: B1 to B8 as they are, the emergency
		/// position the emergency brake when the train has no more notches than the controller; an air brake in three steps.</summary>
		private static void Brake(TrainBase train, int notch)
		{
			if (notch == Emergency && train.Handles.Brake.MaximumNotch <= ControllerBrakeNotches)
			{
				Notch(train, Translations.Command.BrakeEmergency, 0);
				return;
			}

			int option = notch;
			if (train.Handles.Brake is AirBrakeHandle && notch > 0)
			{
				option = Math.Max(0, (int)Math.Round(3.0 / ControllerBrakeNotches * notch, MidpointRounding.AwayFromZero) - 1);
			}

			Notch(train, Translations.Command.BrakeAnyNotch, option);
		}

		/// <summary>A handle command with its notch, straight to the train's handles (upstream's CabHandles.ControlDown), as
		/// upstream's plugin's KeyDown does - not while the AI drives or the train's plugin blocks the driver's input.</summary>
		private static void Notch(TrainBase train, Translations.Command command, int option)
		{
			if (train == null || train.AI != null || train.Plugin != null && train.Plugin.BlockingInput)
			{
				return;
			}

			train.Handles.ControlDown(new Control { Command = command, Option = option });
		}
	}
}
