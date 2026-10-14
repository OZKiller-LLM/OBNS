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
using System.Text;
using Android.Util;
using Android.Views;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>A key bound to a command: upstream's key name (OpenBveApi.Input.Key) and modifiers (Shift 1, Ctrl 2, Alt 4).</summary>
	public sealed record KeyBinding(Translations.Command Command, string Key, int Modifiers);

	/// <summary>A controller button (an Android key code) bound to a command.</summary>
	public sealed record PadBinding(Translations.Command Command, Keycode Button);

	/// <summary>
	/// The keyboard and controller bindings, as the Controls page edits them and KeyboardInput
	/// uses them.
	/// </summary>
	/// <remarks>
	/// Keyboard bindings are upstream's own: read from and written to Settings/1.5.0/controls.cfg
	/// in its format, falling back to Data/Controls/Default.controls. Upstream binds joysticks by
	/// device, axis and hat, which Android does not expose alike, so controller buttons are kept
	/// apart, in Settings/android/pad.cfg ("COMMAND, pad, ButtonA"), with a built-in layout as
	/// their default.
	/// </remarks>
	public sealed class ControlBindings
	{
		private const string Tag = "OpenBVE";

		public List<KeyBinding> Keys { get; } = new List<KeyBinding>();

		public List<PadBinding> Pad { get; } = new List<PadBinding>();

		/// <summary>Where the keyboard bindings came from, for the log.</summary>
		public string KeysSource { get; private set; } = "none";

		/*
		 * The built-in controller layout, after the standard pad's shape: triggers notch power
		 * (right) and brake (left) up, bumpers notch them back, the right stick click is the
		 * emergency brake; A is the ATS acknowledge (S), B the horn, X and Y the doors; the D-pad
		 * sets the reverser (up, down) and steps the view (left, right); left stick click is the
		 * cab view; Start pauses and Select opens the timetable. Power and brake name both the
		 * two-handle and the single-handle commands: the train's handles take the ones that apply.
		 */
		public static readonly PadBinding[] DefaultPad =
		{
			new PadBinding(Translations.Command.PowerIncrease, Keycode.ButtonR2),
			new PadBinding(Translations.Command.SinglePower, Keycode.ButtonR2),
			new PadBinding(Translations.Command.PowerDecrease, Keycode.ButtonR1),
			new PadBinding(Translations.Command.SingleNeutral, Keycode.ButtonR1),
			new PadBinding(Translations.Command.BrakeIncrease, Keycode.ButtonL2),
			new PadBinding(Translations.Command.SingleBrake, Keycode.ButtonL2),
			new PadBinding(Translations.Command.BrakeDecrease, Keycode.ButtonL1),
			new PadBinding(Translations.Command.SingleNeutral, Keycode.ButtonL1),
			new PadBinding(Translations.Command.BrakeEmergency, Keycode.ButtonThumbr),
			new PadBinding(Translations.Command.SingleEmergency, Keycode.ButtonThumbr),
			new PadBinding(Translations.Command.SecurityS, Keycode.ButtonA),
			new PadBinding(Translations.Command.HornPrimary, Keycode.ButtonB),
			new PadBinding(Translations.Command.DoorsLeft, Keycode.ButtonX),
			new PadBinding(Translations.Command.DoorsRight, Keycode.ButtonY),
			new PadBinding(Translations.Command.CameraInterior, Keycode.ButtonThumbl),
			new PadBinding(Translations.Command.MiscPause, Keycode.ButtonStart),
			new PadBinding(Translations.Command.TimetableToggle, Keycode.ButtonSelect),
			new PadBinding(Translations.Command.ReverserForward, Keycode.DpadUp),
			new PadBinding(Translations.Command.ReverserBackward, Keycode.DpadDown),
			new PadBinding(Translations.Command.CameraPOIPrevious, Keycode.DpadLeft),
			new PadBinding(Translations.Command.CameraPOINext, Keycode.DpadRight)
		};

		private static string KeysFile(string settingsFolder) => Path.Combine(settingsFolder, "1.5.0", "controls.cfg");

		private static string PadFile(string settingsFolder) => Path.Combine(settingsFolder, "android", "pad.cfg");

		/// <summary>The player's bindings, or the defaults where they have none.</summary>
		public static ControlBindings Load(string settingsFolder, string dataFolder)
		{
			ControlBindings bindings = new ControlBindings();
			string keys = settingsFolder == null ? null : KeysFile(settingsFolder);
			if (keys == null || !File.Exists(keys))
			{
				keys = dataFolder == null ? null : Path.Combine(dataFolder, "Default.controls");
			}

			if (keys != null && File.Exists(keys))
			{
				bindings.ReadKeys(keys);
				bindings.KeysSource = keys;
			}

			string pad = settingsFolder == null ? null : PadFile(settingsFolder);
			if (pad != null && File.Exists(pad))
			{
				bindings.ReadPad(pad);
			}
			else
			{
				bindings.Pad.AddRange(DefaultPad);
			}

			return bindings;
		}

		/// <summary>Writes the player's bindings (upstream's controls.cfg format for the keyboard).</summary>
		public void Save(string settingsFolder)
		{
			StringBuilder keys = new StringBuilder();
			keys.AppendLine("; Current control configuration");
			keys.AppendLine("; =============================");
			keys.AppendLine("; This file was automatically generated. Please modify only if you know what you're doing.");
			keys.AppendLine("; Written by OpenBVE for Android");
			keys.AppendLine();
			foreach (KeyBinding binding in Keys)
			{
				keys.AppendLine(Name(binding.Command) + ", Keyboard, " + binding.Key + ", " + binding.Modifiers + ", 0");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(KeysFile(settingsFolder)));
			File.WriteAllText(KeysFile(settingsFolder), keys.ToString(), new UTF8Encoding(true));

			StringBuilder pad = new StringBuilder();
			pad.AppendLine("; Controller buttons for OpenBVE for Android: COMMAND, pad, Android button name");
			foreach (PadBinding binding in Pad)
			{
				pad.AppendLine(Name(binding.Command) + ", pad, " + binding.Button);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(PadFile(settingsFolder)));
			File.WriteAllText(PadFile(settingsFolder), pad.ToString(), new UTF8Encoding(false));
		}

		/// <summary>Removes the player's bindings, so the defaults apply again.</summary>
		public static void Reset(string settingsFolder)
		{
			foreach (string file in new[] { KeysFile(settingsFolder), PadFile(settingsFolder) })
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
		}

		private void ReadKeys(string file)
		{
			foreach (string[] parts in Lines(file))
			{
				if (parts.Length >= 4 && parts[1].Equals("keyboard", StringComparison.OrdinalIgnoreCase) &&
				    Commands.TryGetValue(parts[0], out Translations.Command command) && int.TryParse(parts[3], out int modifiers))
				{
					Keys.Add(new KeyBinding(command, parts[2], modifiers));
				}
			}
		}

		private void ReadPad(string file)
		{
			foreach (string[] parts in Lines(file))
			{
				if (parts.Length >= 3 && parts[1].Equals("pad", StringComparison.OrdinalIgnoreCase) &&
				    Commands.TryGetValue(parts[0], out Translations.Command command) && Enum.TryParse(parts[2], true, out Keycode button))
				{
					Pad.Add(new PadBinding(command, button));
				}
			}
		}

		private static IEnumerable<string[]> Lines(string file)
		{
			foreach (string raw in File.ReadAllLines(file))
			{
				string line = raw.Trim();
				int comment = line.IndexOf(';');
				if (comment >= 0)
				{
					line = line.Substring(0, comment).Trim();
				}

				if (line.Length > 0)
				{
					yield return line.Split(',').Select(p => p.Trim()).ToArray();
				}
			}
		}

		/// <summary>Commands by upstream's names (POWER_INCREASE...).</summary>
		public static readonly Dictionary<string, Translations.Command> Commands = Translations.CommandInfos.Values
			.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(g => g.Key, g => g.First().Command, StringComparer.OrdinalIgnoreCase);

		public static string Name(Translations.Command command) =>
			Translations.CommandInfos.TryGetValue(command, out Translations.CommandInfo info) ? info.Name : command.ToString();

		/// <summary>The command as the player reads it: upstream's translated description, else its name.</summary>
		public static string Describe(Translations.Command command)
		{
			try
			{
				string description = Translations.CommandInfos[command].Description;
				if (!string.IsNullOrWhiteSpace(description))
				{
					return description;
				}
			}
			catch (Exception)
			{
				// No translation loaded: the name will do.
			}

			return Name(command);
		}

		/// <summary>A key binding as the player reads it: "Ctrl+T".</summary>
		public static string Describe(KeyBinding binding)
		{
			return ((binding.Modifiers & 2) != 0 ? "Ctrl+" : string.Empty) + ((binding.Modifiers & 1) != 0 ? "Shift+" : string.Empty) +
			       ((binding.Modifiers & 4) != 0 ? "Alt+" : string.Empty) + binding.Key;
		}

		/// <summary>A controller button as the player reads it: "A", "RT", "D-pad up".</summary>
		public static string Describe(Keycode button)
		{
			switch (button)
			{
				case Keycode.ButtonR2: return "RT";
				case Keycode.ButtonL2: return "LT";
				case Keycode.ButtonR1: return "RB";
				case Keycode.ButtonL1: return "LB";
				case Keycode.ButtonThumbl: return "L3";
				case Keycode.ButtonThumbr: return "R3";
				case Keycode.DpadUp: return "D-pad up";
				case Keycode.DpadDown: return "D-pad down";
				case Keycode.DpadLeft: return "D-pad left";
				case Keycode.DpadRight: return "D-pad right";
				default:
					string name = button.ToString();
					return name.StartsWith("Button", StringComparison.Ordinal) ? name.Substring("Button".Length) : name;
			}
		}

		// --- upstream key names from Android key codes ---

		/// <summary>OpenTK's key names have aliases; comparisons use one of each, lower case.</summary>
		public static string Normalise(string key)
		{
			switch (key.ToLowerInvariant())
			{
				case "lbracket": return "bracketleft";
				case "rbracket": return "bracketright";
				case "keypadsubtract": return "keypadminus";
				case "keypadadd": return "keypadplus";
				case "keypaddecimal": return "keypadperiod";
				case "back": return "backspace";
				case "lshift": return "shiftleft";
				case "rshift": return "shiftright";
				case "lcontrol": return "controlleft";
				case "rcontrol": return "controlright";
				case "grave": return "tilde";
				default: return key.ToLowerInvariant();
			}
		}

		/// <summary>Android's key codes by the normalised OpenTK name the bindings use.</summary>
		public static readonly Dictionary<Keycode, string> KeyNames = BuildKeyNames();

		/// <summary>Upstream's spelling of each normalised key name ("comma" to "Comma"), for writing controls.cfg.</summary>
		private static readonly Dictionary<string, string> Spellings = BuildSpellings();

		/// <summary>The upstream key name for an Android key code, or null if it has none.</summary>
		public static string KeyName(Keycode code)
		{
			return KeyNames.TryGetValue(code, out string name) ? (Spellings.TryGetValue(name, out string spelt) ? spelt : name) : null;
		}

		private static Dictionary<string, string> BuildSpellings()
		{
			Dictionary<string, string> spellings = new Dictionary<string, string>();
			string[] names = Enum.GetNames(typeof(OpenBveApi.Input.Key));
			// Canonical spellings first (the ones whose own lower case is the normalised form), then aliases.
			foreach (string name in names.Where(n => Normalise(n) == n.ToLowerInvariant()).Concat(names))
			{
				spellings.TryAdd(Normalise(name), name);
			}

			return spellings;
		}

		private static Dictionary<Keycode, string> BuildKeyNames()
		{
			Dictionary<Keycode, string> names = new Dictionary<Keycode, string>();
			for (int i = 0; i < 26; i++)
			{
				names[Keycode.A + i] = ((char)('a' + i)).ToString();
			}

			for (int i = 0; i < 10; i++)
			{
				names[Keycode.Num0 + i] = "number" + i;
				names[Keycode.Numpad0 + i] = "keypad" + i;
			}

			for (int i = 0; i < 12; i++)
			{
				names[Keycode.F1 + i] = "f" + (i + 1);
			}

			(Keycode, string)[] others =
			{
				(Keycode.Space, "space"), (Keycode.Enter, "enter"), (Keycode.NumpadEnter, "keypadenter"), (Keycode.Escape, "escape"),
				(Keycode.Tab, "tab"), (Keycode.Del, "backspace"), (Keycode.ForwardDel, "delete"), (Keycode.Insert, "insert"),
				(Keycode.MoveHome, "home"), (Keycode.MoveEnd, "end"), (Keycode.PageUp, "pageup"), (Keycode.PageDown, "pagedown"),
				(Keycode.DpadLeft, "left"), (Keycode.DpadRight, "right"), (Keycode.DpadUp, "up"), (Keycode.DpadDown, "down"),
				(Keycode.Comma, "comma"), (Keycode.Period, "period"), (Keycode.Slash, "slash"), (Keycode.Semicolon, "semicolon"),
				(Keycode.Minus, "minus"), (Keycode.Equals, "plus"), (Keycode.Plus, "plus"), (Keycode.LeftBracket, "bracketleft"),
				(Keycode.RightBracket, "bracketright"), (Keycode.Apostrophe, "quote"), (Keycode.Grave, "tilde"),
				(Keycode.Backslash, "backslash"), (Keycode.Break, "pause"), (Keycode.NumpadDivide, "keypaddivide"),
				(Keycode.NumpadMultiply, "keypadmultiply"), (Keycode.NumpadSubtract, "keypadminus"), (Keycode.NumpadAdd, "keypadplus"),
				(Keycode.NumpadDot, "keypadperiod")
			};
			foreach ((Keycode code, string name) in others)
			{
				names[code] = name;
			}

			return names;
		}

		/// <summary>Whether a key code is a controller button (not a keyboard key or the D-pad).</summary>
		public static bool IsPadButton(Keycode code) => code >= Keycode.ButtonA && code <= Keycode.ButtonMode || code >= Keycode.Button1 && code <= Keycode.Button16;
	}
}
