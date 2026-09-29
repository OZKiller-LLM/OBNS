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
using System.Linq;
using Android.Content;
using OpenBveApi.Graphics;
using OpenTK.Graphics;

namespace OpenBve.Android
{
	/// <summary>
	/// The app's settings, kept in Android shared preferences: the Options screen writes them, and
	/// the game reads them when it starts. The equivalent of upstream's options.cfg.
	/// </summary>
	/// <remarks>
	/// The menu and the game run in separate processes, and shared preferences are cached per
	/// process, so the game reads a fresh copy (<see cref="FileCreationMode.MultiProcess"/>).
	/// </remarks>
	public static class AndroidSettings
	{
		private const string File = "openbve";
		private const string BackendKey = "graphics_backend";
		private const string LanguageKey = "language";
		private const string ViewingDistanceKey = "viewing_distance";
		private const string SoundNumberKey = "sound_number";
		private const string InterpolationKey = "interpolation";
		private const string RouteFolderKey = "route_folder";
		private const string TrainFolderKey = "train_folder";
		private const string RecentRoutesKey = "recent_routes";
		private const string RecentTrainsKey = "recent_trains";
		private const string UseDefaultTrainKey = "use_default_train";
		private const string InterfaceStyleKey = "interface_style";
		private const string TouchHandlesKey = "panel2_extended";
		private const string ButtonSizeKey = "touch_button_size";
		private const string ButtonOpacityKey = "touch_button_opacity";
		private const string ButtonsHiddenKey = "touch_buttons_hidden";

		/// <summary>The intent extra that sets the backend from the command line, e.g. for testing.</summary>
		/// <example>adb shell am start -S -n net.openbve/net.openbve.GameActivity --es graphics vulkan</example>
		public const string BackendExtra = "graphics";

#pragma warning disable CS0618, CA1422 // MultiProcess: the only way to share preferences across processes without a provider
		private static ISharedPreferences Prefs(Context context) =>
			context.GetSharedPreferences(File, FileCreationMode.Private | FileCreationMode.MultiProcess);
#pragma warning restore CS0618, CA1422

		// --- graphics backend ---

		/// <summary>Reads the chosen graphics backend; OpenGL if none has been chosen.</summary>
		public static GraphicsBackend GetBackend(Context context)
		{
			string value = Prefs(context)?.GetString(BackendKey, null);
			return Enum.TryParse(value, true, out GraphicsBackend backend) ? backend : GraphicsBackend.OpenGL;
		}

		/// <summary>Stores the graphics backend to use from the next game.</summary>
		public static void SetBackend(Context context, GraphicsBackend backend) => PutString(context, BackendKey, backend.ToString());

		/// <summary>The interface style chosen in Options.</summary>
		public static InterfaceStyle GetInterfaceStyle(Context context)
		{
			string value = Prefs(context)?.GetString(InterfaceStyleKey, null);
			return Enum.TryParse(value, true, out InterfaceStyle style) ? style : InterfaceStyle.Auto;
		}

		/// <summary>Sets the interface style.</summary>
		public static void SetInterfaceStyle(Context context, InterfaceStyle style) => PutString(context, InterfaceStyleKey, style.ToString());

		/// <summary>
		/// Whether the game uses the desktop interface - upstream's HUD and timetable, keyboard and
		/// controller input, no touch controls - rather than the touch one. Automatic chooses the
		/// desktop interface only when the app runs as a desktop (Samsung DeX, or a display Android
		/// reports as a desk dock).
		/// </summary>
		public static bool UseDesktopInterface(Context context)
		{
			switch (GetInterfaceStyle(context))
			{
				case InterfaceStyle.Desktop:
					return true;
				case InterfaceStyle.Touch:
					return false;
				default:
					return IsDesktopMode(context);
			}
		}

		/// <summary>Whether the app is running in a desktop mode (Samsung DeX or a desk dock).</summary>
		public static bool IsDesktopMode(Context context)
		{
			global::Android.Content.Res.Configuration configuration = context?.Resources?.Configuration;
			if (configuration == null)
			{
				return false;
			}

			/*
			 * The app on a display other than the built-in one: the common sign of every phone
			 * desktop - Samsung DeX (One UI 7 runs it on a virtual "Desktop" display), Motorola
			 * Ready For, Huawei/Honor PC mode and Android's own desktop on a connected display all
			 * put the app's window on the monitor.
			 */
			global::Android.Views.Display display = DisplayOf(context);
			if (display != null && display.DisplayId != global::Android.Views.Display.DefaultDisplay)
			{
				return true;
			}

			bool desk = (configuration.UiMode & global::Android.Content.Res.UiMode.TypeMask) == global::Android.Content.Res.UiMode.TypeDesk ||
			            SamsungDesktopFlag(configuration) == true;
			if (display != null)
			{
				/*
				 * On the built-in screen a desktop mode is only possible on a tablet (DeX on the
				 * tablet's own screen). A phone runs DeX on a monitor only - and while its window
				 * comes back from one, Samsung reports desk mode and the DeX flag for a while
				 * after the move, so on a phone-sized screen they are not believed.
				 */
				return desk && configuration.SmallestScreenWidthDp >= 600;
			}

			// No display to go by: a desk dock or Samsung's flag.
			return desk;
		}

		/// <summary>What the desktop-mode decision rests on, for the log.</summary>
		public static string DesktopModeDetail(Context context)
		{
			global::Android.Content.Res.Configuration configuration = context?.Resources?.Configuration;
			global::Android.Views.Display display = DisplayOf(context);
			bool keyboard = configuration?.Keyboard == global::Android.Content.Res.KeyboardType.Qwerty &&
			                configuration.HardKeyboardHidden == global::Android.Content.Res.HardKeyboardHidden.No;
			bool pointer = false;
			foreach (int id in global::Android.Views.InputDevice.GetDeviceIds() ?? Array.Empty<int>())
			{
				global::Android.Views.InputDevice device = global::Android.Views.InputDevice.GetDevice(id);
				pointer |= device != null && device.IsExternal && (device.Sources & global::Android.Views.InputSourceType.Mouse) == global::Android.Views.InputSourceType.Mouse;
			}

			return "uiMode type " + ((int)(configuration?.UiMode ?? 0) & 0xf) + ", sw " + (configuration?.SmallestScreenWidthDp ?? 0) + "dp, Samsung desktop flag " + (SamsungDesktopFlag(configuration)?.ToString() ?? "absent") +
			       ", display " + (display == null ? "unknown" : display.DisplayId + " \"" + display.Name + "\"") +
			       ", density " + (context?.Resources?.DisplayMetrics?.DensityDpi.ToString() ?? "?") + " dpi, keyboard " + keyboard + ", external mouse " + pointer;
		}

		/// <summary>The display the context's window is on, or null if it cannot tell.</summary>
		private static global::Android.Views.Display DisplayOf(Context context)
		{
			try
			{
				// The window's own display first: it follows a move between displays at once,
				// where the activity's may still name the one it left.
				global::Android.Views.Display window = (context as global::Android.App.Activity)?.Window?.DecorView?.Display;
				if (window != null)
				{
					return window;
				}

				if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.R)
				{
					return context?.Display;
				}

#pragma warning disable CS0618, CA1422 // DefaultDisplay: the only way before Android 11
				return (context as global::Android.App.Activity)?.WindowManager?.DefaultDisplay;
#pragma warning restore CS0618, CA1422
			}
			catch (Java.Lang.Exception)
			{
				// An application context has no display of its own.
				return null;
			}
		}

		/// <summary>Samsung DeX's flag on Configuration (a Samsung-only field, read by reflection), or null elsewhere.</summary>
		private static bool? SamsungDesktopFlag(global::Android.Content.Res.Configuration configuration)
		{
			try
			{
				Java.Lang.Class type = Java.Lang.Class.FromType(typeof(global::Android.Content.Res.Configuration));
				int enabled = type.GetField("SEM_DESKTOP_MODE_ENABLED").GetInt(null);
				return type.GetField("semDesktopModeEnabled").GetInt(configuration) == enabled;
			}
			catch (Java.Lang.Exception)
			{
				return null;
			}
		}

		/// <summary>Applies a backend given as an intent extra ("opengl" or "vulkan"), if any.</summary>
		public static void ApplyIntent(Context context, Intent intent)
		{
			string value = intent?.GetStringExtra(BackendExtra);
			if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out GraphicsBackend backend))
			{
				SetBackend(context, backend);
			}

			// For tests: --es interface_style auto|touch|desktop, as the Options setting.
			value = intent?.GetStringExtra(InterfaceStyleKey);
			if (!string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out InterfaceStyle style))
			{
				SetInterfaceStyle(context, style);
			}

			// --ei touch_button_size 0|1|2, --ei touch_button_opacity 0|1|2, --ez touch_buttons_hidden true|false
			if (intent?.HasExtra(ButtonSizeKey) == true)
			{
				SetTouchButtonSize(context, intent.GetIntExtra(ButtonSizeKey, 1));
			}

			if (intent?.HasExtra(ButtonOpacityKey) == true)
			{
				SetTouchButtonOpacity(context, intent.GetIntExtra(ButtonOpacityKey, 0));
			}

			if (intent?.HasExtra(ButtonsHiddenKey) == true)
			{
				SetTouchButtonsHidden(context, intent.GetBooleanExtra(ButtonsHiddenKey, false));
			}
		}

		// --- language and options ---

		/// <summary>The language code; by default the device's language if OpenBVE has it, else en-US.</summary>
		public static string GetLanguage(Context context)
		{
			string stored = Prefs(context)?.GetString(LanguageKey, null);
			if (!string.IsNullOrEmpty(stored))
			{
				return stored;
			}

			Java.Util.Locale locale = Java.Util.Locale.Default;
			string full = locale.Language + "-" + locale.Country;
			return full.Length > 1 ? full : "en-US";
		}

		public static void SetLanguage(Context context, string code) => PutString(context, LanguageKey, code);

		public static int GetViewingDistance(Context context) => Prefs(context)?.GetInt(ViewingDistanceKey, 400) ?? 400;

		public static void SetViewingDistance(Context context, int value) => PutInt(context, ViewingDistanceKey, value);

		public static int GetSoundNumber(Context context) => Prefs(context)?.GetInt(SoundNumberKey, 16) ?? 16;

		public static void SetSoundNumber(Context context, int value) => PutInt(context, SoundNumberKey, value);

		public static InterpolationMode GetInterpolation(Context context)
		{
			string value = Prefs(context)?.GetString(InterpolationKey, null);
			return Enum.TryParse(value, out InterpolationMode mode) ? mode : InterpolationMode.BilinearMipmapped;
		}

		public static void SetInterpolation(Context context, InterpolationMode mode) => PutString(context, InterpolationKey, mode.ToString());

		/// <summary>Copies the stored options onto the options the game runs with.</summary>
		public static void ApplyTo(Context context, AndroidOptions options)
		{
			options.ViewingDistance = GetViewingDistance(context);
			options.SoundNumber = Math.Max(16, GetSoundNumber(context));
			options.Interpolation = GetInterpolation(context);
			options.LanguageCode = GetLanguage(context);
			// Upstream's panel2 extended mode: a 2D panel's power, brake and reverser indicators become touch areas.
			options.Panel2ExtendedMode = GetTouchHandles(context);
			options.Panel2ExtendedMinSize = 128;
		}

		/// <summary>
		/// Whether a 2D panel's handle indicators can be touched (upstream's "panel2 extended
		/// mode"). Off by default upstream; on here, where the cab is under the player's finger.
		/// </summary>
		public static bool GetTouchHandles(Context context) => Prefs(context)?.GetBoolean(TouchHandlesKey, true) ?? true;

		public static void SetTouchHandles(Context context, bool value)
		{
			ISharedPreferencesEditor editor = Prefs(context)?.Edit();
			editor?.PutBoolean(TouchHandlesKey, value);
			editor?.Apply();
		}

		// --- the touch buttons ---

		private static readonly float[] ButtonScales = { 0.8f, 1.0f, 1.25f };
		private static readonly float[] ButtonOpacities = { 1.0f, 0.6f, 0.3f };

		/// <summary>The touch buttons' size: 0 small, 1 normal, 2 large.</summary>
		public static int GetTouchButtonSize(Context context) => Math.Clamp(Prefs(context)?.GetInt(ButtonSizeKey, 1) ?? 1, 0, ButtonScales.Length - 1);

		public static void SetTouchButtonSize(Context context, int value) => PutInt(context, ButtonSizeKey, value);

		/// <summary>What the size choice scales the touch interface's measurements by.</summary>
		public static float GetTouchButtonScale(Context context) => ButtonScales[GetTouchButtonSize(context)];

		/// <summary>The touch buttons' opacity: 0 solid, 1 translucent, 2 faint.</summary>
		public static int GetTouchButtonOpacityIndex(Context context) => Math.Clamp(Prefs(context)?.GetInt(ButtonOpacityKey, 0) ?? 0, 0, ButtonOpacities.Length - 1);

		public static void SetTouchButtonOpacity(Context context, int value) => PutInt(context, ButtonOpacityKey, value);

		public static float GetTouchButtonOpacity(Context context) => ButtonOpacities[GetTouchButtonOpacityIndex(context)];

		/// <summary>Whether the touch buttons are hidden, all but pause (set from the pause menu).</summary>
		public static bool GetTouchButtonsHidden(Context context) => Prefs(context)?.GetBoolean(ButtonsHiddenKey, false) ?? false;

		public static void SetTouchButtonsHidden(Context context, bool value)
		{
			ISharedPreferencesEditor editor = Prefs(context)?.Edit();
			editor?.PutBoolean(ButtonsHiddenKey, value);
			editor?.Apply();
		}

		// --- the Start page's memory ---

		public static string GetRouteFolder(Context context) => Prefs(context)?.GetString(RouteFolderKey, null);

		public static void SetRouteFolder(Context context, string folder) => PutString(context, RouteFolderKey, folder);

		public static string GetTrainFolder(Context context) => Prefs(context)?.GetString(TrainFolderKey, null);

		public static void SetTrainFolder(Context context, string folder) => PutString(context, TrainFolderKey, folder);

		public static bool GetUseDefaultTrain(Context context) => Prefs(context)?.GetBoolean(UseDefaultTrainKey, true) ?? true;

		public static void SetUseDefaultTrain(Context context, bool value)
		{
			ISharedPreferencesEditor editor = Prefs(context)?.Edit();
			editor?.PutBoolean(UseDefaultTrainKey, value);
			editor?.Apply();
		}

		/// <summary>Recently used routes, newest first, as upstream's Recently Used list.</summary>
		public static List<string> GetRecentRoutes(Context context) => GetList(context, RecentRoutesKey);

		public static void AddRecentRoute(Context context, string file) => AddToList(context, RecentRoutesKey, file);

		/// <summary>Recently used trains, newest first.</summary>
		public static List<string> GetRecentTrains(Context context) => GetList(context, RecentTrainsKey);

		public static void AddRecentTrain(Context context, string folder) => AddToList(context, RecentTrainsKey, folder);

		// --- helpers ---

		private static List<string> GetList(Context context, string key)
		{
			string value = Prefs(context)?.GetString(key, null);
			return string.IsNullOrEmpty(value) ? new List<string>() : value.Split('\n').Where(s => s.Length != 0).ToList();
		}

		private static void AddToList(Context context, string key, string item)
		{
			if (string.IsNullOrEmpty(item))
			{
				return;
			}

			// Upstream keeps the ten most recent.
			List<string> list = GetList(context, key);
			list.Remove(item);
			list.Insert(0, item);
			PutString(context, key, string.Join("\n", list.Take(10)));
		}

		private static void PutString(Context context, string key, string value)
		{
			ISharedPreferencesEditor editor = Prefs(context)?.Edit();
			editor?.PutString(key, value);
			editor?.Commit();
		}

		private static void PutInt(Context context, string key, int value)
		{
			ISharedPreferencesEditor editor = Prefs(context)?.Edit();
			editor?.PutInt(key, value);
			editor?.Commit();
		}
	}

	/// <summary>Which in-game interface the game uses.</summary>
	public enum InterfaceStyle
	{
		/// <summary>Desktop in a desktop mode (Samsung DeX, a desk dock), touch otherwise.</summary>
		Auto,

		/// <summary>Touch controls, the information line and the touch timetable.</summary>
		Touch,

		/// <summary>Upstream's HUD and timetable, keyboard and controller input, no touch controls.</summary>
		Desktop
	}
}
