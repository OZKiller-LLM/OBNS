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
	/// <summary>The unit a measurement is expressed in.</summary>
	public enum GraphicsUnit
	{
		World = 0,
		Display = 1,
		Pixel = 2,
		Point = 3,
		Inch = 4,
		Document = 5,
		Millimeter = 6
	}

	/// <summary>Font style flags.</summary>
	[Flags]
	public enum FontStyle
	{
		Regular = 0,
		Bold = 1,
		Italic = 2,
		Underline = 4,
		Strikeout = 8
	}

	/// <summary>Base class for brushes.</summary>
	public abstract class Brush : IDisposable
	{
		internal abstract Color FillColor { get; }

		/// <inheritdoc />
		public virtual void Dispose()
		{
		}
	}

	/// <summary>A brush of a single colour.</summary>
	public sealed class SolidBrush : Brush
	{
		/// <summary>The brush's colour.</summary>
		public Color Color { get; set; }

		/// <summary>Creates a brush of the given colour.</summary>
		public SolidBrush(Color color)
		{
			Color = color;
		}

		internal override Color FillColor => Color;
	}

	/// <summary>A font family, by name.</summary>
	public sealed class FontFamily
	{
		/// <summary>The family's name.</summary>
		public string Name { get; }

		/// <summary>Names a font family.</summary>
		public FontFamily(string name)
		{
			Name = name;
		}

		/// <summary>The generic sans-serif family: the console's standard font.</summary>
		public static FontFamily GenericSansSerif { get; } = new FontFamily("sans-serif");

		/// <summary>The generic serif family: the console's standard font too.</summary>
		public static FontFamily GenericSerif { get; } = new FontFamily("serif");

		/// <summary>The generic monospace family: the console's standard font too.</summary>
		public static FontFamily GenericMonospace { get; } = new FontFamily("monospace");

		/// <summary>The families on the console: its shared fonts.</summary>
		public static FontFamily[] Families
		{
			get
			{
				FontFamily[] families = new FontFamily[Native.Families.Length];
				for (int i = 0; i < families.Length; i++)
				{
					families[i] = new FontFamily(Native.Families[i]);
				}

				return families;
			}
		}

		/// <inheritdoc />
		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>The Switch's replacement for System.Drawing.Font: the console's shared fonts through FreeType (NX/runner).</summary>
	public sealed unsafe class Font : IDisposable
	{
		internal IntPtr Handle;
		private readonly float ascent, descent;

		/// <summary>The name of the font family asked for.</summary>
		public string Name { get; }

		/// <summary>The font family.</summary>
		public FontFamily FontFamily => new FontFamily(Name);

		/// <summary>The em size, in the unit it was created with.</summary>
		public float Size { get; }

		/// <summary>The em size in points.</summary>
		public float SizeInPoints { get; }

		/// <summary>The em size in pixels.</summary>
		internal float Pixels { get; }

		/// <summary>The style.</summary>
		public FontStyle Style { get; }

		/// <summary>The unit the size is in.</summary>
		public GraphicsUnit Unit { get; }

		/// <summary>The line spacing, in pixels.</summary>
		public float Height => descent - ascent;

		/// <summary>The ascent, negative (above the baseline), in pixels.</summary>
		internal float Ascent => ascent;

		/// <summary>Creates a font.</summary>
		public Font(string familyName, float emSize) : this(familyName, emSize, FontStyle.Regular, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a font.</summary>
		public Font(string familyName, float emSize, FontStyle style) : this(familyName, emSize, style, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a font.</summary>
		public Font(FontFamily family, float emSize) : this(family.Name, emSize, FontStyle.Regular, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a font.</summary>
		public Font(FontFamily family, float emSize, GraphicsUnit unit) : this(family.Name, emSize, FontStyle.Regular, unit)
		{
		}

		/// <summary>Creates a font.</summary>
		public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit) : this(family.Name, emSize, style, unit)
		{
		}

		/// <summary>Creates a font from another, in a different style.</summary>
		public Font(Font prototype, FontStyle style) : this(prototype.Name, prototype.Size, style, prototype.Unit)
		{
		}

		/// <summary>Creates a font.</summary>
		public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit)
		{
			Name = familyName;
			Size = emSize;
			Style = style;
			Unit = unit;
			// Points unless the caller says pixels, as on Android: OpenBVE's glyph atlas asks for pixels.
			Pixels = unit == GraphicsUnit.Pixel ? emSize : emSize * 96.0f / 72.0f;
			SizeInPoints = unit == GraphicsUnit.Pixel ? emSize * 72.0f / 96.0f : emSize;
			Handle = Native.FontOpen(familyName ?? string.Empty, (style & FontStyle.Bold) != 0 ? 1 : 0, (style & FontStyle.Italic) != 0 ? 1 : 0, Pixels);
			if (Handle != IntPtr.Zero)
			{
				Native.FontMetrics(Handle, out ascent, out descent);
			}
			else
			{
				ascent = -Pixels * 0.8f;
				descent = Pixels * 0.2f;
			}
		}

		/// <summary>The advance width of the text; the console's other fonts fill the characters this one lacks.</summary>
		internal float Measure(string text)
		{
			if (Handle == IntPtr.Zero || string.IsNullOrEmpty(text))
			{
				return 0.0f;
			}

			fixed (char* p = text)
			{
				return Native.FontMeasure(Handle, p, text.Length);
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (Handle != IntPtr.Zero)
			{
				Native.FontClose(Handle);
				Handle = IntPtr.Zero;
			}

			GC.SuppressFinalize(this);
		}

		~Font()
		{
			if (Handle != IntPtr.Zero)
			{
				Native.FontClose(Handle);
				Handle = IntPtr.Zero;
			}
		}
	}

	/// <summary>Brushes for the standard colours.</summary>
	public static class Brushes
	{
		/// <summary>A black brush.</summary>
		public static Brush Black { get; } = new SolidBrush(Color.Black);

		/// <summary>A white brush.</summary>
		public static Brush White { get; } = new SolidBrush(Color.White);

		/// <summary>A red brush.</summary>
		public static Brush Red { get; } = new SolidBrush(Color.Red);

		/// <summary>A grey brush.</summary>
		public static Brush Gray { get; } = new SolidBrush(Color.Gray);

		/// <summary>A light grey brush.</summary>
		public static Brush LightGray { get; } = new SolidBrush(Color.LightGray);

		/// <summary>A sky blue brush.</summary>
		public static Brush SkyBlue { get; } = new SolidBrush(Color.SkyBlue);

		/// <summary>A pale goldenrod brush.</summary>
		public static Brush PaleGoldenrod { get; } = new SolidBrush(Color.PaleGoldenrod);

		/// <summary>A tan brush.</summary>
		public static Brush Tan { get; } = new SolidBrush(Color.Tan);
	}

	/// <summary>Horizontal or vertical alignment of text within its layout.</summary>
	public enum StringAlignment
	{
		Near = 0,
		Center = 1,
		Far = 2
	}

	/// <summary>Text layout options.</summary>
	public sealed class StringFormat : IDisposable
	{
		/// <summary>Whether this format measures tight typographic bounds rather than padded ones.</summary>
		internal bool Typographic { get; private set; }

		/// <summary>Horizontal alignment.</summary>
		public StringAlignment Alignment { get; set; } = StringAlignment.Near;

		/// <summary>Vertical alignment.</summary>
		public StringAlignment LineAlignment { get; set; } = StringAlignment.Near;

		/// <summary>The generic default format, with the padding GDI+ adds around a string.</summary>
		public static StringFormat GenericDefault => new StringFormat();

		/// <summary>The generic typographic format, measuring the glyphs alone.</summary>
		public static StringFormat GenericTypographic => new StringFormat { Typographic = true };

		/// <inheritdoc />
		public void Dispose()
		{
		}
	}

	/// <summary>The Switch's replacement for System.Drawing.Graphics: drawing goes straight into the bitmap's pixels.</summary>
	public sealed partial class Graphics : IDisposable
	{
		private readonly Bitmap target;
		private Affine transform = Affine.Identity;
		private Rectangle clip;

		private Graphics(Bitmap target)
		{
			this.target = target;
			clip = new Rectangle(0, 0, target.Width, target.Height);
		}

		/// <summary>Creates a drawing surface for an image.</summary>
		public static Graphics FromImage(Image image)
		{
			if (image is Bitmap bitmap)
			{
				return new Graphics(bitmap);
			}

			throw new ArgumentException("Only Bitmap is supported by the Switch graphics shim.", nameof(image));
		}

		/// <summary>Fills the whole surface with a colour, replacing what was there.</summary>
		public unsafe void Clear(Color color)
		{
			uint value = (uint)(color.B | (color.G << 8) | (color.R << 16) | (color.A << 24));
			for (int y = clip.Top; y < clip.Bottom; y++)
			{
				uint* row = (uint*)((byte*)target.Pixels + (long)y * target.Stride);
				for (int x = clip.Left; x < clip.Right; x++)
				{
					row[x] = value;
				}
			}
		}

		private PointF[] Mapped(float x, float y, float width, float height)
		{
			return new[] { transform.Map(x, y), transform.Map(x + width, y), transform.Map(x + width, y + height), transform.Map(x, y + height) };
		}

		/// <summary>Fills a rectangle (not antialiased, as on Android).</summary>
		public void FillRectangle(Brush brush, float x, float y, float width, float height)
		{
			Raster.Fill(target, new List<PointF[]> { Mapped(x, y, width, height) }, brush.FillColor, false, clip);
		}

		/// <summary>Fills a rectangle.</summary>
		public void FillRectangle(Brush brush, Rectangle rect)
		{
			FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Fills a rectangle.</summary>
		public void FillRectangle(Brush brush, RectangleF rect)
		{
			FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);
		}

		/// <summary>Draws a string inside a layout rectangle.</summary>
		public void DrawString(string text, Font font, Brush brush, RectangleF layout)
		{
			DrawString(text, font, brush, layout, null);
		}

		/// <summary>Draws a string inside a layout rectangle, wrapping at word boundaries and clipping to it, as GDI+ does.</summary>
		public void DrawString(string text, Font font, Brush brush, RectangleF layout, StringFormat format)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			float x = layout.X;
			if (format != null && format.Alignment == StringAlignment.Center)
			{
				x = layout.X + layout.Width / 2.0f;
			}
			else if (format != null && format.Alignment == StringAlignment.Far)
			{
				x = layout.Right;
			}

			Rectangle saved = clip;
			PointF[] corners = Mapped(layout.X, layout.Y, layout.Width, layout.Height);
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (PointF p in corners)
			{
				minX = Math.Min(minX, p.X);
				maxX = Math.Max(maxX, p.X);
				minY = Math.Min(minY, p.Y);
				maxY = Math.Max(maxY, p.Y);
			}

			clip = Rectangle.Intersect(clip, Rectangle.FromLTRB((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY)));
			try
			{
				float y = layout.Y;
				foreach (string line in WrapLines(text, font, layout.Width))
				{
					DrawString(line, font, brush, x, y, format);
					y += font.Height;
					if (y >= layout.Y + layout.Height)
					{
						break;
					}
				}
			}
			finally
			{
				clip = saved;
			}
		}

		/// <summary>
		/// Splits text into lines no wider than a width: at line breaks, then between words, and within a word (CJK text
		/// has no spaces) only where a word alone is too wide. Android's rule.
		/// </summary>
		private static List<string> WrapLines(string text, Font font, float width)
		{
			List<string> lines = new List<string>();
			foreach (string paragraph in text.Replace("\r\n", "\n").Split('\n'))
			{
				string current = string.Empty;
				foreach (string word in paragraph.Split(' '))
				{
					string candidate = current.Length == 0 ? word : current + " " + word;
					if (font.Measure(candidate) <= width || current.Length == 0 && font.Measure(word) <= width)
					{
						current = candidate;
						continue;
					}

					if (current.Length != 0)
					{
						lines.Add(current);
					}

					current = string.Empty;
					foreach (char c in word)
					{
						if (current.Length != 0 && font.Measure(current + c) > width)
						{
							lines.Add(current);
							current = string.Empty;
						}

						current += c;
					}
				}

				lines.Add(current);
			}

			return lines;
		}

		/// <summary>How text is antialiased: always antialiased here, so this is recorded and ignored, as on Android.</summary>
		public Text.TextRenderingHint TextRenderingHint { get; set; } = Text.TextRenderingHint.SystemDefault;

		/// <summary>Draws an image scaled into a rectangle.</summary>
		public void DrawImage(Image image, float x, float y, float width, float height)
		{
			DrawImage(image, new Rectangle((int)x, (int)y, (int)width, (int)height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
		}

		/// <summary>Draws an image at a position.</summary>
		public void DrawImage(Image image, int x, int y)
		{
			DrawImage(image, new Rectangle(x, y, image.Width, image.Height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
		}

		/// <summary>Draws a whole image into a rectangle.</summary>
		public void DrawImage(Image image, Rectangle destRect)
		{
			DrawImage(image, destRect, new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
		}

		/// <summary>Draws a region of an image into a rectangle: nearest-pixel sampling, Skia's default on Android.</summary>
		public unsafe void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit unit)
		{
			if (!(image is Bitmap source))
			{
				throw new ArgumentException("Only Bitmap is supported by the Switch graphics shim.", nameof(image));
			}

			if (destRect.Width == 0 || destRect.Height == 0 || srcRect.Width == 0 || srcRect.Height == 0 || !transform.TryInvert(out Affine inverse))
			{
				return;
			}

			// Drawn onto itself: read from a copy.
			Bitmap from = source == target ? (Bitmap)source.Clone() : source;
			try
			{
				PointF[] corners = Mapped(destRect.X, destRect.Y, destRect.Width, destRect.Height);
				float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
				foreach (PointF p in corners)
				{
					minX = Math.Min(minX, p.X);
					maxX = Math.Max(maxX, p.X);
					minY = Math.Min(minY, p.Y);
					maxY = Math.Max(maxY, p.Y);
				}

				int left = Math.Max(clip.Left, (int)Math.Floor(minX)), right = Math.Min(clip.Right, (int)Math.Ceiling(maxX));
				int top = Math.Max(clip.Top, (int)Math.Floor(minY)), bottom = Math.Min(clip.Bottom, (int)Math.Ceiling(maxY));
				float sx = (float)srcRect.Width / destRect.Width, sy = (float)srcRect.Height / destRect.Height;
				for (int y = top; y < bottom; y++)
				{
					byte* row = (byte*)target.Pixels + (long)y * target.Stride;
					for (int x = left; x < right; x++)
					{
						PointF local = inverse.Map(x + 0.5f, y + 0.5f);
						float u = local.X - destRect.X, v = local.Y - destRect.Y;
						if (u < 0 || v < 0 || u >= destRect.Width || v >= destRect.Height)
						{
							continue;
						}

						int px = srcRect.X + (int)(u * sx), py = srcRect.Y + (int)(v * sy);
						if (px < 0 || py < 0 || px >= from.Width || py >= from.Height)
						{
							continue;
						}

						byte* s = (byte*)from.Pixels + (long)py * from.Stride + px * 4;
						if (s[3] != 0)
						{
							Raster.Blend(row + x * 4, s[0], s[1], s[2], s[3] / 255.0f);
						}
					}
				}
			}
			finally
			{
				if (from != source)
				{
					from.Dispose();
				}
			}
		}

		/// <summary>Draws a string with its top-left corner at a position.</summary>
		public unsafe void DrawString(string text, Font font, Brush brush, float x, float y)
		{
			if (string.IsNullOrEmpty(text) || font.Handle == IntPtr.Zero)
			{
				return;
			}

			Color c = brush.FillColor;
			uint argb = (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));
			float* matrix = stackalloc float[6];
			matrix[0] = transform.M11;
			matrix[1] = transform.M12;
			matrix[2] = transform.M21;
			matrix[3] = transform.M22;
			matrix[4] = transform.Dx;
			matrix[5] = transform.Dy;
			fixed (char* p = text)
			{
				// System.Drawing places text by its top-left corner; the baseline is the ascent below it.
				Native.FontDraw(font.Handle, p, text.Length, x, y - font.Ascent, argb, matrix,
					target.Pixels, target.Width, target.Height, target.Stride, clip.Left, clip.Top, clip.Right, clip.Bottom);
			}
		}

		/// <summary>Draws a string at a position.</summary>
		public void DrawString(string text, Font font, Brush brush, PointF point)
		{
			DrawString(text, font, brush, point.X, point.Y);
		}

		/// <summary>Measures a string.</summary>
		public SizeF MeasureString(string text, Font font)
		{
			return MeasureString(text, font, int.MaxValue, null);
		}

		/// <summary>Measures a string.</summary>
		public SizeF MeasureString(string text, Font font, int width)
		{
			return MeasureString(text, font, width, null);
		}

		/// <summary>Measures a string.</summary>
		public SizeF MeasureString(string text, Font font, PointF origin, StringFormat format)
		{
			return MeasureString(text, font, int.MaxValue, format);
		}

		/// <summary>Measures a string: padded by a sixth of an em unless typographic, as on Android (OpenBVE's glyph atlas uses the difference).</summary>
		public SizeF MeasureString(string text, Font font, int width, StringFormat format)
		{
			if (string.IsNullOrEmpty(text))
			{
				return new SizeF(0.0f, font.Height);
			}

			float advance = font.Measure(text);
			bool typographic = format != null && format.Typographic;
			return new SizeF(advance + (typographic ? 0.0f : font.Pixels / 6.0f), font.Height);
		}

		/// <summary>Drawing is already in the pixels: nothing to write back.</summary>
		public void Flush()
		{
		}

		/// <inheritdoc />
		public void Dispose()
		{
		}
	}
}
