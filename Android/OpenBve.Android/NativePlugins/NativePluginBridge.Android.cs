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

extern alias ContentPlugins;
using System;
using System.IO;
using System.Text;
using OpenBveApi.Runtime;
using TrainManager.Trains;

namespace OpenBve.NativePlugins
{
	/// <summary>
	/// The Android app's way into the bridge (OBNS/shared/NativePlugins): it installs a train's safety plugin itself, so it
	/// finds the plugin file first, from the train's ats.cfg or its BVE5 vehicle file.
	/// </summary>
	public static partial class NativePluginBridge
	{
		/// <summary>
		/// Loads the train's Win32 plugin in the x86 layer, else as a managed translation.
		/// </summary>
		/// <returns>Whether a plugin was installed; false leaves the train for the usual loader.</returns>
		public static bool TryLoad(TrainBase train, string trainFolder, Encoding encoding, InitializationModes mode, out string summary)
		{
			summary = string.Empty;
			string file = ResolvePlugin(trainFolder, encoding);
			return file != null && TryRun(train, file, trainFolder, encoding, mode, out summary);
		}

		/// <summary>
		/// A BVE5 train's safety plugin (OBNS/BVE5.md): its vehicle file's 32-bit plugin, which the x86 layer runs,
		/// else its 64-bit one; null if the train is not a BVE5 train or names neither. The train plugin's own
		/// call to load it fails on Android, as every Win32 plugin's does, so it comes here as an ats.cfg's would.
		/// </summary>
		private static string Bve5Plugin(string trainFolder)
		{
			try
			{
				string vehicleFile = ContentPlugins::Train.OpenBve.Bve5Vehicle.Find(trainFolder);
				ContentPlugins::Train.OpenBve.Bve5Vehicle vehicle = vehicleFile != null ? ContentPlugins::Train.OpenBve.Bve5Vehicle.Load(vehicleFile) : null;
				// OBNS_ATS64=1 (--ez plugin_ats64 true): the 64-bit plugin where a train has both, for testing it.
				string file = Environment.GetEnvironmentVariable("OBNS_ATS64") == "1" && vehicle?.Ats64 != null ? vehicle.Ats64 : vehicle?.Ats32 ?? vehicle?.Ats64;
				return file != null && File.Exists(file) ? file : null;
			}
			catch (Exception ex)
			{
				PluginLog.Warn("BVE5 vehicle in " + trainFolder + " could not be read: " + ex.Message);
				return null;
			}
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
			string bve5Plugin = Bve5Plugin(trainFolder);
			if (bve5Plugin != null)
			{
				return bve5Plugin;
			}

			if (File.Exists(trainFolder))
			{
				return null;
			}

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
