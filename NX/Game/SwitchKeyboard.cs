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
	/// A USB keyboard driving the train (OBNS/SWITCH.md, "Keyboard and mouse stay supported"; the user, 2026-11-25), with
	/// upstream's key bindings as on the desktop and Android (KeyBindings: UserData/1.5.0/controls.cfg, else upstream's
	/// Default.controls, built into the game; the Controls page changes them). Keys go to AndroidControls as
	/// Android's keyboard input sends them (KeyboardInput.cs): held while the key is, Shift, Ctrl and Alt telling bindings
	/// apart. Escape (and Pause) pauses, Ctrl+T the route card, as + and − do; the menus take the keyboard themselves
	/// (runner/source/obns_input.c: the arrows, Enter, Escape).
	/// </summary>
	public sealed class SwitchKeyboard
	{
		[DllImport("__Internal", EntryPoint = "obns_input_keyboard")]
		private static extern unsafe int ReadKeyboard(ulong* keys, ulong* modifiers);

		/// <summary>What the game does rather than the train.</summary>
		public enum Special
		{
			None,
			Pause,
			Timetable
		}

		/// <summary>The commands each held key (USB usage code) sent, to release them with it.</summary>
		private readonly Dictionary<int, List<Translations.Command>> held = new Dictionary<int, List<Translations.Command>>();

		private readonly ulong[] last = new ulong[4];

		/// <summary>The bindings by key and modifiers, made again when the Controls page changes them.</summary>
		private Dictionary<(string Key, int Modifiers), List<Translations.Command>> bindings;
		private int bindingsVersion = -1;

		/// <summary>Whether a key has been pressed since the game started (a keyboard is plugged in and used).</summary>
		public bool Used { get; private set; }

		/// <summary>How many key bindings there are, and from where, for the log.</summary>
		public string Description => KeyBindings.Current.Count + " key bindings from " + KeyBindings.Source;

		private Dictionary<(string Key, int Modifiers), List<Translations.Command>> Bindings()
		{
			IReadOnlyList<KeyBindings.Binding> now = KeyBindings.Current;
			if (bindings == null || bindingsVersion != KeyBindings.Version)
			{
				bindings = new Dictionary<(string, int), List<Translations.Command>>();
				foreach (KeyBindings.Binding binding in now)
				{
					if (!bindings.TryGetValue((binding.Key, binding.Modifiers), out List<Translations.Command> list))
					{
						bindings[(binding.Key, binding.Modifiers)] = list = new List<Translations.Command>();
					}

					list.Add(binding.Command);
				}

				bindingsVersion = KeyBindings.Version;
			}

			return bindings;
		}

		/// <summary>
		/// For the Controls page: the first key that went down since the last call (not Shift, Ctrl or Alt themselves), by
		/// its normalised name, with the modifiers held (upstream's: 1 Shift, 2 Ctrl, 4 Alt). False if none did.
		/// </summary>
		public static unsafe bool NextKey(ulong[] previous, out string name, out int modifiers)
		{
			name = null;
			modifiers = 0;
			ulong* keys = stackalloc ulong[4];
			ulong modifierBits;
			if (ReadKeyboard(keys, &modifierBits) == 0)
			{
				return false;
			}

			modifiers = Modifiers(modifierBits);
			bool found = false;
			for (int word = 0; word < 4; word++)
			{
				ulong down = keys[word] & ~previous[word];
				previous[word] = keys[word];
				for (int bit = 0; bit < 64 && !found; bit++)
				{
					int code = word * 64 + bit;
					if ((down & (1UL << bit)) != 0 && code < 224 && Names.TryGetValue(code, out name))
					{
						found = true;
					}
				}
			}

			return found;
		}

		private static int Modifiers(ulong bits) => ((bits & 2) != 0 ? 1 : 0) | ((bits & 1) != 0 ? 2 : 0) | ((bits & 12) != 0 ? 4 : 0);

		/// <summary>
		/// Reads the keyboard and sends what changed to the controls (with <paramref name="commands"/> false, nothing: a
		/// menu over the game has the keys). Returns a pause or the route card, on the key's press.
		/// </summary>
		public unsafe Special Update(AndroidControls controls, bool commands = true)
		{
			ulong* keys = stackalloc ulong[4];
			ulong modifierBits;
			if (ReadKeyboard(keys, &modifierBits) == 0)
			{
				return Special.None;
			}

			// Upstream's modifiers: 1 Shift, 2 Ctrl, 4 Alt.
			int modifiers = Modifiers(modifierBits);
			Dictionary<(string Key, int Modifiers), List<Translations.Command>> bindings = Bindings();
			Special special = Special.None;
			for (int word = 0; word < 4; word++)
			{
				ulong changed = keys[word] ^ last[word];
				last[word] = keys[word];
				if (changed == 0 || controls == null)
				{
					continue;
				}

				for (int bit = 0; bit < 64; bit++)
				{
					if ((changed & (1UL << bit)) == 0)
					{
						continue;
					}

					int code = word * 64 + bit;
					if ((keys[word] & (1UL << bit)) == 0)
					{
						// Up: what it sent is let go of, whatever the modifiers are now.
						if (held.TryGetValue(code, out List<Translations.Command> pressed))
						{
							held.Remove(code);
							foreach (Translations.Command command in pressed)
							{
								controls.Release(command, exact: true);
							}
						}

						continue;
					}

					Used = true;
					if (!commands || !Names.TryGetValue(code, out string name) || !bindings.TryGetValue((name, modifiers), out List<Translations.Command> bound))
					{
						continue;
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
							// The menus take their own keys (obns_input.c); the window is the console's screen.
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
				}
			}

			return special;
		}

		/// <summary>Lets go of every key's commands (a menu opened over the game, or the game paused).</summary>
		public void ReleaseAll(AndroidControls controls)
		{
			foreach (List<Translations.Command> pressed in held.Values)
			{
				foreach (Translations.Command command in pressed)
				{
					controls?.Release(command, exact: true);
				}
			}

			held.Clear();
		}

		/// <summary>The normalised OpenTK name of each USB usage code (libnx's HidKeyboardKey).</summary>
		private static readonly Dictionary<int, string> Names = BuildNames();

		private static Dictionary<int, string> BuildNames()
		{
			Dictionary<int, string> names = new Dictionary<int, string>();
			for (int i = 0; i < 26; i++)
			{
				names[4 + i] = ((char)('a' + i)).ToString();
			}

			// 1 to 9, then 0; the keypad's the same.
			for (int i = 0; i < 9; i++)
			{
				names[30 + i] = "number" + (i + 1);
				names[89 + i] = "keypad" + (i + 1);
			}

			names[39] = "number0";
			names[98] = "keypad0";
			for (int i = 0; i < 12; i++)
			{
				names[58 + i] = "f" + (i + 1);
			}

			(int, string)[] others =
			{
				(40, "enter"), (41, "escape"), (42, "backspace"), (43, "tab"), (44, "space"), (45, "minus"), (46, "plus"),
				(47, "bracketleft"), (48, "bracketright"), (49, "backslash"), (50, "backslash"), (51, "semicolon"), (52, "quote"),
				(53, "tilde"), (54, "comma"), (55, "period"), (56, "slash"), (57, "capslock"), (70, "printscreen"),
				(71, "scrolllock"), (72, "pause"), (73, "insert"), (74, "home"), (75, "pageup"), (76, "delete"), (77, "end"),
				(78, "pagedown"), (79, "right"), (80, "left"), (81, "down"), (82, "up"), (83, "numlock"), (84, "keypaddivide"),
				(85, "keypadmultiply"), (86, "keypadminus"), (87, "keypadplus"), (88, "keypadenter"), (99, "keypadperiod"),
				(100, "backslash"), (224, "controlleft"), (225, "shiftleft"), (226, "altleft"), (228, "controlright"),
				(229, "shiftright"), (230, "altright")
			};
			foreach ((int code, string name) in others)
			{
				names[code] = name;
			}

			return names;
		}
	}
}
