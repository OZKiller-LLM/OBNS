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
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace OpenBve.Android
{
	/// <summary>
	/// The in-game pause menu, with the items of upstream's (Menu.SingleMenu, MenuType.Top):
	/// Resume, Jump to station, Exit to main menu, Customise controls and Quit, each leading to
	/// the same sub-menus and questions. The simulation stands still while it is open.
	/// </summary>
	/// <remarks>
	/// Native views over the scene rather than upstream's GL-drawn menu, for touch and for
	/// Android's text rendering of every script the translations use. Customising controls
	/// on desktop reassigns keys; on a touch screen it shows what each on-screen control does.
	/// </remarks>
	public class PauseMenu : FrameLayout
	{
		private readonly LinearLayout list;
		private readonly TextView title;
		private readonly ScrollView scroll;

		/// <summary>The session's stations to jump to; set when the menu opens.</summary>
		public Func<IReadOnlyList<(int Index, string Name)>> Stations { get; set; }

		/// <summary>The station the player last stopped at, to preselect the next one; set when the menu opens.</summary>
		public Func<int> LastStation { get; set; }

		/// <summary>Whether the touch buttons are hidden, or null when there are none (the desktop interface).</summary>
		public Func<bool?> ButtonsHidden { get; set; }

		/// <summary>Raised with the new state when the player hides or shows the touch buttons.</summary>
		public event Action<bool> ButtonsHiddenChanged;

		public event Action Resumed;
		public event Action<int> JumpRequested;
		public event Action ExitRequested;
		public event Action QuitRequested;

		/// <summary>Raised when the player leaves the Controls page, which may have changed the bindings.</summary>
		public event Action ControlsClosed;

		private readonly LinearLayout panel;
		private ControlsPage controls;

		public PauseMenu(Context context) : base(context)
		{
			SetBackgroundColor(Color.Argb(170, 0, 0, 0));
			Clickable = true; // the scene behind takes no touches while paused
			// ...but the backdrop is not a control: focus goes to the menu's items.
			Focusable = false;
			FocusableInTouchMode = false;

			panel = new LinearLayout(context) { Orientation = Orientation.Vertical };
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(235, 22, 28, 40));
			background.SetCornerRadius(Dp(14));
			background.SetStroke(Dp(1), Color.Argb(120, 255, 255, 255));
			panel.Background = background;
			panel.SetPadding(Dp(18), Dp(14), Dp(18), Dp(14));

			title = new TextView(context) { TextSize = 20.0f, Gravity = GravityFlags.Center };
			title.SetTextColor(Color.White);
			panel.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(8) });

			scroll = new ScrollView(context);
			list = new LinearLayout(context) { Orientation = Orientation.Vertical };
			scroll.AddView(list);
			panel.AddView(scroll);

			AddView(panel, new LayoutParams(Dp(420), ViewGroup.LayoutParams.WrapContent, GravityFlags.Center) { TopMargin = Dp(16), BottomMargin = Dp(16) });
		}

		/// <summary>Opens at the top level.</summary>
		/// <param name="fromKeys">
		/// Whether a keyboard or controller opened it: the first item then takes focus at once
		/// (which also leaves touch mode - Start and Escape, unlike the D-pad, do not).
		/// </param>
		public void Open(bool fromKeys = false)
		{
			ShowTop();
			Visibility = ViewStates.Visible;
			if (fromKeys)
			{
				Post(() => firstItem?.RequestFocusFromTouch());
			}
		}

		/// <summary>Goes back one level; from the top level, resumes.</summary>
		public void Back()
		{
			if (controls != null)
			{
				CloseControls();
			}
			else if (level == Level.Top)
			{
				Resume();
			}
			else
			{
				ShowTop();
			}
		}

		private enum Level
		{
			Top,
			Sub
		}

		private Level level;

		private void Resume()
		{
			Visibility = ViewStates.Gone;
			Resumed?.Invoke();
		}

		private void ShowTop()
		{
			level = Level.Top;
			Begin(Menu.T("menu", "title", "Pause"));
			Item(Menu.T("menu", "resume", "Resume simulation"), Resume);
			if (Stations?.Invoke().Count > 0)
			{
				Item(Menu.T("menu", "jump", "Jump to station"), ShowStations);
			}

			Item(Menu.T("menu", "exit", "Exit to main menu"), () => Question(
				Menu.T("menu", "exit_question", "Do you really want to exit to the main menu?"),
				Menu.T("menu", "exit_no", "No"),
				Menu.T("menu", "exit_yes", "Yes"),
				() => ExitRequested?.Invoke()));
			Item(Menu.T("menu", "customize_controls", "Customize controls"), ShowControls);
			bool? hidden = ButtonsHidden?.Invoke();
			if (hidden.HasValue)
			{
				Item(hidden.Value ? Menu.T("android", "show_buttons", "Show touch buttons") : Menu.T("android", "hide_buttons", "Hide touch buttons"), () =>
				{
					ButtonsHiddenChanged?.Invoke(!hidden.Value);
					Resume();
				});
			}

			Item(Menu.T("menu", "quit", "Quit"), () => Question(
				Menu.T("menu", "quit_question", "Do you really want to quit?"),
				Menu.T("menu", "quit_no", "No"),
				Menu.T("menu", "quit_yes", "Yes"),
				() => QuitRequested?.Invoke()));
		}

		private void ShowStations()
		{
			level = Level.Sub;
			Begin(Menu.T("menu", "jump", "Jump to station"));
			Item(Menu.T("menu", "back", "Back"), ShowTop);
			int last = LastStation?.Invoke() ?? -1;
			Button preselected = null;
			foreach ((int index, string name) in Stations?.Invoke() ?? Array.Empty<(int, string)>())
			{
				int station = index;
				Button button = Item(string.IsNullOrEmpty(name) ? "#" + index : name, () =>
				{
					Visibility = ViewStates.Gone;
					JumpRequested?.Invoke(station);
					Resumed?.Invoke();
				});
				// As upstream: the first station after the one last stopped at is the suggested choice.
				if (preselected == null && index > last)
				{
					preselected = button;
					Highlight(button);
				}
			}

			if (preselected != null)
			{
				scroll.Post(() => scroll.SmoothScrollTo(0, Math.Max(0, preselected.Top - Dp(60))));
			}
		}

		private void Question(string question, string no, string yes, Action confirmed)
		{
			level = Level.Sub;
			Begin(question);
			Item(no, ShowTop);
			Item(yes, confirmed);
		}

		/// <summary>
		/// Upstream lists every command with its key; the touch controls are fixed, so this lists
		/// what each does and where it is instead.
		/// </summary>
		/*
		 * The main menu's Controls page, over the whole screen rather than in the narrow menu
		 * panel: every command with its keys and controller buttons, to change mid-game. Back
		 * (its button, the Back key, Escape or B) returns to the menu, and the game re-reads the
		 * bindings then (ControlsClosed).
		 */
		private void ShowControls()
		{
			panel.Visibility = ViewStates.Gone;
			controls = new ControlsPage((global::Android.App.Activity)Context, CloseControls);
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(245, 18, 22, 32));
			background.SetCornerRadius(Dp(14));
			controls.Background = background;
			AddView(controls, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
			{
				LeftMargin = Dp(24),
				RightMargin = Dp(24),
				TopMargin = Dp(12),
				BottomMargin = Dp(12)
			});
			if (!IsInTouchMode)
			{
				Post(() => controls?.FocusSearch(FocusSearchDirection.Forward)?.RequestFocus());
			}
		}

		private void CloseControls()
		{
			if (controls == null)
			{
				return;
			}

			RemoveView(controls);
			controls = null;
			panel.Visibility = ViewStates.Visible;
			ShowTop();
			ControlsClosed?.Invoke();
		}

		private Button firstItem, focusTarget;

		private void Begin(string heading)
		{
			title.Text = heading;
			list.RemoveAllViews();
			scroll.ScrollTo(0, 0);
			firstItem = focusTarget = null;
			// Once the page is built: a controller or keyboard user starts on its first item (or
			// the suggested one), so the D-pad works at once. Touch leaves focus alone.
			Post(() =>
			{
				if (!IsInTouchMode)
				{
					(focusTarget ?? firstItem)?.RequestFocus();
				}
			});
		}

		private Button Item(string text, Action action)
		{
			Button button = new Button(Context) { Text = text, TextSize = 15.0f };
			button.SetAllCaps(false);
			button.SetTextColor(Color.White);
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(200, 50, 60, 80));
			background.SetCornerRadius(Dp(8));
			button.Background = background;
			button.StateListAnimator = null;
			button.Click += (s, e) => action();
			firstItem ??= button;
			list.AddView(button, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(48)) { TopMargin = Dp(4), BottomMargin = Dp(4) });
			return button;
		}

		private void Highlight(Button button)
		{
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(220, 40, 100, 190));
			background.SetCornerRadius(Dp(8));
			button.Background = background;
			focusTarget = button;
		}

		private int Dp(float value)
		{
			return (int)(value * (Context.Resources?.DisplayMetrics?.Density ?? 2.0f) + 0.5f);
		}
	}
}
