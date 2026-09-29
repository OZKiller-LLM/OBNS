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
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using OpenBveApi.Interface;
using MessageColor = OpenBveApi.Colors.MessageColor;

namespace OpenBve.Android
{
	/// <summary>
	/// The driving controls drawn over the cab view: power and brake levers on the right, reverser,
	/// horn and doors on the left, the emergency brake, and a line of driving information.
	/// </summary>
	/// <remarks>
	/// Plain Android views rather than GL: they get the platform's touch handling, multi-touch and
	/// accessibility for free, and cost the renderer nothing. Every button reports both press and
	/// release, because upstream's controls care about both (a held power button keeps notching,
	/// the music horn plays while held, door buttons latch until released).
	/// </remarks>
	public class ControlOverlay : FrameLayout
	{
		private readonly AndroidControls controls;
		private readonly TextView info;
		private readonly float density;
		private readonly float scale, opacity;
		private readonly Button pauseButton;
		private bool singleHandle, buttonsHidden;
		private LinearLayout atsKeys;
		private readonly LinearLayout levers;
		private readonly LinearLayout unifiedLever;
		private readonly LinearLayout numpad;
		private readonly TimetablePanel timetable;
		private readonly DriverInfoBar hud;
		private string lastCameraMode;
		private long cameraModeShownUntil;
		private readonly LinearLayout ats;
		private readonly Button aiButton;
		private bool aiActive;
		private static readonly Color AiIdle = Color.Argb(160, 50, 70, 90);
		private static readonly Color AiDriving = Color.Argb(210, 40, 150, 90);

		/// <summary>Raised on the UI thread when the pause button is pressed.</summary>
		public event System.Action PauseRequested;

		public ControlOverlay(Context context, AndroidControls controls, System.Func<TimetableSnapshot> timetableSource,
			System.Func<(RouteGeometry, RouteLive)> routeSource) : base(context)
		{
			this.controls = controls;
			/*
			 * The player's size choice scales every measurement (all go through Dp, the card's
			 * margins included, so the layout keeps its shape) and the button text; the opacity
			 * choice fades the buttons over the view.
			 */
			float baseDensity = context.Resources?.DisplayMetrics?.Density ?? 2.0f;
			scale = System.Math.Min(AndroidSettings.GetTouchButtonScale(context), FittingScale(context, baseDensity));
			opacity = AndroidSettings.GetTouchButtonOpacity(context);
			density = baseDensity * scale;

			/*
			 * The in-game messages (station arrival, departure countdown, signal warnings), each in
			 * its route-given colour, on a dark card under the top row; hidden when there are none.
			 * The driving information itself is in the bar at the bottom (DriverInfoBar).
			 */
			info = new TextView(context) { TextSize = 14.0f * scale, Visibility = ViewStates.Gone };
			info.SetTextColor(Color.White);
			info.SetLineSpacing(Dp(2), 1.0f);
			info.SetPadding(Dp(14), Dp(7), Dp(14), Dp(7));
			GradientDrawable messagesBackground = new GradientDrawable();
			messagesBackground.SetColor(Color.Argb(170, 14, 18, 27));
			messagesBackground.SetCornerRadius(Dp(12));
			info.Background = messagesBackground;
			AddView(info, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Center) { TopMargin = Dp(52) });

			// Top left: pause, then the camera: view mode, cab, previous/next car or point of interest, reset.
			Color cameraColor = Color.Argb(150, 40, 40, 60);
			LinearLayout camera = Row();
			pauseButton = Action("‖", Color.Argb(190, 70, 70, 90), () => PauseRequested?.Invoke());
			// Never faint: it is the way back to every setting, and the only button left when hidden.
			pauseButton.Alpha = System.Math.Max(opacity, 0.75f);
			camera.AddView(pauseButton, Spaced());
			camera.AddView(Action("VIEW", cameraColor, () => controls.Post(s => s.Camera.CycleView())), Spaced());
			camera.AddView(Button("CAB", Translations.Command.CameraInterior, cameraColor, small: true), Spaced());
			camera.AddView(Button("◀", Translations.Command.CameraPOIPrevious, cameraColor, small: true), Spaced());
			camera.AddView(Button("▶", Translations.Command.CameraPOINext, cameraColor, small: true), Spaced());
			camera.AddView(Button("RESET", Translations.Command.CameraReset, cameraColor, small: true), Spaced());
			camera.AddView(Action("CAM", cameraColor, ToggleNumpad), Spaced());
			/*
			 * The in-game displays: the route card's timetable, and its map and gradient. The
			 * desktop HUD's items have no button: the touch interface leaves the HUD off and shows
			 * its driving information in the line below this row.
			 */
			Color displayColor = Color.Argb(150, 30, 60, 60);
			camera.AddView(Action("TT", displayColor, ToggleTimetable), Spaced());
			camera.AddView(Action("MAP", displayColor, () => timetable.Toggle(TimetablePanel.Tab.Map)), Spaced());
			// A little smaller than the other buttons, so the row stays clear of the emergency brake.
			for (int i = 0; i < camera.ChildCount; i++)
			{
				if (camera.GetChildAt(i) is Button top)
				{
					top.SetMinWidth(Dp(46));
					top.SetMinimumWidth(Dp(46));
					top.SetMinHeight(Dp(36));
					top.SetMinimumHeight(Dp(36));
					top.SetPadding(Dp(4), 0, Dp(4), 0);
					top.TextSize = 10.0f * scale;
				}
			}

			AddView(camera, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Left | GravityFlags.Top)
			{
				LeftMargin = Dp(8),
				TopMargin = Dp(8)
			});

			/*
			 * The camera numpad, opened by CAM: the desktop's keypad camera keys in the keypad's own
			 * layout, so a player who knows them finds each where their fingers expect it. It sits
			 * bottom right, beside whichever lever set the train uses (see PlaceNumpad).
			 */
			numpad = Numpad(cameraColor);
			numpad.Visibility = ViewStates.Gone;
			AddView(numpad, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Right | GravityFlags.Bottom)
			{
				RightMargin = Dp(196),
				BottomMargin = Dp(16)
			});

			// Right: the two levers, each a column of notch-up over notch-down.
			levers = Row();
			levers.AddView(Column(
				Button("POWER\n▲", Translations.Command.PowerIncrease, Color.Argb(170, 30, 90, 200)),
				Button("POWER\n▼", Translations.Command.PowerDecrease, Color.Argb(170, 30, 60, 130))));
			levers.AddView(Column(
				Button("BRAKE\n▲", Translations.Command.BrakeIncrease, Color.Argb(170, 200, 110, 20)),
				Button("BRAKE\n▼", Translations.Command.BrakeDecrease, Color.Argb(170, 130, 75, 20))));
			AddView(levers, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Right | GravityFlags.Bottom)
			{
				RightMargin = Dp(12),
				BottomMargin = Dp(12)
			});

			// A single-handle train's one lever: towards power, to neutral, towards brake.
			unifiedLever = Column(
				Button("POWER\n▲", Translations.Command.SinglePower, Color.Argb(170, 30, 90, 200)),
				Button("N", Translations.Command.SingleNeutral, Color.Argb(170, 70, 70, 70)),
				Button("BRAKE\n▼", Translations.Command.SingleBrake, Color.Argb(170, 200, 110, 20)));
			unifiedLever.Visibility = ViewStates.Gone;
			levers.LayoutChange += (sender, e) => PlaceNumpad();
			unifiedLever.LayoutChange += (sender, e) => PlaceNumpad();
			AddView(unifiedLever, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Right | GravityFlags.Bottom)
			{
				RightMargin = Dp(12),
				BottomMargin = Dp(12)
			});

			// Emergency brake: separate and red, above the levers, so it is found without looking.
			AddView(Button("EMERGENCY", Translations.Command.BrakeEmergency, Color.Argb(200, 190, 20, 20), wide: true),
				new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Right | GravityFlags.Top)
				{
					RightMargin = Dp(12),
					TopMargin = Dp(12)
				});

			/*
			 * The safety system's keys. Without them a plugin's alarm cannot be acknowledged: the
			 * default ATS-Sx rings at a signal, and five seconds later applies the emergency brake
			 * and holds it, which from the cab looks like the train stopping for no reason. S
			 * acknowledges, A1 silences the chime and B1 resets after an emergency application;
			 * the rest are there for trains whose own plugin uses them.
			 */
			atsKeys = Row();
			atsKeys.Visibility = ViewStates.Gone;
			Translations.Command[] keys =
			{
				Translations.Command.SecurityA2, Translations.Command.SecurityB2, Translations.Command.SecurityC1,
				Translations.Command.SecurityC2, Translations.Command.SecurityD, Translations.Command.SecurityE,
				Translations.Command.SecurityF, Translations.Command.SecurityG
			};
			foreach (Translations.Command key in keys)
			{
				string label = key.ToString().Substring("Security".Length);
				atsKeys.AddView(Button(label, key, Color.Argb(160, 80, 60, 30), small: true), Spaced());
			}

			ats = Row();
			ats.AddView(Button("S", Translations.Command.SecurityS, Color.Argb(190, 150, 100, 20), small: true), Spaced());
			ats.AddView(Button("A1", Translations.Command.SecurityA1, Color.Argb(160, 80, 60, 30), small: true), Spaced());
			ats.AddView(Button("B1", Translations.Command.SecurityB1, Color.Argb(160, 80, 60, 30), small: true), Spaced());
			ats.AddView(atsKeys);
			ats.AddView(Action("ATS…", Color.Argb(160, 80, 60, 30),
				() => atsKeys.Visibility = atsKeys.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone), Spaced());
			AddView(ats, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Left | GravityFlags.Bottom)
			{
				LeftMargin = Dp(12),
				BottomMargin = Dp(160)
			});

			/*
			 * Above the safety keys: MISC_AI, upstream's simple human driver taking the player's
			 * train (or handing it back). It lights while the AI drives; see SetAiActive.
			 */
			aiButton = Action("AI", AiIdle, () => Tap(Translations.Command.MiscAI));
			AddView(aiButton, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Left | GravityFlags.Bottom)
			{
				LeftMargin = Dp(15),
				BottomMargin = Dp(160 + 40 + 8)
			});

			/*
			 * The timetable card, opened by TT: top left under the buttons, down to the reverser,
			 * horn and door buttons (the safety system's keys move aside while it is open), or
			 * expanded over those too.
			 */
			timetable = new TimetablePanel(context, timetableSource, routeSource)
			{
				Visibility = ViewStates.Gone,
				CollapsedBottomMargin = Dp(158),
				ExpandedBottomMargin = Dp(12)
			};
			int screenWidth = context.Resources?.DisplayMetrics?.WidthPixels ?? Dp(800);
			AddView(timetable, new LayoutParams(System.Math.Min(Dp(380), (int)(screenWidth * 0.45f)), ViewGroup.LayoutParams.WrapContent,
				GravityFlags.Left | GravityFlags.Top)
			{
				LeftMargin = Dp(8),
				TopMargin = Dp(58),
				BottomMargin = Dp(158)
			});
			timetable.Toggled += PlaceInfo;

			// Left: reverser, horn and doors.
			LinearLayout left = Row();
			left.AddView(Column(
				Button("REV\nFWD", Translations.Command.ReverserForward, Color.Argb(160, 60, 60, 60)),
				Button("REV\nBACK", Translations.Command.ReverserBackward, Color.Argb(160, 60, 60, 60))));
			left.AddView(Column(
				Button("HORN", Translations.Command.HornPrimary, Color.Argb(160, 60, 60, 60)),
				Button("HORN 2", Translations.Command.HornSecondary, Color.Argb(160, 60, 60, 60))));
			left.AddView(Column(
				Button("DOORS\nLEFT", Translations.Command.DoorsLeft, Color.Argb(160, 30, 120, 60)),
				Button("DOORS\nRIGHT", Translations.Command.DoorsRight, Color.Argb(160, 30, 120, 60))));
			AddView(left, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Left | GravityFlags.Bottom)
			{
				LeftMargin = Dp(12),
				BottomMargin = Dp(12)
			});

			// Bottom, between the buttons: the driving information (see PlaceHud).
			hud = new DriverInfoBar(context, density, scale);
			AddView(hud, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.Left)
			{
				BottomMargin = Dp(12)
			});
			ViewTreeObserver.GlobalLayout += (sender, e) => PlaceHud();

			SetButtonsHidden(AndroidSettings.GetTouchButtonsHidden(context));
		}

		/// <summary>
		/// Shows the lever set for the train's handles: separate power and brake levers, or one
		/// combined lever for a single-handle train. Call on the UI thread once the train is loaded.
		/// </summary>
		public void SetSingleHandle(bool singleHandle)
		{
			this.singleHandle = singleHandle;
			ShowLevers();
		}

		private void ShowLevers()
		{
			levers.Visibility = buttonsHidden || singleHandle ? ViewStates.Gone : ViewStates.Visible;
			unifiedLever.Visibility = !buttonsHidden && singleHandle ? ViewStates.Visible : ViewStates.Gone;
		}

		/// <summary>Whether the buttons are hidden (see SetButtonsHidden).</summary>
		public bool ButtonsHidden => buttonsHidden;

		/*
		 * Hidden, only the pause button stays, with the driving information and the card if it
		 * is open: a clear view for driving from the cab's own touch areas, a keyboard or a
		 * controller, or for pictures. The pause menu shows them again.
		 */
		/// <summary>Hides every button but pause, or shows them again. Call on the UI thread.</summary>
		public void SetButtonsHidden(bool hidden)
		{
			buttonsHidden = hidden;
			ViewStates shown = hidden ? ViewStates.Gone : ViewStates.Visible;
			ViewGroup cameraRow = (ViewGroup)pauseButton.Parent;
			for (int i = 0; i < ChildCount; i++)
			{
				View child = GetChildAt(i);
				if (child == info || child == timetable || child == levers || child == unifiedLever || child == hud)
				{
					continue;
				}

				if (child == cameraRow)
				{
					for (int j = 0; j < cameraRow.ChildCount; j++)
					{
						if (cameraRow.GetChildAt(j) != pauseButton)
						{
							cameraRow.GetChildAt(j).Visibility = shown;
						}
					}
				}
				else if (child == numpad)
				{
					// Opened by CAM only.
					if (hidden)
					{
						numpad.Visibility = ViewStates.Gone;
					}
				}
				else
				{
					child.Visibility = shown;
				}
			}

			ShowLevers();
		}

		/*
		 * The card covers the left of the view, so the driving information moves right of it
		 * while it is open, and back to the middle when it closes.
		 */
		/// <summary>Opens or closes the timetable card. Call on the UI thread.</summary>
		public void ToggleTimetable() => timetable.Toggle(TimetablePanel.Tab.Timetable);

		private void PlaceInfo()
		{
			bool open = timetable.Visibility == ViewStates.Visible;
			if (info.LayoutParameters is LayoutParams parameters)
			{
				int panelRight = ((LayoutParams)timetable.LayoutParameters).LeftMargin + timetable.LayoutParameters.Width;
				parameters.Gravity = GravityFlags.Top | (open ? GravityFlags.Left : GravityFlags.Center);
				parameters.LeftMargin = open ? panelRight + Dp(8) : 0;
				info.LayoutParameters = parameters;
				info.SetMaxWidth(open ? Width - panelRight - Dp(16) : int.MaxValue);
			}

			if (ats.LayoutParameters is LayoutParams keys)
			{
				int panelRight = ((LayoutParams)timetable.LayoutParameters).LeftMargin + timetable.LayoutParameters.Width;
				keys.LeftMargin = open ? panelRight + Dp(8) : Dp(12);
				ats.LayoutParameters = keys;
				if (aiButton.LayoutParameters is LayoutParams ai)
				{
					ai.LeftMargin = keys.LeftMargin + Dp(3);
					aiButton.LayoutParameters = ai;
				}
			}
		}

		/// <summary>Shows whether the AI is driving the player's train. Call on the UI thread.</summary>
		public void SetAiActive(bool active)
		{
			if (active == aiActive)
			{
				return;
			}

			aiActive = active;
			((GradientDrawable)aiButton.Background).SetColor(active ? AiDriving : AiIdle);
			aiButton.Text = active ? "AI ●" : "AI";
		}

		private void ToggleNumpad()
		{
			numpad.Visibility = numpad.Visibility == ViewStates.Gone ? ViewStates.Visible : ViewStates.Gone;
		}

		/*
		 * Keeps the numpad just left of the lever set on show: two columns for separate power
		 * and brake levers, one for a single handle. Runs whenever either set is laid out, so it
		 * follows the switch made once the train is known.
		 */
		private void PlaceNumpad()
		{
			LinearLayout shown = unifiedLever.Visibility == ViewStates.Visible ? unifiedLever : levers;
			if (shown.Width == 0 || !(numpad.LayoutParameters is LayoutParams parameters))
			{
				return;
			}

			int margin = Dp(12) + shown.Width + Dp(8);
			if (parameters.RightMargin != margin)
			{
				parameters.RightMargin = margin;
				// Not during this layout pass: a layout change requested inside one is dropped.
				numpad.Post(() => numpad.LayoutParameters = parameters);
			}
		}

		/*
		 * Upstream's keypad camera keys (Data/Controls/Default.controls):
		 *   /  roll left     *  roll right
		 *   7  next POI      8  move up      9  move forward
		 *   4  move left     5  reset        6  move right
		 *   1  previous POI  2  move down    3  move backward
		 *   0  zoom out      .  zoom in
		 * Moves and zooms repeat while held, as the keys do.
		 */
		private LinearLayout Numpad(Color color)
		{
			(string Label, Translations.Command Command)[][] rows =
			{
				new[] { ("/\n↺", Translations.Command.CameraRotateCCW), ("*\n↻", Translations.Command.CameraRotateCW) },
				new[] { ("7\nPOI ▶", Translations.Command.CameraPOINext), ("8\nUP", Translations.Command.CameraMoveUp), ("9\nFWD", Translations.Command.CameraMoveForward) },
				new[] { ("4\nLEFT", Translations.Command.CameraMoveLeft), ("5\nRESET", Translations.Command.CameraReset), ("6\nRIGHT", Translations.Command.CameraMoveRight) },
				new[] { ("1\n◀ POI", Translations.Command.CameraPOIPrevious), ("2\nDOWN", Translations.Command.CameraMoveDown), ("3\nBACK", Translations.Command.CameraMoveBackward) },
				new[] { ("0\nZOOM −", Translations.Command.CameraZoomOut), (".\nZOOM +", Translations.Command.CameraZoomIn) }
			};

			LinearLayout pad = new LinearLayout(Context) { Orientation = Orientation.Vertical };
			foreach ((string Label, Translations.Command Command)[] row in rows)
			{
				LinearLayout line = Row();
				foreach ((string label, Translations.Command command) in row)
				{
					Button key = Button(label, command, color, small: true);
					key.TextSize = 10.0f * scale;
					// The keypad's 0 is two keys wide, as on the real thing.
					line.AddView(key, new LinearLayout.LayoutParams(Dp(label.StartsWith("0") ? 110 : 52), Dp(38)) { LeftMargin = Dp(3), RightMargin = Dp(3), TopMargin = Dp(3) });
				}

				pad.AddView(line);
			}

			return pad;
		}

		/// <summary>Shows the driving information and the in-game messages. Call on the UI thread.</summary>
		public void SetStatus(DriverStatus status)
		{
			hud.Show(status);
			// Its width is fixed once placed, so longer text alone would not lay it out again: refit it.
			PlaceHud();

			// The camera view's name, for two seconds after it changes (the first one included).
			long now = global::Android.OS.SystemClock.UptimeMillis();
			if (status.CameraMode != lastCameraMode)
			{
				lastCameraMode = status.CameraMode;
				cameraModeShownUntil = now + 2000;
			}

			SpannableStringBuilder text = new SpannableStringBuilder();
			foreach ((string message, MessageColor color) in status.Messages)
			{
				if (text.Length() != 0)
				{
					text.Append("\n");
				}

				int start = text.Length();
				text.Append(message);
				text.SetSpan(new ForegroundColorSpan(MessageTint(color)), start, text.Length(), SpanTypes.ExclusiveExclusive);
			}

			if (now < cameraModeShownUntil && !string.IsNullOrEmpty(status.CameraMode))
			{
				if (text.Length() != 0)
				{
					text.Append("\n");
				}

				int start = text.Length();
				text.Append(status.CameraMode);
				text.SetSpan(new ForegroundColorSpan(Color.Rgb(160, 170, 185)), start, text.Length(), SpanTypes.ExclusiveExclusive);
				text.SetSpan(new RelativeSizeSpan(0.85f), start, text.Length(), SpanTypes.ExclusiveExclusive);
			}

			info.Visibility = text.Length() != 0 ? ViewStates.Visible : ViewStates.Gone;
			info.TextFormatted = text;
		}

		/// <summary>A message colour as it reads on the dark card.</summary>
		private static Color MessageTint(MessageColor color) => color switch
		{
			MessageColor.Red => Color.Rgb(255, 105, 100),
			MessageColor.Orange => Color.Rgb(255, 175, 70),
			MessageColor.Green => Color.Rgb(120, 225, 130),
			MessageColor.Blue => Color.Rgb(120, 175, 255),
			MessageColor.Magenta => Color.Rgb(235, 130, 235),
			MessageColor.Gray => Color.Rgb(190, 195, 205),
			_ => Color.White
		};

		/*
		 * The bar sits in the gap along the bottom between whatever is there on the left and on the
		 * right: normally the door buttons and the levers, but also the camera numpad, the
		 * timetable card when expanded, or - with the buttons hidden - the screen's edges. Every
		 * visible child reaching into the bar's band counts, on the side its middle is. Centred in
		 * that gap, and no wider than it.
		 */
		private void PlaceHud()
		{
			if (Width == 0 || hud.Height == 0 || !(hud.LayoutParameters is LayoutParams parameters))
			{
				return;
			}

			int gap = Dp(8);
			int bandBottom = Height - parameters.BottomMargin, bandTop = bandBottom - hud.Height;
			int left = Dp(12), right = Width - Dp(12);
			for (int i = 0; i < ChildCount; i++)
			{
				View child = GetChildAt(i);
				if (child == hud || child == info || child.Visibility != ViewStates.Visible || child.Bottom <= bandTop || child.Top >= bandBottom)
				{
					continue;
				}

				if (child.Left + child.Right < Width)
				{
					left = System.Math.Max(left, child.Right + gap);
				}
				else
				{
					right = System.Math.Min(right, child.Left - gap);
				}
			}

			// Placed by hand: a FrameLayout centres a child with side margins off by half their difference.
			int available = System.Math.Max(0, right - left);
			int width = hud.FitTo(available);
			int leftMargin = left + (available - width) / 2;
			if (parameters.LeftMargin != leftMargin || parameters.Width != width)
			{
				parameters.LeftMargin = leftMargin;
				parameters.Width = width;
				// Not during this layout pass: a layout change requested inside one is dropped.
				hud.Post(() => hud.LayoutParameters = parameters);
			}
		}

		private Button Button(string label, Translations.Command command, Color color, bool wide = false, bool small = false)
		{
			Button button = Styled(label, color, wide, small);

			button.Touch += (sender, e) =>
			{
				switch (e.Event?.ActionMasked)
				{
					case MotionEventActions.Down:
						// Dimmed when pressed; a faded button brightens instead, to be seen.
						button.Alpha = opacity > 0.99f ? 0.6f : System.Math.Min(1.0f, opacity + 0.4f);
						controls.Press(command);
						break;
					case MotionEventActions.Up:
					case MotionEventActions.Cancel:
						button.Alpha = opacity;
						controls.Release(command);
						break;
				}

				e.Handled = true;
			};
			return button;
		}

		/// <summary>A button that runs an action once per tap, rather than sending a command.</summary>
		private Button Action(string label, Color color, System.Action action)
		{
			Button button = Styled(label, color, false, true);
			if (action != null)
			{
				button.Click += (sender, e) => action();
			}

			return button;
		}

		/// <summary>Sends a command as one key press, as a tap of its desktop key would.</summary>
		private void Tap(Translations.Command command)
		{
			controls.Press(command);
			controls.Release(command);
		}

		private Button Styled(string label, Color color, bool wide, bool small)
		{
			Button button = new Button(Context)
			{
				Text = label,
				TextSize = (small ? 11.0f : 12.0f) * scale,
				Focusable = false,
				Alpha = opacity
			};
			button.SetAllCaps(false);
			button.SetTextColor(Color.White);
			int width = wide ? 150 : small ? 52 : 78;
			int height = wide ? 52 : small ? 40 : 64;
			button.SetMinWidth(Dp(width));
			button.SetMinimumWidth(Dp(width));
			button.SetMinHeight(Dp(height));
			button.SetMinimumHeight(Dp(height));
			button.SetPadding(Dp(6), 0, Dp(6), 0);

			GradientDrawable background = new GradientDrawable();
			background.SetColor(color);
			background.SetCornerRadius(Dp(10));
			background.SetStroke(Dp(1), Color.Argb(140, 255, 255, 255));
			button.Background = background;
			button.StateListAnimator = null;
			return button;
		}

		private LinearLayout.LayoutParams Spaced()
		{
			LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
			parameters.SetMargins(Dp(3), 0, Dp(3), 0);
			return parameters;
		}

		// --- the scene: cab touch areas and camera gestures (SceneInput) ---

		private SceneInput scene;

		public override bool OnTouchEvent(MotionEvent e)
		{
			scene ??= new SceneInput(this, controls);
			return scene.OnTouch(e);
		}

		private LinearLayout Row()
		{
			return new LinearLayout(Context) { Orientation = Orientation.Horizontal };
		}

		private LinearLayout Column(params View[] children)
		{
			LinearLayout column = new LinearLayout(Context) { Orientation = Orientation.Vertical };
			foreach (View child in children)
			{
				LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
				parameters.SetMargins(Dp(4), Dp(4), Dp(4), Dp(4));
				column.AddView(child, parameters);
			}

			return column;
		}

		/*
		 * The largest scale the layout fits the screen at. At scale 1 the top row - nine buttons,
		 * then the emergency brake - needs about 680 dp across, and the left side - reverser, horn
		 * and door columns, the safety keys and the AI button, under the two or three lines of
		 * driving information - about 370 dp down. "Large" on a phone 411 dp tall (a Galaxy A73)
		 * so comes to 1.11 rather than 1.25, and a small screen shrinks even "Normal" to fit.
		 */
		private static float FittingScale(Context context, float baseDensity)
		{
			global::Android.Util.DisplayMetrics metrics = context.Resources?.DisplayMetrics;
			if (metrics == null || metrics.WidthPixels <= 0 || metrics.HeightPixels <= 0)
			{
				return 1.0f;
			}

			float across = System.Math.Max(metrics.WidthPixels, metrics.HeightPixels) / baseDensity;
			float down = System.Math.Min(metrics.WidthPixels, metrics.HeightPixels) / baseDensity;
			return System.Math.Max(0.7f, System.Math.Min(across / 680.0f, down / 370.0f));
		}

		private int Dp(float value)
		{
			return (int)(value * density + 0.5f);
		}
	}
}
