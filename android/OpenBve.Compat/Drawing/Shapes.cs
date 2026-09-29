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

using SkiaSharp;

// ReSharper disable once CheckNamespace
namespace System.Drawing.Drawing2D
{
	/// <summary>Antialiasing modes. Recorded and applied to Skia's antialias flag.</summary>
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
	/// <summary>A pen, used to stroke lines and outlines.</summary>
	public sealed class Pen : IDisposable
	{
		/// <summary>The colour of this pen.</summary>
		public Color Color { get; set; }

		/// <summary>The stroke width.</summary>
		public float Width { get; set; }

		/// <summary>The dash style. Recorded but not applied.</summary>
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

		internal SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);

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

		private SKPaint StrokePaint(Pen pen)
		{
			return new SKPaint
			{
				Color = pen.SkiaColor,
				StrokeWidth = pen.Width,
				Style = SKPaintStyle.Stroke,
				IsAntialias = AntiAlias
			};
		}

		private SKPaint FillPaint(Brush brush)
		{
			return new SKPaint
			{
				Color = brush.SkiaColor,
				Style = SKPaintStyle.Fill,
				IsAntialias = AntiAlias
			};
		}

		/// <summary>Draws a line.</summary>
		public void DrawLine(Pen pen, float x1, float y1, float x2, float y2)
		{
			using (SKPaint paint = StrokePaint(pen))
			{
				canvas.DrawLine(x1, y1, x2, y2, paint);
			}
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
			using (SKPaint paint = StrokePaint(pen))
			{
				canvas.DrawRect(x, y, width, height, paint);
			}
		}

		/// <summary>Draws the outline of a rectangle.</summary>
		public void DrawRectangle(Pen pen, Rectangle rect)
		{
			DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Draws the outline of an ellipse.</summary>
		public void DrawEllipse(Pen pen, float x, float y, float width, float height)
		{
			using (SKPaint paint = StrokePaint(pen))
			{
				canvas.DrawOval(new SKRect(x, y, x + width, y + height), paint);
			}
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
			using (SKPaint paint = FillPaint(brush))
			{
				canvas.DrawOval(new SKRect(x, y, x + width, y + height), paint);
			}
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
			if (points == null || points.Length < 3)
			{
				return;
			}

			using (SKPath path = new SKPath())
			using (SKPaint paint = FillPaint(brush))
			{
				path.MoveTo(points[0].X, points[0].Y);
				for (int i = 1; i < points.Length; i++)
				{
					path.LineTo(points[i].X, points[i].Y);
				}

				path.Close();
				canvas.DrawPath(path, paint);
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

		/// <summary>Draws a cardinal spline through the given points.</summary>
		public void DrawCurve(Pen pen, PointF[] points)
		{
			if (points != null)
			{
				DrawCurve(pen, points, 0, points.Length - 1);
			}
		}

		/// <summary>Draws part of a cardinal spline through the given points.</summary>
		/// <remarks>
		/// GDI+ draws a cardinal spline with a default tension of 0.5. The equivalent cubic
		/// Bezier control points are derived from each segment's neighbours (Catmull-Rom form),
		/// which matches GDI+ closely enough for the route map's gradient profile.
		/// </remarks>
		public void DrawCurve(Pen pen, PointF[] points, int offset, int numberOfSegments)
		{
			if (points == null || numberOfSegments < 1 || offset < 0 || offset + numberOfSegments >= points.Length)
			{
				return;
			}

			const float tension = 0.5f / 3.0f;

			using (SKPath path = new SKPath())
			using (SKPaint paint = StrokePaint(pen))
			{
				path.MoveTo(points[offset].X, points[offset].Y);
				for (int i = offset; i < offset + numberOfSegments; i++)
				{
					PointF previous = points[i > 0 ? i - 1 : i];
					PointF start = points[i];
					PointF end = points[i + 1];
					PointF next = points[i + 2 < points.Length ? i + 2 : i + 1];

					float c1x = start.X + (end.X - previous.X) * tension;
					float c1y = start.Y + (end.Y - previous.Y) * tension;
					float c2x = end.X - (next.X - start.X) * tension;
					float c2y = end.Y - (next.Y - start.Y) * tension;

					path.CubicTo(c1x, c1y, c2x, c2y, end.X, end.Y);
				}

				canvas.DrawPath(path, paint);
			}
		}

		/// <summary>Draws a cardinal spline through the given points.</summary>
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

		/// <summary>Draws a string with the given layout options.</summary>
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

		/// <summary>Draws a string with the given layout options.</summary>
		public void DrawString(string text, Font font, Brush brush, PointF origin, StringFormat format)
		{
			DrawString(text, font, brush, origin.X, origin.Y, format);
		}

		/// <summary>Resets the transform to the identity.</summary>
		public void ResetTransform()
		{
			canvas.ResetMatrix();
		}

		/// <summary>Rotates subsequent drawing about the origin.</summary>
		public void RotateTransform(float angle)
		{
			canvas.RotateDegrees(angle);
		}

		/// <summary>Translates subsequent drawing.</summary>
		public void TranslateTransform(float dx, float dy)
		{
			canvas.Translate(dx, dy);
		}

		/// <summary>Translates subsequent drawing, combining before or after the existing transform.</summary>
		/// <remarks>
		/// Skia's canvas transforms are always prepended, i.e. applied in local space. GDI+ can
		/// append instead, applying the new transform in device space — which is what the route
		/// map does to place rotated station names. Getting this backwards puts the labels in
		/// the wrong place, so Append is composed explicitly.
		/// </remarks>
		public void TranslateTransform(float dx, float dy, Drawing2D.MatrixOrder order)
		{
			if (order == Drawing2D.MatrixOrder.Prepend)
			{
				canvas.Translate(dx, dy);
				return;
			}

			canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateTranslation(dx, dy), canvas.TotalMatrix));
		}

		/// <summary>Rotates subsequent drawing, combining before or after the existing transform.</summary>
		public void RotateTransform(float angle, Drawing2D.MatrixOrder order)
		{
			if (order == Drawing2D.MatrixOrder.Prepend)
			{
				canvas.RotateDegrees(angle);
				return;
			}

			canvas.SetMatrix(SKMatrix.Concat(SKMatrix.CreateRotationDegrees(angle), canvas.TotalMatrix));
		}

		/// <summary>Scales subsequent drawing.</summary>
		public void ScaleTransform(float sx, float sy)
		{
			canvas.Scale(sx, sy);
		}
	}
}
