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
namespace System.Drawing
{
	/// <summary>A 2D affine transform, GDI+'s way round: a point maps to (x m11 + y m21 + dx, x m12 + y m22 + dy).</summary>
	internal struct Affine
	{
		internal float M11, M12, M21, M22, Dx, Dy;

		internal static Affine Identity => new Affine { M11 = 1, M22 = 1 };

		internal static Affine Translation(float dx, float dy) => new Affine { M11 = 1, M22 = 1, Dx = dx, Dy = dy };

		internal static Affine Scaling(float sx, float sy) => new Affine { M11 = sx, M22 = sy };

		internal static Affine Rotation(float degrees)
		{
			double r = degrees * Math.PI / 180.0;
			float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
			return new Affine { M11 = c, M12 = s, M21 = -s, M22 = c };
		}

		/// <summary>The transform that applies <paramref name="first"/>, then <paramref name="second"/>.</summary>
		internal static Affine Then(Affine first, Affine second)
		{
			return new Affine
			{
				M11 = first.M11 * second.M11 + first.M12 * second.M21,
				M12 = first.M11 * second.M12 + first.M12 * second.M22,
				M21 = first.M21 * second.M11 + first.M22 * second.M21,
				M22 = first.M21 * second.M12 + first.M22 * second.M22,
				Dx = first.Dx * second.M11 + first.Dy * second.M21 + second.Dx,
				Dy = first.Dx * second.M12 + first.Dy * second.M22 + second.Dy
			};
		}

		internal PointF Map(float x, float y) => new PointF(x * M11 + y * M21 + Dx, x * M12 + y * M22 + Dy);

		internal bool TryInvert(out Affine inverse)
		{
			float det = M11 * M22 - M12 * M21;
			if (Math.Abs(det) < 1e-12f)
			{
				inverse = Identity;
				return false;
			}

			inverse = new Affine
			{
				M11 = M22 / det,
				M12 = -M12 / det,
				M21 = -M21 / det,
				M22 = M11 / det,
				Dx = (M21 * Dy - M22 * Dx) / det,
				Dy = (M12 * Dx - M11 * Dy) / det
			};
			return true;
		}
	}

	/// <summary>
	/// Fills polygons (non-zero winding, as Skia's paths on Android) onto a bitmap: antialiased with 4 sub-rows a pixel
	/// and exact horizontal coverage, or by pixel centres without; blended over the unpremultiplied pixels.
	/// </summary>
	internal static unsafe class Raster
	{
		private struct Crossing : IComparable<Crossing>
		{
			internal float X;
			internal int Direction;

			public int CompareTo(Crossing other) => X.CompareTo(other.X);
		}

		/// <summary>Fills device-space contours with a colour, clipped to a rectangle.</summary>
		internal static void Fill(Bitmap target, List<PointF[]> contours, Color color, bool antialias, Rectangle clip)
		{
			if (color.A == 0 || contours.Count == 0)
			{
				return;
			}

			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (PointF[] contour in contours)
			{
				foreach (PointF p in contour)
				{
					minX = Math.Min(minX, p.X);
					maxX = Math.Max(maxX, p.X);
					minY = Math.Min(minY, p.Y);
					maxY = Math.Max(maxY, p.Y);
				}
			}

			int left = Math.Max(clip.Left, (int)Math.Floor(minX)), right = Math.Min(clip.Right, (int)Math.Ceiling(maxX) + 1);
			int top = Math.Max(clip.Top, (int)Math.Floor(minY)), bottom = Math.Min(clip.Bottom, (int)Math.Ceiling(maxY) + 1);
			if (left >= right || top >= bottom)
			{
				return;
			}

			int samples = antialias ? 4 : 1;
			float[] coverage = new float[right - left];
			List<Crossing> crossings = new List<Crossing>();
			for (int y = top; y < bottom; y++)
			{
				Array.Clear(coverage, 0, coverage.Length);
				bool any = false;
				for (int k = 0; k < samples; k++)
				{
					float sy = y + (k + 0.5f) / samples;
					crossings.Clear();
					foreach (PointF[] contour in contours)
					{
						for (int i = 0; i < contour.Length; i++)
						{
							PointF a = contour[i], b = contour[(i + 1) % contour.Length];
							if (a.Y == b.Y || sy < Math.Min(a.Y, b.Y) || sy >= Math.Max(a.Y, b.Y))
							{
								continue;
							}

							crossings.Add(new Crossing { X = a.X + (sy - a.Y) * (b.X - a.X) / (b.Y - a.Y), Direction = b.Y > a.Y ? 1 : -1 });
						}
					}

					if (crossings.Count < 2)
					{
						continue;
					}

					crossings.Sort();
					int winding = 0;
					for (int i = 0; i < crossings.Count - 1; i++)
					{
						winding += crossings[i].Direction;
						if (winding == 0)
						{
							continue;
						}

						float x0 = Math.Max(left, crossings[i].X), x1 = Math.Min(right, crossings[i + 1].X);
						if (x1 <= x0)
						{
							continue;
						}

						any = true;
						if (antialias)
						{
							Spread(coverage, left, x0, x1, 1.0f / samples);
						}
						else
						{
							int c0 = (int)Math.Ceiling(x0 - 0.5f), c1 = (int)Math.Ceiling(x1 - 0.5f);
							for (int c = Math.Max(left, c0); c < Math.Min(right, c1); c++)
							{
								coverage[c - left] = 1.0f;
							}
						}
					}
				}

				if (any)
				{
					byte* row = (byte*)target.Pixels + (long)y * target.Stride;
					for (int x = left; x < right; x++)
					{
						float c = coverage[x - left];
						if (c > 0.0f)
						{
							Blend(row + x * 4, color.B, color.G, color.R, color.A * Math.Min(1.0f, c) / 255.0f);
						}
					}
				}
			}
		}

		/// <summary>Adds a span's horizontal coverage, weighted, to the pixels it overlaps.</summary>
		private static void Spread(float[] coverage, int left, float x0, float x1, float weight)
		{
			int first = (int)Math.Floor(x0), last = (int)Math.Floor(x1);
			if (first == last)
			{
				coverage[first - left] += (x1 - x0) * weight;
				return;
			}

			coverage[first - left] += (first + 1 - x0) * weight;
			for (int c = first + 1; c < last && c - left < coverage.Length; c++)
			{
				coverage[c - left] += weight;
			}

			if (last - left < coverage.Length)
			{
				coverage[last - left] += (x1 - last) * weight;
			}
		}

		/// <summary>Source-over, both unpremultiplied: the source's alpha already multiplied by coverage.</summary>
		internal static void Blend(byte* d, byte b, byte g, byte r, float alpha)
		{
			if (alpha >= 0.999f)
			{
				d[0] = b;
				d[1] = g;
				d[2] = r;
				d[3] = 255;
				return;
			}

			float da = d[3] / 255.0f;
			float outA = alpha + da * (1.0f - alpha);
			if (outA <= 0.0f)
			{
				*(uint*)d = 0;
				return;
			}

			float keep = da * (1.0f - alpha);
			d[0] = (byte)Math.Min(255, (int)((b * alpha + d[0] * keep) / outA + 0.5f));
			d[1] = (byte)Math.Min(255, (int)((g * alpha + d[1] * keep) / outA + 0.5f));
			d[2] = (byte)Math.Min(255, (int)((r * alpha + d[2] * keep) / outA + 0.5f));
			d[3] = (byte)Math.Min(255, (int)(outA * 255.0f + 0.5f));
		}

		/// <summary>The polygon outlining a straight stroke of a width, extended by half the width at each end when asked.</summary>
		internal static PointF[] StrokeSegment(PointF a, PointF b, float width, bool extend)
		{
			float dx = b.X - a.X, dy = b.Y - a.Y;
			float length = (float)Math.Sqrt(dx * dx + dy * dy);
			if (length < 1e-6f)
			{
				dx = 1;
				dy = 0;
				length = 1;
			}

			dx /= length;
			dy /= length;
			float h = Math.Max(1.0f, width) / 2.0f;
			float nx = -dy * h, ny = dx * h;
			float ex = extend ? dx * h : 0, ey = extend ? dy * h : 0;
			// Always the same way round, so overlapping strokes add up under the non-zero rule.
			return new[]
			{
				new PointF(a.X - ex + nx, a.Y - ey + ny),
				new PointF(b.X + ex + nx, b.Y + ey + ny),
				new PointF(b.X + ex - nx, b.Y + ey - ny),
				new PointF(a.X - ex - nx, a.Y - ey - ny)
			};
		}

		/// <summary>Strokes a polyline (closed or not), each segment its own polygon, all filled together.</summary>
		internal static List<PointF[]> Stroke(IList<PointF> points, float width, bool closed)
		{
			List<PointF[]> contours = new List<PointF[]>();
			int segments = closed ? points.Count : points.Count - 1;
			for (int i = 0; i < segments; i++)
			{
				contours.Add(StrokeSegment(points[i], points[(i + 1) % points.Count], width, closed || segments > 1));
			}

			return contours;
		}

		/// <summary>An ellipse as a polygon, fine enough that the facets don't show.</summary>
		internal static List<PointF> Ellipse(float x, float y, float width, float height)
		{
			int n = Math.Max(16, Math.Min(256, (int)((Math.Abs(width) + Math.Abs(height)) * 1.5f)));
			List<PointF> points = new List<PointF>(n);
			float cx = x + width / 2.0f, cy = y + height / 2.0f;
			for (int i = 0; i < n; i++)
			{
				double t = 2.0 * Math.PI * i / n;
				points.Add(new PointF(cx + (float)Math.Cos(t) * width / 2.0f, cy + (float)Math.Sin(t) * height / 2.0f));
			}

			return points;
		}
	}
}
