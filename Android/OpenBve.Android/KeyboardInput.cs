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
using System.IO;
using System.Linq;
using Android.Util;
using Android.Views;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// Keyboard and game controller input: upstream's key bindings (the player's controls.cfg if
	/// there is one, else Data/Controls/Default.controls), and a fixed layout for controllers:
	/// triggers notch power (right) and brake (left) up, bumpers notch them back, right stick
	/// click is the emergency brake; A is the ATS acknowledge (S), B the horn, X and Y the left
	/// and right doors; the D-pad sets the reverser (up, down) and steps the view (left, right);
	/// the right stick looks around and the left moves the camera; left stick click is the cab
	/// view; Start pauses and Select opens the timetable.
	/// </summary>
	/// <remarks>
	/// Keys are matched with their modifiers on press, as upstream's do, and every command a key
	/// pressed is released when it comes up, whatever the modifiers are by then. Held keys repeat
	/// through the commands' own handling, not the keyboard's auto-repeat.
	/// </remarks>
	public class KeyboardInput
	{
		private const string Tag = "OpenBVE";

		/// <summary>Commands the activity acts on rather than the simulation.</summary>
		public enum Special
		{
			None,
			Pause,
			Timetable
		}

		private readonly Dictionary<(string Key, int Modifiers), List<Translations.Command>> bindings =
			new Dictionary<(string, int), List<Translations.Command>>();

		private readonly Dictionary<Keycode, List<Translations.Command>> held = new Dictionary<Keycode, List<Translations.Command>>();
		private readonly Dictionary<Translations.Command, double> analogHeld = new Dictionary<Translations.Command, double>();

		/// <summary>The number of key bindings read.</summary>
		public int Count { get; }

		/// <summary>The controller buttons and what they do (ControlBindings.Pad).</summary>
		private readonly Dictionary<Keycode, Translations.Command[]> pad;

		public KeyboardInput(string settingsFolder, string dataFolder)
		{
			// The player's bindings from the Controls page, or upstream's defaults and the built-in pad layout.
			ControlBindings loaded = ControlBindings.Load(settingsFolder, dataFolder);
			foreach (KeyBinding binding in loaded.Keys)
			{
				(string, int) key = (ControlBindings.Normalise(binding.Key), binding.Modifiers);
				if (!bindings.TryGetValue(key, out List<Translations.Command> list))
				{
					bindings[key] = list = new List<Translations.Command>();
				}

				list.Add(binding.Command);
				Count++;
			}

			pad = loaded.Pad.GroupBy(b => b.Button).ToDictionary(g => g.Key, g => g.Select(b => b.Command).ToArray());
			Log.Info(Tag, "read " + Count + " key bindings from " + loaded.KeysSource + ", " + loaded.Pad.Count + " controller bindings");
		}

		/// <summary>
		/// Handles a key or controller button going down or up. Returns false for keys with no
		/// binding, which are left to the system (volume, Back and so on).
		/// </summary>
		/// <param name="special">A command for the activity, on a press.</param>
		public bool Handle(KeyEvent e, AndroidControls controls, out Special special)
		{
			special = Special.None;
			Keycode code = e.KeyCode;
			if (e.Action == KeyEventActions.Up)
			{
				if (!held.TryGetValue(code, out List<Translations.Command> pressed))
				{
					return Bound(e) != null;
				}

				held.Remove(code);
				foreach (Translations.Command command in pressed)
				{
					controls.Release(command, exact: true);
				}

				return true;
			}

			if (e.Action != KeyEventActions.Down)
			{
				return false;
			}

			List<Translations.Command> bound = Bound(e);
			if (bound == null)
			{
				return false;
			}

			// The keyboard's auto-repeat: the commands repeat by themselves while held.
			if (e.RepeatCount > 0 || held.ContainsKey(code) || axisHeld.Contains(code))
			{
				return true;
			}

			List<Translations.Command> sent = new List<Translations.Command>();
			foreach (Translations.Command command in bound)
			{
				switch (command)
				{
					case Translations.Command.MenuActivate:
					case Translations.Command.MiscPause:
					case Translations.Command.MiscQuit:
						special = Special.Pause;
						continue;
					case Translations.Command.TimetableToggle:
						special = Special.Timetable;
						continue;
					// The pause menu is an Android view and takes its own keys.
					case Translations.Command.MenuUp:
					case Translations.Command.MenuDown:
					case Translations.Command.MenuEnter:
					case Translations.Command.MenuBack:
					case Translations.Command.MiscFullscreen:
						continue;
				}

				controls.Press(command, exact: true);
				sent.Add(command);
			}

			held[code] = sent;
			/*
			 * One line per key press (not per frame): what a key did is the first question when
			 * one seems dead. Debug level, out of the normal log and the beta log; logcat *:D shows it.
			 */
			Log.Debug(Tag, "key " + code + (e.IsCtrlPressed ? "+Ctrl" : string.Empty) + (e.IsShiftPressed ? "+Shift" : string.Empty) +
			              (e.IsAltPressed ? "+Alt" : string.Empty) + " (" + e.Device?.Name + ") -> " +
			              (special != Special.None ? special + " " : string.Empty) + string.Join(",", sent));
			return true;
		}

		/// <summary>Releases everything held, e.g. when the pause menu takes the keys mid-press.</summary>
		public void ReleaseAll(AndroidControls controls)
		{
			foreach (List<Translations.Command> pressed in held.Values)
			{
				foreach (Translations.Command command in pressed)
				{
					controls.Release(command, exact: true);
				}
			}

			held.Clear();
			foreach (Keycode button in axisHeld.ToArray())
			{
				Digital(controls, button, false, PadCommands(button));
			}

			foreach (Translations.Command command in analogHeld.Keys.ToArray())
			{
				Set(controls, command, 0.0);
			}
		}

		/// <summary>
		/// A controller's sticks: the right stick looks around, as a drag on the view does, and
		/// the left stick moves the camera. Returns whether the event was a joystick's.
		/// </summary>
		public bool HandleMotion(MotionEvent e, AndroidControls controls)
		{
			if ((e.Source & InputSourceType.Joystick) != InputSourceType.Joystick || e.Action != MotionEventActions.Move)
			{
				return false;
			}

			Axis(controls, e.GetAxisValue(global::Android.Views.Axis.Z), Translations.Command.CameraRotateRight, Translations.Command.CameraRotateLeft);
			Axis(controls, e.GetAxisValue(global::Android.Views.Axis.Rz), Translations.Command.CameraRotateDown, Translations.Command.CameraRotateUp);
			Axis(controls, e.GetAxisValue(global::Android.Views.Axis.X), Translations.Command.CameraMoveRight, Translations.Command.CameraMoveLeft);
			Axis(controls, e.GetAxisValue(global::Android.Views.Axis.Y), Translations.Command.CameraMoveBackward, Translations.Command.CameraMoveForward);
			HatAndTriggers(e, controls);
			return true;
		}

		/*
		 * Many controllers report the D-pad as a hat and the triggers as axes instead of (or as
		 * well as) keys. Each is turned into the press and release its button would send; a pad
		 * sending both is harmless, as the key's press is ignored while the axis holds it.
		 */
		private void HatAndTriggers(MotionEvent e, AndroidControls controls)
		{
			float hatX = e.GetAxisValue(global::Android.Views.Axis.HatX), hatY = e.GetAxisValue(global::Android.Views.Axis.HatY);
			float right = Math.Max(e.GetAxisValue(global::Android.Views.Axis.Rtrigger), e.GetAxisValue(global::Android.Views.Axis.Gas));
			float left = Math.Max(e.GetAxisValue(global::Android.Views.Axis.Ltrigger), e.GetAxisValue(global::Android.Views.Axis.Brake));
			Digital(controls, Keycode.DpadLeft, hatX < -0.5f, PadCommands(Keycode.DpadLeft));
			Digital(controls, Keycode.DpadRight, hatX > 0.5f, PadCommands(Keycode.DpadRight));
			Digital(controls, Keycode.DpadUp, hatY < -0.5f, PadCommands(Keycode.DpadUp));
			Digital(controls, Keycode.DpadDown, hatY > 0.5f, PadCommands(Keycode.DpadDown));
			Digital(controls, Keycode.ButtonR2, right > 0.5f, PadCommands(Keycode.ButtonR2));
			Digital(controls, Keycode.ButtonL2, left > 0.5f, PadCommands(Keycode.ButtonL2));
		}

		private readonly HashSet<Keycode> axisHeld = new HashSet<Keycode>();

		private void Digital(AndroidControls controls, Keycode button, bool down, Translations.Command[] commands)
		{
			if (down == axisHeld.Contains(button) || down && held.ContainsKey(button))
			{
				return;
			}

			foreach (Translations.Command command in commands)
			{
				if (down)
				{
					controls.Press(command, exact: true);
				}
				else
				{
					controls.Release(command, exact: true);
				}
			}

			if (down)
			{
				axisHeld.Add(button);
				Log.Debug(Tag, "pad axis " + button + " -> " + string.Join(",", commands));
			}
			else
			{
				axisHeld.Remove(button);
			}
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
			/*
			 * Small changes are skipped so a hand's tremor does not flood the queue - but never
			 * the return to zero: a stick let go from just past the dead zone (strength 0.01, say)
			 * would otherwise leave that strength held and the camera creeping for ever.
			 */
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

		private static bool FromController(InputEvent e)
		{
			return (e.Source & InputSourceType.Gamepad) == InputSourceType.Gamepad || (e.Source & InputSourceType.Joystick) == InputSourceType.Joystick;
		}

		private List<Translations.Command> Lookup(KeyEvent e)
		{
			// Controller buttons; a controller's D-pad arrives as the arrow keys, and has its own meaning.
			if ((ControlBindings.IsPadButton(e.KeyCode) || FromController(e)) && pad.TryGetValue(e.KeyCode, out Translations.Command[] commands))
			{
				return commands.ToList();
			}

			return null;
		}

		private List<Translations.Command> Bound(KeyEvent e)
		{
			List<Translations.Command> buttons = Lookup(e);
			if (buttons != null || !ControlBindings.KeyNames.TryGetValue(e.KeyCode, out string name))
			{
				return buttons;
			}

			int modifiers = (e.IsShiftPressed ? 1 : 0) | (e.IsCtrlPressed ? 2 : 0) | (e.IsAltPressed ? 4 : 0);
			return bindings.TryGetValue((name, modifiers), out List<Translations.Command> list) ? list : null;
		}

		/// <summary>What a controller button (or D-pad direction) does, or nothing.</summary>
		private Translations.Command[] PadCommands(Keycode code)
		{
			return pad.TryGetValue(code, out Translations.Command[] commands) ? commands : Array.Empty<Translations.Command>();
		}
	}
}
