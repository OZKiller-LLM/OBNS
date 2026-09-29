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
using Android.App;
using Android.Graphics;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace OpenBve.Android
{
	/// <summary>
	/// The main menu, and the app's launcher: the Android counterpart of the Windows main form,
	/// with its sections down the left - Start new game, Package Management, Options - and the
	/// chosen section on the right.
	/// </summary>
	[Activity(Label = "OpenBVE",
		Name = "net.openbve.MenuActivity",
		MainLauncher = true,
		Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
		ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape,
		ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize |
		                       global::Android.Content.PM.ConfigChanges.KeyboardHidden | global::Android.Content.PM.ConfigChanges.ScreenLayout)]
	public class MenuActivity : Activity
	{
		private enum Section
		{
			Start,
			Packages,
			Controls,
			Options,
			About
		}

		private FrameLayout content;
		private LinearLayout rail;
		private StartPage startPage;
		private PackagesPage packagesPage;
		private OptionsPage optionsPage;
		private ControlsPage controlsPage;
		private View aboutPage;
		private Section section = Section.Start;

		/// <summary>Intent extra: the game's pause menu chose Quit, so the whole app closes.</summary>
		public const string QuitExtra = "quit";

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			// Where the device's own ANGLE is copied for the Vulkan backend (see GraphicsLibraries).
			OpenTK.Graphics.GraphicsLibraries.PrivateFolder = System.IO.Path.Combine(FilesDir.AbsolutePath, "angle");
			if (Intent?.GetBooleanExtra(QuitExtra, false) == true)
			{
				FinishAndRemoveTask();
				return;
			}

			Menu.Init(this);
			BetaLog.Start(this, "menu");
			Build();
		}

		protected override void OnNewIntent(Intent intent)
		{
			base.OnNewIntent(intent);
			if (intent?.GetBooleanExtra(QuitExtra, false) == true)
			{
				FinishAndRemoveTask();
			}
		}

		/// <summary>Pads a view by the system bars and display cutout, plus a fixed margin.</summary>
		private sealed class InsetsPadding : Java.Lang.Object, View.IOnApplyWindowInsetsListener
		{
			private readonly int pad;

			public InsetsPadding(int pad)
			{
				this.pad = pad;
			}

			public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
			{
				int left = 0, top = 0, right = 0, bottom = 0;
				if (global::Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.R)
				{
					Insets bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
					left = bars.Left;
					top = bars.Top;
					right = bars.Right;
					bottom = bars.Bottom;
				}
				else
				{
#pragma warning disable CA1422
					left = insets.SystemWindowInsetLeft;
					top = insets.SystemWindowInsetTop;
					right = insets.SystemWindowInsetRight;
					bottom = insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
				}

				view.SetPadding(pad + left, pad + top, pad + right, pad + bottom);
				return insets;
			}
		}

		private void Build()
		{
			LinearLayout root = new LinearLayout(this) { Orientation = Orientation.Horizontal };
			root.SetBackgroundColor(Ui.Background);
			int pad = Ui.Dp(this, 10);
			root.SetPadding(pad, pad, pad, pad);
			/*
			 * From Android 15 apps draw edge to edge: the status and navigation bars (on Samsung
			 * phones in landscape, the navigation bar down the right-hand side) overlap the window.
			 * Keep the menu clear of them.
			 */
			root.SetOnApplyWindowInsetsListener(new InsetsPadding(pad));

			rail = new LinearLayout(this) { Orientation = Orientation.Vertical };
			TextView title = Ui.Heading(this, "OpenBVE");
			title.TextSize = 22.0f;
			rail.AddView(title);
			rail.AddView(Ui.Label(this, Menu.Version.Replace("-android.", " · Android r"), 12.0f), Ui.Stacked(this, bottom: 12));
			AddRailButton(Menu.T("panel", "start", "Start new game"), Section.Start);
			AddRailButton(Menu.T("panel", "packages", "Package Management"), Section.Packages);
			AddRailButton(Menu.T("panel", "controls", "Controls"), Section.Controls);
			AddRailButton(Menu.T("panel", "options", "Options"), Section.Options);
			AddRailButton(Menu.T("panel", "about", "About"), Section.About);
			root.AddView(rail, new LinearLayout.LayoutParams(Ui.Dp(this, 190), ViewGroup.LayoutParams.MatchParent));

			content = new FrameLayout(this);
			content.SetPadding(pad, 0, 0, 0);
			root.AddView(content, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1.0f));
			SetContentView(root);
			Ui.EnableFocusRing(this);

			startPage = new StartPage(this);
			packagesPage = new PackagesPage(this);
			packagesPage.ContentChanged += () => startPage.Refresh();
			optionsPage = new OptionsPage(this);
			optionsPage.LanguageChanged += Rebuild;
			aboutPage = BuildAbout();
			controlsPage = new ControlsPage(this);
			foreach (View page in new[] { startPage, packagesPage, controlsPage, (View)optionsPage, aboutPage })
			{
				content.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			}

			Show(section);
		}

		/// <summary>Rebuilds every label, after the language changed.</summary>
		private void Rebuild()
		{
			// Posted: the spinner that changed the language is still dispatching its event.
			content.Post(Build);
		}

		private void AddRailButton(string text, Section target)
		{
			Button button = Ui.Button(this, text);
			button.Gravity = GravityFlags.CenterVertical | GravityFlags.Start;
			button.Tag = (int)target;
			button.Click += (s, e) => Show(target);
			rail.AddView(button, Ui.Stacked(this));
		}

		private void Show(Section target)
		{
			section = target;
			startPage.Visibility = target == Section.Start ? ViewStates.Visible : ViewStates.Gone;
			packagesPage.Visibility = target == Section.Packages ? ViewStates.Visible : ViewStates.Gone;
			optionsPage.Visibility = target == Section.Options ? ViewStates.Visible : ViewStates.Gone;
			controlsPage.Visibility = target == Section.Controls ? ViewStates.Visible : ViewStates.Gone;
			aboutPage.Visibility = target == Section.About ? ViewStates.Visible : ViewStates.Gone;
			for (int i = 0; i < rail.ChildCount; i++)
			{
				if (rail.GetChildAt(i) is Button button && button.Tag != null)
				{
					bool selected = (int)button.Tag == (int)target;
					button.Background = Ui.Panel(this, selected ? Ui.Selection : Ui.SurfaceRaised, 8);
				}
			}
		}

		private View BuildAbout()
		{
			ScrollView scroll = new ScrollView(this);
			LinearLayout column = new LinearLayout(this) { Orientation = Orientation.Vertical };
			column.AddView(Ui.Heading(this, "OpenBVE " + Menu.Version));
			column.AddView(Ui.Body(this,
				"A free train simulator, ported to run natively on Android.\n\n" +
				"The simulation, the content formats and the renderer are OpenBVE's own code, built for Android unchanged " +
				"wherever possible. Graphics run on OpenGL ES, or on Vulkan through ANGLE; sound runs on OpenAL Soft.\n\n" +
				"Routes and trains can be used from anywhere on the device's storage, or installed from packages and " +
				"plain archives under Package Management.\n\n" +
				"Content folders:\n" + Menu.FileSystem.RouteInstallationDirectory + "\n" + Menu.FileSystem.TrainInstallationDirectory));

			/*
			 * Licences. OpenBVE and this port are under the Simplified BSD licence (the original
			 * OpenBVE code is public domain), which asks that binaries reproduce the notice; the
			 * libraries built in have notices of their own. All are packaged under Assets/Licenses.
			 */
			if (BetaLog.Enabled)
			{
				column.AddView(Ui.Heading(this, "Beta build"));
				column.AddView(Ui.Body(this, "This build keeps a data log of each session for debugging: the route and train, the renderer, " +
				                             "the game mode, frame rates and every error. The logs are plain text in " +
				                             (BetaLog.Location != null ? System.IO.Path.GetDirectoryName(BetaLog.Location) : "Documents/OpenBVE") +
				                             " - read them, and send them to the developers with your report."));
			}

			column.AddView(Ui.Heading(this, "Licences"));
			column.AddView(Ui.Body(this,
				"OpenBVE for Android is free software under the Simplified BSD licence (BSD-2-Clause). It includes " +
				"the libraries listed below; tap one to read its licence."));
			string[] notices = Assets?.List("Licenses") ?? Array.Empty<string>();
			Array.Sort(notices, StringComparer.OrdinalIgnoreCase);
			foreach (string notice in notices)
			{
				string file = notice;
				Button button = Ui.Button(this, System.IO.Path.GetFileNameWithoutExtension(file));
				button.Click += (sender, e) => ShowLicence(file);
				column.AddView(button, Ui.Stacked(this, ViewGroup.LayoutParams.WrapContent));
			}

			scroll.AddView(column);
			return scroll;
		}

		private void ShowLicence(string file)
		{
			string text;
			using (System.IO.StreamReader reader = new System.IO.StreamReader(Assets.Open("Licenses/" + file)))
			{
				text = reader.ReadToEnd();
			}

			ScrollView scroll = new ScrollView(this);
			TextView body = new TextView(this) { Text = text, TextSize = 12.0f };
			body.SetTextIsSelectable(true);
			body.SetPadding(40, 24, 40, 24);
			scroll.AddView(body);
			new AlertDialog.Builder(this)
				.SetTitle(System.IO.Path.GetFileNameWithoutExtension(file))
				.SetView(scroll)
				.SetPositiveButton(global::Android.Resource.String.Ok, (IDialogInterfaceOnClickListener)null)
				.Show();
		}

		protected override void OnResume()
		{
			base.OnResume();
			// Coming back from the system's storage access page, or from a game.
			optionsPage?.Refresh();
			if (Intent != null && StorageAccess.IsGranted)
			{
				// Access just granted: routes and trains move to Documents/OpenBVE (SD card first).
				if (Menu.FileSystem != null)
				{
					AndroidFileSystem.UpdateContentFolder(this, Menu.FileSystem);
				}

				startPage?.Refresh();
				packagesPage?.Refresh();
			}
		}

		protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
		{
			base.OnActivityResult(requestCode, resultCode, data);
			if (requestCode == PackagesPage.PickArchiveRequest && resultCode == Result.Ok)
			{
				packagesPage?.OnPicked(data?.Data);
			}
		}
	}
}
