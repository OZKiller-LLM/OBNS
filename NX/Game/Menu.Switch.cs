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
using System.Xml;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// OBNS 1.31N: the Switch's Menu (Android/OpenBve.Android/Menu.cs without Android's Context): the host, the file
	/// system and OpenBVE's translations, shared by the menu and the game. The Switch entry builds the file system on
	/// the SD card and passes it in; the rest is Android's as it is.
	/// </summary>
	public static class Menu
	{
		private static readonly object Sync = new object();

		/// <summary>The host the menu's file system and previews use.</summary>
		public static AndroidHost Host { get; private set; }

		/// <summary>The file system: data folder, content folders, package database.</summary>
		public static OpenBveApi.FileSystem.FileSystem FileSystem { get; private set; }

		/// <summary>The version name.</summary>
		public static string Version { get; private set; } = "1.14.1.2-nx";

		/// <summary>Takes the host and file system, loads the translations and sets the language. Safe to call more than once.</summary>
		public static void Init(AndroidHost host, OpenBveApi.FileSystem.FileSystem fileSystem, string languageCode, string version)
		{
			lock (Sync)
			{
				if (FileSystem != null)
				{
					return;
				}

				System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
				Host = host;
				FileSystem = fileSystem;
				Version = version ?? Version;
				Translations.LoadLanguageFiles(FileSystem.GetDataFolder("Languages"));
				SetLanguage(languageCode);
			}
		}

		/// <summary>Switches the interface and in-game language.</summary>
		public static void SetLanguage(string code)
		{
			if (!Languages().Any(l => l.Code == code))
			{
				code = "en-US";
			}

			Translations.CurrentLanguageCode = code;
			Translations.SetInGameLanguage(code);
		}

		/// <summary>An interface string from OpenBVE's translations, or the fallback if it has none.</summary>
		public static string T(string group, string key, string fallback)
		{
			if (group == "android")
			{
				return AndroidStrings.Get(Translations.CurrentLanguageCode, key) ?? fallback;
			}

			try
			{
				string value = Translations.GetInterfaceString(HostApplication.OpenBve, new[] { group, key });
				if (!string.IsNullOrEmpty(value) && value != key && !value.EndsWith(key, StringComparison.Ordinal))
				{
					// The files write line breaks as literal \r\n, as Windows Forms displayed them.
					return value.Replace("\\r\\n", "\n").Replace("\\n", "\n");
				}
			}
			catch (Exception)
			{
				// No languages loaded yet, or a missing key.
			}

			return fallback;
		}

		/// <summary>A language OpenBVE has a translation file for.</summary>
		public class Language
		{
			public string Code;
			public string Name;

			public override string ToString() => Name + " (" + Code + ")";
		}

		private static List<Language> languages;

		/// <summary>The available languages, from the translation files' own names.</summary>
		public static List<Language> Languages()
		{
			if (languages != null)
			{
				return languages;
			}

			List<Language> result = new List<Language>();
			string folder = FileSystem?.GetDataFolder("Languages");
			if (folder != null && Directory.Exists(folder))
			{
				foreach (string file in Directory.GetFiles(folder, "*.xlf").OrderBy(f => f, StringComparer.Ordinal))
				{
					string code = Path.GetFileNameWithoutExtension(file);
					result.Add(new Language { Code = code, Name = ReadLanguageName(file) ?? code });
				}
			}

			if (result.Count == 0)
			{
				result.Add(new Language { Code = "en-US", Name = "English (United States)" });
			}

			languages = result;
			return result;
		}

		private static string ReadLanguageName(string file)
		{
			try
			{
				using (XmlReader reader = XmlReader.Create(file))
				{
					while (reader.Read())
					{
						if (reader.NodeType == XmlNodeType.Element && reader.Name == "trans-unit" && reader.GetAttribute("id") == "name")
						{
							// Translations carry the name in <target>; en-US has only <source>.
							string source = null;
							while (reader.Read())
							{
								if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "trans-unit")
								{
									return source;
								}

								if (reader.NodeType == XmlNodeType.Element && reader.Name == "source")
								{
									source = reader.ReadElementContentAsString();
								}
								else if (reader.NodeType == XmlNodeType.Element && reader.Name == "target")
								{
									return reader.ReadElementContentAsString();
								}
							}
						}
					}
				}
			}
			catch (Exception)
			{
				// Unreadable: fall back to the code.
			}

			return null;
		}
	}
}
