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
using Android.App;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using OpenBveApi.Graphics;
using OpenTK.Graphics;

namespace OpenBve.Android
{
	/// <summary>
	/// Options: the subset of the Windows options that applies on a phone, grouped under the same
	/// headings, and the Advanced Options graphics backend choice (OpenGL ES or Vulkan).
	/// </summary>
	/// <remarks>Every change is saved at once and applies from the next game.</remarks>
	public class OptionsPage : ScrollView
	{
		private readonly Activity activity;
		private readonly LinearLayout column;
		private TextView storageStatus;

		/// <summary>Raised when the language changes, so the menu can rebuild its labels.</summary>
		public event Action LanguageChanged;

		public OptionsPage(Activity activity) : base(activity)
		{
			this.activity = activity;
			column = new LinearLayout(activity) { Orientation = Orientation.Vertical };
			int pad = Ui.Dp(activity, 12);
			column.SetPadding(pad, 0, pad, pad);
			AddView(column);

			// --- language ---
			Section(Menu.T("options", "language", "Language"));
			List<Menu.Language> languages = Menu.Languages();
			string current = AndroidSettings.GetLanguage(activity);
			int languageIndex = Math.Max(0, languages.FindIndex(l => l.Code == current));
			Dropdown(Menu.T("options", "language", "Language"), languages.Select(l => l.ToString()).ToArray(), languageIndex, i =>
			{
				if (languages[i].Code == AndroidSettings.GetLanguage(activity))
				{
					return;
				}

				AndroidSettings.SetLanguage(activity, languages[i].Code);
				Menu.SetLanguage(languages[i].Code);
				LanguageChanged?.Invoke();
			});

			// --- quality ---
			Section(Menu.T("options", "quality", "Quality"));
			InterpolationMode[] modes =
			{
				InterpolationMode.NearestNeighbor, InterpolationMode.Bilinear, InterpolationMode.NearestNeighborMipmapped,
				InterpolationMode.BilinearMipmapped, InterpolationMode.TrilinearMipmapped
			};
			string[] modeNames =
			{
				Menu.T("options", "quality_interpolation_mode_nearest", "Nearest neighbor"),
				Menu.T("options", "quality_interpolation_mode_bilinear", "Bilinear"),
				Menu.T("options", "quality_interpolation_mode_nearestmipmap", "Nearest neighbor (mipmapping)"),
				Menu.T("options", "quality_interpolation_mode_bilinearmipmap", "Bilinear (mipmapping)"),
				Menu.T("options", "quality_interpolation_mode_trilinearmipmap", "Trilinear (mipmapping)")
			};
			Dropdown(Menu.T("options", "quality_interpolation_mode", "Mode:"), modeNames,
				Math.Max(0, Array.IndexOf(modes, AndroidSettings.GetInterpolation(activity))),
				i => AndroidSettings.SetInterpolation(activity, modes[i]));

			Slider(Menu.T("options", "quality_distance_viewingdistance", "Viewing distance:"), 100, 2000, 50,
				AndroidSettings.GetViewingDistance(activity), v => v + " " + Menu.T("options", "quality_distance_viewingdistance_meters", "m"),
				v => AndroidSettings.SetViewingDistance(activity, v));

			// --- sound ---
			Section(Menu.T("options", "misc_sound", "Sound"));
			Slider(Menu.T("options", "misc_sound_number", "Number of allowed sounds:"), 16, 128, 4,
				AndroidSettings.GetSoundNumber(activity), v => v.ToString(), v => AndroidSettings.SetSoundNumber(activity, v));

			// --- content folders ---
			Section(Menu.T("panel", "packages", "Package Management"));
			Info(Menu.T("options", "package_route_directory", "Route installation directory:") + "\n" + Menu.FileSystem.RouteInstallationDirectory);
			Info(Menu.T("options", "package_train_directory", "Train installation directory:") + "\n" + Menu.FileSystem.TrainInstallationDirectory);
			Info(Menu.T("options", "package_other_directory", "Other items installation directory:") + "\n" + Menu.FileSystem.OtherInstallationDirectory);
			storageStatus = Ui.Label(activity, string.Empty);
			column.AddView(storageStatus, Ui.Stacked(activity));
			Button storage = Ui.Button(activity, Menu.T("android", "storage_button", "Allow access to all files…"));
			storage.Click += (s, e) => StorageAccess.Request(activity);
			column.AddView(storage, Ui.Stacked(activity, ViewGroup.LayoutParams.WrapContent));

			// --- advanced ---
			Section(Menu.T("options", "advanced", "Advanced Options"));
			bool anglePackaged = GraphicsLibraries.IsAngleAvailable();
			bool vulkanDevice = activity.PackageManager.HasSystemFeature(PackageManager.FeatureVulkanHardwareVersion);
			string[] backends =
			{
				"OpenGL ES",
				"Vulkan (ANGLE)" + (anglePackaged && vulkanDevice ? string.Empty : " — " + Menu.T("android", "unavailable", "unavailable"))
			};
			Dropdown(Menu.T("android", "graphics_backend", "Graphics backend:"), backends,
				AndroidSettings.GetBackend(activity) == GraphicsBackend.Vulkan ? 1 : 0,
				i => AndroidSettings.SetBackend(activity, i == 1 ? GraphicsBackend.Vulkan : GraphicsBackend.OpenGL));
			Dropdown(Menu.T("android", "touch_handles", "Touch the cab panel's handles:"),
				new[] { Menu.T("android", "on", "On"), Menu.T("android", "off", "Off") },
				AndroidSettings.GetTouchHandles(activity) ? 0 : 1,
				i => AndroidSettings.SetTouchHandles(activity, i == 0));
			Dropdown(Menu.T("android", "touch_button_size", "Touch button size:"),
				new[] { Menu.T("android", "size_small", "Small"), Menu.T("android", "size_normal", "Normal"), Menu.T("android", "size_large", "Large") },
				AndroidSettings.GetTouchButtonSize(activity),
				i => AndroidSettings.SetTouchButtonSize(activity, i));
			Dropdown(Menu.T("android", "touch_button_opacity", "Touch button opacity:"),
				new[] { Menu.T("android", "opacity_solid", "Solid"), Menu.T("android", "opacity_translucent", "Translucent"), Menu.T("android", "opacity_faint", "Faint") },
				AndroidSettings.GetTouchButtonOpacityIndex(activity),
				i => AndroidSettings.SetTouchButtonOpacity(activity, i));
			Dropdown(Menu.T("android", "interface_style", "In-game interface:"),
				new[]
				{
					Menu.T("android", "interface_auto", "Automatic (desktop style in Samsung DeX / desktop mode)"),
					Menu.T("android", "interface_touch", "Touch"),
					Menu.T("android", "interface_desktop", "Desktop (HUD, keyboard and controller)")
				},
				(int)AndroidSettings.GetInterfaceStyle(activity),
				i => AndroidSettings.SetInterfaceStyle(activity, (InterfaceStyle)i));
			string note = Menu.T("android", "graphics_backend_note",
				"Vulkan runs the same renderer through ANGLE, which turns its OpenGL ES calls into Vulkan. The change applies from the next game.");
			if (!anglePackaged)
			{
				note += "\n" + Menu.T("android", "angle_missing", "This device's system has no ANGLE for the app to use, so Vulkan falls back to OpenGL ES.");
			}
			else if (!vulkanDevice)
			{
				note += "\n" + Menu.T("android", "vulkan_missing", "This device reports no Vulkan support, so Vulkan falls back to OpenGL ES.");
			}

			Info(note);
			Refresh();
		}

		/// <summary>Updates the storage access line, e.g. on returning from the system settings page.</summary>
		public void Refresh()
		{
			storageStatus.Text = StorageAccess.IsGranted
				? "✓ " + Menu.T("android", "storage_granted", "The app can read content anywhere on this device's storage.")
				: Menu.T("android", "storage_denied", "The app can only read its own folders. Allow access to all files to use content stored elsewhere.");
		}

		private void Section(string title)
		{
			TextView heading = Ui.Heading(activity, title);
			heading.SetTextColor(Ui.Accent);
			column.AddView(heading, Ui.Stacked(activity, top: 14, bottom: 2));
		}

		private void Info(string text)
		{
			column.AddView(Ui.Label(activity, text, 13.0f), Ui.Stacked(activity));
		}

		private void Dropdown(string label, string[] items, int selected, Action<int> changed)
		{
			LinearLayout row = Row(label);
			Spinner spinner = new Spinner(activity)
			{
				Adapter = new ArrayAdapter<string>(activity, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, items)
			};
			spinner.SetSelection(selected);
			spinner.ItemSelected += (s, e) => changed(e.Position);
			row.AddView(spinner, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
		}

		private void Slider(string label, int min, int max, int step, int value, Func<int, string> format, Action<int> changed)
		{
			LinearLayout row = Row(label);
			TextView shown = Ui.Body(activity, format(value));
			shown.SetMinWidth(Ui.Dp(activity, 80));
			SeekBar bar = new SeekBar(activity) { Max = (max - min) / step, Progress = (Math.Max(min, Math.Min(max, value)) - min) / step };
			bar.ProgressChanged += (s, e) => shown.Text = format(min + e.Progress * step);
			bar.StopTrackingTouch += (s, e) => changed(min + bar.Progress * step);
			row.AddView(bar, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			row.AddView(shown);
		}

		private LinearLayout Row(string label)
		{
			LinearLayout row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
			row.SetGravity(GravityFlags.CenterVertical);
			TextView text = Ui.Label(activity, label);
			text.SetMinWidth(Ui.Dp(activity, 220));
			row.AddView(text);
			column.AddView(row, Ui.Stacked(activity));
			return row;
		}
	}
}
