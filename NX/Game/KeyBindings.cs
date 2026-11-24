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
	/// A USB keyboard's bindings (the user, 2026-11-25): upstream's, as on the desktop and Android - UserData/1.5.0/controls.cfg
	/// in upstream's own format (a desktop's file works as it is), else upstream's Default.controls, built into the game.
	/// The Controls page's keyboard list changes them, and writes the file.
	/// </summary>
	public static class KeyBindings
	{
		/// <summary>A command's key: OpenTK's name, normalised (lower case, one of its aliases), and upstream's modifiers
		/// (1 Shift, 2 Ctrl, 4 Alt).</summary>
		public readonly struct Binding
		{
			public readonly Translations.Command Command;
			public readonly string Key;
			public readonly int Modifiers;

			public Binding(Translations.Command command, string key, int modifiers)
			{
				Command = command;
				Key = key;
				Modifiers = modifiers;
			}
		}

		/// <summary>The settings folder (Program sets it).</summary>
		public static string SettingsFolder = "/switch/OBNS/UserData";

		private static string File => Path.Combine(SettingsFolder, "1.5.0", "controls.cfg");

		private static List<Binding> current;

		/// <summary>Changes whenever the bindings do (the game's keyboard makes its lookup again).</summary>
		public static int Version { get; private set; }

		/// <summary>Where the bindings came from, for the log.</summary>
		public static string Source { get; private set; }

		/// <summary>Every binding now (read the first time).</summary>
		public static IReadOnlyList<Binding> Current
		{
			get
			{
				current ??= Load();
				return current;
			}
		}

		/// <summary>The keys bound to a command, as the player reads them ("Ctrl+T, F5"), or "—".</summary>
		public static string Describe(Translations.Command command)
		{
			string[] keys = Current.Where(b => b.Command == command).Select(b => Name(b.Key, b.Modifiers)).ToArray();
			return keys.Length == 0 ? "—" : string.Join(", ", keys);
		}

		/// <summary>A key with its modifiers, as the player reads it ("Ctrl+Shift+F5").</summary>
		public static string Name(string key, int modifiers) =>
			((modifiers & 2) != 0 ? "Ctrl+" : string.Empty) + ((modifiers & 1) != 0 ? "Shift+" : string.Empty) + ((modifiers & 4) != 0 ? "Alt+" : string.Empty) + Spelling(key);

		/// <summary>Gives the command the key too.</summary>
		public static void Add(Translations.Command command, string key, int modifiers)
		{
			if (!Current.Any(b => b.Command == command && b.Key == key && b.Modifiers == modifiers))
			{
				current.Add(new Binding(command, key, modifiers));
				Version++;
				Save();
			}
		}

		/// <summary>Takes every key off the command.</summary>
		public static void Clear(Translations.Command command)
		{
			if (Current.Any(b => b.Command == command))
			{
				current.RemoveAll(b => b.Command == command);
				Version++;
				Save();
			}
		}

		/// <summary>Upstream's defaults again (the file goes).</summary>
		public static void Reset()
		{
			try
			{
				System.IO.File.Delete(File);
			}
			catch (Exception)
			{
				// Read again from the built-in defaults below all the same.
			}

			current = Load();
			Version++;
		}

		/// <summary>OpenTK's key names have aliases; one of each, lower case (Android's ControlBindings.Normalise).</summary>
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

		/// <summary>Upstream's spelling of a normalised key name ("comma" to "Comma"), for the file and the page.</summary>
		public static string Spelling(string key) => Spellings.TryGetValue(key, out string spelt) ? spelt : key;

		private static readonly Dictionary<string, string> Spellings = BuildSpellings();

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

		private static List<Binding> Load()
		{
			IEnumerable<string> lines;
			if (System.IO.File.Exists(File))
			{
				lines = System.IO.File.ReadAllLines(File);
				Source = File;
			}
			else
			{
				using (Stream stream = typeof(KeyBindings).Assembly.GetManifestResourceStream("Default.controls"))
				using (StreamReader reader = stream == null ? null : new StreamReader(stream))
				{
					lines = reader?.ReadToEnd().Split('\n') ?? Array.Empty<string>();
				}

				Source = "upstream's Default.controls";
			}

			Dictionary<string, Translations.Command> commands = new Dictionary<string, Translations.Command>(StringComparer.OrdinalIgnoreCase);
			foreach (Translations.CommandInfo info in Translations.CommandInfos.Values)
			{
				commands.TryAdd(info.Name, info.Command);
			}

			List<Binding> loaded = new List<Binding>();
			foreach (string raw in lines)
			{
				// "POWER_INCREASE, Keyboard, Z, 0, 0" (a ';' begins a comment).
				string line = raw;
				int comment = line.IndexOf(';');
				if (comment >= 0)
				{
					line = line.Substring(0, comment);
				}

				string[] parts = line.Split(',').Select(p => p.Trim()).ToArray();
				if (parts.Length >= 4 && parts[1].Equals("keyboard", StringComparison.OrdinalIgnoreCase) && commands.TryGetValue(parts[0], out Translations.Command command) &&
				    int.TryParse(parts[3], out int modifiers))
				{
					loaded.Add(new Binding(command, Normalise(parts[2]), modifiers & 7));
				}
			}

			return loaded;
		}

		private static void Save()
		{
			try
			{
				StringBuilder text = new StringBuilder();
				text.Append("; Current control configuration\n; =============================\n; This file was automatically generated. Please modify only if you know what you're doing.\n; Written by OBNS for the Switch\n\n");
				foreach (Binding binding in current)
				{
					string name = Translations.CommandInfos.TryGetValue(binding.Command, out Translations.CommandInfo info) ? info.Name : binding.Command.ToString();
					text.Append(name).Append(", Keyboard, ").Append(Spelling(binding.Key)).Append(", ").Append(binding.Modifiers).Append(", 0\n");
				}

				Directory.CreateDirectory(Path.GetDirectoryName(File));
				System.IO.File.WriteAllText(File, text.ToString());
				Source = File;
			}
			catch (Exception)
			{
				// Not saved: the bindings still hold for this run.
			}
		}
	}
}
