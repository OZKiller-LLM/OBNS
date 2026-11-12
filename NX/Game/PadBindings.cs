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
using OpenBveApi.Interface;

namespace OpenBve.Switch
{
	/// <summary>
	/// The console's buttons and the commands they give (Android's ControlBindings, its controller part, for the Switch):
	/// Android's default pad layout until the player changes it in Controls, kept in UserData/pad-nx.cfg as upstream's
	/// command names with their buttons ("POWER_INCREASE = ZR"). + and − aren't bound: they are the pause menu and the
	/// timetable, always, so a game can always be paused.
	/// </summary>
	public static class PadBindings
	{
		/// <summary>The file, beside the Switch's options.</summary>
		public static string File = "/switch/OBNS/UserData/pad-nx.cfg";

		/// <summary>Android's DefaultPad, by button: power and brake name both the two-handle and the single-handle
		/// commands, and the train's handles take the ones that apply.</summary>
		public static readonly (ulong Button, Translations.Command Command)[] Defaults =
		{
			(SwitchPad.ZR, Translations.Command.PowerIncrease), (SwitchPad.ZR, Translations.Command.SinglePower),
			(SwitchPad.R, Translations.Command.PowerDecrease), (SwitchPad.R, Translations.Command.SingleNeutral),
			(SwitchPad.ZL, Translations.Command.BrakeIncrease), (SwitchPad.ZL, Translations.Command.SingleBrake),
			(SwitchPad.L, Translations.Command.BrakeDecrease), (SwitchPad.L, Translations.Command.SingleNeutral),
			(SwitchPad.StickR, Translations.Command.BrakeEmergency), (SwitchPad.StickR, Translations.Command.SingleEmergency),
			(SwitchPad.A, Translations.Command.SecurityS),
			(SwitchPad.B, Translations.Command.HornPrimary),
			(SwitchPad.X, Translations.Command.DoorsLeft),
			(SwitchPad.Y, Translations.Command.DoorsRight),
			(SwitchPad.StickL, Translations.Command.CameraInterior),
			(SwitchPad.Up, Translations.Command.ReverserForward),
			(SwitchPad.Down, Translations.Command.ReverserBackward),
			(SwitchPad.Left, Translations.Command.CameraPOIPrevious),
			(SwitchPad.Right, Translations.Command.CameraPOINext)
		};

		/// <summary>The buttons that can be bound, in the order they are named.</summary>
		public static readonly (ulong Button, string Name)[] Buttons =
		{
			(SwitchPad.A, "A"), (SwitchPad.B, "B"), (SwitchPad.X, "X"), (SwitchPad.Y, "Y"),
			(SwitchPad.L, "L"), (SwitchPad.R, "R"), (SwitchPad.ZL, "ZL"), (SwitchPad.ZR, "ZR"),
			(SwitchPad.StickL, "L stick"), (SwitchPad.StickR, "R stick"),
			(SwitchPad.Up, "D-pad up"), (SwitchPad.Down, "D-pad down"), (SwitchPad.Left, "D-pad left"), (SwitchPad.Right, "D-pad right")
		};

		private static List<(ulong Button, Translations.Command Command)> current;

		/// <summary>Every binding now (loaded from the file the first time).</summary>
		public static IReadOnlyList<(ulong Button, Translations.Command Command)> Current
		{
			get
			{
				current ??= Load();
				return current;
			}
		}

		/// <summary>The button's name ("ZR").</summary>
		public static string Name(ulong button)
		{
			foreach ((ulong b, string name) in Buttons)
			{
				if (b == button)
				{
					return name;
				}
			}

			return "?";
		}

		/// <summary>The buttons bound to a command, by name ("ZR, L stick"), or "—".</summary>
		public static string Describe(Translations.Command command)
		{
			string[] names = Current.Where(b => b.Command == command).Select(b => Name(b.Button)).ToArray();
			return names.Length == 0 ? "—" : string.Join(", ", names);
		}

		/// <summary>Gives the command the button too (a button can serve several commands, a command have several buttons).</summary>
		public static void Add(Translations.Command command, ulong button)
		{
			if (!Current.Contains((button, command)))
			{
				current.Add((button, command));
				Save();
			}
		}

		/// <summary>Takes every button off the command.</summary>
		public static void Clear(Translations.Command command)
		{
			if (Current.Any(b => b.Command == command))
			{
				current.RemoveAll(b => b.Command == command);
				Save();
			}
		}

		/// <summary>Android's layout again (the file goes).</summary>
		public static void Reset()
		{
			current = new List<(ulong, Translations.Command)>(Defaults);
			try
			{
				System.IO.File.Delete(File);
			}
			catch (Exception)
			{
				// Kept: Load reads the defaults only when there is no file, so it is written over now.
				Save();
			}
		}

		private static List<(ulong Button, Translations.Command Command)> Load()
		{
			List<(ulong, Translations.Command)> loaded = new List<(ulong, Translations.Command)>();
			if (!System.IO.File.Exists(File))
			{
				loaded.AddRange(Defaults);
				return loaded;
			}

			Dictionary<string, Translations.Command> commands = new Dictionary<string, Translations.Command>(StringComparer.OrdinalIgnoreCase);
			foreach (Translations.CommandInfo info in Translations.CommandInfos.Values)
			{
				commands.TryAdd(info.Name, info.Command);
			}

			try
			{
				foreach (string line in System.IO.File.ReadAllLines(File))
				{
					int equals = line.IndexOf('=');
					if (line.TrimStart().StartsWith(";") || equals <= 0 || !commands.TryGetValue(line.Substring(0, equals).Trim(), out Translations.Command command))
					{
						continue;
					}

					foreach (string name in line.Substring(equals + 1).Split(','))
					{
						foreach ((ulong button, string buttonName) in Buttons)
						{
							if (string.Equals(buttonName, name.Trim(), StringComparison.OrdinalIgnoreCase))
							{
								loaded.Add((button, command));
							}
						}
					}
				}
			}
			catch (Exception)
			{
				loaded.Clear();
				loaded.AddRange(Defaults);
			}

			return loaded;
		}

		private static void Save()
		{
			try
			{
				StringBuilder text = new StringBuilder("; OBNS for the Switch: the controller's buttons for each command (Options > Controls)\n");
				foreach (IGrouping<Translations.Command, (ulong Button, Translations.Command Command)> group in current.GroupBy(b => b.Command))
				{
					string name = Translations.CommandInfos.TryGetValue(group.Key, out Translations.CommandInfo info) ? info.Name : group.Key.ToString();
					text.Append(name).Append(" = ").Append(string.Join(", ", group.Select(b => Name(b.Button)))).Append('\n');
				}

				Directory.CreateDirectory(Path.GetDirectoryName(File));
				System.IO.File.WriteAllText(File, text.ToString());
			}
			catch (Exception)
			{
				// Not saved: the bindings still hold for this run.
			}
		}
	}
}
