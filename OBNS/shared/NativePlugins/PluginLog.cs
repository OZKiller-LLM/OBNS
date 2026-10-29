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

namespace OpenBve.NativePlugins
{
	/// <summary>
	/// Where the plugin bridge reports: logcat on Android, the program's log file on the desktop.
	/// </summary>
	/// <remarks>
	/// The per-call diagnostics (keys, signals, beacons, panel changes) go to logcat always, as they did before the bridge
	/// was shared; on the desktop they would fill the log file over a long run, so they are written there only with
	/// OBNS_X86_DEBUG=1.
	/// </remarks>
	internal static class PluginLog
	{
		private const string Tag = "OpenBVE";

#if !ANDROID
		private static readonly bool verbose = Environment.GetEnvironmentVariable("OBNS_X86_DEBUG") == "1";
#endif

		public static void Info(string message)
		{
#if ANDROID
			global::Android.Util.Log.Info(Tag, message);
#else
			Write("info", message);
#endif
		}

		public static void Warn(string message)
		{
#if ANDROID
			global::Android.Util.Log.Warn(Tag, message);
#else
			Write("warning", message);
#endif
		}

		public static void Error(string message)
		{
#if ANDROID
			global::Android.Util.Log.Error(Tag, message);
#else
			Write("error", message);
#endif
		}

		/// <summary>A diagnostic of one call into the plugin.</summary>
		public static void Verbose(string message)
		{
#if ANDROID
			global::Android.Util.Log.Info(Tag, message);
#else
			if (verbose)
			{
				Write("info", message);
			}
#endif
		}

#if !ANDROID
		private static void Write(string level, string message)
		{
			global::TrainManager.TrainManagerBase.FileSystem?.AppendToLogFile(Tag + " plugin " + level + ": " + message.Replace("→", "->"));
		}
#endif
	}
}
