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
using System.Linq;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace OpenBve.Android
{
	/// <summary>
	/// The switch map (upstream's SWITCH_MENU): the line around the train with every track, the way
	/// the train will go highlighted, and its switches to tap; beside it the switches nearby; over the
	/// map's foot, the chosen one's setting with a button to change it.
	/// </summary>
	/// <remarks>
	/// It replaces upstream's switch change dialog - a route picture drawn at the monitor's size with
	/// buttons on it, worked by the mouse - with the touch interface's own look (the timetable card's
	/// and the route map's). The game runs on while it is open, as upstream's does. It refreshes
	/// itself twice a second while shown.
	/// </remarks>
	public class SwitchMapPanel : FrameLayout
	{
		/// <summary>How far either side of the train the map reaches, in metres.</summary>
		public const double Radius = 2000.0;

		private static readonly Color Card = Color.Argb(240, 16, 20, 30);
		private static readonly Color Muted = Color.Argb(150, 255, 255, 255);
		private static readonly Color RowSelected = Color.Argb(70, 92, 160, 255);

		private readonly Func<SwitchMapSnapshot> source;
		private readonly SwitchMapView map;
		private readonly LinearLayout list;
		private readonly TextView empty;
		private readonly TextView detailName;
		private readonly TextView detailSetting;
		private readonly TextView detailDistance;
		private readonly TextView detailNote;
		private readonly Button changeButton;
		private readonly LinearLayout detail;
		private readonly Dictionary<Guid, RowViews> rows = new Dictionary<Guid, RowViews>();
		private readonly Action tick;
		private string rowsKey;
		private SwitchMapSnapshot snapshot;
		private Guid? selected;

		/// <summary>Raised when the chosen switch is to change to its next setting.</summary>
		public event Action<Guid> ChangeRequested;

		/// <summary>Raised when the panel opens (true) or closes (false).</summary>
		public event Action<bool> Toggled;

		/// <summary>Whether the panel is open.</summary>
		public bool IsOpen => Visibility == ViewStates.Visible;

		public SwitchMapPanel(Context context, Func<SwitchMapSnapshot> source) : base(context)
		{
			this.source = source;
			// The view behind is dimmed, and its touches are the panel's while it is open.
			SetBackgroundColor(Color.Argb(110, 0, 0, 0));
			Clickable = true;

			LinearLayout card = new LinearLayout(context) { Orientation = Orientation.Vertical, Clickable = true };
			card.SetPadding(Dp(14), Dp(8), Dp(10), Dp(12));
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Card);
			background.SetCornerRadius(Dp(16));
			background.SetStroke(Dp(1), Color.Argb(60, 255, 255, 255));
			card.Background = background;
			card.Elevation = Dp(8);

			// Header: the title, then close.
			LinearLayout header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			header.SetGravity(GravityFlags.CenterVertical);
			TextView title = new TextView(context) { Text = Upstream("title", "Select a Switch To Change"), TextSize = 17.0f };
			title.SetTextColor(Color.White);
			title.SetTypeface(title.Typeface, TypefaceStyle.Bold);
			header.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			header.AddView(HeaderButton("✕", Hide));
			card.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			LinearLayout body = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			card.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f) { TopMargin = Dp(6) });

			// The map, as large as the card allows.
			map = new SwitchMapView(context);
			map.SwitchTapped += Select;
			FrameLayout mapFrame = new FrameLayout(context);
			GradientDrawable mapBackground = new GradientDrawable();
			mapBackground.SetColor(Color.Argb(255, 11, 14, 21));
			mapBackground.SetCornerRadius(Dp(12));
			mapFrame.Background = mapBackground;
			mapFrame.ClipToOutline = true;
			mapFrame.AddView(map, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
			TextView hint = new TextView(context) { Text = Menu.T("android", "switch_way", "Your train's way ahead is highlighted. Tap a switch to choose it."), TextSize = 11.0f };
			hint.SetTextColor(Muted);
			hint.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
			hint.SetMaxWidth(Dp(300));
			mapFrame.AddView(hint, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Left));

			// Beside it: the switches nearby.
			LinearLayout side = new LinearLayout(context) { Orientation = Orientation.Vertical };
			body.AddView(side, new LinearLayout.LayoutParams(Dp(270), ViewGroup.LayoutParams.MatchParent) { LeftMargin = Dp(12) });

			TextView nearby = new TextView(context) { Text = Menu.T("android", "switches_nearby", "Nearby switches"), TextSize = 12.0f };
			nearby.SetTextColor(Muted);
			nearby.SetAllCaps(true);
			nearby.LetterSpacing = 0.08f;
			side.AddView(nearby);

			ScrollView scroll = new ScrollView(context) { FillViewport = false };
			list = new LinearLayout(context) { Orientation = Orientation.Vertical };
			scroll.AddView(list);
			side.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1.0f) { TopMargin = Dp(4) });
			empty = new TextView(context) { TextSize = 13.0f, Visibility = ViewStates.Gone };
			empty.SetTextColor(Muted);
			empty.SetPadding(0, Dp(8), 0, Dp(8));
			list.AddView(empty);

			// The chosen switch: a strip along the foot of the map, as a map app's place card -
			// its name and setting, and the button - low enough to leave the train in sight.
			detail = new LinearLayout(context) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone, Clickable = true };
			detail.SetGravity(GravityFlags.CenterVertical);
			detail.SetPadding(Dp(12), Dp(6), Dp(8), Dp(6));
			GradientDrawable detailBackground = new GradientDrawable();
			detailBackground.SetColor(Color.Argb(235, 28, 34, 48));
			detailBackground.SetCornerRadius(Dp(12));
			detailBackground.SetStroke(Dp(1), Color.Argb(50, 255, 255, 255));
			detail.Background = detailBackground;
			detail.Elevation = Dp(4);
			LinearLayout detailText = new LinearLayout(context) { Orientation = Orientation.Vertical };
			detailName = new TextView(context) { TextSize = 15.0f };
			detailName.SetTextColor(Color.White);
			detailName.SetTypeface(detailName.Typeface, TypefaceStyle.Bold);
			detailName.SetSingleLine(true);
			detailName.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
			detailText.AddView(detailName);
			detailSetting = DetailLine(context, Color.White);
			detailText.AddView(detailSetting);
			detailDistance = DetailLine(context, Muted);
			detailText.AddView(detailDistance);
			detailNote = DetailLine(context, Color.Rgb(255, 184, 77));
			detailText.AddView(detailNote);
			detail.AddView(detailText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			changeButton = new Button(context) { Text = Menu.T("android", "switch_change", "Change setting"), TextSize = 15.0f, Focusable = true };
			changeButton.SetAllCaps(false);
			changeButton.SetTextColor(Color.White);
			changeButton.StateListAnimator = null;
			changeButton.Click += (sender, e) =>
			{
				if (selected is Guid id)
				{
					ChangeRequested?.Invoke(id);
					// The change happens on the game's thread; show it as soon as it has.
					RemoveCallbacks(tick);
					PostDelayed(tick, 120);
				}
			};
			changeButton.SetPadding(Dp(18), 0, Dp(18), 0);
			detail.AddView(changeButton, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(44)) { LeftMargin = Dp(10) });
			mapFrame.AddView(detail, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom)
			{
				LeftMargin = Dp(8), RightMargin = Dp(8), BottomMargin = Dp(8)
			});

			body.AddView(mapFrame, 0, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1.0f));

			AddView(card, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent)
			{
				LeftMargin = Dp(28), RightMargin = Dp(28), TopMargin = Dp(18), BottomMargin = Dp(18)
			});

			// Touching the dimmed edge closes the panel, as tapping outside a dialog does.
			Click += (sender, e) => Hide();

			tick = () =>
			{
				if (!IsOpen)
				{
					return;
				}

				Refresh();
				PostDelayed(tick, 500);
			};
		}

		/// <summary>Opens the panel. Call on the UI thread.</summary>
		public void Show()
		{
			if (IsOpen)
			{
				return;
			}

			Visibility = ViewStates.Visible;
			map.Follow();
			Refresh();
			RemoveCallbacks(tick);
			PostDelayed(tick, 500);
			Toggled?.Invoke(true);
		}

		/// <summary>Closes the panel. Call on the UI thread.</summary>
		public void Hide()
		{
			if (!IsOpen)
			{
				return;
			}

			Visibility = ViewStates.Gone;
			RemoveCallbacks(tick);
			Toggled?.Invoke(false);
		}

		/// <summary>Opens the panel, or closes it if open. Call on the UI thread.</summary>
		public void Toggle()
		{
			if (IsOpen)
			{
				Hide();
			}
			else
			{
				Show();
			}
		}

		private void Refresh()
		{
			SwitchMapSnapshot next;
			try
			{
				next = source();
			}
			catch (Exception e)
			{
				// Read from the UI thread while the game moves on: a torn read shows next time.
				global::Android.Util.Log.Warn("OpenBVE", "switch map: " + e.Message);
				return;
			}

			if (next == null)
			{
				return;
			}

			snapshot = next;
			// At first, the nearest switch ahead that can be changed: one tap changes it.
			if (selected == null || !Contains(selected.Value))
			{
				selected = null;
				foreach (SwitchMark mark in snapshot.Switches)
				{
					if (!mark.Fixed && mark.Distance >= 0)
					{
						selected = mark.Id;
						break;
					}
				}

				if (selected == null && snapshot.Switches.Count > 0)
				{
					selected = snapshot.Switches[0].Id;
				}
			}

			map.SetSnapshot(snapshot, selected);
			UpdateList();
			UpdateDetail();
		}

		private bool Contains(Guid id)
		{
			foreach (SwitchMark mark in snapshot.Switches)
			{
				if (mark.Id == id)
				{
					return true;
				}
			}

			return false;
		}

		private void Select(Guid id) => Select(id, false);

		/// <summary>Chooses a switch; from the list, the map also goes to it, close enough to see its tracks part.</summary>
		private void Select(Guid id, bool focus)
		{
			selected = id;
			if (snapshot != null)
			{
				if (focus)
				{
					foreach (SwitchMark mark in snapshot.Switches)
					{
						if (mark.Id == id)
						{
							map.FocusOn(mark.X, mark.Z);
						}
					}
				}

				map.SetSnapshot(snapshot, selected);
				UpdateList();
				UpdateDetail();
			}
		}

		private void UpdateList()
		{
			// The rows are made again only when the switches in range change; their text each time.
			string key = string.Join(",", snapshot.Switches.Select(m => m.Id.ToString()));
			if (key != rowsKey)
			{
				rowsKey = key;
				foreach (RowViews row in rows.Values)
				{
					list.RemoveView(row.Root);
				}

				rows.Clear();
				foreach (SwitchMark mark in snapshot.Switches)
				{
					RowViews row = new RowViews(Context, this, mark.Id);
					rows[mark.Id] = row;
					list.AddView(row.Root, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(2) });
				}
			}

			empty.Visibility = snapshot.Switches.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
			empty.Text = string.Format(CultureInfo.CurrentCulture, Menu.T("android", "switches_none", "No switches within {0} m of the train."), (int)snapshot.Radius);
			foreach (SwitchMark mark in snapshot.Switches)
			{
				rows[mark.Id].Show(mark, mark.Id == selected);
			}
		}

		private void UpdateDetail()
		{
			SwitchMark mark = null;
			foreach (SwitchMark candidate in snapshot.Switches)
			{
				if (candidate.Id == selected)
				{
					mark = candidate;
					break;
				}
			}

			detail.Visibility = mark == null ? ViewStates.Gone : ViewStates.Visible;
			if (mark == null)
			{
				return;
			}

			detailName.Text = NameOf(mark);
			detailSetting.Text = Upstream("current", "Current Setting") + ": " + mark.Setting;
			detailDistance.Text = Upstream("distance", "Distance from Player") + ": " + DistanceText(mark.Distance);
			string note = mark.Fixed
				? Menu.T("android", "switch_fixed", "Fixed route: this switch cannot be changed.")
				: mark.RunThrough
					? Menu.T("android", "switch_run_through", "A train has run through this switch: change it to reset it.")
					: null;
			detailNote.Text = note ?? string.Empty;
			detailNote.Visibility = note == null ? ViewStates.Gone : ViewStates.Visible;
			changeButton.Enabled = !mark.Fixed;
			changeButton.Alpha = mark.Fixed ? 0.4f : 1.0f;
			GradientDrawable buttonBackground = new GradientDrawable();
			buttonBackground.SetColor(Ui.Accent);
			buttonBackground.SetCornerRadius(Dp(10));
			changeButton.Background = buttonBackground;
		}

		/// <summary>A switch's name, or "Switch n" for one the route left unnamed.</summary>
		private string NameOf(SwitchMark mark)
		{
			if (!string.IsNullOrWhiteSpace(mark.Name))
			{
				return mark.Name;
			}

			int index = snapshot.Switches.ToList().FindIndex(m => m.Id == mark.Id) + 1;
			return string.Format(CultureInfo.CurrentCulture, Menu.T("android", "switch_unnamed", "Switch {0}"), index);
		}

		private static string DistanceText(double metres)
		{
			string amount = Math.Abs(metres).ToString("0", CultureInfo.CurrentCulture);
			return metres >= 0
				? string.Format(CultureInfo.CurrentCulture, Menu.T("android", "switch_ahead", "{0} m ahead"), amount)
				: string.Format(CultureInfo.CurrentCulture, Menu.T("android", "switch_behind", "{0} m behind"), amount);
		}

		/// <summary>Upstream's own switch menu text, without the ": " its desktop layout ends some with.</summary>
		private static string Upstream(string key, string fallback) => Menu.T("switchmenu", key, fallback).Trim().TrimEnd(':', '：').Trim();

		private TextView DetailLine(Context context, Color color)
		{
			TextView line = new TextView(context) { TextSize = 12.5f };
			line.SetTextColor(color);
			line.SetPadding(0, Dp(1), 0, 0);
			return line;
		}

		private Button HeaderButton(string label, Action action)
		{
			Button button = new Button(Context) { Text = label, TextSize = 16.0f, Focusable = true };
			button.SetTextColor(Color.White);
			button.SetMinWidth(0);
			button.SetMinimumWidth(0);
			button.SetMinHeight(0);
			button.SetMinimumHeight(0);
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(170, 40, 46, 60));
			background.SetCornerRadius(Dp(18));
			button.Background = background;
			button.StateListAnimator = null;
			button.Click += (sender, e) => action();
			button.LayoutParameters = new LinearLayout.LayoutParams(Dp(40), Dp(36));
			return button;
		}

		private int Dp(float value) => Ui.Dp(Context, value);

		/// <summary>The colour a switch is shown in, on the map and in the list.</summary>
		internal static Color ColourOf(SwitchMark mark) =>
			mark.RunThrough ? Color.Rgb(235, 80, 80) : mark.Fixed ? Color.Rgb(120, 128, 142) : Color.Rgb(255, 184, 77);

		private sealed class RowViews
		{
			internal readonly LinearLayout Root;
			private readonly View dot;
			private readonly TextView name;
			private readonly TextView detail;
			private readonly SwitchMapPanel panel;

			internal RowViews(Context context, SwitchMapPanel panel, Guid id)
			{
				this.panel = panel;
				Root = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
				Root.SetGravity(GravityFlags.CenterVertical);
				Root.SetPadding(panel.Dp(8), panel.Dp(7), panel.Dp(8), panel.Dp(7));
				Root.Click += (sender, e) => panel.Select(id, true);

				dot = new View(context);
				Root.AddView(dot, new LinearLayout.LayoutParams(panel.Dp(12), panel.Dp(12)) { RightMargin = panel.Dp(10) });

				LinearLayout text = new LinearLayout(context) { Orientation = Orientation.Vertical };
				name = new TextView(context) { TextSize = 14.0f };
				name.SetTextColor(Color.White);
				name.SetSingleLine(true);
				name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
				text.AddView(name);
				detail = new TextView(context) { TextSize = 12.0f };
				detail.SetTextColor(Muted);
				detail.SetSingleLine(true);
				detail.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
				text.AddView(detail);
				Root.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			}

			internal void Show(SwitchMark mark, bool isSelected)
			{
				name.Text = panel.NameOf(mark);
				detail.Text = DistanceText(mark.Distance) + " · " + mark.Setting;
				GradientDrawable circle = new GradientDrawable();
				circle.SetShape(ShapeType.Oval);
				circle.SetColor(ColourOf(mark));
				dot.Background = circle;
				GradientDrawable background = new GradientDrawable();
				background.SetColor(isSelected ? RowSelected : Color.Transparent);
				background.SetCornerRadius(panel.Dp(10));
				Root.Background = background;
				Root.Alpha = mark.Distance >= 0 ? 1.0f : 0.6f;
			}
		}
	}

	/// <summary>
	/// The switch map's chart: every track near the train, the way ahead in the accent colour, the
	/// switches as dots to tap (amber: can be changed, grey: fixed, red: run through), the stations,
	/// and the train. Pinch to zoom, drag to look around, ◎ to follow the train again.
	/// </summary>
	public sealed class SwitchMapView : RouteChart
	{
		private SwitchMapSnapshot snapshot;
		private Guid? selected;
		// World (x, z) at the centre of the view, and pixels per metre.
		private double centreX, centreZ, scale = -1;
		private float downX, downY;
		private bool tapPossible;

		/// <summary>Raised when a switch is tapped.</summary>
		public event Action<Guid> SwitchTapped;

		public SwitchMapView(Context context) : base(context)
		{
		}

		protected override IEnumerable<(string Label, Action Action)> ExtraButtons()
		{
			yield return ("+", () => ZoomBy(1.6f));
			yield return ("−", () => ZoomBy(1 / 1.6f));
		}

		/// <summary>Updates what is drawn. Call on the UI thread.</summary>
		public void SetSnapshot(SwitchMapSnapshot next, Guid? chosen)
		{
			snapshot = next;
			selected = chosen;
			// The base's ◎ follows its own data; it is told only to refresh the button.
			Set(null, null);
		}

		/// <summary>Back to the train, at the opening zoom.</summary>
		public void Follow()
		{
			Following = true;
			scale = -1;
			Invalidate();
		}

		/// <summary>Centres the map on a point, close enough to tell tracks a few metres apart.</summary>
		public void FocusOn(double x, double z)
		{
			Following = false;
			centreX = x;
			centreZ = z;
			scale = Math.Max(scale, 2.5 * Density);
			Set(null, null);
		}

		private void ZoomBy(float factor)
		{
			Zoom(factor, Width / 2.0f, Height / 2.0f);
			Invalidate();
		}

		private double MinScale => Math.Min(Width, Height) / (2.2 * (snapshot?.Radius ?? SwitchMapPanel.Radius));

		protected override void Pan(float dx, float dy)
		{
			centreX -= dx / scale;
			centreZ += dy / scale;
		}

		protected override void Zoom(float factor, float focusX, float focusY)
		{
			if (scale <= 0)
			{
				return;
			}

			double wx = centreX + (focusX - Width / 2.0) / scale, wz = centreZ - (focusY - Height / 2.0) / scale;
			double next = Math.Max(MinScale, Math.Min(scale * factor, 8.0 * Density));
			if (!Following)
			{
				centreX = wx - (focusX - Width / 2.0) / next;
				centreZ = wz + (focusY - Height / 2.0) / next;
			}

			scale = next;
		}

		public override bool OnTouchEvent(MotionEvent e)
		{
			bool handled = base.OnTouchEvent(e);
			switch (e.ActionMasked)
			{
				case MotionEventActions.Down:
					downX = e.GetX();
					downY = e.GetY();
					tapPossible = true;
					break;
				case MotionEventActions.PointerDown:
					tapPossible = false;
					break;
				case MotionEventActions.Move:
					if (Math.Abs(e.GetX() - downX) + Math.Abs(e.GetY() - downY) > 8 * Density)
					{
						tapPossible = false;
					}

					break;
				case MotionEventActions.Up:
					if (tapPossible && NearestSwitch(e.GetX(), e.GetY()) is Guid id)
					{
						SwitchTapped?.Invoke(id);
					}

					tapPossible = false;
					break;
			}

			return handled;
		}

		private Guid? NearestSwitch(float x, float y)
		{
			if (snapshot == null || scale <= 0)
			{
				return null;
			}

			Guid? nearest = null;
			double best = 30 * Density;
			foreach (SwitchMark mark in snapshot.Switches)
			{
				double d = Math.Sqrt(Math.Pow(Sx(mark.X) - x, 2) + Math.Pow(Sy(mark.Z) - y, 2));
				if (d < best)
				{
					best = d;
					nearest = mark.Id;
				}
			}

			return nearest;
		}

		private float Sx(double x) => Width / 2.0f + (float)((x - centreX) * scale);

		private float Sy(double z) => Height / 2.0f - (float)((z - centreZ) * scale);

		protected override void OnDraw(Canvas canvas)
		{
			base.OnDraw(canvas);
			if (snapshot == null || Width == 0 || Height == 0)
			{
				return;
			}

			if (scale <= 0)
			{
				// About 700 m across to start: the next few switches ahead.
				scale = Math.Max(MinScale, Math.Min(Width, Height) / 700.0);
			}

			if (Following)
			{
				centreX = snapshot.X;
				centreZ = snapshot.Z;
			}

			Paint.SetStyle(Paint.Style.Stroke);
			Paint.StrokeCap = Paint.Cap.Round;
			Paint.StrokeJoin = Paint.Join.Round;

			// Every track, then the way ahead over them.
			Paint.StrokeWidth = 3 * Density;
			Paint.Color = Color.Argb(120, 190, 200, 220);
			foreach (float[] track in snapshot.Tracks)
			{
				DrawLine(canvas, track);
			}

			Paint.StrokeWidth = 9 * Density;
			Paint.Color = Color.Argb(70, Accent.R, Accent.G, Accent.B);
			DrawLine(canvas, snapshot.Way);
			Paint.StrokeWidth = 4 * Density;
			Paint.Color = Accent;
			DrawLine(canvas, snapshot.Way);

			// Stations, as on the route map.
			List<RectF> taken = new List<RectF>();
			foreach ((string name, double x, double z) in snapshot.Stations)
			{
				float sx = Sx(x), sy = Sy(z);
				Dot(canvas, sx, sy, 4 * Density, Muted);
				RectF chip = MeasureChip(name, sx + 8 * Density, sy - 14 * Density);
				if (!Overlaps(taken, chip))
				{
					taken.Add(DrawChip(canvas, name, sx + 8 * Density, sy - 14 * Density, Chip, Muted));
				}
			}

			// The train.
			Dot(canvas, Sx(snapshot.X), Sy(snapshot.Z), 7 * Density, Here);

			// The switches: the chosen one last, ringed, with its name.
			SwitchMark chosen = null;
			for (int i = snapshot.Switches.Count - 1; i >= 0; i--)
			{
				SwitchMark mark = snapshot.Switches[i];
				if (mark.Id == selected)
				{
					chosen = mark;
					continue;
				}

				Dot(canvas, Sx(mark.X), Sy(mark.Z), 7 * Density, SwitchMapPanel.ColourOf(mark));
			}

			if (chosen != null)
			{
				float sx = Sx(chosen.X), sy = Sy(chosen.Z);
				Paint.SetStyle(Paint.Style.Stroke);
				Paint.StrokeWidth = 3 * Density;
				Paint.Color = Color.White;
				canvas.DrawCircle(sx, sy, 15 * Density, Paint);
				Dot(canvas, sx, sy, 9 * Density, SwitchMapPanel.ColourOf(chosen));
				DrawChip(canvas, chosen.Setting, sx + 20 * Density, sy, Chip, Color.White, true);
			}
		}

		private void DrawLine(Canvas canvas, float[] points)
		{
			if (points == null || points.Length < 4)
			{
				return;
			}

			using (Path path = new Path())
			{
				path.MoveTo(Sx(points[0]), Sy(points[1]));
				for (int i = 2; i + 1 < points.Length; i += 2)
				{
					path.LineTo(Sx(points[i]), Sy(points[i + 1]));
				}

				canvas.DrawPath(path, Paint);
			}
		}
	}
}
