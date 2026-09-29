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
using OpenBveApi.Runtime;
using TrainManager.Trains;
using PluginBase = global::TrainManager.SafetySystems.Plugin;

namespace OpenBve.Android.NativePlugins
{
	/// <summary>
	/// Runs a train's Win32 safety-system plugin on Android, as far as that is possible: the file
	/// is identified from its headers, and a managed re-implementation takes its place.
	/// </summary>
	/// <remarks>
	/// Upstream loads such a plugin either directly (32-bit Windows) or through a proxy process,
	/// neither of which exists here, so its own loader refuses the file and the train falls back
	/// to OpenBVE's default ATS-Sx. That fallback is worse than nothing for a train that never had
	/// ATS-Sx: its alarm rings at a signal the train's real system would pass, and five seconds
	/// later it applies the emergency brake and holds it - the train stops for no reason the
	/// driver can see. This bridge is the alternative: run what can be run, stand in for what
	/// cannot, and say which is which.
	/// </remarks>
	public static class NativePluginBridge
	{
		private const string Tag = "OpenBVE";

		/// <summary>
		/// Loads the train's Win32 plugin as a managed translation.
		/// </summary>
		/// <returns>Whether a plugin was installed; false leaves the train for the usual loader.</returns>
		public static bool TryLoad(TrainBase train, string trainFolder, Encoding encoding, InitializationModes mode, out string summary)
		{
			summary = string.Empty;
			string file = ResolvePlugin(trainFolder, encoding);
			if (file == null)
			{
				return false;
			}

			NativePluginImage image = NativePluginImage.Read(file);
			if (image.Kind == NativePluginKind.Managed)
			{
				// A .NET plugin: portable already, so upstream's own loader can have it.
				return false;
			}

			if (image.Kind == NativePluginKind.NotAWindowsBinary || !image.IsAtsPlugin)
			{
				Log.Warn(Tag, "train plugin " + image + " does not export the ATS interface");
				return false;
			}

			/*
			 * First choice: run the plugin itself, in the x86 emulator. A 32-bit Windows DLL is
			 * interpreted instruction by instruction, with its Windows and C runtime calls answered
			 * by a small compatibility layer, so it behaves as it does on Windows - signalling,
			 * panel indices and sounds included. Only if it cannot start there does a managed
			 * translation (or a pass-through) stand in.
			 */
			if (image.Kind == NativePluginKind.NativeX86 && EmulationEnabled)
			{
				string root = EmulationRoot(trainFolder);
				EmulatedWin32Plugin emulated = new EmulatedWin32Plugin(file, root, AnsiEncoding(encoding, trainFolder), train);
				if (emulated.Load(train.GetVehicleSpecs(), mode))
				{
					train.Plugin = emulated;
					string modules = string.Join(", ", System.Linq.Enumerable.Select(emulated.Emulated.Process.Modules, m => m.Name));
					summary = image.FileName + " → running in the x86 compatibility layer (" + modules + ")";
					Log.Info(Tag, "train plugin: " + summary);
					return true;
				}

				Log.Warn(Tag, "train plugin " + image.FileName + " could not run in the x86 compatibility layer" +
				              (emulated.LastException != null ? ": " + emulated.LastException.Message : string.Empty) + "; using a translation");
			}

			IRuntime runtime = TranslatedPlugins.Translate(image, out string description);
			TranslatedPlugins.Report(image, description);

			PluginBase plugin = new global::TrainManager.SafetySystems.NetPlugin(file, trainFolder, runtime, train);
			try
			{
				if (!plugin.Load(train.GetVehicleSpecs(), mode))
				{
					Log.Warn(Tag, "the translation of " + image.FileName + " failed to load");
					return false;
				}
			}
			catch (Exception ex)
			{
				Log.Error(Tag, "the translation of " + image.FileName + " raised " + ex);
				return false;
			}

			train.Plugin = plugin;
			summary = description + (runtime is OsAtsRuntime osAts && osAts.Summary.Length > 0 ? "; " + osAts.Summary : string.Empty);
			return true;
		}

		/// <summary>
		/// Where an emulated plugin records its calls (plugin-trace.bin), or null for no recording.
		/// Replayed with <c>OpenBve.X86.Tests &lt;root&gt; &lt;dll&gt; replay &lt;trace&gt;</c>.
		/// </summary>
		public static string TraceFolder { get; set; }

		/// <summary>Whether Win32 plugins run in the x86 compatibility layer (the default).</summary>
		public static bool EmulationEnabled { get; set; } = true;

		/// <summary>
		/// The code page the emulated process uses for its narrow (…A) strings - file paths above
		/// all - from the train's text encoding.
		/// </summary>
		/// <remarks>
		/// The menu, as upstream's, takes the train's encoding from its description file, and that
		/// is often UTF-16. A Windows ANSI code page never is: every character would become two
		/// bytes with a zero in them, so the first path a plugin built ended at its first letter.
		/// DetailManager then could not read detailmodules.txt, its DllMain returned FALSE, and the
		/// train fell back to the translation - with the KCR train that meant no TBL at all. Only
		/// launches from the menu were affected; a launch with no encoding used UTF-8 and worked.
		/// </remarks>
		/// <remarks>
		/// Beyond that, a plugin written on Japanese Windows assumes code page 932, one from Hong
		/// Kong or Taiwan 950: it converts its own path and settings with that code page, and a
		/// Japanese plugin that sets its C runtime to "japanese" and is handed UTF-8 paths opens
		/// nothing. So when the train's encoding is a Unicode one, the train's own legacy-encoded
		/// text files (panel, sound and plugin settings) decide, and only all-ASCII or all-Unicode
		/// content falls back to UTF-8.
		/// </remarks>
		internal static Encoding AnsiEncoding(Encoding encoding, string trainFolder)
		{
			if (encoding != null && !OpenBveApi.TextEncoding.IsUtf(encoding) && !(encoding is UnicodeEncoding) && !(encoding is UTF32Encoding) && encoding.CodePage != 65000)
			{
				return encoding;
			}

			Encoding detected = null;
			try
			{
				detected = DetectLegacyEncoding(trainFolder);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
			}

			Log.Info("OpenBVE", "x86 layer: ANSI code page " + (detected ?? Encoding.UTF8).CodePage + (detected != null ? " (from the train's text files)" : " (UTF-8; the train's text is ASCII or Unicode)"));
			return detected ?? Encoding.UTF8;
		}

		private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cfg", ".ini", ".txt", ".dat", ".csv", ".animated", ".b3d" };

		/// <summary>The legacy (non-Unicode) encoding most of the train's text files are in, or null.</summary>
		private static Encoding DetectLegacyEncoding(string trainFolder)
		{
			if (string.IsNullOrEmpty(trainFolder) || !Directory.Exists(trainFolder))
			{
				return null;
			}

			Dictionary<int, (Encoding Encoding, int Votes)> votes = new Dictionary<int, (Encoding, int)>();
			UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
			int examined = 0;
			foreach (string file in Directory.EnumerateFiles(trainFolder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 }))
			{
				if (!TextExtensions.Contains(Path.GetExtension(file)) || examined >= 60)
				{
					continue;
				}

				FileInfo info = new FileInfo(file);
				if (info.Length == 0 || info.Length > 512 * 1024)
				{
					continue;
				}

				examined++;
				byte[] bytes = File.ReadAllBytes(file);
				bool bom = bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF) || bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
				if (bom || !bytes.Any(b => b >= 0x80))
				{
					continue;
				}

				try
				{
					strictUtf8.GetString(bytes);
					continue; // valid UTF-8, so not a legacy code page
				}
				catch (DecoderFallbackException)
				{
				}

				Encoding encoding = OpenBveApi.TextEncoding.GetSystemEncodingFromBytes(bytes);
				if (encoding == null || OpenBveApi.TextEncoding.IsUtf(encoding))
				{
					continue;
				}

				votes[encoding.CodePage] = (encoding, votes.TryGetValue(encoding.CodePage, out var current) ? current.Votes + 1 : 1);
			}

			return votes.Count == 0 ? null : votes.Values.OrderByDescending(v => v.Votes).First().Encoding;
		}

		/// <summary>
		/// The folder the emulated plugin sees as C:\: the one above the train's folder (normally
		/// the folder holding Train and Railway), so paths relative to the plugin still resolve.
		/// </summary>
		private static string EmulationRoot(string trainFolder)
		{
			DirectoryInfo train = new DirectoryInfo(trainFolder);
			return train.Parent?.Parent?.FullName ?? train.Parent?.FullName ?? train.FullName;
		}

		/// <summary>
		/// The plugin file named by the train's ats.cfg, or null if there is none.
		/// </summary>
		/// <remarks>
		/// As upstream's LoadCustomPlugin reads it: the first line that is not a comment names the
		/// plugin, in a Windows-relative path, and a mis-decoded name is retried in code page 1252.
		/// </remarks>
		private static string ResolvePlugin(string trainFolder, Encoding encoding)
		{
			string config = OpenBveApi.Path.CombineFile(trainFolder, "ats.cfg");
			if (!File.Exists(config))
			{
				return null;
			}

			foreach (Encoding candidate in new[] { encoding ?? Encoding.UTF8, Encoding.GetEncoding(1252) })
			{
				string name = FirstEntry(config, candidate);
				if (name == null)
				{
					continue;
				}

				try
				{
					string file = OpenBveApi.Path.CombineFile(trainFolder, name);
					if (File.Exists(file))
					{
						return file;
					}
				}
				catch (Exception)
				{
					// A malformed path: try the other encoding.
				}
			}

			return null;
		}

		private static string FirstEntry(string config, Encoding encoding)
		{
			foreach (string line in File.ReadAllLines(config, encoding))
			{
				string text = line;
				int comment = text.IndexOf(';');
				if (comment >= 0)
				{
					text = text.Substring(0, comment);
				}

				text = text.Trim();
				if (text.Length > 0)
				{
					return text;
				}
			}

			return null;
		}
	}
}
