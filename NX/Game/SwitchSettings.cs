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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OpenBveApi.Graphics;
using OpenBveApi.Interface;
using OpenTK.Graphics;

namespace OpenBve.Android
{
	/// <summary>
	/// OBNS 1.31N: the Switch's settings, Android's (AndroidSettings) kept in a file on the SD card instead of shared
	/// preferences: UserData/options-nx.cfg, one "key = value" a line, the same keys and defaults as Android's. The menu
	/// writes them (each change saved at once) and the game reads them as it starts (<see cref="ApplyTo"/>).
	/// </summary>
	/// <remarks>
	/// What Android chooses by the phone's RAM is chosen here as for a phone of 4.5 GB or less (title takeover gives the
	/// game 3.1 GB): textures up to 1,024 px and 16-bit colour unless the options say otherwise. Left out, as the
	/// Switch has no use for them: the graphics backend (deko3d alone), the native Vulkan texture switch, the desktop
	/// interface style and storage access. Added: the touch controls, off by default (OBNS/SWITCH.md).
	/// </remarks>
	/// <summary>The clocks and fan in play (SwitchSettings.GetPerformanceMode; the runner's obns_set_cpu_boost modes 2 to 4).</summary>
	public enum PerformanceMode
	{
		Normal = 2,
		Turbo = 3,
		Overclock = 4
	}

	public static class SwitchSettings
	{
		/// <summary>The settings file, under the SD card's UserData folder.</summary>
		public static string File = "/switch/OBNS/UserData/options-nx.cfg";

		private static readonly object Sync = new object();
		private static Dictionary<string, string> values;

		private const string LanguageKey = "language";
		private const string ViewingDistanceKey = "viewing_distance";
		private const string SoundNumberKey = "sound_number";
		private const string InterpolationKey = "interpolation";
		private const string TransparencyKey = "transparency";
		private const string TextureLimitKey = "texture_limit";
		private const string TextureDepthKey = "texture_depth";
		private const string CompressedTexturesKey = "compressed_textures";
		private const string MotionBlurKey = "motion_blur";
		private const string ShadowResolutionKey = "shadow_resolution";
		private const string ShadowDistanceKey = "shadow_distance";
		private const string ShadowCascadesKey = "shadow_cascades";
		private const string ShadowStrengthKey = "shadow_strength";
		private const string InteriorTransitionKey = "camera_interior_transition";
		private const string ExteriorTransitionKey = "camera_exterior_transition";
		private const string TransitionTenthsKey = "camera_transition_tenths";
		private const string TimeFactorKey = "time_acceleration_factor";
		private const string GameModeKey = "game_mode";
		private const string PerformanceKey = "performance_mode";
		private const string TouchControlsKey = "touch_controls";
		private const string TouchHandlesKey = "panel2_extended";
		private const string ButtonSizeKey = "touch_button_size";
		private const string ButtonOpacityKey = "touch_button_opacity";
		private const string ContentFolderKey = "content_folder";
		private const string RouteFolderKey = "route_folder";
		private const string TrainFolderKey = "train_folder";
		private const string RecentRoutesKey = "recent_routes";
		private const string RecentTrainsKey = "recent_trains";
		private const string UseDefaultTrainKey = "use_default_train";

		/// <summary>The automatic texture size limit: Android's for a phone of 4.5 GB or less.</summary>
		public const int AutomaticTextureLimit = 1024;

		// --- the store ---

		private static Dictionary<string, string> Values()
		{
			if (values != null)
			{
				return values;
			}

			values = new Dictionary<string, string>(StringComparer.Ordinal);
			try
			{
				// A save cut short between deleting the old file and renaming the new one leaves only the new one.
				string source = System.IO.File.Exists(File) ? File : System.IO.File.Exists(File + ".new") ? File + ".new" : null;
				if (source != null)
				{
					foreach (string line in System.IO.File.ReadAllLines(source, Encoding.UTF8))
					{
						int equals = line.IndexOf('=');
						if (equals > 0 && !line.StartsWith("#", StringComparison.Ordinal))
						{
							values[line.Substring(0, equals).Trim()] = Unescape(line.Substring(equals + 1).Trim());
						}
					}
				}
			}
			catch (Exception)
			{
				// An unreadable file: the defaults, and the next change writes a new one.
			}

			return values;
		}

		private static void Save()
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(File) ?? ".");
				StringBuilder text = new StringBuilder("# OBNS for the Switch: the menu's options (written by the game)\n");
				foreach (KeyValuePair<string, string> pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
				{
					text.Append(pair.Key).Append(" = ").Append(Escape(pair.Value)).Append('\n');
				}

				/*
				 * Written beside, then put in the old file's place, so a power cut mid-write leaves the old file. The Switch's
				 * file system doesn't rename over an existing file (File.Move's overwrite failed there and every change stayed
				 * in the .new file, unread at the next start, until 2026-11-07): the old one is deleted first, and a start that
				 * finds only the .new file reads that.
				 */
				string temporary = File + ".new";
				System.IO.File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
				if (System.IO.File.Exists(File))
				{
					System.IO.File.Delete(File);
				}

				System.IO.File.Move(temporary, File);
			}
			catch (Exception)
			{
				// Not saved: kept for this session.
			}
		}

		/// <summary>Lists are kept on one line: a new line is written as \n, a backslash as \\.</summary>
		private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n");

		private static string Unescape(string value)
		{
			if (value.IndexOf('\\') < 0)
			{
				return value;
			}

			StringBuilder text = new StringBuilder(value.Length);
			for (int i = 0; i < value.Length; i++)
			{
				if (value[i] == '\\' && i + 1 < value.Length)
				{
					i++;
					text.Append(value[i] == 'n' ? '\n' : value[i]);
				}
				else
				{
					text.Append(value[i]);
				}
			}

			return text.ToString();
		}

		private static string GetString(string key, string fallback)
		{
			lock (Sync)
			{
				return Values().TryGetValue(key, out string value) ? value : fallback;
			}
		}

		private static void PutString(string key, string value)
		{
			lock (Sync)
			{
				Dictionary<string, string> store = Values();
				if (value == null)
				{
					store.Remove(key);
				}
				else
				{
					store[key] = value;
				}

				Save();
			}
		}

		private static int GetInt(string key, int fallback) =>
			int.TryParse(GetString(key, null), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

		private static void PutInt(string key, int value) => PutString(key, value.ToString(CultureInfo.InvariantCulture));

		private static bool GetBool(string key, bool fallback)
		{
			string value = GetString(key, null);
			return value == null ? fallback : value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
		}

		private static void PutBool(string key, bool value) => PutString(key, value ? "true" : "false");

		private static T GetEnum<T>(string key, T fallback) where T : struct =>
			Enum.TryParse(GetString(key, null), true, out T value) ? value : fallback;

		/// <summary>Forgets what was read, so the next read is from the file (for a test).</summary>
		public static void Reload()
		{
			lock (Sync)
			{
				values = null;
			}
		}

		// --- Android's options ---

		public static string GetLanguage() => GetString(LanguageKey, "en-US");
		public static void SetLanguage(string code) => PutString(LanguageKey, code);

		public static int GetViewingDistance() => Math.Clamp(GetInt(ViewingDistanceKey, 400), 100, 2000);
		public static void SetViewingDistance(int value) => PutInt(ViewingDistanceKey, value);

		public static int GetSoundNumber() => Math.Clamp(GetInt(SoundNumberKey, 16), 16, 128);
		public static void SetSoundNumber(int value) => PutInt(SoundNumberKey, value);

		public static InterpolationMode GetInterpolation() => GetEnum(InterpolationKey, InterpolationMode.BilinearMipmapped);
		public static void SetInterpolation(InterpolationMode mode) => PutString(InterpolationKey, mode.ToString());

		public static TransparencyMode GetTransparency() => GetEnum(TransparencyKey, TransparencyMode.Quality);
		public static void SetTransparency(TransparencyMode mode) => PutString(TransparencyKey, mode.ToString());

		/// <summary>-1 automatic (<see cref="AutomaticTextureLimit"/>), 0 none, otherwise the largest side kept.</summary>
		public static int GetTextureLimit() => GetInt(TextureLimitKey, -1);
		public static void SetTextureLimit(int value) => PutInt(TextureLimitKey, value);

		/// <summary>-1 automatic (16 bits, as on a phone of 4.5 GB or less), 32 or 16.</summary>
		public static int GetTextureDepth() => GetInt(TextureDepthKey, -1);
		public static void SetTextureDepth(int value) => PutInt(TextureDepthKey, value);

		/// <summary>GPU-compressed textures (OBNS/SWITCH.md, step 2): on by default.</summary>
		public static bool GetCompressedTextures() => GetBool(CompressedTexturesKey, true);
		public static void SetCompressedTextures(bool value) => PutBool(CompressedTexturesKey, value);

		public static LibRender2.MotionBlurs.MotionBlurMode GetMotionBlur() => GetEnum(MotionBlurKey, LibRender2.MotionBlurs.MotionBlurMode.None);
		public static void SetMotionBlur(LibRender2.MotionBlurs.MotionBlurMode mode) => PutString(MotionBlurKey, mode.ToString());

		public static ShadowMapResolution GetShadowResolution()
		{
			int value = GetInt(ShadowResolutionKey, 0);
			return value == 512 || value == 1024 || value == 2048 ? (ShadowMapResolution)value : ShadowMapResolution.Off;
		}

		public static void SetShadowResolution(ShadowMapResolution value) => PutInt(ShadowResolutionKey, (int)value);

		public static ShadowDistance GetShadowDistance()
		{
			int value = GetInt(ShadowDistanceKey, 300);
			return Enum.IsDefined(typeof(ShadowDistance), value) ? (ShadowDistance)value : ShadowDistance.Medium;
		}

		public static void SetShadowDistance(ShadowDistance value) => PutInt(ShadowDistanceKey, (int)value);

		public static ShadowCascadeCount GetShadowCascades() => (ShadowCascadeCount)Math.Clamp(GetInt(ShadowCascadesKey, 3), 2, 4);
		public static void SetShadowCascades(ShadowCascadeCount value) => PutInt(ShadowCascadesKey, (int)value);

		public static int GetShadowStrength() => Math.Clamp(GetInt(ShadowStrengthKey, 70), 0, 100);
		public static void SetShadowStrength(int value) => PutInt(ShadowStrengthKey, value);

		public static bool GetCameraInteriorTransition() => GetBool(InteriorTransitionKey, true);
		public static void SetCameraInteriorTransition(bool value) => PutBool(InteriorTransitionKey, value);

		public static bool GetCameraExteriorTransition() => GetBool(ExteriorTransitionKey, true);
		public static void SetCameraExteriorTransition(bool value) => PutBool(ExteriorTransitionKey, value);

		/// <summary>The transition's duration in tenths of a second (1 to 50).</summary>
		public static int GetCameraTransitionTenths() => Math.Clamp(GetInt(TransitionTenthsKey, 4), 1, 50);
		public static void SetCameraTransitionTenths(int value) => PutInt(TransitionTenthsKey, value);

		public static int GetTimeAccelerationFactor() => Math.Clamp(GetInt(TimeFactorKey, 5), 1, 5);
		public static void SetTimeAccelerationFactor(int value) => PutInt(TimeFactorKey, value);

		/// <summary>The clocks and fan in play (the user, 2026-11-06): Normal the system's; Turbo the highest Nintendo gives
		/// games, the fan at full; Overclock the highest the console's clock table lists, the fan at full.</summary>
		public static PerformanceMode GetPerformanceMode() => GetEnum(PerformanceKey, PerformanceMode.Turbo);
		public static void SetPerformanceMode(PerformanceMode mode) => PutString(PerformanceKey, mode.ToString());

		public static OpenBveApi.GameMode GetGameMode() => GetEnum(GameModeKey, OpenBveApi.GameMode.Normal);
		public static void SetGameMode(OpenBveApi.GameMode mode) => PutString(GameModeKey, mode.ToString());

		// --- touch, handheld only ---

		/// <summary>The touch controls (ControlOverlay's buttons and the cab's touch areas): off by default, and only ever
		/// in handheld mode - docked there is no touch screen, whatever this says.</summary>
		public static bool GetTouchControls() => GetTouchMode() != TouchOff;
		public static void SetTouchControls(bool value) => SetTouchMode(value ? TouchFull : TouchOff);

		/// <summary>The touch controls' mode: off (the default), all of Android's buttons, or the safety system's keys alone
		/// (ATS: every key shown, larger, where the other buttons would be - the train driven with the controllers).</summary>
		public const int TouchOff = 0, TouchFull = 1, TouchAts = 2;

		private const string TouchModeKey = "touch_mode";

		public static int GetTouchMode() => Math.Clamp(GetInt(TouchModeKey, GetBool(TouchControlsKey, false) ? TouchFull : TouchOff), TouchOff, TouchAts);
		public static void SetTouchMode(int mode) => PutInt(TouchModeKey, Math.Clamp(mode, TouchOff, TouchAts));

		/// <summary>Upstream's panel2 extended mode (the 2D panel's handles touched), with the touch controls.</summary>
		public static bool GetTouchHandles() => GetBool(TouchHandlesKey, true);
		public static void SetTouchHandles(bool value) => PutBool(TouchHandlesKey, value);

		private static readonly float[] ButtonScales = { 0.8f, 1.0f, 1.25f };
		private static readonly float[] ButtonOpacities = { 1.0f, 0.6f, 0.3f };

		public static int GetTouchButtonSize() => Math.Clamp(GetInt(ButtonSizeKey, 1), 0, ButtonScales.Length - 1);
		public static void SetTouchButtonSize(int value) => PutInt(ButtonSizeKey, value);
		public static float GetTouchButtonScale() => ButtonScales[GetTouchButtonSize()];

		public static int GetTouchButtonOpacityIndex() => Math.Clamp(GetInt(ButtonOpacityKey, 0), 0, ButtonOpacities.Length - 1);
		public static void SetTouchButtonOpacity(int value) => PutInt(ButtonOpacityKey, value);
		public static float GetTouchButtonOpacity() => ButtonOpacities[GetTouchButtonOpacityIndex()];

		// --- content and the Start page's memory ---

		/// <summary>The content folder (Railway and Train within): LegacyContent beside the program unless chosen.</summary>
		public static string GetContentFolder() => GetString(ContentFolderKey, "/switch/OBNS/LegacyContent");
		public static void SetContentFolder(string folder) => PutString(ContentFolderKey, folder);

		public static string GetRouteFolder() => GetString(RouteFolderKey, null);
		public static void SetRouteFolder(string folder) => PutString(RouteFolderKey, folder);

		public static string GetTrainFolder() => GetString(TrainFolderKey, null);
		public static void SetTrainFolder(string folder) => PutString(TrainFolderKey, folder);

		public static bool GetUseDefaultTrain() => GetBool(UseDefaultTrainKey, true);
		public static void SetUseDefaultTrain(bool value) => PutBool(UseDefaultTrainKey, value);

		/// <summary>Recently used routes, newest first, as upstream's Recently Used list.</summary>
		public static List<string> GetRecentRoutes() => GetList(RecentRoutesKey);
		public static void AddRecentRoute(string file) => AddToList(RecentRoutesKey, file);

		public static List<string> GetRecentTrains() => GetList(RecentTrainsKey);
		public static void AddRecentTrain(string folder) => AddToList(RecentTrainsKey, folder);

		private static List<string> GetList(string key)
		{
			string value = GetString(key, null);
			return string.IsNullOrEmpty(value) ? new List<string>() : value.Split('\n').Where(s => s.Length != 0).ToList();
		}

		private static void AddToList(string key, string item)
		{
			if (string.IsNullOrEmpty(item))
			{
				return;
			}

			// Upstream keeps the ten most recent.
			List<string> list = GetList(key);
			list.Remove(item);
			list.Insert(0, item);
			PutString(key, string.Join("\n", list.Take(10)));
		}

		// --- into the game ---

		/// <summary>The texture size limit in effect: the stored choice, the automatic one resolved.</summary>
		public static int EffectiveTextureLimit()
		{
			int limit = GetTextureLimit();
			return limit >= 0 ? limit : AutomaticTextureLimit;
		}

		/// <summary>Copies the stored options onto the options the game runs with, as AndroidSettings.ApplyTo does.</summary>
		public static void ApplyTo(AndroidOptions options)
		{
			TextureSizeLimit.MaxSize = EffectiveTextureLimit();
			// Two decodes at a time, as a phone of 4.5 GB or less: each holds a whole image's pixels.
			AndroidHost.ParallelDecodes = 2;
			int depth = GetTextureDepth();
			AndroidHost.Textures16Bit = depth < 0 || depth == 16;
			options.ViewingDistance = GetViewingDistance();
			options.SoundNumber = GetSoundNumber();
			options.Interpolation = GetInterpolation();
			options.TransparencyMode = GetTransparency();
			options.LanguageCode = GetLanguage();
			options.GameMode = GetGameMode();
			options.PreviousGameMode = options.GameMode;
			options.Panel2ExtendedMode = GetTouchControls() && GetTouchHandles();
			options.Panel2ExtendedMinSize = 128;
			options.TimeAccelerationFactor = GetTimeAccelerationFactor();
			options.MotionBlur = GetMotionBlur();
			options.ShadowResolution = GetShadowResolution();
			options.ShadowDrawDistance = GetShadowDistance();
			options.ShadowCascades = GetShadowCascades();
			options.ShadowStrength = GetShadowStrength() / 100.0;
			AndroidCamera.InteriorTransition = GetCameraInteriorTransition();
			AndroidCamera.ExteriorTransition = GetCameraExteriorTransition();
			AndroidCamera.TransitionSeconds = GetCameraTransitionTenths() / 10.0;
		}
	}
}
