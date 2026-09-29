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

namespace OpenBve.Android
{
	/// <summary>
	/// The touch interface's driving information: a card at the bottom of the view, between the
	/// reverser, horn and door buttons and the levers, where the cab's own gauges usually are.
	/// </summary>
	/// <remarks>
	/// Its top line: the speed (red above the limit), the limit as a round sign (red for a signal
	/// at danger), the handles coloured by what they do (neutral, power, brake, emergency), the
	/// reverser and the clock. Below: the next stop, how far, and its timetabled times. Above
	/// both, only while it happens, what the safety system is doing to the handles.
	/// </remarks>
	public class DriverInfoBar : LinearLayout
	{
		private readonly float density, scale;
		private readonly TextView warning, speed, unit, limit, handles, reverser, clock, next;
		private readonly LinearLayout top;
		private readonly GradientDrawable limitSign, handleChip, reverserChip;

		private static readonly Color Text = Color.Rgb(240, 243, 248);
		private static readonly Color Dim = Color.Rgb(160, 170, 185);
		private static readonly Color Over = Color.Rgb(255, 95, 90);

		public DriverInfoBar(Context context, float density, float scale) : base(context)
		{
			this.density = density;
			this.scale = scale;
			Orientation = Orientation.Vertical;
			SetPadding(Dp(12), Dp(5), Dp(12), Dp(6));
			SetMinimumWidth(Dp(300));
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(205, 14, 18, 27));
			background.SetCornerRadius(Dp(14));
			background.SetStroke(Math.Max(1, Dp(1)), Color.Argb(70, 255, 255, 255));
			Background = background;

			warning = Label(12.0f, Color.Rgb(30, 22, 0), true);
			GradientDrawable warningBackground = new GradientDrawable();
			warningBackground.SetColor(Color.Rgb(255, 190, 60));
			warningBackground.SetCornerRadius(Dp(6));
			warning.Background = warningBackground;
			warning.SetPadding(Dp(8), Dp(1), Dp(8), Dp(1));
			warning.Visibility = ViewStates.Gone;
			AddView(warning, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });

			top = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			top.SetGravity(GravityFlags.CenterVertical);
			speed = Label(24.0f, Text, true);
			speed.FontFeatureSettings = "tnum";
			top.AddView(speed);
			unit = Label(11.0f, Dim, false);
			unit.Text = " km/h";
			top.AddView(unit, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) });

			limit = Label(12.0f, Color.Black, true);
			limit.Gravity = GravityFlags.Center;
			limitSign = new GradientDrawable();
			limitSign.SetShape(ShapeType.Oval);
			limit.Background = limitSign;
			top.AddView(limit, new LayoutParams(Dp(30), Dp(30)) { RightMargin = Dp(10) });

			handles = Chip(out handleChip);
			top.AddView(handles, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(6) });
			reverser = Chip(out reverserChip);
			top.AddView(reverser);

			top.AddView(new View(context), new LayoutParams(Dp(12), 1, 1.0f));
			clock = Label(15.0f, Text, true);
			clock.FontFeatureSettings = "tnum";
			top.AddView(clock);
			AddView(top, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			next = Label(13.0f, Dim, false);
			next.FontFeatureSettings = "tnum";
			AddView(next, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(1) });
		}

		/// <summary>Shows the latest status. Call on the UI thread.</summary>
		public void Show(DriverStatus status)
		{
			warning.Visibility = status.Warning != null ? ViewStates.Visible : ViewStates.Gone;
			warning.Text = status.Warning != null ? "⚠  " + status.Warning : string.Empty;

			bool over = status.Limit > 0.0 && status.Speed > status.Limit.Value + 1.0;
			speed.Text = status.Speed.ToString("0");
			speed.SetTextColor(over ? Over : Text);

			if (status.Limit == null)
			{
				limit.Visibility = ViewStates.Gone;
			}
			else
			{
				limit.Visibility = ViewStates.Visible;
				bool danger = status.Limit <= 0.0;
				limitSign.SetColor(danger ? Color.Rgb(210, 35, 35) : Color.White);
				limitSign.SetStroke(Dp(3), Color.Rgb(210, 35, 35));
				limit.SetTextColor(danger ? Color.White : Color.Black);
				limit.Text = status.Limit.Value.ToString("0");
				limit.TextSize = (status.Limit >= 100.0 ? 10.5f : 12.0f) * scale;
			}

			handles.Text = status.Handles;
			handleChip.SetColor(status.HandleState switch
			{
				HandleState.Power => Color.Rgb(30, 105, 215),
				HandleState.Brake => Color.Rgb(215, 125, 25),
				HandleState.Emergency => Color.Rgb(205, 35, 35),
				_ => Color.Rgb(72, 80, 96)
			});
			reverser.Text = status.Reverser;
			reverserChip.SetColor(status.Reverser == "F" ? Color.Rgb(40, 150, 85) : status.Reverser == "R" ? Color.Rgb(150, 70, 175) : Color.Rgb(72, 80, 96));
			clock.Text = status.Clock;

			if (status.NextName == null)
			{
				next.Text = Menu.T("android", "timetable_end", "End of the line");
				return;
			}

			// "▸ Lo Wu · 350 m · Arr 10:04:00 · Dep 10:05:00", the station and distance brightest.
			SpannableStringBuilder line = new SpannableStringBuilder();
			Append(line, "▸ ", Dim);
			Append(line, status.NextName, Text, true);
			Append(line, "  ·  ", Dim);
			Append(line, status.NextDistance, Text);
			if (status.NextArrival != null)
			{
				Append(line, "  ·  " + Menu.T("android", "timetable_arrival", "Arr") + " " + status.NextArrival, Dim);
			}

			if (status.NextDeparture != null)
			{
				Append(line, "  ·  " + Menu.T("android", "timetable_departure", "Dep") + " " + status.NextDeparture, Dim);
			}

			next.TextFormatted = line;
		}

		/// <summary>
		/// The width to give it within <paramref name="available"/>: enough to show every line
		/// whole if that fits; otherwise all of it, the "km/h" dropped first and then the clock so
		/// the speed, limit and handles stay readable (the next-stop line is cut short instead).
		/// </summary>
		public int FitTo(int available)
		{
			int padding = PaddingLeft + PaddingRight + Dp(2);
			int pieces = Piece(speed) + Piece(handles) + Piece(reverser) + Dp(12) + (limit.Visibility == ViewStates.Visible ? Piece(limit) : 0);
			int unitWidth = Piece(unit), clockWidth = Piece(clock);
			int lines = (int)Math.Ceiling(global::Android.Text.Layout.GetDesiredWidth(next.TextFormatted ?? new Java.Lang.String(string.Empty), next.Paint));
			if (warning.Visibility == ViewStates.Visible)
			{
				lines = Math.Max(lines, warning.PaddingLeft + warning.PaddingRight +
				                        (int)Math.Ceiling(global::Android.Text.Layout.GetDesiredWidth(warning.TextFormatted ?? new Java.Lang.String(string.Empty), warning.Paint)));
			}

			int full = Math.Max(MinimumWidth, padding + Math.Max(pieces + unitWidth + clockWidth, lines));
			bool showUnit = pieces + unitWidth + clockWidth + padding <= available;
			bool showClock = pieces + clockWidth + padding <= available;
			Show(unit, showUnit);
			Show(clock, showClock);
			return Math.Min(full, available);
		}

		// Only on a change: setting a view's visibility lays it out again.
		private static void Show(View view, bool shown)
		{
			ViewStates state = shown ? ViewStates.Visible : ViewStates.Gone;
			if (view.Visibility != state)
			{
				view.Visibility = state;
			}
		}

		// From the text and padding: measuring the live views here, outside a layout pass, would upset how they draw.
		private static int Piece(TextView view)
		{
			int width = view.LayoutParameters != null && view.LayoutParameters.Width > 0
				? view.LayoutParameters.Width
				: view.PaddingLeft + view.PaddingRight + (int)Math.Ceiling(global::Android.Text.Layout.GetDesiredWidth(view.TextFormatted ?? new Java.Lang.String(string.Empty), view.Paint));
			return width + (view.LayoutParameters is MarginLayoutParams margins ? margins.LeftMargin + margins.RightMargin : 0);
		}


		private static void Append(SpannableStringBuilder line, string text, Color color, bool bold = false)
		{
			int start = line.Length();
			line.Append(text);
			line.SetSpan(new ForegroundColorSpan(color), start, line.Length(), SpanTypes.ExclusiveExclusive);
			if (bold)
			{
				line.SetSpan(new StyleSpan(TypefaceStyle.Bold), start, line.Length(), SpanTypes.ExclusiveExclusive);
			}
		}

		private TextView Label(float size, Color color, bool bold)
		{
			TextView view = new TextView(Context) { TextSize = size * scale };
			view.SetTextColor(color);
			view.SetSingleLine(true);
			view.Ellipsize = TextUtils.TruncateAt.End;
			view.SetIncludeFontPadding(false);
			if (bold)
			{
				view.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold), TypefaceStyle.Bold);
			}

			return view;
		}

		private TextView Chip(out GradientDrawable background)
		{
			TextView view = Label(12.0f, Color.White, true);
			view.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
			background = new GradientDrawable();
			background.SetCornerRadius(Dp(7));
			view.Background = background;
			return view;
		}

		private int Dp(float value) => (int)(value * density + 0.5f);
	}
}
