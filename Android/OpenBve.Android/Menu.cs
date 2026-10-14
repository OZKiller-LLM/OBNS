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
using Android.Content;
using OpenBveApi.Hosts;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// Process-wide setup shared by the menu and the game: the data folder, the file system, and
	/// OpenBVE's own translations, which supply every label in the menu just as they do for the
	/// Windows main form.
	/// </summary>
	public static class Menu
	{
		private static readonly object Sync = new object();

		/// <summary>The host the menu's file system and previews use.</summary>
		public static AndroidHost Host { get; private set; }

		/// <summary>The file system: data folder, content folders, package database.</summary>
		public static OpenBveApi.FileSystem.FileSystem FileSystem { get; private set; }

		/// <summary>
		/// Extracts the data assets if needed, builds the file system, and loads the translations
		/// in the chosen language. Safe to call more than once.
		/// </summary>
		/// <summary>The app's version name, e.g. 1.14.0.3-android.1 (set by Init).</summary>
		public static string Version { get; private set; } = "1.14.0.3";

		public static void Init(Context context)
		{
			lock (Sync)
			{
				if (FileSystem != null)
				{
					return;
				}

				System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
				Host = new AndroidHost();
				FileSystem = AndroidFileSystem.Create(context.ApplicationContext, Host);
				try
				{
					Version = context.PackageManager.GetPackageInfo(context.PackageName, 0).VersionName ?? Version;
				}
				catch (Exception)
				{
					// Keep the default.
				}

				Translations.LoadLanguageFiles(FileSystem.GetDataFolder("Languages"));
				SetLanguage(AndroidSettings.GetLanguage(context));
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
		/// <param name="group">The translation group, e.g. "start".</param>
		/// <param name="key">The key within it, e.g. "route_browse".</param>
		/// <param name="fallback">English text for when no translation file is loaded or has the key.</param>
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

		/// <summary>The available languages, read from the translation files' own names.</summary>
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
				// Unreadable file: fall back to the code.
			}

			return null;
		}
	}
}
