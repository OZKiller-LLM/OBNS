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

using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace OpenBve.Android
{
	/// <summary>The menu's look: a dark theme, and small factories for its recurring widgets.</summary>
	public static class Ui
	{
		public static readonly Color Background = Color.Rgb(18, 21, 28);
		public static readonly Color Surface = Color.Rgb(28, 32, 42);
		public static readonly Color SurfaceRaised = Color.Rgb(38, 44, 58);
		public static readonly Color Primary = Color.Rgb(232, 236, 244);
		public static readonly Color Secondary = Color.Rgb(150, 160, 178);
		public static readonly Color Accent = Color.Rgb(92, 160, 255);
		public static readonly Color Selection = Color.Argb(90, 92, 160, 255);
		public static readonly Color Danger = Color.Rgb(230, 90, 80);

		public static int Dp(Context context, float value) => (int)(value * (context.Resources?.DisplayMetrics?.Density ?? 2.0f) + 0.5f);

		/// <summary>A rounded panel.</summary>
		public static Drawable Panel(Context context, Color color, float radius = 10)
		{
			GradientDrawable drawable = new GradientDrawable();
			drawable.SetColor(color);
			drawable.SetCornerRadius(Dp(context, radius));
			return drawable;
		}

		public static TextView Heading(Context context, string text)
		{
			TextView view = new TextView(context) { Text = text, TextSize = 16.0f };
			view.SetTextColor(Primary);
			view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
			view.SetPadding(0, Dp(context, 6), 0, Dp(context, 4));
			return view;
		}

		public static TextView Label(Context context, string text, float size = 14.0f)
		{
			TextView view = new TextView(context) { Text = text, TextSize = size };
			view.SetTextColor(Secondary);
			return view;
		}

		public static TextView Body(Context context, string text)
		{
			TextView view = new TextView(context) { Text = text, TextSize = 14.0f };
			view.SetTextColor(Primary);
			view.SetTextIsSelectable(true);
			return view;
		}

		/// <summary>
		/// Outlines whichever view has focus, so a controller or keyboard user can see where they
		/// are. The pages' own backgrounds (selection, accent) stay as they are: the ring is drawn
		/// in the foreground, and only outside touch mode, so touch use never shows it.
		/// </summary>
		/// <remarks>
		/// A controller's D-pad moves focus, A selects (Android's key map turns it into
		/// DPAD_CENTER) and B goes back, so views that can take focus are all it takes.
		/// </remarks>
		public static void EnableFocusRing(global::Android.App.Activity activity)
		{
			View root = activity.Window?.DecorView;
			if (root == null)
			{
				return;
			}

			root.ViewTreeObserver.GlobalFocusChange += (sender, e) =>
			{
				if (e.OldFocus != null && e.OldFocus.Foreground is FocusRingDrawable)
				{
					e.OldFocus.Foreground = null;
				}

				// A list shows its chosen row with its selector: ring that row, not the whole list.
				if (e.NewFocus is AbsListView list)
				{
					if (!(list.Selector is FocusRingDrawable))
					{
						list.Selector = new FocusRingDrawable(activity);
						list.SetDrawSelectorOnTop(true);
					}

					return;
				}

				// Not on the game's own surface, and never in touch mode.
				if (e.NewFocus != null && !e.NewFocus.IsInTouchMode && !(e.NewFocus is SurfaceView))
				{
					e.NewFocus.Foreground = new FocusRingDrawable(activity);
				}
			};
		}

		private sealed class FocusRingDrawable : GradientDrawable
		{
			public FocusRingDrawable(Context context)
			{
				SetColor(global::Android.Graphics.Color.Argb(40, Accent.R, Accent.G, Accent.B));
				SetCornerRadius(Dp(context, 8));
				SetStroke(Dp(context, 3), global::Android.Graphics.Color.Argb(255, 140, 200, 255));
			}
		}

		/// <summary>A standard button; <paramref name="prominent"/> fills it with the accent colour.</summary>
		public static Button Button(Context context, string text, bool prominent = false)
		{
			Button button = new Button(context) { Text = text, TextSize = 14.0f };
			button.SetAllCaps(false);
			button.SetTextColor(prominent ? Color.White : Primary);
			button.Background = Panel(context, prominent ? Accent : SurfaceRaised, 8);
			button.StateListAnimator = null;
			int h = Dp(context, 16);
			int v = Dp(context, 8);
			button.SetPadding(h, v, h, v);
			return button;
		}

		public static Button SmallButton(Context context, string text)
		{
			Button button = Button(context, text);
			button.TextSize = 13.0f;
			button.SetMinWidth(Dp(context, 44));
			button.SetMinimumWidth(Dp(context, 44));
			button.SetMinHeight(Dp(context, 36));
			button.SetMinimumHeight(Dp(context, 36));
			int h = Dp(context, 10);
			button.SetPadding(h, 0, h, 0);
			LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(context, 36));
			parameters.SetMargins(0, 0, Dp(context, 6), 0);
			button.LayoutParameters = parameters;
			return button;
		}

		/// <summary>Layout parameters with margins, for stacking views in a vertical LinearLayout.</summary>
		public static LinearLayout.LayoutParams Stacked(Context context, int width = ViewGroup.LayoutParams.MatchParent, float top = 4, float bottom = 4)
		{
			LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(width, ViewGroup.LayoutParams.WrapContent);
			parameters.SetMargins(0, Dp(context, top), 0, Dp(context, bottom));
			return parameters;
		}

		/// <summary>A horizontal weight share, for side-by-side panes.</summary>
		public static LinearLayout.LayoutParams Weighted(float weight, Context context, float marginEnd = 0)
		{
			LinearLayout.LayoutParams parameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, weight);
			parameters.SetMargins(0, 0, Dp(context, marginEnd), 0);
			return parameters;
		}
	}
}
