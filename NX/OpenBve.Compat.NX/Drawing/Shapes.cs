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

using System.Collections.Generic;

// ReSharper disable once CheckNamespace
namespace System.Drawing.Drawing2D
{
	/// <summary>Antialiasing modes, applied to the rasteriser's antialiasing.</summary>
	public enum SmoothingMode
	{
		Default = 0,
		HighSpeed = 1,
		HighQuality = 2,
		None = 3,
		AntiAlias = 4
	}

	/// <summary>Whether a new transform is combined before or after the existing one.</summary>
	public enum MatrixOrder
	{
		Prepend = 0,
		Append = 1
	}

	/// <summary>Dash styles for pens.</summary>
	public enum DashStyle
	{
		Solid = 0,
		Dash = 1,
		Dot = 2,
		DashDot = 3,
		DashDotDot = 4,
		Custom = 5
	}
}

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>A pen, to stroke lines and outlines.</summary>
	public sealed class Pen : IDisposable
	{
		/// <summary>The pen's colour.</summary>
		public Color Color { get; set; }

		/// <summary>The stroke width.</summary>
		public float Width { get; set; }

		/// <summary>The dash style: recorded but not applied, as on Android.</summary>
		public Drawing2D.DashStyle DashStyle { get; set; } = Drawing2D.DashStyle.Solid;

		/// <summary>Creates a pen one unit wide.</summary>
		public Pen(Color color) : this(color, 1.0f)
		{
		}

		/// <summary>Creates a pen.</summary>
		public Pen(Color color, float width)
		{
			Color = color;
			Width = width;
		}

		/// <summary>Creates a pen from a brush.</summary>
		public Pen(Brush brush) : this(brush, 1.0f)
		{
		}

		/// <summary>Creates a pen from a brush.</summary>
		public Pen(Brush brush, float width)
		{
			Color = brush is SolidBrush solid ? solid.Color : Color.Black;
			Width = width;
		}

		/// <inheritdoc />
		public void Dispose()
		{
		}
	}

	/// <summary>Pens for the standard colours.</summary>
	public static class Pens
	{
		/// <summary>A black pen.</summary>
		public static Pen Black { get; } = new Pen(Color.Black);

		/// <summary>A white pen.</summary>
		public static Pen White { get; } = new Pen(Color.White);

		/// <summary>A red pen.</summary>
		public static Pen Red { get; } = new Pen(Color.Red);

		/// <summary>A dark red pen.</summary>
		public static Pen DarkRed { get; } = new Pen(Color.DarkRed);

		/// <summary>A blue pen.</summary>
		public static Pen Blue { get; } = new Pen(Color.Blue);

		/// <summary>A grey pen.</summary>
		public static Pen Gray { get; } = new Pen(Color.Gray);

		/// <summary>A light grey pen.</summary>
		public static Pen LightGray { get; } = new Pen(Color.LightGray);
	}

	/// <summary>Lines, outlines, shapes and transforms, drawn by the rasteriser (Raster.cs).</summary>
	public sealed partial class Graphics
	{
		private Drawing2D.SmoothingMode smoothing = Drawing2D.SmoothingMode.Default;

		/// <summary>The antialiasing mode applied to subsequent drawing.</summary>
		public Drawing2D.SmoothingMode SmoothingMode
		{
			get => smoothing;
			set => smoothing = value;
		}

		private bool AntiAlias => smoothing == Drawing2D.SmoothingMode.AntiAlias || smoothing == Drawing2D.SmoothingMode.HighQuality;

		private List<PointF> MapAll(IEnumerable<PointF> points)
		{
			List<PointF> mapped = new List<PointF>();
			foreach (PointF p in points)
			{
				mapped.Add(transform.Map(p.X, p.Y));
			}

			return mapped;
		}

		// A pen's width scales with the transform, as with GDI+ and Skia.
		private float DeviceWidth(Pen pen)
		{
			return pen.Width * (float)Math.Sqrt(Math.Abs(transform.M11 * transform.M22 - transform.M12 * transform.M21));
		}

		private void StrokePolyline(Pen pen, IEnumerable<PointF> points, bool closed)
		{
			List<PointF> mapped = MapAll(points);
			if (mapped.Count >= 2)
			{
				Raster.Fill(target, Raster.Stroke(mapped, DeviceWidth(pen), closed), pen.Color, AntiAlias, clip);
			}
		}

		/// <summary>Draws a line.</summary>
		public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
		{
			StrokePolyline(pen, new[] { new PointF(x1, y1), new PointF(x2, y2) }, false);
		}

		/// <summary>Draws a line.</summary>
		public void DrawLine(Pen pen, PointF from, PointF to)
		{
			DrawLine(pen, from.X, from.Y, to.X, to.Y);
		}

		/// <summary>Draws a line.</summary>
		public void DrawLine(Pen pen, Point from, Point to)
		{
			DrawLine(pen, from.X, from.Y, to.X, to.Y);
		}

		/// <summary>Draws the outline of a rectangle.</summary>
		public void DrawRectangle(Pen pen, float x, float y, float width, float height)
		{
			StrokePolyline(pen, new[] { new PointF(x, y), new PointF(x + width, y), new PointF(x + width, y + height), new PointF(x, y + height) }, true);
		}

		/// <summary>Draws the outline of a rectangle.</summary>
		public void DrawRectangle(Pen pen, Rectangle rect)
		{
			DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Draws the outline of an ellipse.</summary>
		public void DrawEllipse(Pen pen, float x, float y, float width, float height)
		{
			StrokePolyline(pen, Raster.Ellipse(x, y, width, height), true);
		}

		/// <summary>Draws the outline of an ellipse.</summary>
		public void DrawEllipse(Pen pen, RectangleF rect)
		{
			DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Draws the outline of an ellipse.</summary>
		public void DrawEllipse(Pen pen, Rectangle rect)
		{
			DrawEllipse(pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Fills an ellipse.</summary>
		public void FillEllipse(Brush brush, float x, float y, float width, float height)
		{
			Raster.Fill(target, new List<PointF[]> { MapAll(Raster.Ellipse(x, y, width, height)).ToArray() }, brush.FillColor, AntiAlias, clip);
		}

		/// <summary>Fills an ellipse.</summary>
		public void FillEllipse(Brush brush, RectangleF rect)
		{
			FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Fills an ellipse.</summary>
		public void FillEllipse(Brush brush, Rectangle rect)
		{
			FillEllipse(brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Fills a polygon.</summary>
		public void FillPolygon(Brush brush, PointF[] points)
		{
			if (points != null && points.Length >= 3)
			{
				Raster.Fill(target, new List<PointF[]> { MapAll(points).ToArray() }, brush.FillColor, AntiAlias, clip);
			}
		}

		/// <summary>Fills a polygon.</summary>
		public void FillPolygon(Brush brush, Point[] points)
		{
			if (points == null)
			{
				return;
			}

			PointF[] converted = new PointF[points.Length];
			for (int i = 0; i < points.Length; i++)
			{
				converted[i] = new PointF(points[i].X, points[i].Y);
			}

			FillPolygon(brush, converted);
		}

		/// <summary>Draws a cardinal spline through the points.</summary>
		public void DrawCurve(Pen pen, PointF[] points)
		{
			if (points != null)
			{
				DrawCurve(pen, points, 0, points.Length - 1);
			}
		}

		/// <summary>
		/// Draws part of a cardinal spline (GDI+'s tension of 0.5): each segment a cubic Bezier with Catmull-Rom control
		/// points from its neighbours, as on Android, flattened into 16 straight pieces.
		/// </summary>
		public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments)
		{
			if (points == null || numberOfSegments < 1 || offset < 0 || offset + numberOfSegments >= points.Length)
			{
				return;
			}

			const float tension = 0.5f / 3.0f;
			List<PointF> line = new List<PointF> { points[offset] };
			for (int i = offset; i < offset + numberOfSegments; i++)
			{
				PointF previous = points[i > 0 ? i - 1 : i];
				PointF start = points[i];
				PointF end = points[i + 1];
				PointF next = points[i + 2 < points.Length ? i + 2 : i + 1];
				PointF c1 = new PointF(start.X + (end.X - previous.X) * tension, start.Y + (end.Y - previous.Y) * tension);
				PointF c2 = new PointF(end.X - (next.X - start.X) * tension, end.Y - (next.Y - start.Y) * tension);
				for (int s = 1; s <= 16; s++)
				{
					float t = s / 16.0f, u = 1 - t;
					line.Add(new PointF(
						u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
						u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y));
				}
			}

			StrokePolyline(pen, line, false);
		}

		/// <summary>Draws a cardinal spline through the points.</summary>
		public void DrawCurve(Pen pen, Point[] points, int offset, int numberOfSegments)
		{
			if (points == null)
			{
				return;
			}

			PointF[] converted = new PointF[points.Length];
			for (int i = 0; i < points.Length; i++)
			{
				converted[i] = new PointF(points[i].X, points[i].Y);
			}

			DrawCurve(pen, converted, offset, numberOfSegments);
		}

		/// <summary>Draws a string with layout options.</summary>
		public void DrawString(string text, Font font, Brush brush, float x, float y, StringFormat format)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			float width = font.Measure(text);
			float left = x;
			if (format != null)
			{
				switch (format.Alignment)
				{
					case StringAlignment.Center:
						left = x - width / 2.0f;
						break;
					case StringAlignment.Far:
						left = x - width;
						break;
				}
			}

			float top = y;
			if (format != null && format.LineAlignment == StringAlignment.Center)
			{
				top = y - font.Height / 2.0f;
			}
			else if (format != null && format.LineAlignment == StringAlignment.Far)
			{
				top = y - font.Height;
			}

			DrawString(text, font, brush, left, top);
		}

		/// <summary>Draws a string with layout options.</summary>
		public void DrawString(string text, Font font, Brush brush, PointF origin, StringFormat format)
		{
			DrawString(text, font, brush, origin.X, origin.Y, format);
		}

		/// <summary>Resets the transform to the identity.</summary>
		public void ResetTransform()
		{
			transform = Affine.Identity;
		}

		// Prepend applies the new transform first, in local space (Skia's canvas operations on Android); Append applies it
		// after the existing one, in device space, which the route map uses to place rotated station names.
		private void Combine(Affine change, Drawing2D.MatrixOrder order)
		{
			transform = order == Drawing2D.MatrixOrder.Prepend ? Affine.Then(change, transform) : Affine.Then(transform, change);
		}

		/// <summary>Rotates subsequent drawing about the origin.</summary>
		public void RotateTransform(float angle)
		{
			Combine(Affine.Rotation(angle), Drawing2D.MatrixOrder.Prepend);
		}

		/// <summary>Translates subsequent drawing.</summary>
		public void TranslateTransform(float dx, float dy)
		{
			Combine(Affine.Translation(dx, dy), Drawing2D.MatrixOrder.Prepend);
		}

		/// <summary>Translates subsequent drawing, before or after the existing transform.</summary>
		public void TranslateTransform(float dx, float dy, Drawing2D.MatrixOrder order)
		{
			Combine(Affine.Translation(dx, dy), order);
		}

		/// <summary>Rotates subsequent drawing, before or after the existing transform.</summary>
		public void RotateTransform(float angle, Drawing2D.MatrixOrder order)
		{
			Combine(Affine.Rotation(angle), order);
		}

		/// <summary>Scales subsequent drawing.</summary>
		public void ScaleTransform(float sx, float sy)
		{
			Combine(Affine.Scaling(sx, sy), Drawing2D.MatrixOrder.Prepend);
		}
	}
}
