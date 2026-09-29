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
using System.Drawing.Imaging;
using SkiaSharp;

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>Unit of measure used by drawing operations. Only Pixel is honoured by this shim.</summary>
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
		internal abstract SKColor SkiaColor { get; }

		/// <inheritdoc />
		public virtual void Dispose()
		{
		}
	}

	/// <summary>A brush of a single solid colour.</summary>
	public sealed class SolidBrush : Brush
	{
		/// <summary>The colour of this brush.</summary>
		public Color Color { get; set; }

		/// <summary>Creates a new solid brush.</summary>
		public SolidBrush(Color color)
		{
			Color = color;
		}

		internal override SKColor SkiaColor => new SKColor(Color.R, Color.G, Color.B, Color.A);
	}

	/// <summary>A font family.</summary>
	public sealed class FontFamily
	{
		/// <summary>The name of this family.</summary>
		public string Name { get; }

		/// <summary>Creates a family by name.</summary>
		public FontFamily(string name)
		{
			Name = name;
		}

		/// <summary>The default sans-serif family.</summary>
		public static FontFamily GenericSansSerif { get; } = new FontFamily("sans-serif");

		/// <summary>The default serif family.</summary>
		public static FontFamily GenericSerif { get; } = new FontFamily("serif");

		/// <summary>The default monospace family.</summary>
		public static FontFamily GenericMonospace { get; } = new FontFamily("monospace");

		/// <summary>The families available on this device.</summary>
		public static FontFamily[] Families
		{
			get
			{
				string[] names = SKFontManager.Default.GetFontFamilies();
				FontFamily[] families = new FontFamily[names.Length];
				for (int i = 0; i < names.Length; i++)
				{
					families[i] = new FontFamily(names[i]);
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

	/// <summary>A Skia-backed replacement for System.Drawing.Font.</summary>
	public sealed class Font : IDisposable
	{
		internal readonly SKFont NativeFont;

		/// <summary>The name of the font family in use.</summary>
		public string Name { get; }

		/// <summary>The em size of this font, in the unit it was created with.</summary>
		public float Size { get; }

		/// <summary>The em size of this font in points.</summary>
		public float SizeInPoints { get; }

		/// <summary>The style of this font.</summary>
		public FontStyle Style { get; }

		/// <summary>The unit the size is expressed in.</summary>
		public GraphicsUnit Unit { get; }

		/// <summary>The line spacing of this font, in pixels.</summary>
		public float Height => NativeFont.Metrics.Descent - NativeFont.Metrics.Ascent;

		/// <summary>Creates a new font.</summary>
		public Font(string familyName, float emSize) : this(familyName, emSize, FontStyle.Regular, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a new font.</summary>
		public Font(string familyName, float emSize, FontStyle style) : this(familyName, emSize, style, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a new font.</summary>
		public Font(FontFamily family, float emSize) : this(family.Name, emSize, FontStyle.Regular, GraphicsUnit.Point)
		{
		}

		/// <summary>Creates a new font.</summary>
		public Font(FontFamily family, float emSize, GraphicsUnit unit) : this(family.Name, emSize, FontStyle.Regular, unit)
		{
		}

		/// <summary>Creates a new font.</summary>
		public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit) : this(family.Name, emSize, style, unit)
		{
		}

		/// <summary>Creates a new font from an existing one, with a different style.</summary>
		public Font(Font prototype, FontStyle style) : this(prototype.Name, prototype.Size, style, prototype.Unit)
		{
		}

		/// <summary>Creates a new font.</summary>
		public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit)
		{
			Size = emSize;
			Style = style;
			Unit = unit;
			SKFontStyle fontStyle = new SKFontStyle(
				(style & FontStyle.Bold) != 0 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
				SKFontStyleWidth.Normal,
				(style & FontStyle.Italic) != 0 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

			SKTypeface typeface = SKTypeface.FromFamilyName(familyName, fontStyle) ?? SKTypeface.Default;
			Name = typeface.FamilyName;

			/*
			 * Skia always works in pixels. System.Drawing sizes are in points unless the caller
			 * says otherwise, and OpenBVE's glyph atlas asks for pixels — getting this wrong
			 * renders all in-game text a third too large.
			 */
			float pixels = unit == GraphicsUnit.Pixel ? emSize : emSize * 96.0f / 72.0f;
			SizeInPoints = unit == GraphicsUnit.Pixel ? emSize * 72.0f / 96.0f : emSize;
			NativeFont = new SKFont(typeface, pixels);
			this.fontStyle = fontStyle;
		}

		private readonly SKFontStyle fontStyle;
		private readonly Dictionary<string, SKFont> fallbacks = new Dictionary<string, SKFont>();

		/*
		 * Per-character fallback. Windows' GDI+ substitutes a font that has a missing character
		 * (station names in Chinese or Japanese drawn with a Latin UI font come out right there); Skia
		 * draws a box instead. So text is split into runs by the font that actually has each
		 * character, the system choosing one where this font does not.
		 */
		private SKFont FontFor(int codepoint)
		{
			if (codepoint < 0x80 || NativeFont.Typeface.ContainsGlyph(codepoint))
			{
				return NativeFont;
			}

			SKTypeface match = SKFontManager.Default.MatchCharacter(Name, fontStyle, null, codepoint);
			if (match == null)
			{
				return NativeFont;
			}

			if (!fallbacks.TryGetValue(match.FamilyName, out SKFont fallback))
			{
				fallback = new SKFont(match, NativeFont.Size);
				fallbacks[match.FamilyName] = fallback;
			}

			return fallback;
		}

		private IEnumerable<(string Text, SKFont Font)> Runs(string text)
		{
			SKFont current = null;
			int start = 0;
			for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
			{
				SKFont font = FontFor(char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text, i) : text[i]);
				if (current != null && font != current)
				{
					yield return (text.Substring(start, i - start), current);
					start = i;
				}

				current = font;
			}

			if (current != null)
			{
				yield return (text.Substring(start), current);
			}
		}

		/// <summary>The advance width of the text, with fallback fonts where this one lacks characters.</summary>
		internal float Measure(string text)
		{
			float width = 0.0f;
			foreach ((string run, SKFont font) in Runs(text))
			{
				width += font.MeasureText(run);
			}

			return width;
		}

		/// <summary>Draws the text on a baseline, with fallback fonts where this one lacks characters.</summary>
		internal void Draw(SKCanvas canvas, string text, float x, float baseline, SKPaint paint)
		{
			foreach ((string run, SKFont font) in Runs(text))
			{
				canvas.DrawText(run, x, baseline, SKTextAlign.Left, font, paint);
				x += font.MeasureText(run);
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			NativeFont.Dispose();
			foreach (SKFont fallback in fallbacks.Values)
			{
				fallback.Dispose();
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

	/// <summary>Horizontal or vertical alignment of text within its layout rectangle.</summary>
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

		/// <summary>The generic default format, which includes the padding GDI+ adds around a string.</summary>
		public static StringFormat GenericDefault => new StringFormat();

		/// <summary>The generic typographic format, which measures the glyphs alone.</summary>
		public static StringFormat GenericTypographic => new StringFormat { Typographic = true };

		/// <inheritdoc />
		public void Dispose()
		{
		}
	}

	/// <summary>A Skia-backed replacement for System.Drawing.Graphics.</summary>
	/// <remarks>
	/// Skia cannot draw directly onto the unpremultiplied surface used by <see cref="Bitmap"/>,
	/// so drawing happens on a premultiplied working copy that is written back on Flush or Dispose.
	/// </remarks>
	public sealed partial class Graphics : IDisposable
	{
		private readonly Bitmap target;
		private readonly SKBitmap work;
		private readonly SKCanvas canvas;
		private bool disposed;

		private Graphics(Bitmap target)
		{
			this.target = target;
			work = new SKBitmap(new SKImageInfo(target.Width, target.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (SKImage source = SKImage.FromBitmap(target.Native))
			{
				source.ReadPixels(work.Info, work.GetPixels(), work.RowBytes, 0, 0);
			}

			canvas = new SKCanvas(work);
			target.Drawing = this;
		}

		/// <summary>Creates a drawing surface for the given image.</summary>
		public static Graphics FromImage(Image image)
		{
			if (image is Bitmap bitmap)
			{
				return new Graphics(bitmap);
			}

			throw new ArgumentException("Only Bitmap is supported by the Android graphics shim.", nameof(image));
		}

		/// <summary>Fills the whole surface with a colour.</summary>
		public void Clear(Color color)
		{
			canvas.Clear(new SKColor(color.R, color.G, color.B, color.A));
		}

		/// <summary>Fills a rectangle.</summary>
		public void FillRectangle(Brush brush, float x, float y, float width, float height)
		{
			using (SKPaint paint = new SKPaint { Color = brush.SkiaColor, IsAntialias = false, Style = SKPaintStyle.Fill })
			{
				canvas.DrawRect(x, y, width, height, paint);
			}
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

		/// <summary>Draws a string inside a layout rectangle, wrapping at word boundaries and clipping to it, as GDI+ does.</summary>
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

			// Each line is placed by the format's alignment, as the point overloads do.
			float x = layout.X;
			if (format != null && format.Alignment == StringAlignment.Center)
			{
				x = layout.X + layout.Width / 2.0f;
			}
			else if (format != null && format.Alignment == StringAlignment.Far)
			{
				x = layout.Right;
			}

			canvas.Save();
			canvas.ClipRect(new SKRect(layout.X, layout.Y, layout.X + layout.Width, layout.Y + layout.Height));
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

			canvas.Restore();
		}

		/// <summary>
		/// Splits text into lines no wider than the given width: at line breaks, then between words,
		/// and within a word (CJK text has no spaces) only where a single word is itself too wide.
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

					// A word wider than the line is broken by characters.
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

		/// <summary>Controls how text is antialiased. Skia antialiases text regardless, so this is recorded and ignored.</summary>
		public Text.TextRenderingHint TextRenderingHint { get; set; } = Text.TextRenderingHint.SystemDefault;

		/// <summary>Draws an image scaled into the given rectangle.</summary>
		public void DrawImage(Image image, float x, float y, float width, float height)
		{
			DrawImage(image,
				new Rectangle((int)x, (int)y, (int)width, (int)height),
				new Rectangle(0, 0, image.Width, image.Height),
				GraphicsUnit.Pixel);
		}

		/// <summary>Draws an image at the given position.</summary>
		public void DrawImage(Image image, int x, int y)
		{
			DrawImage(image, new Rectangle(x, y, image.Width, image.Height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
		}

		/// <summary>Draws a region of an image into a destination rectangle.</summary>
		public void DrawImage(Image image, Rectangle destRect, Rectangle srcRect, GraphicsUnit unit)
		{
			if (!(image is Bitmap bitmap))
			{
				throw new ArgumentException("Only Bitmap is supported by the Android graphics shim.", nameof(image));
			}

			if (bitmap != target)
			{
				bitmap.Sync();
			}

			using (SKImage source = SKImage.FromBitmap(bitmap.Native))
			{
				canvas.DrawImage(source,
					new SKRect(srcRect.X, srcRect.Y, srcRect.X + srcRect.Width, srcRect.Y + srcRect.Height),
					new SKRect(destRect.X, destRect.Y, destRect.X + destRect.Width, destRect.Y + destRect.Height));
			}
		}

		/// <summary>Draws a string at the given position.</summary>
		public void DrawString(string text, Font font, Brush brush, float x, float y)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			using (SKPaint paint = new SKPaint { Color = brush.SkiaColor, IsAntialias = true })
			{
				// System.Drawing positions text by its top-left corner, Skia by the baseline.
				font.Draw(canvas, text, x, y - font.NativeFont.Metrics.Ascent, paint);
			}
		}

		/// <summary>Draws a string at the given position.</summary>
		public void DrawString(string text, Font font, Brush brush, PointF point)
		{
			DrawString(text, font, brush, point.X, point.Y);
		}

		/// <summary>Measures the size of a string when rendered with the given font.</summary>
		public SizeF MeasureString(string text, Font font)
		{
			return MeasureString(text, font, int.MaxValue, null);
		}

		/// <summary>Measures the size of a string when rendered with the given font.</summary>
		public SizeF MeasureString(string text, Font font, int width)
		{
			return MeasureString(text, font, width, null);
		}

		/// <summary>Measures the size of a string when rendered with the given font.</summary>
		public SizeF MeasureString(string text, Font font, PointF origin, StringFormat format)
		{
			return MeasureString(text, font, int.MaxValue, format);
		}

		/// <summary>Measures the size of a string when rendered with the given font.</summary>
		/// <remarks>
		/// GDI+ pads a measured string by roughly a sixth of an em unless the typographic format
		/// is used. OpenBVE measures each glyph both ways and uses the difference to place glyphs
		/// in its atlas, so the distinction has to be preserved even though the exact GDI+
		/// padding cannot be reproduced here.
		/// </remarks>
		public SizeF MeasureString(string text, Font font, int width, StringFormat format)
		{
			if (string.IsNullOrEmpty(text))
			{
				return new SizeF(0.0f, font.Height);
			}

			float advance = font.Measure(text);
			bool typographic = format != null && format.Typographic;
			float padding = typographic ? 0.0f : font.NativeFont.Size / 6.0f;
			return new SizeF(advance + padding, font.Height);
		}

		/// <summary>Writes pending drawing operations back to the target bitmap.</summary>
		public void Flush()
		{
			canvas.Flush();
			using (SKImage rendered = SKImage.FromBitmap(work))
			{
				rendered.ReadPixels(target.Native.Info, target.Native.GetPixels(), target.Native.RowBytes, 0, 0);
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			Flush();
			if (target.Drawing == this)
			{
				target.Drawing = null;
			}

			canvas.Dispose();
			work.Dispose();
		}
	}
}
