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
using System.Text;

namespace OpenBve.Android
{
	/// <summary>
	/// The translations of the port's own labels (the "android" group, which upstream's language
	/// files do not have): Data/Languages/android/&lt;code&gt;.txt, one "key = text" per line.
	/// </summary>
	/// <remarks>
	/// Kept out of upstream's .xlf files so those stay as upstream ships them. A language without
	/// its own file borrows one of the same language (de-CH takes de-DE); a key a file lacks falls
	/// back to the English text in the code. en-US.txt lists every key, for translators.
	/// </remarks>
	public static class AndroidStrings
	{
		private static readonly object Sync = new object();
		private static readonly Dictionary<string, Dictionary<string, string>> Loaded = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>The text for a key in a language, or null if there is none.</summary>
		public static string Get(string language, string key)
		{
			if (string.IsNullOrEmpty(language) || language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}

			lock (Sync)
			{
				if (!Loaded.TryGetValue(language, out Dictionary<string, string> strings))
				{
					strings = Load(language);
					Loaded[language] = strings;
				}

				return strings.TryGetValue(key, out string text) ? text : null;
			}
		}

		private static Dictionary<string, string> Load(string language)
		{
			Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.Ordinal);
			string folder;
			try
			{
				folder = Path.Combine(Menu.FileSystem.GetDataFolder("Languages"), "android");
			}
			catch (Exception)
			{
				return strings;
			}

			if (!Directory.Exists(folder))
			{
				return strings;
			}

			// This language's own file, else another of the same language (ms_MY and nb_NO use underscores).
			string file = Path.Combine(folder, language + ".txt");
			if (!File.Exists(file))
			{
				string prefix = language.Split('-', '_')[0] + "-";
				file = null;
				foreach (string candidate in Directory.GetFiles(folder, "*.txt"))
				{
					if (Path.GetFileName(candidate).Replace('_', '-').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						file = candidate;
						break;
					}
				}
			}

			if (file == null)
			{
				return strings;
			}

			try
			{
				foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
				{
					string line = raw.Trim();
					int equals = line.IndexOf('=');
					if (line.Length == 0 || line[0] == '#' || equals <= 0)
					{
						continue;
					}

					string text = line.Substring(equals + 1).Trim().Replace("\n", "\n");
					if (text.Length != 0)
					{
						strings[line.Substring(0, equals).Trim()] = text;
					}
				}

				global::Android.Util.Log.Info("OpenBVE", "android labels: " + strings.Count + " for " + language + " from " + Path.GetFileName(file));
			}
			catch (Exception ex)
			{
				global::Android.Util.Log.Warn("OpenBVE", "android labels: could not read " + file + ": " + ex.Message);
			}

			return strings;
		}
	}
}
