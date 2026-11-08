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
using OpenBve.Android;
using OpenBveApi.Graphics;
using OpenBveApi.Interface;
using OpenTK.Graphics;

namespace OpenBve.Switch
{
	/// <summary>
	/// Options: Android's (OptionsPage.cs), in its order and wording, as a list for the console's buttons. Left out as
	/// not applying to the Switch (OBNS/SWITCH.md, step 6): the graphics backend and its notes (deko3d draws
	/// everything), the native Vulkan texture switch, the desktop interface style, Android's storage access. The touch
	/// settings are one group under a touch controls switch, off by default and handheld only. Added: GPU-compressed
	/// textures (step 2), the content folder. Every change is saved at once and applies from the next game.
	/// </summary>
	public sealed class OptionsPage
	{
		public readonly MenuList List = new MenuList();

		/// <summary>Raised when the language changes, so the menu can rebuild its labels.</summary>
		public event Action LanguageChanged;

		public OptionsPage()
		{
			Build();
		}

		private static string T(string group, string key, string fallback) => Menu.T(group, key, fallback);

		public void Build()
		{
			List.Clear();
			string[] onOff = { T("android", "on", "On"), T("android", "off", "Off") };

			// --- language ---
			List.Add(new HeadingItem(T("options", "language", "Language")));
			List<Menu.Language> languages = Menu.Languages();
			string current = SwitchSettings.GetLanguage();
			List.Add(new ChoiceItem(T("options", "language", "Language"), languages.Select(l => l.ToString()).ToArray(),
				Math.Max(0, languages.FindIndex(l => l.Code == current)), i =>
				{
					if (languages[i].Code == SwitchSettings.GetLanguage())
					{
						return;
					}

					SwitchSettings.SetLanguage(languages[i].Code);
					Menu.SetLanguage(languages[i].Code);
					LanguageChanged?.Invoke();
				}));

			// --- quality ---
			List.Add(new HeadingItem(T("options", "quality", "Quality")));
			InterpolationMode[] modes =
			{
				InterpolationMode.NearestNeighbor, InterpolationMode.Bilinear, InterpolationMode.NearestNeighborMipmapped,
				InterpolationMode.BilinearMipmapped, InterpolationMode.TrilinearMipmapped
			};
			List.Add(new ChoiceItem(T("options", "quality_interpolation_mode", "Mode:"), new[]
				{
					T("options", "quality_interpolation_mode_nearest", "Nearest neighbor"),
					T("options", "quality_interpolation_mode_bilinear", "Bilinear"),
					T("options", "quality_interpolation_mode_nearestmipmap", "Nearest neighbor (mipmapping)"),
					T("options", "quality_interpolation_mode_bilinearmipmap", "Bilinear (mipmapping)"),
					T("options", "quality_interpolation_mode_trilinearmipmap", "Trilinear (mipmapping)")
				}, Math.Max(0, Array.IndexOf(modes, SwitchSettings.GetInterpolation())), i => SwitchSettings.SetInterpolation(modes[i])));
			// Upstream's own option and wording: Sharp = one pass (faster), Smooth = two.
			List.Add(new ChoiceItem(T("options", "quality_interpolation_transparency", "Transparency:"),
				new[] { T("options", "quality_interpolation_transparency_sharp", "Sharp"), T("options", "quality_interpolation_transparency_smooth", "Smooth") },
				SwitchSettings.GetTransparency() == TransparencyMode.Performance ? 0 : 1,
				i => SwitchSettings.SetTransparency(i == 0 ? TransparencyMode.Performance : TransparencyMode.Quality)));
			int[] limits = { -1, 0, 2048, 1024, 768, 512 };
			List.Add(new ChoiceItem(T("android", "texture_limit", "Largest texture size:"), new[]
				{
					T("android", "texture_limit_auto", "Automatic") + " (" + SwitchSettings.AutomaticTextureLimit + " px)",
					T("android", "texture_limit_none", "No limit"),
					"2048 px", "1024 px", "768 px", "512 px"
				}, Math.Max(0, Array.IndexOf(limits, SwitchSettings.GetTextureLimit())), i => SwitchSettings.SetTextureLimit(limits[i])));
			List.Add(new InfoItem(T("switch", "texture_limit_note",
				"Larger textures are scaled down as they load. The Switch gives a game about 3 GB, shared with the graphics: heavy routes need the limit (automatically 1024 px).")));
			int[] depths = { -1, 32, 16 };
			List.Add(new ChoiceItem(T("android", "texture_depth", "Texture colours:"), new[]
				{
					T("android", "texture_limit_auto", "Automatic") + " (16 bit)",
					T("android", "texture_depth_32", "Full (32 bit)"),
					T("android", "texture_depth_16", "Reduced (16 bit)")
				}, Math.Max(0, Array.IndexOf(depths, SwitchSettings.GetTextureDepth())), i => SwitchSettings.SetTextureDepth(depths[i])));
			List.Add(new ChoiceItem(T("switch", "compressed_textures", "Compressed textures:"), onOff,
				SwitchSettings.GetCompressedTextures() ? 0 : 1, i => SwitchSettings.SetCompressedTextures(i == 0)));
			List.Add(new InfoItem(T("switch", "compressed_textures_note",
				"The Switch's GPU reads compressed textures directly, in a quarter to an eighth of the memory, with slightly less detail.")));
			List.Add(new ChoiceItem(T("android", "unload_textures", "Unload unused textures:"), onOff,
				SwitchSettings.GetUnloadTextures() ? 0 : 1, i => SwitchSettings.SetUnloadTextures(i == 0)));
			List.Add(new InfoItem(T("android", "unload_textures_note",
				"Textures not drawn for 20 seconds are freed and loaded again when they come back into view, so long drives on heavy routes do not run out of memory. Turn it off to keep every texture loaded.")));
			List.Add(new SliderItem(T("options", "quality_distance_viewingdistance", "Viewing distance:"), 100, 2000, 50,
				SwitchSettings.GetViewingDistance(), v => v + " " + T("options", "quality_distance_viewingdistance_meters", "m"), SwitchSettings.SetViewingDistance));
			LibRender2.MotionBlurs.MotionBlurMode[] blurModes =
			{
				LibRender2.MotionBlurs.MotionBlurMode.None, LibRender2.MotionBlurs.MotionBlurMode.Low,
				LibRender2.MotionBlurs.MotionBlurMode.Medium, LibRender2.MotionBlurs.MotionBlurMode.High
			};
			List.Add(new ChoiceItem(T("options", "quality_distance_motionblur", "Motion blur:"), new[]
				{
					T("options", "quality_distance_motionblur_none", "None"), T("options", "quality_distance_motionblur_low", "Low"),
					T("options", "quality_distance_motionblur_medium", "Medium"), T("options", "quality_distance_motionblur_high", "High")
				}, Math.Max(0, Array.IndexOf(blurModes, SwitchSettings.GetMotionBlur())), i => SwitchSettings.SetMotionBlur(blurModes[i])));

			// --- performance: the console's clocks and fan in play ---
			List.Add(new HeadingItem(T("switch", "performance", "Performance")));
			PerformanceMode[] performanceModes = { PerformanceMode.Normal, PerformanceMode.Turbo, PerformanceMode.Overclock };
			List.Add(new ChoiceItem(T("switch", "performance_mode", "Performance mode:"), new[]
				{
					T("switch", "performance_normal", "Normal"), T("switch", "performance_turbo", "Turbo"), T("switch", "performance_overclock", "Overclock")
				}, Math.Max(0, Array.IndexOf(performanceModes, SwitchSettings.GetPerformanceMode())), i => SwitchSettings.SetPerformanceMode(performanceModes[i])));
			List.Add(new InfoItem(T("switch", "performance_note",
				"Normal: the console's own clocks and fan control. Turbo: on battery CPU 1581 MHz and GPU 460.8 MHz; plugged in or docked CPU 1785 MHz and GPU 921.6 MHz; memory 1600 MHz. Overclock: CPU 2091 MHz and GPU 998.4 MHz on battery or plugged in, memory 1600 MHz, where the console's clock table offers them (an overclocking module such as Horizon OC, with its undervolts set; without one it stops at CPU 1785 MHz and GPU 921.6 MHz). Both with the fan at full speed: more heat and battery use. Clocks go back to normal when OBNS closes.")));

			// --- shadows, as upstream's Options ---
			List.Add(new HeadingItem(T("options", "shadows_header", "Shadows")));
			ShadowMapResolution[] resolutions = { ShadowMapResolution.Off, ShadowMapResolution.Low, ShadowMapResolution.Medium, ShadowMapResolution.High };
			List.Add(new ChoiceItem(T("options", "shadows_resolution", "Shadow Resolution:"), new[]
				{
					T("options", "shadows_resolution_off", "Off"), T("options", "shadows_resolution_low", "Low (512×512)"),
					T("options", "shadows_resolution_medium", "Medium (1024×1024)"), T("options", "shadows_resolution_high", "High (2048×2048)")
				}, Math.Max(0, Array.IndexOf(resolutions, SwitchSettings.GetShadowResolution())), i => SwitchSettings.SetShadowResolution(resolutions[i])));
			ShadowDistance[] distances = { ShadowDistance.Near, ShadowDistance.Medium, ShadowDistance.Far, ShadowDistance.VeryFar, ShadowDistance.ViewingDistance };
			List.Add(new ChoiceItem(T("options", "shadows_distance", "Shadow Distance:"), new[]
				{
					T("options", "shadows_distance_near", "Near (150m)"), T("options", "shadows_distance_medium", "Medium (300m)"),
					T("options", "shadows_distance_far", "Far (500m)"), T("options", "shadows_distance_veryfar", "Very Far (800m)"),
					T("options", "shadows_distance_viewingdistance", "Follow Viewing Distance")
				}, Math.Max(0, Array.IndexOf(distances, SwitchSettings.GetShadowDistance())), i => SwitchSettings.SetShadowDistance(distances[i])));
			List.Add(new ChoiceItem(T("options", "shadows_cascades", "Shadow Cascades:"), new[]
				{
					T("options", "shadows_cascades_2", "2 Cascades (Fastest)"), T("options", "shadows_cascades_3", "3 Cascades (Balanced)"),
					T("options", "shadows_cascades_4", "4 Cascades (Best Quality)")
				}, (int)SwitchSettings.GetShadowCascades() - 2, i => SwitchSettings.SetShadowCascades((ShadowCascadeCount)(i + 2))));
			List.Add(new SliderItem(T("options", "shadows_strength", "Shadow Strength:"), 0, 100, 5,
				SwitchSettings.GetShadowStrength(), v => v + "%", SwitchSettings.SetShadowStrength));
			List.Add(new InfoItem(T("switch", "shadows_note", "Shadows draw the scene again from the sun, which lowers the frame rate. The change applies from the next game.")));

			// --- sound ---
			List.Add(new HeadingItem(T("options", "misc_sound", "Sound")));
			List.Add(new SliderItem(T("options", "misc_sound_number", "Number of allowed sounds:"), 16, 128, 4,
				SwitchSettings.GetSoundNumber(), v => v.ToString(), SwitchSettings.SetSoundNumber));

			// --- camera and time ---
			List.Add(new HeadingItem(T("options", "camera", "Camera options")));
			List.Add(new ChoiceItem(T("options", "camera_interior_transition", "Smooth interior transition"), onOff,
				SwitchSettings.GetCameraInteriorTransition() ? 0 : 1, i => SwitchSettings.SetCameraInteriorTransition(i == 0)));
			List.Add(new ChoiceItem(T("options", "camera_exterior_transition", "Smooth exterior transition"), onOff,
				SwitchSettings.GetCameraExteriorTransition() ? 0 : 1, i => SwitchSettings.SetCameraExteriorTransition(i == 0)));
			List.Add(new SliderItem(T("options", "camera_transition_duration", "Transition duration (sec):"), 1, 50, 1,
				SwitchSettings.GetCameraTransitionTenths(), v => (v / 10.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), SwitchSettings.SetCameraTransitionTenths));
			List.Add(new SliderItem(T("options", "advanced_timefactor", "Time acceleration factor:"), 1, 5, 1,
				SwitchSettings.GetTimeAccelerationFactor(), v => "×" + v, SwitchSettings.SetTimeAccelerationFactor));

			// --- touch, handheld only ---
			List.Add(new HeadingItem(T("switch", "touch", "Touch screen")));
			// On (Android's buttons), ATS (the safety system's keys alone, for driving with the controllers), off.
			int[] touchModes = { SwitchSettings.TouchFull, SwitchSettings.TouchAts, SwitchSettings.TouchOff };
			List.Add(new ChoiceItem(T("switch", "touch_controls", "Touch controls in handheld mode:"),
				new[] { onOff[0], T("switch", "touch_ats", "ATS keys only"), onOff[1] },
				Array.IndexOf(touchModes, SwitchSettings.GetTouchMode()), i => SwitchSettings.SetTouchMode(touchModes[i])));
			Func<bool> touchOn = SwitchSettings.GetTouchControls;
			List.Add(new ChoiceItem(T("android", "touch_handles", "Touch the cab panel's handles:"), onOff,
				SwitchSettings.GetTouchHandles() ? 0 : 1, i => SwitchSettings.SetTouchHandles(i == 0), touchOn));
			List.Add(new ChoiceItem(T("android", "touch_button_size", "Touch button size:"),
				new[] { T("android", "size_small", "Small"), T("android", "size_normal", "Normal"), T("android", "size_large", "Large") },
				SwitchSettings.GetTouchButtonSize(), SwitchSettings.SetTouchButtonSize, touchOn));
			List.Add(new ChoiceItem(T("android", "touch_button_opacity", "Touch button opacity:"),
				new[] { T("android", "opacity_solid", "Solid"), T("android", "opacity_translucent", "Translucent"), T("android", "opacity_faint", "Faint") },
				SwitchSettings.GetTouchButtonOpacityIndex(), SwitchSettings.SetTouchButtonOpacity, touchOn));
			List.Add(new InfoItem(T("switch", "touch_note",
				"The on-screen buttons and the cab's touch areas, for playing on the console's screen. ATS keys only shows just the safety system's keys, larger, for driving with the controllers. Docked there is no touch screen, so they are off whatever is chosen here.")));

			// --- content ---
			List.Add(new HeadingItem(T("panel", "packages", "Package Management")));
			List.Add(new InfoItem(() => T("options", "package_route_directory", "Route installation directory:") + "\n" + Menu.FileSystem?.RouteInstallationDirectory));
			List.Add(new InfoItem(() => T("options", "package_train_directory", "Train installation directory:") + "\n" + Menu.FileSystem?.TrainInstallationDirectory));
			List.Add(new InfoItem(() => T("options", "package_other_directory", "Other items installation directory:") + "\n" + Menu.FileSystem?.OtherInstallationDirectory));
			List.Add(new InfoItem(T("switch", "content_note",
				"Routes and trains are read from LegacyContent beside OBNS on the SD card (/switch/OBNS/LegacyContent/Railway and Train).")));
			List.FocusFirst();
		}
	}
}
