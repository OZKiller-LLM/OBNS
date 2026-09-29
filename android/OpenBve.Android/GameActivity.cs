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
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using OpenBveApi.Interface;

namespace OpenBve.Android
{
	/// <summary>
	/// The simulation: the cab view full screen, the driving controls over it, and a loading
	/// screen while the route and train load.
	/// </summary>
	/// <remarks>
	/// Runs in its own process (<c>:game</c>). OpenBVE keeps a great deal of state in statics -
	/// the current route, the player train, sound sources, the plugins' own references - and the
	/// GL bindings attach to a graphics library once per process. A fresh process per session
	/// gives every game the clean start upstream gets from a fresh launch, lets a graphics
	/// backend change in Options apply to the next game without restarting the app, and keeps a
	/// crash in the simulation from taking the menu with it.
	/// </remarks>
	/*
	 * In a desktop mode (DeX, Android's freeform windows) the game opens at 1280 x 720 dp,
	 * centred, rather than in the system's small default window.
	 */
	[Layout(DefaultWidth = "1280dp", DefaultHeight = "720dp", Gravity = "center", MinWidth = "480dp", MinHeight = "300dp")]
	[Activity(Label = "OpenBVE",
		Name = "net.openbve.GameActivity",
		Process = ":game",
		Exported = true,
		Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
		ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape,
		/*
		 * Everything a desktop mode changes - the display, its size and density, the UI mode,
		 * a keyboard or mouse coming and going - is handled in place (OnConfigurationChanged):
		 * recreating the activity would reload the route.
		 */
		ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize |
		                       global::Android.Content.PM.ConfigChanges.SmallestScreenSize | global::Android.Content.PM.ConfigChanges.ScreenLayout |
		                       global::Android.Content.PM.ConfigChanges.Density | global::Android.Content.PM.ConfigChanges.UiMode |
		                       global::Android.Content.PM.ConfigChanges.Keyboard | global::Android.Content.PM.ConfigChanges.KeyboardHidden |
		                       global::Android.Content.PM.ConfigChanges.Navigation)]
	public class GameActivity : Activity
	{
		/// <summary>Intent extra: let the demo driver drive, for unattended tests.</summary>
		public const string AutoDriveExtra = "autodrive";

		private TextView status;
		private ControlOverlay overlay;
		private LoadingScreen loading;
		private PauseMenu pauseMenu;
		private GameView view;
		private bool started;
		private FrameLayout layout;

		/// <summary>The density the touch controls and pause menu were built for; see RebuildForDensity.</summary>
		private float builtDensity;

		/// <summary>Sizes the start-up / stall panel for the current density (again after a move to another display).</summary>
		private void StyleStatus()
		{
			float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
			// Text sizes are turned into pixels when set, with the density of the moment.
			status.TextSize = 12.0f;
			status.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));
			status.SetMaxWidth((int)(Resources.DisplayMetrics.WidthPixels * 0.55f));
			global::Android.Graphics.Drawables.GradientDrawable panel = new global::Android.Graphics.Drawables.GradientDrawable();
			panel.SetColor(Color.Argb(190, 10, 14, 22));
			panel.SetCornerRadius(10 * density);
			status.Background = panel;
		}

		private ControlOverlay CreateOverlay()
		{
			ControlOverlay created = new ControlOverlay(this, view.Controls, () => view.Session?.GetTimetable(),
				() => view.Session is AndroidTrainSession session ? (session.GetRouteGeometry(), session.GetRouteLive()) : (null, null))
			{
				Visibility = ViewStates.Gone
			};
			created.PauseRequested += OpenPauseMenu;
			return created;
		}

		private PauseMenu CreatePauseMenu()
		{
			PauseMenu created = new PauseMenu(this)
			{
				Visibility = ViewStates.Gone,
				Stations = () => view.Session?.JumpStations ?? (System.Collections.Generic.IReadOnlyList<(int, string)>)Array.Empty<(int, string)>(),
				LastStation = () => view.Session?.Train.LastStation ?? -1,
				ButtonsHidden = () => desktopInterface ? (bool?)null : overlay.ButtonsHidden
			};
			created.ButtonsHiddenChanged += hidden =>
			{
				AndroidSettings.SetTouchButtonsHidden(this, hidden);
				overlay.SetButtonsHidden(hidden);
			};
			created.Resumed += () => view.Paused = false;
			created.JumpRequested += station => view.Controls.Post(s => s.JumpToStation(station));
			created.ExitRequested += Quit;
			created.QuitRequested += QuitApp;
			// Bindings changed on the Controls page apply at once: the keys are read again on the next press.
			created.ControlsClosed += () =>
			{
				keyboard?.ReleaseAll(view.Controls);
				keyboard = null;
			};
			return created;
		}

		/*
		 * The touch controls and the pause menu are sized in pixels for the density they were
		 * built at. Moved to a display of another density - a DeX monitor at 160 dpi, back to
		 * the phone's 600 - they would be tiny or huge, so they are built again for the new one.
		 */
		private void RebuildForDensity(float density)
		{
			builtDensity = density;
			int index = layout.IndexOfChild(overlay);
			ViewStates visibility = overlay.Visibility;
			layout.RemoveView(overlay);
			overlay = CreateOverlay();
			overlay.Visibility = visibility;
			if (started)
			{
				overlay.SetSingleHandle(view.Session?.Train.Handles.HandleType == global::TrainManager.Handles.HandleType.SingleHandle);
			}

			layout.AddView(overlay, index, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

			/*
			 * The pause menu too, even while open: moving to another display pauses the game,
			 * so it is usually open just then. It reopens at its top level.
			 */
			bool menuOpen = pauseMenu.Visibility == ViewStates.Visible;
			index = layout.IndexOfChild(pauseMenu);
			layout.RemoveView(pauseMenu);
			pauseMenu = CreatePauseMenu();
			layout.AddView(pauseMenu, index, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			if (menuOpen)
			{
				pauseMenu.Open();
			}

			StyleStatus();

			global::Android.Util.Log.Info("OpenBVE", "touch controls rebuilt for density " + density);
		}
		private bool desktopInterface;
		private KeyboardInput keyboard;

		protected override void OnCreate(Bundle savedInstanceState)
		{
			base.OnCreate(savedInstanceState);
			// Where the device's own ANGLE is copied for the Vulkan backend (see GraphicsLibraries).
			OpenTK.Graphics.GraphicsLibraries.PrivateFolder = System.IO.Path.Combine(FilesDir.AbsolutePath, "angle");

			/*
			 * .NET Framework has every Windows code page built in; modern .NET, including on Android,
			 * has only the Unicode encodings unless a provider is registered. BVE content is full of
			 * legacy encodings - Big5 for Hong Kong and Taiwan, Shift-JIS for Japan, GBK for the
			 * mainland - in route files, object files and folder names alike. Upstream never needs
			 * to register this, so without it those files decode to garbage and paths fail to resolve.
			 */
			System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
			// Translations for the loading screen and dialogs.
			Menu.Init(this);
			BetaLog.Start(this, "game");

			Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
			AndroidSettings.ApplyIntent(this, Intent);
			// For A/B checks of the renderer's draw-call merging (see AndroidRenderer.DrawOpaqueFaces).
			AndroidRenderer.MergeOpaqueFaces = Intent?.GetBooleanExtra("merge_faces", true) ?? true;
			AndroidRenderer.CheckMerging = Intent?.GetBooleanExtra("merge_check", false) ?? false;
			// For diagnosing a Win32 plugin: record its calls for replay in OpenBve.X86.Tests.
			NativePlugins.NativePluginBridge.TraceFolder = Intent?.GetBooleanExtra("plugin_trace", false) == true ? GetExternalFilesDir(null)?.AbsolutePath : null;
			LaunchParameters launch = LaunchParameters.ReadFrom(Intent);

			view = new GameView(this)
			{
				AutoDrive = Intent?.GetBooleanExtra(AutoDriveExtra, false) ?? false,
				SkipTrackFollowingObjects = Intent?.GetBooleanExtra("skip_tfo", false) ?? false,
				Launch = launch
			};
			view.Started += message => RunOnUiThread(() => OnStarted(message));
			view.Failed += error => RunOnUiThread(() => OnFailed(error.Message));
			view.InfoChanged += status => RunOnUiThread(() =>
			{
				overlay?.SetStatus(status);
				overlay?.SetAiActive(view.Session?.Train.AI != null);
			});

			/*
			 * The interface: touch controls with the touch timetable, or - in Samsung DeX or another
			 * desktop mode, or when chosen in Options - upstream's HUD and timetable driven from the
			 * keyboard or a controller, with no touch controls over the view.
			 */
			desktopInterface = AndroidSettings.UseDesktopInterface(this);
			view.DesktopInterface = desktopInterface;
			global::Android.Util.Log.Info("OpenBVE", "interface: " + (desktopInterface ? "desktop" : "touch") + " (" + AndroidSettings.GetInterfaceStyle(this) + "; " +
			                                         AndroidSettings.DesktopModeDetail(this) + ")");
			overlay = CreateOverlay();
			pauseMenu = CreatePauseMenu();
			builtDensity = Resources?.DisplayMetrics?.Density ?? 1.0f;
			loading = new LoadingScreen(this, launch);

			/*
			 * The startup summary and the stall warning: a panel in the middle of the view, clear
			 * of the controls round the edges, which a tap dismisses.
			 */
			float density = Resources?.DisplayMetrics?.Density ?? 2.0f;
			status = new TextView(this) { Visibility = ViewStates.Gone };
			status.SetTextColor(Color.White);
			StyleStatus();
			status.Click += (sender, e) => status.Visibility = ViewStates.Gone;
			// Tapped away, but never focused: a controller's D-pad belongs to the menus beneath.
			status.Focusable = false;
			status.FocusableInTouchMode = false;

			layout = new FrameLayout(this);
			layout.AddView(view, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			layout.AddView(overlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			layout.AddView(pauseMenu, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			layout.AddView(loading, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			layout.AddView(status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
			SetContentView(layout);
			Ui.EnableFocusRing(this);

			// A controller or keyboard unplugged mid-press never sends its releases; see DeviceListener.
			inputDevices = (global::Android.Hardware.Input.InputManager)GetSystemService(InputService);
			deviceListener = new DeviceListener(this);
			inputDevices?.RegisterInputDeviceListener(deviceListener, null);

			// The interface follows the game between the phone's screen and a desktop display.
			recheckInterface = RecheckInterface;
			view.WindowBack += () => RunOnUiThread(() =>
			{
				view.RemoveCallbacks(recheckInterface);
				view.PostDelayed(recheckInterface, 300);
			});
			((global::Android.Hardware.Display.DisplayManager)GetSystemService(DisplayService))?.RegisterDisplayListener(new DisplayWatcher(this), null);

			// From Android 13, and always when targeting 16, Back goes to a registered callback
			// instead of OnBackPressed; without one the activity just finishes, leaving the game
			// process running.
			if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
			{
				OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(HandleBack));
			}
		}

		private sealed class BackCallback : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
		{
			private readonly Action action;

			public BackCallback(Action action)
			{
				this.action = action;
			}

			public void OnBackInvoked() => action();
		}

		public override void OnWindowFocusChanged(bool hasFocus)
		{
			base.OnWindowFocusChanged(hasFocus);
			if (hasFocus)
			{
				HideSystemBars();
			}
		}

#pragma warning disable CS0672, CA1422 // OnBackPressed: the replacement callback API needs AndroidX
		public override void OnBackPressed() => HandleBack();
#pragma warning restore CS0672, CA1422

		private void HandleBack()
		{
			if (!started)
			{
				// As upstream's loading screen: cancelling stops the plugins, then the game closes.
				LoadProgress.Cancel();
				Quit();
				return;
			}

			// As upstream's Escape: back opens the pause menu, and inside it goes back a level.
			if (pauseMenu.Visibility == ViewStates.Visible)
			{
				pauseMenu.Back();
			}
			else
			{
				OpenPauseMenu();
			}
		}

		private void OpenPauseMenu() => OpenPauseMenu(false);

		private void OpenPauseMenu(bool fromKeys)
		{
			if (!started || pauseMenu.Visibility == ViewStates.Visible)
			{
				return;
			}

			// The menu takes the keys now; a key held down would otherwise never come up.
			keyboard?.ReleaseAll(view.Controls);

			view.Paused = true;
			// The start-up summary would sit on top of the menu.
			status.Visibility = ViewStates.Gone;
			pauseMenu.Open(fromKeys);
		}

		/*
		 * The keyboard and controllers: upstream's key bindings in both interfaces (a keyboard
		 * paired with a phone works too), read once the game's folders are known.
		 */
		private KeyboardInput Keyboard()
		{
			if (keyboard == null && started && Program.FileSystem != null)
			{
				keyboard = new KeyboardInput(Program.FileSystem.SettingsFolder, Program.FileSystem.GetDataFolder("Controls"));
			}

			return keyboard;
		}

		public override bool DispatchKeyEvent(KeyEvent e)
		{

			if (started && pauseMenu.Visibility == ViewStates.Visible)
			{
				// Escape backs out of the pause menu as upstream's does; its other keys are the menu's.
				if (e.KeyCode == Keycode.Escape)
				{
					if (e.Action == KeyEventActions.Down && e.RepeatCount == 0)
					{
						pauseMenu.Back();
					}

					return true;
				}

				return base.DispatchKeyEvent(e);
			}

			if (started && e.KeyCode != Keycode.Back && Keyboard() is KeyboardInput input && input.Handle(e, view.Controls, out KeyboardInput.Special special))
			{
				switch (special)
				{
					case KeyboardInput.Special.Pause:
						OpenPauseMenu(true);
						break;
					case KeyboardInput.Special.Timetable when desktopInterface:
						view.Controls.Press(OpenBveApi.Interface.Translations.Command.TimetableToggle, exact: true);
						view.Controls.Release(OpenBveApi.Interface.Translations.Command.TimetableToggle, exact: true);
						break;
					case KeyboardInput.Special.Timetable:
						overlay.ToggleTimetable();
						break;
				}

				return true;
			}

			return base.DispatchKeyEvent(e);
		}

		public override bool DispatchGenericMotionEvent(MotionEvent e)
		{
			if (started && pauseMenu.Visibility != ViewStates.Visible && Keyboard() is KeyboardInput input && input.HandleMotion(e, view.Controls))
			{
				return true;
			}

			return base.DispatchGenericMotionEvent(e);
		}

		protected override void OnPause()
		{
			base.OnPause();
			// A tester may never quit properly: the frame rate summary so far, each time the game leaves the screen.
			BetaLog.Summary();
			// Leaving the app (home, a call, the recents screen) pauses the game, as a desktop window losing focus does not - but a phone should.
			OpenPauseMenu();
		}

		/// <summary>Quit from the pause menu: the whole app closes, the main menu included.</summary>
		private void QuitApp()
		{
			BetaLog.Summary();
			Intent intent = new Intent(this, typeof(MenuActivity));
			intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
			intent.PutExtra(MenuActivity.QuitExtra, true);
			StartActivity(intent);
			Quit();
		}

		/// <summary>Ends the session and its process; the menu, in its own process, is shown again.</summary>
		private void Quit()
		{
			BetaLog.Summary();
			FinishAndRemoveTask();
			Process.KillProcess(Process.MyPid());
		}

		private global::Android.Hardware.Input.InputManager inputDevices;
		private DeviceListener deviceListener;

		/*
		 * When an input device goes - a controller dropping off a hub, a keyboard unplugged -
		 * whatever it was holding is released: otherwise a stick deflected at that moment keeps
		 * turning the camera, and a held notch key keeps notching.
		 */
		private sealed class DeviceListener : Java.Lang.Object, global::Android.Hardware.Input.InputManager.IInputDeviceListener
		{
			private readonly GameActivity activity;

			public DeviceListener(GameActivity activity)
			{
				this.activity = activity;
			}

			public void OnInputDeviceAdded(int deviceId)
			{
				// By kind and vendor/product number, not its name: a paired device's name can be personal.
				InputDevice device = InputDevice.GetDevice(deviceId);
				global::Android.Util.Log.Info("OpenBVE", "input device added: " + (device == null ? "?" :
					((device.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad ? "game controller" : device.KeyboardType == InputKeyboardType.Alphabetic ? "keyboard" : "other") +
					", vendor " + device.VendorId.ToString("x4") + ", product " + device.ProductId.ToString("x4")));
			}

			public void OnInputDeviceChanged(int deviceId)
			{
			}

			public void OnInputDeviceRemoved(int deviceId)
			{
				global::Android.Util.Log.Info("OpenBVE", "input device " + deviceId + " removed: releasing held keys and sticks");
				activity.keyboard?.ReleaseAll(activity.view.Controls);
			}
		}

		/*
		 * Entering or leaving a desktop mode, or the window moving to another display, arrives
		 * here rather than recreating the activity. Under Automatic the interface follows: touch
		 * controls and the timetable card, or upstream's HUD with keyboard and controller input.
		 */
		public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
		{
			base.OnConfigurationChanged(newConfig);
			CheckInterface("configuration changed");
			// Samsung's reports settle a moment after a move between displays: look again then.
			view.RemoveCallbacks(recheckInterface);
			view.PostDelayed(recheckInterface, 1500);
		}

		private void RecheckInterface() => CheckInterface("re-check");

		private Action recheckInterface;

		/// <summary>Brings the interface into line with where the game now is (see OnConfigurationChanged).</summary>
		private void CheckInterface(string reason)
		{
			view.DisplayDensity = Resources?.DisplayMetrics?.Density ?? view.DisplayDensity;
			if (layout != null && Math.Abs(view.DisplayDensity - builtDensity) > 0.01f)
			{
				RebuildForDensity(view.DisplayDensity);
			}

			bool desktop = AndroidSettings.UseDesktopInterface(this);
			global::Android.Util.Log.Info("OpenBVE", reason + ": " + AndroidSettings.DesktopModeDetail(this) +
			                                         (desktop != desktopInterface ? " - switching to the " + (desktop ? "desktop" : "touch") + " interface" : string.Empty));
			if (desktop != desktopInterface)
			{
				desktopInterface = desktop;
				BetaLog.Line("game mode: switched to " + (desktop ? "Desktop" : "Phone (touch)") + " (" + reason + ")");
				view.SetInterface(desktop);
				if (started)
				{
					overlay.Visibility = desktop ? ViewStates.Gone : ViewStates.Visible;
				}
			}
		}

		/// <summary>A display added or removed (a monitor plugged or unplugged): the window may have moved.</summary>
		private sealed class DisplayWatcher : Java.Lang.Object, global::Android.Hardware.Display.DisplayManager.IDisplayListener
		{
			private readonly GameActivity activity;

			public DisplayWatcher(GameActivity activity)
			{
				this.activity = activity;
			}

			public void OnDisplayAdded(int displayId) => Recheck();

			public void OnDisplayRemoved(int displayId) => Recheck();

			public void OnDisplayChanged(int displayId)
			{
			}

			private void Recheck()
			{
				activity.view.RemoveCallbacks(activity.recheckInterface);
				activity.view.PostDelayed(activity.recheckInterface, 1000);
			}
		}

		protected override void OnDestroy()
		{
			if (deviceListener != null)
			{
				inputDevices?.UnregisterInputDeviceListener(deviceListener);
			}

			base.OnDestroy();
			if (IsFinishing)
			{
				// However the game ended, its process goes with it, so the next game starts clean.
				Process.KillProcess(Process.MyPid());
			}
		}

		private void OnStarted(string message)
		{
			started = true;
			loading.Visibility = ViewStates.Gone;
			overlay.Visibility = desktopInterface ? ViewStates.Gone : ViewStates.Visible;
			overlay.SetSingleHandle(view.Session?.Train.Handles.HandleType == global::TrainManager.Handles.HandleType.SingleHandle);
			status.Text = message;
			status.Visibility = ViewStates.Visible;
			// The startup summary is for the first look; after that the cab view needs the space.
			status.PostDelayed(() => status.Visibility = ViewStates.Gone, 10000);
			status.PostDelayed(WatchForStall, 2000);
		}

		/// <summary>
		/// Watches the render thread from the UI thread. A frozen simulation with working buttons
		/// looks like nothing at all from the inside, so say what it is and where it stopped.
		/// </summary>
		private void WatchForStall()
		{
			if (!started || IsFinishing)
			{
				return;
			}

			TimeSpan stalled = view.Stalled;
			if (stalled > TimeSpan.Zero)
			{
				string text = "The simulation has stopped responding (" + stalled.TotalSeconds.ToString("0") + " s in \"" + view.LastPhase + "\").";
				if (!stallReported)
				{
					stallReported = true;
					global::Android.Util.Log.Error("OpenBVE", text);
				}

				status.Text = text;
				status.Visibility = ViewStates.Visible;
			}
			else if (stallReported)
			{
				stallReported = false;
				status.Visibility = ViewStates.Gone;
			}

			status.PostDelayed(WatchForStall, 2000);
		}

		private bool stallReported;

		private void OnFailed(string message)
		{
			BetaLog.Event("ERROR", (started ? "the game stopped: " : "loading failed: ") + message, true);
			if (!started)
			{
				loading.ShowError(message);
				return;
			}

			/*
			 * The simulation stopped part-way through a run. Before, this went to the (hidden)
			 * loading screen, so the view simply froze with the buttons still answering.
			 */
			overlay.Visibility = ViewStates.Gone;
			new AlertDialog.Builder(this)
				.SetTitle(Menu.T("errors", "critical", "The simulation has stopped"))
				.SetMessage(message + "\n\n" + GameView.CrashLogHint)
				.SetCancelable(false)
				.SetPositiveButton(Menu.T("menu", "exit", "Exit to main menu"), (s, e) => Quit())
				.Show();
		}

		/// <summary>Full screen: the status and navigation bars come back with a swipe from the edge.</summary>
		private void HideSystemBars()
		{
			if (Build.VERSION.SdkInt >= BuildVersionCodes.R && Window?.InsetsController != null)
			{
				Window.InsetsController.Hide(WindowInsets.Type.SystemBars());
				Window.InsetsController.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
			}
			else if (Window?.DecorView != null)
			{
#pragma warning disable CA1422
				Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen |
				                                                            SystemUiFlags.HideNavigation | SystemUiFlags.LayoutStable |
				                                                            SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation);
#pragma warning restore CA1422
			}
		}
	}

	/// <summary>
	/// The loading screen: the route's image, its name, and a progress bar fed by the plugins, as
	/// upstream's loading screen shows.
	/// </summary>
	public class LoadingScreen : FrameLayout
	{
		private readonly ProgressBar bar;
		private readonly TextView label;
		private bool failed;

		public LoadingScreen(Context context, LaunchParameters launch) : base(context)
		{
			SetBackgroundColor(Color.Rgb(12, 16, 24));

			ImageView image = new ImageView(context);
			image.SetScaleType(ImageView.ScaleType.CenterCrop);
			image.Alpha = 0.55f;
			Bitmap picture = RoutePreviewImage(launch.ImageFile ?? RouteInfo.FindImageBesideRoute(launch.RouteFile));
			if (picture != null)
			{
				image.SetImageBitmap(picture);
			}

			AddView(image, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

			LinearLayout column = new LinearLayout(context) { Orientation = Orientation.Vertical };
			int pad = (int)(24 * Resources.DisplayMetrics.Density);
			column.SetPadding(pad, pad, pad, pad);

			TextView title = new TextView(context)
			{
				Text = string.IsNullOrEmpty(launch.RouteFile) ? "OpenBVE" : System.IO.Path.GetFileNameWithoutExtension(launch.RouteFile),
				TextSize = 22.0f
			};
			title.SetTextColor(Color.White);
			title.SetShadowLayer(6.0f, 0.0f, 0.0f, Color.Black);
			column.AddView(title);

			label = new TextView(context) { Text = Menu.T("loading", "loading_route", "Loading route..."), TextSize = 14.0f };
			label.SetTextColor(Color.White);
			label.SetShadowLayer(6.0f, 0.0f, 0.0f, Color.Black);
			column.AddView(label);

			bar = new ProgressBar(context, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
			column.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			AddView(column, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
			Poll();
		}

		/// <summary>Replaces the progress bar with an error; back returns to the menu.</summary>
		public void ShowError(string message)
		{
			failed = true;
			bar.Visibility = ViewStates.Gone;
			label.Text = message;
			label.SetTextColor(Color.OrangeRed);
		}

		private void Poll()
		{
			if (failed || Visibility != ViewStates.Visible)
			{
				return;
			}

			double value = LoadProgress.Value;
			bar.Progress = (int)(value * 1000.0);
			label.Text = LoadProgress.Train != null
				? Menu.T("loading", "loading_train", "Loading train...")
				: Menu.T("loading", "loading_route", "Loading route...") + " " + (value / 0.8 * 100.0).ToString("0") + "%";
			PostDelayed(Poll, 200);
		}

		/// <summary>Decodes the route's picture, if there is one.</summary>
		private static Bitmap RoutePreviewImage(string path)
		{
			if (path == null)
			{
				return null;
			}

			try
			{
				return BitmapFactory.DecodeFile(path);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
