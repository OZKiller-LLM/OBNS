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
	/// The touch interface's route card, with three tabs. Timetable lists the stations as a
	/// timeline, the next stop highlighted with its distance and how the train stands against the
	/// timetable, and the route's own timetable picture a tap away when it has one. Map and
	/// Gradient draw the line in plan and in elevation from the track itself, with the trains.
	/// </summary>
	/// <remarks>
	/// It replaces upstream's timetable texture and route information overlay, drawn over the
	/// view at a fixed pixel size for a monitor, in the touch interface; the desktop interface keeps
	/// upstream's. It refreshes itself once a second while shown.
	/// </remarks>
	public class TimetablePanel : LinearLayout
	{
		private static readonly Color Card = Color.Argb(240, 16, 20, 30);
		private static readonly Color Muted = Color.Argb(150, 255, 255, 255);
		private static readonly Color Line = Color.Argb(90, 255, 255, 255);
		private static readonly Color Current = Ui.Accent;
		private static readonly Color Here = Color.Rgb(80, 200, 120);
		private static readonly Color Late = Color.Rgb(255, 170, 60);

		/// <summary>The card's tabs.</summary>
		public enum Tab
		{
			Timetable,
			Map,
			Gradient
		}

		private readonly Func<TimetableSnapshot> source;
		private readonly Func<(RouteGeometry Geometry, RouteLive Live)> route;
		private readonly Button[] tabButtons = new Button[3];
		private readonly LinearLayout timetableContent;
		private readonly RouteMapView mapView;
		private readonly GradientChartView gradientView;
		private Tab tab;
		private readonly Button pictureButton;
		private readonly Button expandButton;
		private readonly TextView nextName;
		private readonly TextView nextDetail;
		private readonly LinearLayout nextCard;
		private readonly ScrollView listScroll;
		private readonly LinearLayout list;
		private readonly ScrollView pictureScroll;
		private readonly ImageView picture;
		private readonly List<RowViews> rows = new List<RowViews>();
		private readonly Action tick;
		private string rowsKey;
		private string picturePath;
		private int scrolledTo = -1;
		private bool showPicture;
		private bool expanded;

		/// <summary>Raised when the card is shown or hidden.</summary>
		public event Action Toggled;

		/// <summary>The bottom margin that keeps the collapsed card clear of the controls below it.</summary>
		public int CollapsedBottomMargin { get; set; }

		/// <summary>The bottom margin of the expanded card.</summary>
		public int ExpandedBottomMargin { get; set; }

		public TimetablePanel(Context context, Func<TimetableSnapshot> source, Func<(RouteGeometry Geometry, RouteLive Live)> route) : base(context)
		{
			this.source = source;
			this.route = route;
			Orientation = Orientation.Vertical;
			// Touches on the card are the card's: scrolling it must not turn the camera.
			Clickable = true;
			SetPadding(Dp(12), Dp(6), Dp(10), Dp(8));
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Card);
			background.SetCornerRadius(Dp(16));
			background.SetStroke(Dp(1), Color.Argb(60, 255, 255, 255));
			Background = background;
			Elevation = Dp(6);

			// Header: the tabs, then the card's own controls. (The clock is in the information line.)
			LinearLayout header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			header.SetGravity(GravityFlags.CenterVertical);
			string[] tabNames =
			{
				Menu.T("android", "timetable", "Timetable"), Menu.T("android", "route_map", "Map"), Menu.T("android", "route_gradient", "Gradient")
			};
			for (int i = 0; i < tabNames.Length; i++)
			{
				Tab chosen = (Tab)i;
				tabButtons[i] = TabButton(tabNames[i], () => SelectTab(chosen));
				header.AddView(tabButtons[i]);
			}

			header.AddView(new View(context), new LayoutParams(0, 1, 1.0f));
			pictureButton = HeaderButton("▦", () =>
			{
				showPicture = !showPicture;
				Refresh();
			});
			pictureButton.Visibility = ViewStates.Gone;
			header.AddView(pictureButton);
			expandButton = HeaderButton("▼", () => SetExpanded(!expanded));
			header.AddView(expandButton);
			header.AddView(HeaderButton("✕", Hide));
			AddView(header, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			/*
			 * The next stop, large, with how the train stands against the timetable: in the
			 * expanded card only. Collapsed, the space goes to the list, whose highlighted row
			 * says the same in brief.
			 */
			nextCard = new LinearLayout(context) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
			nextCard.SetPadding(Dp(10), Dp(6), Dp(10), Dp(7));
			GradientDrawable highlight = new GradientDrawable();
			highlight.SetColor(Color.Argb(50, Current.R, Current.G, Current.B));
			highlight.SetCornerRadius(Dp(10));
			nextCard.Background = highlight;
			nextName = Text(string.Empty, 16.0f, Color.White, bold: true);
			nextDetail = Text(string.Empty, 12.5f, Muted);
			nextCard.AddView(nextName);
			nextCard.AddView(nextDetail);
			AddView(nextCard, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6), BottomMargin = Dp(4) });

			// The timetable tab: the column heads, then the stations.
			timetableContent = new LinearLayout(context) { Orientation = Orientation.Vertical };
			AddView(timetableContent, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
			LinearLayout heads = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			heads.AddView(new View(context), new LayoutParams(Dp(22), 1));
			heads.AddView(Text(Menu.T("android", "timetable_station", "Station"), 11.0f, Muted), new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f));
			heads.AddView(TimeColumn(Text(Menu.T("android", "timetable_arrival", "Arr"), 11.0f, Muted)), new LayoutParams(Dp(TimeWidth), ViewGroup.LayoutParams.WrapContent));
			heads.AddView(TimeColumn(Text(Menu.T("android", "timetable_departure", "Dep"), 11.0f, Muted)), new LayoutParams(Dp(TimeWidth), ViewGroup.LayoutParams.WrapContent));
			timetableContent.AddView(heads, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

			list = new LinearLayout(context) { Orientation = Orientation.Vertical };
			listScroll = new ScrollView(context) { VerticalScrollBarEnabled = true, OverScrollMode = OverScrollMode.Never };
			listScroll.AddView(list);
			timetableContent.AddView(listScroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

			// The route's own timetable picture, shown instead of the list on request.
			picture = new ImageView(context);
			picture.SetAdjustViewBounds(true);
			picture.SetScaleType(ImageView.ScaleType.FitCenter);
			pictureScroll = new ScrollView(context) { Visibility = ViewStates.Gone };
			pictureScroll.AddView(picture, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
			timetableContent.AddView(pictureScroll, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

			// The map and gradient tabs: drawn from the track, filling the card.
			mapView = new RouteMapView(context) { Visibility = ViewStates.Gone };
			AddView(mapView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6) });
			gradientView = new GradientChartView(context) { Visibility = ViewStates.Gone };
			AddView(gradientView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(6) });
			SelectTab(Tab.Timetable);

			tick = () =>
			{
				if (Visibility == ViewStates.Visible)
				{
					Refresh();
					PostDelayed(tick, tab == Tab.Timetable ? 1000 : 200);
				}
			};
		}

		/// <summary>
		/// Shows the card at a tab, or hides it if it is already showing that tab. Call on the UI thread.
		/// </summary>
		public void Toggle(Tab shown)
		{
			if (Visibility == ViewStates.Visible && tab == shown)
			{
				Hide();
				return;
			}

			SelectTab(shown);
			if (Visibility != ViewStates.Visible)
			{
				Show();
			}
		}

		private void SelectTab(Tab chosen)
		{
			tab = chosen;
			for (int i = 0; i < tabButtons.Length; i++)
			{
				bool selected = i == (int)chosen;
				((GradientDrawable)tabButtons[i].Background).SetColor(selected ? Color.Argb(90, Current.R, Current.G, Current.B) : Color.Transparent);
				tabButtons[i].SetTextColor(selected ? Color.White : Muted);
			}

			timetableContent.Visibility = chosen == Tab.Timetable ? ViewStates.Visible : ViewStates.Gone;
			// The next stop strip is the timetable's; the map and gradient need the room.
			nextCard.Visibility = expanded && chosen == Tab.Timetable ? ViewStates.Visible : ViewStates.Gone;
			mapView.Visibility = chosen == Tab.Map ? ViewStates.Visible : ViewStates.Gone;
			gradientView.Visibility = chosen == Tab.Gradient ? ViewStates.Visible : ViewStates.Gone;
			scrolledTo = -1;
			if (Visibility == ViewStates.Visible)
			{
				Refresh();
			}
		}

		/// <summary>Shows the card.</summary>
		public void Show()
		{
			Visibility = ViewStates.Visible;
			scrolledTo = -1;
			RemoveCallbacks(tick);
			tick();
			Toggled?.Invoke();
		}

		/// <summary>Hides the card.</summary>
		public void Hide()
		{
			Visibility = ViewStates.Gone;
			RemoveCallbacks(tick);
			Toggled?.Invoke();
		}

		private void SetExpanded(bool value)
		{
			expanded = value;
			expandButton.Text = expanded ? "▲" : "▼";
			nextCard.Visibility = expanded && tab == Tab.Timetable ? ViewStates.Visible : ViewStates.Gone;
			if (LayoutParameters is FrameLayout.LayoutParams parameters)
			{
				parameters.BottomMargin = expanded ? ExpandedBottomMargin : CollapsedBottomMargin;
				LayoutParameters = parameters;
			}

			scrolledTo = -1;
		}

		private void Refresh()
		{
			TimetableSnapshot snapshot;
			try
			{
				snapshot = source();
			}
			catch (Exception)
			{
				// The session is between states (loading, jumping); the next tick will do.
				return;
			}

			if (snapshot == null)
			{
				return;
			}

			if (tab != Tab.Timetable)
			{
				RefreshIllustration();
			}

			pictureButton.Visibility = snapshot.CustomImage != null && tab == Tab.Timetable ? ViewStates.Visible : ViewStates.Gone;
			bool pictureShown = showPicture && snapshot.CustomImage != null;
			pictureButton.Text = pictureShown ? "☰" : "▦";
			if (pictureShown && snapshot.CustomImage != picturePath)
			{
				picturePath = snapshot.CustomImage;
				picture.SetImageBitmap(LoadPicture(picturePath));
			}

			pictureScroll.Visibility = pictureShown ? ViewStates.Visible : ViewStates.Gone;
			listScroll.Visibility = pictureShown ? ViewStates.Gone : ViewStates.Visible;

			BuildRows(snapshot.Rows);
			int next = -1;
			for (int i = 0; i < snapshot.Rows.Count; i++)
			{
				TimetableRow row = snapshot.Rows[i];
				UpdateRow(rows[i], row, i == 0, i == snapshot.Rows.Count - 1);
				if (next < 0 && (row.State == TimetableRowState.Next || row.State == TimetableRowState.AtStation))
				{
					next = i;
				}
			}

			UpdateNext(next >= 0 ? snapshot.Rows[next] : null);
			if (next >= 0 && next != scrolledTo && !pictureShown)
			{
				// Keep the stop just made in sight above the next one.
				scrolledTo = next;
				View target = rows[Math.Max(0, next - 1)].Root;
				listScroll.Post(() => listScroll.SmoothScrollTo(0, target.Top));
			}
		}

		/// <summary>The map or gradient tab: the line (built once) and where the trains are now.</summary>
		private void RefreshIllustration()
		{
			(RouteGeometry Geometry, RouteLive Live) now;
			try
			{
				now = route();
			}
			catch (Exception)
			{
				return;
			}

			if (now.Geometry == null || now.Live == null)
			{
				return;
			}

			if (tab == Tab.Map)
			{
				mapView.Set(now.Geometry, now.Live);
			}
			else
			{
				gradientView.Set(now.Geometry, now.Live);
			}
		}

		private void UpdateNext(TimetableRow row)
		{
			if (row == null)
			{
				nextName.Text = Menu.T("android", "timetable_end", "End of the line");
				nextDetail.Text = string.Empty;
				return;
			}

			nextName.Text = (row.State == TimetableRowState.AtStation ? "● " : "→ ") + row.Name;
			List<string> parts = new List<string>();
			if (row.State == TimetableRowState.AtStation)
			{
				if (row.Departure != null)
				{
					parts.Add(Menu.T("android", "timetable_departs", "departs") + " " + row.Departure + Relative(row.ToDeparture, departing: true));
				}
				else
				{
					parts.Add(Menu.T("android", "timetable_stopped", "stopped"));
				}
			}
			else
			{
				parts.Add(Distance(row.Distance));
				if (row.Arrival != null)
				{
					parts.Add(Menu.T("android", "timetable_arrives", "arr") + " " + row.Arrival + Relative(row.ToArrival, departing: false));
				}
			}

			nextDetail.Text = string.Join("  ·  ", parts);
			bool late = row.State == TimetableRowState.AtStation ? row.ToDeparture < 0.0 : row.ToArrival < 0.0;
			nextDetail.SetTextColor(late ? Late : Muted);
		}

		/// <summary>" (in 2:15)", or " (late 0:45)" once the time has gone.</summary>
		private static string Relative(double seconds, bool departing)
		{
			if (double.IsNaN(seconds))
			{
				return string.Empty;
			}

			// Past midnight the timetable's times wrap; take the nearer way round.
			seconds %= 86400.0;
			if (seconds > 43200.0)
			{
				seconds -= 86400.0;
			}
			else if (seconds < -43200.0)
			{
				seconds += 86400.0;
			}

			TimeSpan span = TimeSpan.FromSeconds(Math.Abs(Math.Round(seconds)));
			string text = span.TotalHours >= 1.0 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
			if (seconds >= 0.0)
			{
				return " (" + string.Format(Menu.T("android", "timetable_in", "in {0}"), text) + ")";
			}

			return departing
				? " (" + string.Format(Menu.T("android", "timetable_overdue", "overdue {0}"), text) + ")"
				: " (" + string.Format(Menu.T("android", "timetable_late", "late {0}"), text) + ")";
		}

		private static string Distance(double metres)
		{
			return Math.Abs(metres) < 1000.0 ? metres.ToString("0") + " m" : (metres / 1000.0).ToString("0.00") + " km";
		}

		// --- rows ---

		private sealed class RowViews
		{
			public LinearLayout Root;
			public Marker Marker;
			public TextView Name;
			public TextView Detail;
			public TextView Arrival;
			public TextView Departure;
		}

		/// <summary>Builds the row views once per station list (which changes only with a new route).</summary>
		private void BuildRows(IReadOnlyList<TimetableRow> stations)
		{
			string key = stations.Count.ToString();
			foreach (TimetableRow station in stations)
			{
				key += "|" + station.Name;
			}

			if (key == rowsKey)
			{
				return;
			}

			rowsKey = key;
			rows.Clear();
			list.RemoveAllViews();
			foreach (TimetableRow _ in stations)
			{
				RowViews row = new RowViews { Root = new LinearLayout(Context) { Orientation = Orientation.Horizontal } };
				row.Root.SetMinimumHeight(Dp(36));
				row.Marker = new Marker(Context, Dp(1));
				row.Root.AddView(row.Marker, new LayoutParams(Dp(22), ViewGroup.LayoutParams.MatchParent));
				LinearLayout names = new LinearLayout(Context) { Orientation = Orientation.Vertical };
				names.SetGravity(GravityFlags.CenterVertical);
				names.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));
				row.Name = Text(string.Empty, 14.0f, Color.White);
				row.Name.SetSingleLine(true);
				row.Name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
				row.Detail = Text(string.Empty, 11.0f, Muted);
				names.AddView(row.Name);
				names.AddView(row.Detail);
				row.Root.AddView(names, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f) { Gravity = GravityFlags.CenterVertical });
				row.Arrival = TimeColumn(Text(string.Empty, 12.5f, Color.White));
				row.Departure = TimeColumn(Text(string.Empty, 12.5f, Color.White));
				row.Root.AddView(row.Arrival, new LayoutParams(Dp(TimeWidth), ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.CenterVertical });
				row.Root.AddView(row.Departure, new LayoutParams(Dp(TimeWidth), ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.CenterVertical });
				list.AddView(row.Root, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
				rows.Add(row);
			}

			scrolledTo = -1;
		}

		private void UpdateRow(RowViews views, TimetableRow row, bool first, bool last)
		{
			bool current = row.State == TimetableRowState.Next || row.State == TimetableRowState.AtStation;
			bool done = row.State == TimetableRowState.Done;
			views.Marker.Set(row.State, row.Pass, first, last);
			views.Name.Text = row.Name;
			views.Name.SetTypeface(null, current ? TypefaceStyle.Bold : TypefaceStyle.Normal);
			Color text = done ? Muted : Color.White;
			views.Name.SetTextColor(current ? Color.White : text);

			string detail;
			if (row.Pass)
			{
				detail = Menu.T("android", "timetable_pass", "pass");
			}
			else if (done)
			{
				detail = row.Terminal ? Menu.T("android", "timetable_terminal", "terminus") : string.Empty;
			}
			else if (row.State == TimetableRowState.AtStation)
			{
				detail = row.Departure != null
					? Menu.T("android", "timetable_departs", "departs") + Relative(row.ToDeparture, departing: true)
					: Menu.T("android", "timetable_stopped", "stopped");
			}
			else
			{
				detail = Distance(row.Distance) + (current && row.Arrival != null ? "  ·" + Relative(row.ToArrival, departing: false) : string.Empty) +
				         (row.Terminal ? "  ·  " + Menu.T("android", "timetable_terminal", "terminus") : string.Empty);
			}

			bool late = row.State == TimetableRowState.AtStation ? row.ToDeparture < 0.0 : current && row.ToArrival < 0.0;
			views.Detail.SetTextColor(late ? Late : current ? Color.Argb(210, 255, 255, 255) : Muted);
			views.Detail.Text = detail;
			views.Detail.Visibility = detail.Length != 0 ? ViewStates.Visible : ViewStates.Gone;
			views.Arrival.Text = row.Arrival != null ? row.Arrival : row.Pass ? string.Empty : "—";
			views.Departure.Text = row.Departure != null ? row.Departure : string.Empty;
			views.Arrival.SetTextColor(text);
			views.Departure.SetTextColor(text);
			views.Root.SetBackgroundColor(current ? Color.Argb(40, Current.R, Current.G, Current.B) : Color.Transparent);
		}

		/// <summary>The timeline beside each station: a line through, and a dot for the station.</summary>
		private sealed class Marker : View
		{
			private readonly Paint paint = new Paint(PaintFlags.AntiAlias);
			private readonly float stroke;
			private TimetableRowState state;
			private bool pass, first, last;

			public Marker(Context context, float stroke) : base(context)
			{
				this.stroke = stroke;
			}

			public void Set(TimetableRowState state, bool pass, bool first, bool last)
			{
				if (state == this.state && pass == this.pass && first == this.first && last == this.last)
				{
					return;
				}

				this.state = state;
				this.pass = pass;
				this.first = first;
				this.last = last;
				Invalidate();
			}

			protected override void OnDraw(Canvas canvas)
			{
				float x = Width / 2.0f, y = Height / 2.0f;
				bool reached = state != TimetableRowState.Ahead;
				paint.SetStyle(Paint.Style.Stroke);
				paint.StrokeWidth = stroke * 2.0f;
				// The line above is travelled once the train reaches this station; below, once past it.
				if (!first)
				{
					paint.Color = reached ? Current : Line;
					canvas.DrawLine(x, 0, x, y, paint);
				}

				if (!last)
				{
					paint.Color = state == TimetableRowState.Done ? Current : Line;
					canvas.DrawLine(x, y, x, Height, paint);
				}

				float radius = stroke * (pass ? 3.0f : 5.0f);
				switch (state)
				{
					case TimetableRowState.AtStation:
						paint.SetStyle(Paint.Style.Fill);
						paint.Color = Color.Argb(70, Here.R, Here.G, Here.B);
						canvas.DrawCircle(x, y, radius * 2.0f, paint);
						paint.Color = Here;
						canvas.DrawCircle(x, y, radius * 1.3f, paint);
						break;
					case TimetableRowState.Next:
						paint.SetStyle(Paint.Style.Fill);
						paint.Color = Color.Argb(70, Current.R, Current.G, Current.B);
						canvas.DrawCircle(x, y, radius * 2.0f, paint);
						paint.SetStyle(Paint.Style.Stroke);
						paint.Color = Current;
						canvas.DrawCircle(x, y, radius * 1.2f, paint);
						break;
					case TimetableRowState.Done:
						paint.SetStyle(Paint.Style.Fill);
						paint.Color = Current;
						canvas.DrawCircle(x, y, radius, paint);
						break;
					default:
						paint.SetStyle(Paint.Style.Fill);
						paint.Color = Card;
						canvas.DrawCircle(x, y, radius, paint);
						paint.SetStyle(Paint.Style.Stroke);
						paint.Color = Color.White;
						canvas.DrawCircle(x, y, radius, paint);
						break;
				}
			}
		}

		// --- helpers ---

		private static Bitmap LoadPicture(string path)
		{
			try
			{
				// Timetable pictures are small, but keep a huge one from exhausting the heap.
				BitmapFactory.Options bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
				BitmapFactory.DecodeFile(path, bounds);
				int sample = 1;
				while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > 2048)
				{
					sample *= 2;
				}

				return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
			}
			catch (Exception)
			{
				return null;
			}
		}

		private TextView Text(string text, float size, Color color, bool bold = false, bool mono = false)
		{
			TextView view = new TextView(Context) { Text = text, TextSize = size };
			view.SetTextColor(color);
			if (bold || mono)
			{
				view.SetTypeface(mono ? Typeface.Monospace : Typeface.Default, bold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
			}

			return view;
		}

		private const int TimeWidth = 72;

		/// <summary>A right-aligned time column with even-width digits, so the times line up.</summary>
		private static TextView TimeColumn(TextView view)
		{
			view.Gravity = GravityFlags.End;
			view.FontFeatureSettings = "tnum";
			return view;
		}

		private Button TabButton(string label, Action action)
		{
			Button button = new Button(Context) { Text = label, TextSize = 13.0f, Focusable = false };
			button.SetAllCaps(false);
			button.SetMinWidth(0);
			button.SetMinimumWidth(0);
			button.SetMinHeight(Dp(28));
			button.SetMinimumHeight(Dp(28));
			button.SetPadding(Dp(10), 0, Dp(10), 0);
			GradientDrawable background = new GradientDrawable();
			background.SetCornerRadius(Dp(14));
			button.Background = background;
			button.StateListAnimator = null;
			button.Click += (sender, e) => action();
			button.LayoutParameters = new LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(28)) { RightMargin = Dp(2) };
			return button;
		}

		private Button HeaderButton(string label, Action action)
		{
			Button button = new Button(Context) { Text = label, TextSize = 13.0f, Focusable = false };
			button.SetTextColor(Color.White);
			button.SetMinWidth(Dp(38));
			button.SetMinimumWidth(Dp(38));
			button.SetMinHeight(Dp(28));
			button.SetMinimumHeight(Dp(28));
			button.SetPadding(0, 0, 0, 0);
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(50, 255, 255, 255));
			background.SetCornerRadius(Dp(14));
			button.Background = background;
			button.StateListAnimator = null;
			button.Click += (sender, e) => action();
			LayoutParams parameters = new LayoutParams(Dp(38), Dp(28)) { LeftMargin = Dp(6) };
			button.LayoutParameters = parameters;
			return button;
		}

		private int Dp(float value) => Ui.Dp(Context, value);
	}
}
