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
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using OpenTK.Graphics;

namespace OpenBve.Android
{
	/// <summary>
	/// The beta testers' data log: one plain text file per session in Documents/OpenBVE - on the SD
	/// card when the phone has one, else in the phone's own storage - for the tester to read and
	/// send back. Only in a beta build (-p:BetaLog=true, defining BETA_LOG); in any other build
	/// every call does nothing.
	/// </summary>
	/// <remarks>
	/// Laid out to be read: a header of labelled sections (session, app, device, settings, input
	/// devices), the game (route, train, safety plugin, renderer, game mode, load time), then a
	/// time-stamped log - frame rate statistics every 30 seconds with where the train was and what
	/// the frame cost, game mode switches, every warning and error (the game's own, crash traces,
	/// the graphics driver's) marked as such - and a summary at the end. The game's own lines come
	/// from this process's logcat (information level for the game, warnings and errors from
	/// everything else), so nothing is reported twice; its five-second diagnostics are folded into
	/// the 30-second lines rather than copied.
	///
	/// Private data is left out: no location, accounts, contacts, phone number, device serial or
	/// advertising identifiers, installed apps, or names of paired devices (controllers and
	/// keyboards are recorded by kind and USB vendor/product number only).
	/// </remarks>
	public static class BetaLog
	{
#if BETA_LOG
		public const bool Enabled = true;
#else
		public const bool Enabled = false;
#endif

		private static readonly object Sync = new object();
		private static TextWriter writer;
		private static Timer flusher;
		private static Context appContext;
		private static DateTime started;

		/// <summary>Where the log is being written, for the About page and the log itself.</summary>
		public static string Location { get; private set; }

		/// <summary>
		/// Opens this process's log ("game" or "menu"), writes its header and starts copying its
		/// logcat into it. Call once per process, after Menu.Init (the content folder decides where).
		/// </summary>
		public static void Start(Context context, string part)
		{
			if (!Enabled || writer != null)
			{
				return;
			}

			appContext = context.ApplicationContext;
			started = DateTime.Now;
			string name = "OpenBVE_beta_" + started.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + part + ".txt";
			try
			{
				writer = Open(context, name, out string location);
				Location = location;
			}
			catch (Exception ex)
			{
				global::Android.Util.Log.Error("OpenBVE", "beta log: could not open " + name + ": " + ex.Message);
				return;
			}

			Raw(new string('=', 78));
			Raw("  OpenBVE for Android - beta test log (" + part + ")");
			Raw(new string('=', 78));
			Raw("  For finding problems: the route and train, graphics, game mode, frame rates and");
			Raw("  error messages. No personal data: no location, accounts, contacts, device");
			Raw("  identifiers or names of paired devices. Send this file with your report.");

			Section("Session");
			TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(started);
			Field("Started", started.ToString("yyyy-MM-dd HH:mm:ss") + " (UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm") + ")");
			Field("Log file", Location);

			Section("App");
			Field("Version", AppVersion(context));

			Section("Device");
			Field("Model", Build.Manufacturer + " " + Build.Model + " (" + Build.Device + ")");
			if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
			{
				Field("Chip", Build.SocManufacturer + " " + Build.SocModel);
			}

			Field("CPU types", string.Join(", ", Build.SupportedAbis ?? Array.Empty<string>()));
			Field("Android", Build.VERSION.Release + " (API " + (int)Build.VERSION.SdkInt + "), build " + Build.Display);
			Field("Memory", MemoryText(context));
			Field("Screen", ScreenText(context));
			Field("Storage", StorageText());
			Field("Battery", BatteryText(context));
			Field("Temperature", ThermalText(context));
			Field("Language", "app " + AndroidSettings.GetLanguage(context) + ", system " + Java.Util.Locale.Default);

			Section("Settings");
			Field("Graphics", AndroidSettings.GetBackend(context) == GraphicsBackend.Vulkan ? "Vulkan (via ANGLE)" : "OpenGL ES");
			Field("Interface", AndroidSettings.GetInterfaceStyle(context) + " (automatic = desktop style in Android desktop mode)");
			Field("Quality", "viewing distance " + AndroidSettings.GetViewingDistance(context) + " m, " + AndroidSettings.GetSoundNumber(context) +
			                 " sound sources, texture filtering " + AndroidSettings.GetInterpolation(context));
			Field("Touch buttons", new[] { "small", "normal", "large" }[AndroidSettings.GetTouchButtonSize(context)] + " size, " +
			                       new[] { "solid", "translucent", "faint" }[AndroidSettings.GetTouchButtonOpacityIndex(context)] +
			                       (AndroidSettings.GetTouchButtonsHidden(context) ? ", hidden" : string.Empty) +
			                       "; cab panel handles " + (AndroidSettings.GetTouchHandles(context) ? "touchable" : "not touchable"));

			Section("Input devices");
			InputDevices();

			if (part == "game")
			{
				Section("Log");
				Raw("  Time      What");
			}
			else
			{
				Section("Log");
			}

			// Crashes the render loop does not catch still reach the file before the process dies.
			AppDomain.CurrentDomain.UnhandledException += (s, e) => Event("CRASH", e.ExceptionObject?.ToString() ?? "unknown exception", true);
			global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (s, e) => Event("CRASH", e.Exception?.ToString() ?? "unknown exception", true);

			Thread reader = new Thread(() => CopyLogcat(Process.MyPid())) { IsBackground = true, Name = "beta log" };
			reader.Start();
			// Written through at once for errors; otherwise every two seconds, so a crash loses little.
			flusher = new Timer(_ => Flush(), null, 2000, 2000);
		}

		// --- writing ---

		/// <summary>A heading: "[ Game ]".</summary>
		public static void Section(string title)
		{
			Raw(string.Empty);
			Raw("[ " + title + " ]");
		}

		/// <summary>
		/// A heading and its labelled values written in one piece, so no line from the game's own
		/// log lands in the middle.
		/// </summary>
		public static void Block(string title, params (string Label, string Value)[] fields)
		{
			if (!Enabled || writer == null)
			{
				return;
			}

			StringBuilder text = new StringBuilder();
			text.AppendLine().AppendLine("[ " + title + " ]");
			foreach ((string label, string value) in fields)
			{
				text.AppendLine("  " + label.PadRight(16) + (value ?? "-"));
			}

			lock (Sync)
			{
				writer.Write(text.ToString());
			}
		}

		/// <summary>A labelled value under a heading: "  Route           TMLDN 20144.csv".</summary>
		public static void Field(string label, string value)
		{
			Raw("  " + label.PadRight(16) + (value ?? "-"));
		}

		/// <summary>A time-stamped line in the log section.</summary>
		public static void Line(string text) => Event(null, text);

		/// <summary>A time-stamped line, marked with its level ("WARNING", "ERROR") if given.</summary>
		public static void Event(string level, string text, bool flush = false)
		{
			if (!Enabled || writer == null)
			{
				return;
			}

			string time = DateTime.Now.ToString("HH:mm:ss");
			string prefix = "  " + time + "  " + (level != null ? level.PadRight(8) : string.Empty);
			string indent = new string(' ', prefix.Length);
			string[] lines = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n');
			lock (Sync)
			{
				writer.WriteLine(prefix + lines[0]);
				for (int i = 1; i < lines.Length; i++)
				{
					writer.WriteLine(indent + lines[i]);
				}
			}

			if (flush)
			{
				Flush();
			}
		}

		private static void Raw(string text)
		{
			lock (Sync)
			{
				writer?.WriteLine(text);
			}
		}

		/// <summary>Writes everything buffered to the file.</summary>
		public static void Flush()
		{
			if (!Enabled)
			{
				return;
			}

			lock (Sync)
			{
				try
				{
					writer?.Flush();
				}
				catch (Exception)
				{
					// The file went away (storage removed): nothing more can be done.
				}
			}
		}

		/// <summary>Seconds since this process's log started: the game's load time, when called at startup.</summary>
		public static double SecondsSinceStart => (DateTime.Now - started).TotalSeconds;

		// --- frame rate statistics ---

		private static int frames, slowFrames, sessionFrames, sessionSlowFrames;
		private static double seconds, longest, worstSecond = double.MaxValue, sessionSeconds, sessionWorstSecond = double.MaxValue, sessionLongest;
		private static int secondFrames;
		private static double secondTime;
		private static string lastFrameProfile, lastMemory, lastFaces;

		/// <summary>
		/// Render thread, once a frame: the real time the frame took. Every 30 seconds a line with
		/// the average, the worst one-second rate and the longest frame, where the train was, what
		/// the frame cost, memory and the phone's temperature state.
		/// </summary>
		public static void Frame(double elapsed, Func<string> situation)
		{
			if (!Enabled || writer == null || elapsed <= 0.0 || elapsed > 5.0)
			{
				return;
			}

			frames++;
			sessionFrames++;
			seconds += elapsed;
			sessionSeconds += elapsed;
			longest = Math.Max(longest, elapsed);
			sessionLongest = Math.Max(sessionLongest, elapsed);
			if (elapsed > 0.05)
			{
				slowFrames++;
				sessionSlowFrames++;
			}

			secondFrames++;
			secondTime += elapsed;
			if (secondTime >= 1.0)
			{
				double rate = secondFrames / secondTime;
				worstSecond = Math.Min(worstSecond, rate);
				sessionWorstSecond = Math.Min(sessionWorstSecond, rate);
				secondFrames = 0;
				secondTime = 0.0;
			}

			if (seconds >= 30.0)
			{
				string text = "Frame rate   " + (frames / seconds).ToString("0.0") + " fps average, worst second " + Rate(worstSecond) +
				              ", longest frame " + (longest * 1000.0).ToString("0") + " ms, " + slowFrames + " frame(s) over 50 ms\n" +
				              "Where        " + Safe(situation);
				if (lastFrameProfile != null)
				{
					text += "\nFrame cost   " + lastFrameProfile;
				}

				if (lastFaces != null)
				{
					// "102 opaque faces (...) from 41 objects ...; drawn in 102 calls": the scene's size and draw calls.
					text += "\nScene        " + lastFaces;
				}

				if (lastMemory != null)
				{
					text += "\nMemory       " + lastMemory;
				}

				text += "\nTemperature  " + ThermalText(appContext);
				Event(null, text);
				frames = slowFrames = 0;
				seconds = longest = 0.0;
				worstSecond = double.MaxValue;
			}
		}

		/// <summary>The session's summary so far; call when the game ends or leaves the screen.</summary>
		public static void Summary()
		{
			if (!Enabled || writer == null || sessionSeconds <= 0.0)
			{
				return;
			}

			Section("Session summary (" + DateTime.Now.ToString("HH:mm:ss") + ")");
			Field("Played", TimeSpan.FromSeconds(sessionSeconds).ToString(@"h\:mm\:ss") + " (" + sessionFrames + " frames)");
			Field("Frame rate", (sessionFrames / sessionSeconds).ToString("0.0") + " fps average, worst second " + Rate(sessionWorstSecond));
			Field("Slow frames", (100.0 * sessionSlowFrames / Math.Max(1, sessionFrames)).ToString("0.0") + "% over 50 ms, longest " +
			                     (sessionLongest * 1000.0).ToString("0") + " ms");
			Field("Battery", BatteryText(appContext));
			Field("Temperature", ThermalText(appContext));
			Raw(string.Empty);
			Flush();
		}

		private static string Rate(double rate) => rate == double.MaxValue ? "-" : rate.ToString("0.0");

		private static string Safe(Func<string> text)
		{
			try
			{
				return text?.Invoke() ?? string.Empty;
			}
			catch (Exception)
			{
				return string.Empty;
			}
		}

		// --- device facts (nothing personal) ---

		private static string MemoryText(Context context)
		{
			try
			{
				ActivityManager manager = (ActivityManager)context.GetSystemService(Context.ActivityService);
				ActivityManager.MemoryInfo info = new ActivityManager.MemoryInfo();
				manager.GetMemoryInfo(info);
				return (info.TotalMem / 1073741824.0).ToString("0.0") + " GB total, " + (info.AvailMem / 1073741824.0).ToString("0.0") + " GB free" +
				       (info.LowMemory ? " (low)" : string.Empty);
			}
			catch (Exception)
			{
				return "-";
			}
		}

		private static string ScreenText(Context context)
		{
			global::Android.Util.DisplayMetrics metrics = context.Resources?.DisplayMetrics;
			float refresh = 0.0f;
			try
			{
				refresh = ((IWindowManager)context.GetSystemService(Context.WindowService)).DefaultDisplay.RefreshRate;
			}
			catch (Exception)
			{
				// Not known.
			}

			return Math.Max(metrics?.WidthPixels ?? 0, metrics?.HeightPixels ?? 0) + " x " + Math.Min(metrics?.WidthPixels ?? 0, metrics?.HeightPixels ?? 0) +
			       " px, " + (int)(metrics?.DensityDpi ?? 0) + " dpi" + (refresh > 0.0f ? ", " + refresh.ToString("0") + " Hz" : string.Empty);
		}

		private static string StorageText()
		{
			try
			{
				string content = Path.GetDirectoryName(Menu.FileSystem.RouteInstallationDirectory.TrimEnd('/'));
				StatFs stat = new StatFs(content);
				string where = content.StartsWith("/storage/emulated", StringComparison.Ordinal) ? "phone storage" : "SD card";
				if (content.Contains("/Android/data/"))
				{
					where += ", app folder (no all-files access)";
				}

				return "routes and trains on " + where + ": " + (stat.AvailableBytes / 1073741824.0).ToString("0.0") + " GB free of " +
				       (stat.TotalBytes / 1073741824.0).ToString("0") + " GB";
			}
			catch (Exception)
			{
				return "-";
			}
		}

		private static string BatteryText(Context context)
		{
			try
			{
				BatteryManager battery = (BatteryManager)context.GetSystemService(Context.BatteryService);
				int level = battery.GetIntProperty((int)BatteryProperty.Capacity);
				return level + "%" + (battery.IsCharging ? ", charging" : string.Empty);
			}
			catch (Exception)
			{
				return "-";
			}
		}

		// The phone's own thermal state: heat makes it slow its processors down, and frame rates with them.
		private static string ThermalText(Context context)
		{
			if (context == null || Build.VERSION.SdkInt < BuildVersionCodes.Q)
			{
				return "-";
			}

			try
			{
				PowerManager power = (PowerManager)context.GetSystemService(Context.PowerService);
				return power.CurrentThermalStatus switch
				{
					ThermalStatus.None => "normal",
					ThermalStatus.Light => "warm (light throttling)",
					ThermalStatus.Moderate => "hot (moderate throttling)",
					ThermalStatus.Severe => "very hot (severe throttling)",
					_ => "critical (" + power.CurrentThermalStatus + ")"
				};
			}
			catch (Exception)
			{
				return "-";
			}
		}

		/// <summary>Keyboards and game controllers, by kind and USB vendor/product number (never by name).</summary>
		public static void InputDevices()
		{
			if (!Enabled || writer == null)
			{
				return;
			}

			int shown = 0;
			foreach (int id in InputDevice.GetDeviceIds() ?? Array.Empty<int>())
			{
				InputDevice device = InputDevice.GetDevice(id);
				// Plugged in or paired ones: the phone's own buttons and sensors are not of interest.
				if (device == null || device.IsVirtual || !device.IsExternal)
				{
					continue;
				}

				InputSourceType sources = device.Sources;
				bool pad = (sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (sources & InputSourceType.Joystick) == InputSourceType.Joystick;
				bool keyboard = device.KeyboardType == InputKeyboardType.Alphabetic;
				bool mouse = (sources & InputSourceType.Mouse) == InputSourceType.Mouse;
				if (!pad && !keyboard && !mouse)
				{
					continue;
				}

				string kind = pad ? "Game controller" : keyboard ? "Keyboard" : "Mouse";
				Field(kind, "vendor " + device.VendorId.ToString("x4") + ", product " + device.ProductId.ToString("x4"));
				shown++;
			}

			if (shown == 0)
			{
				Field("None", "touch screen only");
			}
		}

		// --- the file and the logcat copy ---

		/*
		 * Documents/OpenBVE, where routes and trains live - the SD card's, or the phone's - written
		 * directly when the app has all-files access. Without it, through MediaStore (which needs
		 * no permission from Android 10) into Documents/OpenBVE on the SD card if there is one,
		 * else the phone; and failing all that, the app's own folder, which the log names.
		 */
		private static TextWriter Open(Context context, string name, out string location)
		{
			string content = Menu.FileSystem?.RouteInstallationDirectory == null ? null : Path.GetDirectoryName(Menu.FileSystem.RouteInstallationDirectory.TrimEnd('/'));
			if (content != null && !content.Contains("/Android/data/"))
			{
				try
				{
					Directory.CreateDirectory(content);
					location = Path.Combine(content, name);
					return new StreamWriter(location, false, new UTF8Encoding(false));
				}
				catch (Exception)
				{
					// Try MediaStore.
				}
			}

			if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
			{
				string volume = MediaStore.VolumeExternalPrimary;
				foreach (string candidate in MediaStore.GetExternalVolumeNames(context))
				{
					if (candidate != MediaStore.VolumeExternalPrimary)
					{
						volume = candidate; // an SD card
						break;
					}
				}

				ContentValues values = new ContentValues();
				values.Put(MediaStore.IMediaColumns.DisplayName, name);
				values.Put(MediaStore.IMediaColumns.MimeType, "text/plain");
				values.Put(MediaStore.IMediaColumns.RelativePath, global::Android.OS.Environment.DirectoryDocuments + "/OpenBVE");
				global::Android.Net.Uri uri = context.ContentResolver.Insert(MediaStore.Files.GetContentUri(volume), values);
				Stream stream = uri == null ? null : context.ContentResolver.OpenOutputStream(uri, "w");
				if (stream != null)
				{
					location = (volume == MediaStore.VolumeExternalPrimary ? "phone storage" : "SD card") + ": Documents/OpenBVE/" + name;
					return new StreamWriter(stream, new UTF8Encoding(false));
				}
			}

			string own = Path.Combine(context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir.AbsolutePath, "logs");
			Directory.CreateDirectory(own);
			location = Path.Combine(own, name) + " (Documents was not writable)";
			return new StreamWriter(Path.Combine(own, name), false, new UTF8Encoding(false));
		}

		// "09-29 17:00:10.123 I/OpenBVE(12345): message"
		private static readonly Regex LogcatLine = new Regex(@"^\d\d-\d\d (\d\d:\d\d:\d\d)\.\d+ ([VDIWEF])/([^(]*?)\s*\(\s*\d+\): ?(.*)$", RegexOptions.Compiled);

		private static void CopyLogcat(int pid)
		{
			try
			{
				// An app may read its own process's log without any permission: the game at
				// information level, everything else (driver, runtime, crashes) at warning and above.
				Java.Lang.Process logcat = Java.Lang.Runtime.GetRuntime().Exec(new[] { "logcat", "-v", "time", "--pid=" + pid, "OpenBVE:I", "*:W" });
				using (StreamReader reader = new StreamReader(logcat.InputStream))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						if (line.StartsWith("---------", StringComparison.Ordinal))
						{
							continue;
						}

						Match match = LogcatLine.Match(line);
						if (!match.Success)
						{
							// A continuation (a stack trace's next line): keep it under the last one.
							Raw("            " + line);
							continue;
						}

						char level = match.Groups[2].Value[0];
						string tag = match.Groups[3].Value;
						string message = match.Groups[4].Value;
						if (tag == "OpenBVE" && level == 'I')
						{
							// The five-second diagnostics go into the 30-second frame rate lines instead.
							if (message.StartsWith("frame: ", StringComparison.Ordinal))
							{
								lastFrameProfile = message.Substring(7);
								continue;
							}

							if (message.StartsWith("memory: ", StringComparison.Ordinal))
							{
								lastMemory = message.Substring(8);
								continue;
							}

							if (message.StartsWith("faces: ", StringComparison.Ordinal))
							{
								lastFaces = message.Substring(7);
								continue;
							}

							if (message.StartsWith("scene: ", StringComparison.Ordinal) || message.StartsWith("train: ", StringComparison.Ordinal) ||
							    message.StartsWith("sound: ", StringComparison.Ordinal) || message.StartsWith("startup: ", StringComparison.Ordinal))
							{
								continue;
							}
						}
						else if (Noise(tag, message))
						{
							continue;
						}

						string label = level switch
						{
							'W' => "WARNING",
							'E' => "ERROR",
							'F' => "FATAL",
							_ => null
						};
						Event(label, (tag == "OpenBVE" ? string.Empty : "[" + tag + "] ") + message, level == 'E' || level == 'F');
					}
				}
			}
			catch (Exception ex)
			{
				Event("WARNING", "beta log: could not read the process's log: " + ex.Message);
			}
		}

		/*
		 * Warnings Android and the .NET runtime print for every app, which say nothing about the
		 * game: the runtime's notes on native libraries it resolves later, property lookups the
		 * system refuses, back-gesture and jank-monitor notices, OpenAL's desktop leftovers.
		 */
		private static bool Noise(string tag, string message) =>
			(tag == "monodroid-assembly" && message.Contains("not loaded, p/invoke")) ||
			(tag == "libc" && message.StartsWith("Access denied finding property", StringComparison.Ordinal)) ||
			tag == "WindowOnBackDispatcher" || tag == "InteractionJankMonitor" ||
			(tag == "openal" && (message.Contains("pthread_setschedparam") || message.Contains("dbus") || message.Contains("D-Bus"))) ||
			message.Contains("libpenguin.so");

		private static string AppVersion(Context context)
		{
			try
			{
				global::Android.Content.PM.PackageInfo info = context.PackageManager.GetPackageInfo(context.PackageName, 0);
				return info.VersionName + " (code " + (Build.VERSION.SdkInt >= BuildVersionCodes.P ? info.LongVersionCode : info.VersionCode) + "), installed " +
				       DateTimeOffset.FromUnixTimeMilliseconds(info.LastUpdateTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
			}
			catch (Exception)
			{
				return context.PackageName;
			}
		}
	}
}
