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
	/// The shared look of the touch map and gradient chart: drawn from the track itself rather
	/// than upstream's fixed-size pictures, so they stay sharp at any size and can be zoomed.
	/// Each follows the train until dragged; ◎ follows it again.
	/// </summary>
	public abstract class RouteChart : FrameLayout
	{
		protected static readonly Color Accent = Ui.Accent;
		protected static readonly Color Here = Color.Rgb(80, 200, 120);
		protected static readonly Color Other = Color.Rgb(235, 80, 80);
		protected static readonly Color Muted = Color.Argb(150, 255, 255, 255);
		protected static readonly Color Faint = Color.Argb(45, 255, 255, 255);
		protected static readonly Color Chip = Color.Argb(215, 22, 27, 38);
		protected static readonly Color Limit = Color.Rgb(255, 184, 77);

		protected readonly Paint Paint = new Paint(PaintFlags.AntiAlias);
		protected readonly Paint TextPaint = new Paint(PaintFlags.AntiAlias);
		protected readonly float Density;
		private readonly ScaleGestureDetector scaler;
		private readonly Button followButton;
		private float lastX, lastY;
		private bool dragging;

		protected RouteGeometry Geometry;
		protected RouteLive Live;

		/// <summary>Whether the view keeps the train in sight; dragging turns it off.</summary>
		protected bool Following = true;

		protected RouteChart(Context context) : base(context)
		{
			Density = context.Resources?.DisplayMetrics?.Density ?? 2.0f;
			SetWillNotDraw(false);
			TextPaint.TextSize = 11.5f * Density;
			TextPaint.Color = Color.White;
			scaler = new ScaleGestureDetector(context, new ScaleListener(this));

			LinearLayout buttons = new LinearLayout(context) { Orientation = Orientation.Horizontal };
			followButton = Small("◎", () =>
			{
				Following = true;
				OnFollow();
				Invalidate();
			});
			buttons.AddView(followButton);
			foreach ((string label, Action action) in ExtraButtons())
			{
				buttons.AddView(Small(label, action));
			}

			AddView(buttons, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Right)
			{
				TopMargin = (int)(4 * Density),
				RightMargin = (int)(4 * Density)
			});
		}

		/// <summary>Buttons beside ◎.</summary>
		protected virtual IEnumerable<(string Label, Action Action)> ExtraButtons()
		{
			yield break;
		}

		/// <summary>Updates what is drawn. Call on the UI thread.</summary>
		public void Set(RouteGeometry geometry, RouteLive live)
		{
			bool first = Geometry == null && geometry != null;
			Geometry = geometry;
			Live = live;
			if (first)
			{
				OnFirstData();
			}

			followButton.Alpha = Following ? 0.45f : 1.0f;
			Invalidate();
		}

		protected virtual void OnFirstData()
		{
		}

		protected virtual void OnFollow()
		{
		}

		/// <summary>A drag by (dx, dy) pixels.</summary>
		protected abstract void Pan(float dx, float dy);

		/// <summary>A pinch by a factor about a point.</summary>
		protected abstract void Zoom(float factor, float focusX, float focusY);

		protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
		{
			// As tall as the card allows: the chart is the tab's whole content.
			int width = MeasureSpec.GetSize(widthMeasureSpec);
			int height = MeasureSpec.GetMode(heightMeasureSpec) == MeasureSpecMode.Unspecified
				? (int)(width * 0.6f)
				: MeasureSpec.GetSize(heightMeasureSpec);
			base.OnMeasure(MeasureSpec.MakeMeasureSpec(width, MeasureSpecMode.Exactly),
				MeasureSpec.MakeMeasureSpec(Math.Max(height, (int)(100 * Density)), MeasureSpecMode.Exactly));
		}

		public override bool OnTouchEvent(MotionEvent e)
		{
			// The card's scrolling and the camera behind must leave the chart's gestures alone.
			Parent?.RequestDisallowInterceptTouchEvent(true);
			scaler.OnTouchEvent(e);
			switch (e.ActionMasked)
			{
				case MotionEventActions.Down:
					lastX = e.GetX();
					lastY = e.GetY();
					dragging = false;
					break;
				case MotionEventActions.PointerDown:
				case MotionEventActions.PointerUp:
					dragging = false;
					lastX = float.NaN;
					break;
				case MotionEventActions.Move:
					if (e.PointerCount == 1 && !scaler.IsInProgress)
					{
						if (float.IsNaN(lastX))
						{
							lastX = e.GetX();
							lastY = e.GetY();
							break;
						}

						float dx = e.GetX() - lastX, dy = e.GetY() - lastY;
						if (dragging || Math.Abs(dx) + Math.Abs(dy) > 6 * Density)
						{
							dragging = true;
							Following = false;
							Pan(dx, dy);
							lastX = e.GetX();
							lastY = e.GetY();
							Invalidate();
						}
					}

					break;
			}

			return true;
		}

		private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
		{
			private readonly RouteChart chart;

			public ScaleListener(RouteChart chart)
			{
				this.chart = chart;
			}

			public override bool OnScale(ScaleGestureDetector detector)
			{
				chart.Zoom(detector.ScaleFactor, detector.FocusX, detector.FocusY);
				chart.Invalidate();
				return true;
			}
		}

		/// <summary>A label in a rounded chip; returns its bounds.</summary>
		protected RectF DrawChip(Canvas canvas, string text, float x, float y, Color background, Color textColor, bool bold = false)
		{
			TextPaint.FakeBoldText = bold;
			float width = TextPaint.MeasureText(text);
			float pad = 5 * Density, height = TextPaint.TextSize + 5 * Density;
			RectF bounds = new RectF(x, y - height / 2, x + width + 2 * pad, y + height / 2);
			Paint.SetStyle(Paint.Style.Fill);
			Paint.Color = background;
			canvas.DrawRoundRect(bounds, height / 2, height / 2, Paint);
			TextPaint.Color = textColor;
			canvas.DrawText(text, x + pad, y + TextPaint.TextSize * 0.36f, TextPaint);
			TextPaint.FakeBoldText = false;
			return bounds;
		}

		protected RectF MeasureChip(string text, float x, float y)
		{
			float width = TextPaint.MeasureText(text);
			float pad = 5 * Density, height = TextPaint.TextSize + 5 * Density;
			return new RectF(x, y - height / 2, x + width + 2 * pad, y + height / 2);
		}

		protected void Dot(Canvas canvas, float x, float y, float radius, Color fill)
		{
			Paint.SetStyle(Paint.Style.Fill);
			Paint.Color = fill;
			canvas.DrawCircle(x, y, radius, Paint);
			Paint.SetStyle(Paint.Style.Stroke);
			Paint.StrokeWidth = Math.Max(1.5f, radius * 0.3f);
			Paint.Color = Color.White;
			canvas.DrawCircle(x, y, radius, Paint);
		}

		protected static bool Overlaps(List<RectF> taken, RectF rect)
		{
			foreach (RectF other in taken)
			{
				if (RectF.Intersects(other, rect))
				{
					return true;
				}
			}

			return false;
		}

		private Button Small(string label, Action action)
		{
			Button button = new Button(Context) { Text = label, TextSize = label.Length > 1 ? 11.0f : 15.0f, Focusable = false };
			button.SetTextColor(Color.White);
			button.SetMinWidth(0);
			button.SetMinimumWidth(0);
			button.SetMinHeight(0);
			button.SetMinimumHeight(0);
			button.SetPadding(0, 0, 0, 0);
			GradientDrawable background = new GradientDrawable();
			background.SetColor(Color.Argb(170, 40, 46, 60));
			background.SetCornerRadius(16 * Density);
			button.Background = background;
			button.StateListAnimator = null;
			button.Click += (sender, e) => action();
			button.LayoutParameters = new LinearLayout.LayoutParams((int)(36 * Density), (int)(32 * Density)) { LeftMargin = (int)(4 * Density) };
			return button;
		}

		protected static string Km(double metres)
		{
			// Rounded away from "-0.0" just behind the start.
			double km = Math.Abs(metres) < 50.0 ? 0.0 : metres / 1000.0;
			return km.ToString(Math.Abs(metres) < 10000 ? "0.0" : "0") + " km";
		}
	}

	/// <summary>
	/// The line in plan: the track drawn as a route line (travelled part in the accent colour),
	/// stations as stops or passing points with their names in chips where they fit, and the
	/// trains - the player's larger and green. Pinch to zoom, drag to look around, ⤢ for the whole
	/// line.
	/// </summary>
	public sealed class RouteMapView : RouteChart
	{
		// World (x, z) at the centre of the view, and pixels per metre.
		private double centreX, centreZ, scale = -1;
		private double fitScale;

		public RouteMapView(Context context) : base(context)
		{
		}

		protected override IEnumerable<(string Label, Action Action)> ExtraButtons()
		{
			yield return ("ALL", () =>
			{
				Following = false;
				FitAll();
				Invalidate();
			});
		}

		protected override void OnFirstData()
		{
			scale = -1;
		}

		protected override void OnFollow()
		{
			// Back to the train at a working zoom: about five kilometres across.
			if (Width > 0)
			{
				scale = Math.Max(scale, Width / 5000.0);
			}
		}

		private void FitAll()
		{
			if (Geometry == null || Width == 0 || Height == 0)
			{
				return;
			}

			double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
			for (int i = 0; i < Geometry.X.Length; i++)
			{
				x0 = Math.Min(x0, Geometry.X[i]);
				x1 = Math.Max(x1, Geometry.X[i]);
				z0 = Math.Min(z0, Geometry.Z[i]);
				z1 = Math.Max(z1, Geometry.Z[i]);
			}

			centreX = (x0 + x1) / 2;
			centreZ = (z0 + z1) / 2;
			double margin = 28 * Density;
			fitScale = Math.Min((Width - 2 * margin) / Math.Max(1, x1 - x0), (Height - 2 * margin) / Math.Max(1, z1 - z0));
			scale = fitScale;
		}

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

			// Keep the point under the fingers where it is.
			double wx = centreX + (focusX - Width / 2.0) / scale, wz = centreZ - (focusY - Height / 2.0) / scale;
			double next = Math.Max(fitScale * 0.5, Math.Min(scale * factor, 2.0));
			if (Following)
			{
				scale = next;
				return;
			}

			centreX = wx - (focusX - Width / 2.0) / next;
			centreZ = wz + (focusY - Height / 2.0) / next;
			scale = next;
		}

		protected override void OnDraw(Canvas canvas)
		{
			base.OnDraw(canvas);
			if (Geometry == null || Live == null || Width == 0)
			{
				return;
			}

			if (scale <= 0)
			{
				FitAll();
				OnFollow();
			}

			if (Following)
			{
				centreX = Live.X;
				centreZ = Live.Z;
			}

			float cx = Width / 2.0f, cy = Height / 2.0f;
			float Sx(double x) => cx + (float)((x - centreX) * scale);
			float Sy(double z) => cy - (float)((z - centreZ) * scale);

			// The line: casing, then the part ahead, then the part travelled over it.
			double[] px = Geometry.X, pz = Geometry.Z, pos = Geometry.Position;
			using (Path ahead = new Path())
			using (Path behind = new Path())
			{
				bool startedAhead = false, startedBehind = false;
				for (int i = 0; i < px.Length; i++)
				{
					float x = Sx(px[i]), y = Sy(pz[i]);
					if (pos[i] <= Live.Position)
					{
						if (!startedBehind)
						{
							behind.MoveTo(x, y);
							startedBehind = true;
						}
						else
						{
							behind.LineTo(x, y);
						}
					}

					if (pos[i] >= Live.Position || (i + 1 < px.Length && pos[i + 1] > Live.Position))
					{
						if (!startedAhead)
						{
							ahead.MoveTo(x, y);
							startedAhead = true;
						}
						else
						{
							ahead.LineTo(x, y);
						}
					}
				}

				if (startedBehind)
				{
					behind.LineTo(Sx(Live.X), Sy(Live.Z));
				}

				Paint.SetStyle(Paint.Style.Stroke);
				Paint.StrokeCap = Paint.Cap.Round;
				Paint.StrokeJoin = Paint.Join.Round;
				Paint.StrokeWidth = 7 * Density;
				Paint.Color = Color.Argb(160, 0, 0, 0);
				canvas.DrawPath(ahead, Paint);
				canvas.DrawPath(behind, Paint);
				Paint.StrokeWidth = 4 * Density;
				Paint.Color = Color.Argb(210, 225, 230, 240);
				canvas.DrawPath(ahead, Paint);
				Paint.Color = Accent;
				canvas.DrawPath(behind, Paint);
			}

			// Stations: dots, then names where they fit (the next stop and stops before passing points).
			List<RectF> taken = new List<RectF>();
			foreach (RouteStationMark station in Geometry.Stations)
			{
				float x = Sx(station.X), y = Sy(station.Z);
				bool next = station.Index == Live.NextStation;
				bool passed = station.Position < Live.Position - 1;
				if (station.Stops)
				{
					Dot(canvas, x, y, (next ? 6.5f : 5f) * Density, next ? Accent : passed ? Accent : Chip);
				}
				else
				{
					Paint.SetStyle(Paint.Style.Fill);
					Paint.Color = Color.White;
					canvas.DrawCircle(x, y, 2.5f * Density, Paint);
				}

				taken.Add(new RectF(x - 6 * Density, y - 6 * Density, x + 6 * Density, y + 6 * Density));
			}

			IEnumerable<RouteStationMark> ordered = Ordered(Geometry.Stations, Live.NextStation);
			foreach (RouteStationMark station in ordered)
			{
				float x = Sx(station.X), y = Sy(station.Z);
				if (x < -50 * Density || x > Width + 50 * Density || y < -20 * Density || y > Height + 20 * Density)
				{
					continue;
				}

				bool next = station.Index == Live.NextStation;
				string name = station.Name;
				RectF right = MeasureChip(name, x + 9 * Density, y);
				RectF left = MeasureChip(name, 0, y);
				left.Offset(x - 9 * Density - left.Width() - left.Left, 0);
				RectF place = !Overlaps(taken, right) ? right : !Overlaps(taken, left) ? left : null;
				if (place == null && !next)
				{
					continue;
				}

				place ??= right;
				DrawChip(canvas, name, place.Left, place.CenterY(), next ? Accent : Chip, station.Stops ? Color.White : Muted, next);
				taken.Add(place);
			}

			// Trains: the others, then the player's on top.
			foreach ((double X, double Z, bool Player) train in Live.Trains)
			{
				if (!train.Player)
				{
					Dot(canvas, Sx(train.X), Sy(train.Z), 5 * Density, Other);
				}
			}

			float tx = Sx(Live.X), ty = Sy(Live.Z);
			Paint.SetStyle(Paint.Style.Fill);
			Paint.Color = Color.Argb(70, Here.R, Here.G, Here.B);
			canvas.DrawCircle(tx, ty, 14 * Density, Paint);
			Dot(canvas, tx, ty, 7 * Density, Here);

			// Scale bar, bottom left.
			double metres = NiceStep(90 * Density / scale);
			float bar = (float)(metres * scale), bx = 10 * Density, by = Height - 12 * Density;
			Paint.SetStyle(Paint.Style.Stroke);
			Paint.StrokeWidth = 2 * Density;
			Paint.Color = Color.White;
			canvas.DrawLine(bx, by, bx + bar, by, Paint);
			canvas.DrawLine(bx, by - 4 * Density, bx, by, Paint);
			canvas.DrawLine(bx + bar, by - 4 * Density, bx + bar, by, Paint);
			TextPaint.Color = Muted;
			canvas.DrawText(metres >= 1000 ? (metres / 1000).ToString("0.#") + " km" : metres.ToString("0") + " m", bx + bar + 6 * Density, by + 4 * Density, TextPaint);
		}

		/// <summary>Names are placed in this order, so the important ones win the space.</summary>
		private static IEnumerable<RouteStationMark> Ordered(IReadOnlyList<RouteStationMark> stations, int next)
		{
			foreach (RouteStationMark station in stations)
			{
				if (station.Index == next)
				{
					yield return station;
				}
			}

			foreach (RouteStationMark station in stations)
			{
				if (station.Index != next && station.Stops)
				{
					yield return station;
				}
			}

			foreach (RouteStationMark station in stations)
			{
				if (!station.Stops)
				{
					yield return station;
				}
			}
		}

		internal static double NiceStep(double raw)
		{
			double power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-6))));
			double unit = raw / power;
			return (unit < 1.5 ? 1 : unit < 3.5 ? 2 : unit < 7.5 ? 5 : 10) * power;
		}
	}

	/// <summary>
	/// The line in elevation: a window of track around the train, the profile filled, the
	/// gradients labelled in per mille where they change, stations marked with their names, the
	/// speed limits as a step line along the bottom, and the train's position with the gradient
	/// and height under it. Drag to look ahead or back, pinch to change the distance shown.
	/// </summary>
	public sealed class GradientChartView : RouteChart
	{
		// The window: its start (track position, when not following) and length, in metres.
		private double start, span = 4000;

		public GradientChartView(Context context) : base(context)
		{
		}

		protected override void Pan(float dx, float dy)
		{
			start -= dx * span / Math.Max(1, Width);
		}

		protected override void Zoom(float factor, float focusX, float focusY)
		{
			double focus = start + focusX / Math.Max(1, Width) * span;
			span = Math.Max(500, Math.Min(span / factor, 60000));
			start = focus - focusX / Math.Max(1, Width) * span;
		}

		protected override void OnDraw(Canvas canvas)
		{
			base.OnDraw(canvas);
			if (Geometry == null || Live == null || Width == 0)
			{
				return;
			}

			double[] pos = Geometry.Position, y = Geometry.Y;
			if (Following)
			{
				// A quarter behind the train, three quarters ahead.
				start = Live.Position - span * 0.25;
			}

			double end = start + span;
			float top = 34 * Density, bottom = Height - 34 * Density;
			float Sx(double p) => (float)((p - start) / span * Width);

			// Height range over what is shown (and a little around), at least 20 m.
			int i0 = Math.Max(0, Array.BinarySearch(pos, start) is int a && a < 0 ? ~a - 1 : a);
			int i1 = Math.Min(pos.Length - 1, Array.BinarySearch(pos, end) is int b && b < 0 ? ~b : b);
			double y0 = double.MaxValue, y1 = double.MinValue;
			for (int i = i0; i <= i1; i++)
			{
				y0 = Math.Min(y0, y[i]);
				y1 = Math.Max(y1, y[i]);
			}

			if (y0 > y1)
			{
				y0 = y1 = Live.Y;
			}

			double middle = (y0 + y1) / 2, range = Math.Max(20, (y1 - y0) * 1.25);
			y0 = middle - range / 2;
			y1 = middle + range / 2;
			float Sy(double h) => bottom - (float)((h - y0) / (y1 - y0) * (bottom - top));

			// Grid: distance ticks along the bottom, height lines behind.
			double step = RouteMapView.NiceStep(span / 5);
			TextPaint.Color = Muted;
			TextPaint.TextSize = 10.5f * Density;
			Paint.SetStyle(Paint.Style.Stroke);
			Paint.StrokeWidth = 1;
			Paint.Color = Faint;
			for (double p = Math.Ceiling(start / step) * step; p <= end; p += step)
			{
				float x = Sx(p);
				canvas.DrawLine(x, top, x, bottom, Paint);
				string label = Km(p);
				if (x + 3 * Density + TextPaint.MeasureText(label) < Width)
				{
					canvas.DrawText(label, x + 3 * Density, Height - 5 * Density, TextPaint);
				}
			}

			// The profile, filled.
			using (Path profile = new Path())
			{
				profile.MoveTo(Sx(pos[i0]), bottom);
				for (int i = i0; i <= i1; i++)
				{
					profile.LineTo(Sx(pos[i]), Sy(y[i]));
				}

				profile.LineTo(Sx(pos[i1]), bottom);
				profile.Close();
				Paint.SetStyle(Paint.Style.Fill);
				// The shader's colours are drawn at the paint's alpha: full, not the grid's.
				Paint.Color = Color.White;
				using (LinearGradient fill = new LinearGradient(0, top, 0, bottom, Color.Argb(150, Accent.R, Accent.G, Accent.B), Color.Argb(20, Accent.R, Accent.G, Accent.B), Shader.TileMode.Clamp))
				{
					Paint.SetShader(fill);
					canvas.DrawPath(profile, Paint);
					Paint.SetShader(null);
				}
			}

			Paint.SetStyle(Paint.Style.Stroke);
			Paint.StrokeWidth = 2.5f * Density;
			Paint.Color = Color.White;
			for (int i = i0; i < i1; i++)
			{
				canvas.DrawLine(Sx(pos[i]), Sy(y[i]), Sx(pos[i + 1]), Sy(y[i + 1]), Paint);
			}

			// Gradients: a label over each stretch of one pitch that is wide enough to hold it.
			TextPaint.TextSize = 10.5f * Density;
			int run = i0;
			for (int i = i0 + 1; i <= i1 + 1; i++)
			{
				if (i <= i1 && Math.Abs(Geometry.Pitch[i] - Geometry.Pitch[run]) < 1e-6)
				{
					continue;
				}

				int last = Math.Min(i, i1);
				float x0 = Sx(pos[run]), x1 = Sx(pos[last]);
				string text = PerMille(Geometry.Pitch[run]);
				if (x1 - x0 > TextPaint.MeasureText(text) + 12 * Density)
				{
					float xm = (x0 + x1) / 2;
					int mid = (run + last) / 2;
					TextPaint.Color = Color.White;
					float ty = Math.Max(top + 12 * Density, Sy(y[mid]) - 8 * Density);
					canvas.DrawText(text, xm - TextPaint.MeasureText(text) / 2, ty, TextPaint);
				}

				run = i;
			}

			// Speed limits: a step line along the bottom of the chart, with its values.
			if (Geometry.Limits.Count > 0)
			{
				double current = double.PositiveInfinity;
				foreach (RouteLimit limit in Geometry.Limits)
				{
					if (limit.Position <= start)
					{
						current = limit.Kmh;
					}
				}

				float band = bottom - 4 * Density;
				float lx = 0;
				Paint.StrokeWidth = 2 * Density;
				Paint.Color = Limit;
				TextPaint.Color = Limit;
				void Segment(float from, float to, double kmh)
				{
					if (!double.IsInfinity(kmh))
					{
						canvas.DrawLine(from, band, to, band, Paint);
						string text = kmh.ToString("0");
						if (to - from > TextPaint.MeasureText(text) + 6 * Density)
						{
							canvas.DrawText(text, from + 3 * Density, band - 4 * Density, TextPaint);
						}
					}
				}

				foreach (RouteLimit limit in Geometry.Limits)
				{
					if (limit.Position <= start || limit.Position > end)
					{
						continue;
					}

					float x = Sx(limit.Position);
					Segment(lx, x, current);
					current = limit.Kmh;
					lx = x;
				}

				Segment(lx, Width, current);
			}

			// Stations: a line down to the profile and the name at the top, where it fits.
			List<RectF> taken = new List<RectF>();
			TextPaint.TextSize = 11.5f * Density;
			foreach (RouteStationMark station in Geometry.Stations)
			{
				if (station.Position < start || station.Position > end)
				{
					continue;
				}

				float x = Sx(station.Position);
				bool next = station.Index == Live.NextStation;
				Paint.StrokeWidth = (next ? 2 : 1) * Density;
				Paint.Color = next ? Accent : Muted;
				canvas.DrawLine(x, top - 4 * Density, x, Sy(station.Y), Paint);
				RectF chip = MeasureChip(station.Name, x - 4 * Density, 16 * Density);
				if (next || !Overlaps(taken, chip))
				{
					DrawChip(canvas, station.Name, chip.Left, chip.CenterY(), next ? Accent : Chip, station.Stops ? Color.White : Muted, next);
					taken.Add(chip);
				}
			}

			// The train: a line, a dot on the profile, and the gradient and height under it.
			float trainX = Sx(Live.Position);
			int k = Array.BinarySearch(pos, Live.Position);
			k = Math.Max(0, Math.Min(pos.Length - 1, k < 0 ? ~k - 1 : k));
			if (trainX >= 0 && trainX <= Width)
			{
				Paint.StrokeWidth = 2 * Density;
				Paint.Color = Here;
				canvas.DrawLine(trainX, top, trainX, bottom, Paint);
				Dot(canvas, trainX, Sy(Live.Y), 6 * Density, Here);
			}

			string status = PerMille(Geometry.Pitch[k]) + "   " + (Math.Round(Live.Y) + 0.0).ToString("0") + " m";
			RectF box = MeasureChip(status, 0, 0);
			float sx = Math.Max(4 * Density, Math.Min(trainX + 8 * Density, Width - box.Width() - 84 * Density));
			DrawChip(canvas, status, sx, top + 14 * Density, Color.Argb(230, Here.R / 3, Here.G / 3, Here.B / 3), Color.White, true);
		}

		/// <summary>A gradient as the drivers' convention writes it: "+12.5‰", "−5‰", "level".</summary>
		private static string PerMille(double pitch)
		{
			double value = Math.Round(pitch * 1000.0, 1);
			if (Math.Abs(value) < 0.05)
			{
				return "level";
			}

			return (value > 0 ? "↗ " : "↘ ") + Math.Abs(value).ToString("0.#") + "‰";
		}
	}
}
