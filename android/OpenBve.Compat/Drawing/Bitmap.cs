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

using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>Base class for the Skia-backed image shims.</summary>
	public abstract class Image : IDisposable
	{
		/// <summary>The width in pixels.</summary>
		public abstract int Width { get; }

		/// <summary>The height in pixels.</summary>
		public abstract int Height { get; }

		/// <summary>The size in pixels.</summary>
		public Size Size => new Size(Width, Height);

		/// <inheritdoc />
		public abstract void Dispose();

		/// <summary>Loads an image from a file.</summary>
		public static Image FromFile(string fileName)
		{
			return new Bitmap(fileName);
		}

		/// <summary>Loads an image from a stream.</summary>
		public static Image FromStream(Stream stream)
		{
			return new Bitmap(stream);
		}
	}

	/// <summary>A SkiaSharp-backed replacement for System.Drawing.Bitmap.</summary>
	/// <remarks>
	/// Pixels are always held as unpremultiplied BGRA8888, which is the memory layout
	/// the OpenBVE texture pipeline expects from Format32bppArgb.
	/// </remarks>
	public sealed class Bitmap : Image
	{
		internal readonly SKBitmap Native;
		private bool disposed;

		/// <summary>The image info used for all bitmaps created by this shim.</summary>
		internal static SKImageInfo InfoFor(int width, int height)
		{
			return new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
		}

		/*
		 * The Graphics drawing on this bitmap, if one is open. It draws on a working copy (see
		 * Graphics), while GDI+ draws straight into the bitmap - and upstream code such as the
		 * route map illustrations reads a bitmap without ever disposing or flushing the Graphics
		 * that drew it. Every read of the pixels therefore brings the drawing across first.
		 */
		internal Graphics Drawing;

		/// <summary>Writes any open Graphics' drawing into the pixels, before they are read.</summary>
		internal void Sync()
		{
			Drawing?.Flush();
		}

		internal Bitmap(SKBitmap native)
		{
			Native = native;
		}

		/// <summary>Creates a blank bitmap.</summary>
		public Bitmap(int width, int height)
		{
			Native = new SKBitmap(InfoFor(width, height));
			Native.Erase(SKColors.Transparent);
		}

		/// <summary>Creates a blank bitmap. The pixel format argument is accepted for source compatibility and always treated as 32bpp BGRA.</summary>
		public Bitmap(int width, int height, PixelFormat format) : this(width, height)
		{
		}

		/// <summary>Loads a bitmap from a file.</summary>
		public Bitmap(string fileName)
		{
			using (FileStream stream = File.OpenRead(fileName))
			{
				Native = Decode(stream, fileName);
			}
		}

		/// <summary>Loads a bitmap from a stream.</summary>
		public Bitmap(Stream stream)
		{
			Native = Decode(stream, "<stream>");
		}

		private static SKBitmap Decode(Stream stream, string source)
		{
			using (SKCodec codec = SKCodec.Create(new SKManagedStream(stream)))
			{
				if (codec == null)
				{
					throw new ArgumentException("The image could not be decoded: " + source);
				}

				SKImageInfo info = InfoFor(codec.Info.Width, codec.Info.Height);
				SKBitmap bitmap = new SKBitmap(info);
				SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
				if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
				{
					bitmap.Dispose();
					throw new ArgumentException("The image could not be decoded (" + result + "): " + source);
				}

				return bitmap;
			}
		}

		/// <inheritdoc />
		public override int Width => Native.Width;

		/// <inheritdoc />
		public override int Height => Native.Height;

		/// <summary>
		/// The pixel format. Always reported as 32bpp BGRA: whatever the source format, pixels
		/// are expanded on construction, so this describes what LockBits will hand back.
		/// </summary>
		public PixelFormat PixelFormat => PixelFormat.Format32bppArgb;

		private byte[] indexedPixels;
		private int indexedStride;
		private ColorPalette palette = new ColorPalette(new Color[0]);

		/// <summary>
		/// The colour palette. Empty unless this bitmap was built from indexed source data, as
		/// the TGA parser does: it reads the palette back, fills it in and assigns it again,
		/// at which point the stored indices are expanded through it.
		/// </summary>
		public ColorPalette Palette
		{
			get => palette;
			set
			{
				palette = value ?? new ColorPalette(new Color[0]);
				if (indexedPixels != null)
				{
					ExpandIndexed();
				}
			}
		}

		/// <summary>Creates a bitmap from raw pixel data in the given format.</summary>
		/// <param name="width">The width in pixels.</param>
		/// <param name="height">The height in pixels.</param>
		/// <param name="stride">The byte offset between rows in the source data.</param>
		/// <param name="format">The format of the source data.</param>
		/// <param name="scan0">A pointer to the first row.</param>
		public Bitmap(int width, int height, int stride, PixelFormat format, IntPtr scan0)
		{
			Native = new SKBitmap(InfoFor(width, height));

			switch (format)
			{
				case PixelFormat.Format8bppIndexed:
					/*
					 * Indices are kept until a palette arrives; a palette of 256 black entries
					 * stands in until then, matching GDI+, which also hands back a default palette.
					 */
					indexedStride = stride;
					indexedPixels = new byte[stride * height];
					Marshal.Copy(scan0, indexedPixels, 0, indexedPixels.Length);
					palette = new ColorPalette(new Color[256]);
					ExpandIndexed();
					break;
				case PixelFormat.Format32bppArgb:
				case PixelFormat.Format32bppPArgb:
				case PixelFormat.Format32bppRgb:
					CopyRows(scan0, stride, width, height, 4, Convert32);
					break;
				case PixelFormat.Format24bppRgb:
					CopyRows(scan0, stride, width, height, 3, Convert24);
					break;
				case PixelFormat.Format16bppRgb555:
					CopyRows(scan0, stride, width, height, 2, Convert555);
					break;
				case PixelFormat.Format16bppArgb1555:
					CopyRows(scan0, stride, width, height, 2, Convert1555);
					break;
				case PixelFormat.Format16bppRgb565:
					CopyRows(scan0, stride, width, height, 2, Convert565);
					break;
				default:
					throw new NotSupportedException("Unsupported source pixel format: " + format);
			}
		}

		private delegate void PixelConverter(byte[] source, int sourceOffset, byte[] destination, int destinationOffset);

		private void CopyRows(IntPtr scan0, int stride, int width, int height, int bytesPerPixel, PixelConverter converter)
		{
			byte[] row = new byte[stride];
			byte[] target = new byte[Native.RowBytes];
			IntPtr pixels = Native.GetPixels();

			for (int y = 0; y < height; y++)
			{
				Marshal.Copy(scan0 + y * stride, row, 0, stride);
				for (int x = 0; x < width; x++)
				{
					converter(row, x * bytesPerPixel, target, x * 4);
				}

				Marshal.Copy(target, 0, pixels + y * Native.RowBytes, Native.RowBytes);
			}
		}

		// The shim stores BGRA, which is the order GDI+ uses in memory for 32bpp ARGB.
		private static void Convert32(byte[] source, int i, byte[] destination, int j)
		{
			destination[j] = source[i];
			destination[j + 1] = source[i + 1];
			destination[j + 2] = source[i + 2];
			destination[j + 3] = source[i + 3];
		}

		private static void Convert24(byte[] source, int i, byte[] destination, int j)
		{
			destination[j] = source[i];
			destination[j + 1] = source[i + 1];
			destination[j + 2] = source[i + 2];
			destination[j + 3] = 255;
		}

		private static void Convert555(byte[] source, int i, byte[] destination, int j)
		{
			Unpack555(source, i, destination, j);
			destination[j + 3] = 255;
		}

		private static void Convert1555(byte[] source, int i, byte[] destination, int j)
		{
			Unpack555(source, i, destination, j);
			destination[j + 3] = (source[i + 1] & 0x80) != 0 ? (byte)255 : (byte)0;
		}

		private static void Unpack555(byte[] source, int i, byte[] destination, int j)
		{
			int value = source[i] | (source[i + 1] << 8);
			int r = (value >> 10) & 0x1F;
			int g = (value >> 5) & 0x1F;
			int b = value & 0x1F;
			// Replicate the high bits into the low ones so that 31 maps to 255, not 248.
			destination[j] = (byte)((b << 3) | (b >> 2));
			destination[j + 1] = (byte)((g << 3) | (g >> 2));
			destination[j + 2] = (byte)((r << 3) | (r >> 2));
		}

		private static void Convert565(byte[] source, int i, byte[] destination, int j)
		{
			int value = source[i] | (source[i + 1] << 8);
			int r = (value >> 11) & 0x1F;
			int g = (value >> 5) & 0x3F;
			int b = value & 0x1F;
			destination[j] = (byte)((b << 3) | (b >> 2));
			destination[j + 1] = (byte)((g << 2) | (g >> 4));
			destination[j + 2] = (byte)((r << 3) | (r >> 2));
			destination[j + 3] = 255;
		}

		private void ExpandIndexed()
		{
			Color[] entries = palette.Entries;
			byte[] target = new byte[Native.RowBytes];
			IntPtr pixels = Native.GetPixels();

			for (int y = 0; y < Native.Height; y++)
			{
				for (int x = 0; x < Native.Width; x++)
				{
					int index = indexedPixels[y * indexedStride + x];
					Color color = index < entries.Length ? entries[index] : Color.Black;
					target[x * 4] = color.B;
					target[x * 4 + 1] = color.G;
					target[x * 4 + 2] = color.R;
					target[x * 4 + 3] = color.A;
				}

				Marshal.Copy(target, 0, pixels + y * Native.RowBytes, Native.RowBytes);
			}
		}

		/// <summary>Returns a pointer to the raw pixel data.</summary>
		public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
		{
			Sync();
			return new BitmapData
			{
				Scan0 = Native.GetPixels(),
				Stride = Native.RowBytes,
				Width = Native.Width,
				Height = Native.Height,
				PixelFormat = PixelFormat.Format32bppArgb
			};
		}

		/// <summary>Releases pixel data obtained by LockBits. Skia pixels are always directly addressable, so this is a no-op.</summary>
		public void UnlockBits(BitmapData data)
		{
		}

		/// <summary>Gets the colour of a single pixel.</summary>
		public Color GetPixel(int x, int y)
		{
			Sync();
			SKColor c = Native.GetPixel(x, y);
			return Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
		}

		/// <summary>Sets the colour of a single pixel.</summary>
		public void SetPixel(int x, int y, Color color)
		{
			Native.SetPixel(x, y, new SKColor(color.R, color.G, color.B, color.A));
		}

		/// <summary>Creates a copy of a rectangular region of this bitmap.</summary>
		public Bitmap Clone(Rectangle rect, PixelFormat format)
		{
			Sync();
			SKBitmap copy = new SKBitmap(InfoFor(rect.Width, rect.Height));
			using (SKCanvas canvas = new SKCanvas(copy))
			{
				canvas.Clear(SKColors.Transparent);
				using (SKImage image = SKImage.FromBitmap(Native))
				{
					canvas.DrawImage(image,
						new SKRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height),
						new SKRect(0, 0, rect.Width, rect.Height));
				}
			}

			return new Bitmap(copy);
		}

		/// <summary>Creates a copy of this bitmap.</summary>
		public object Clone()
		{
			return Clone(new Rectangle(0, 0, Width, Height), PixelFormat.Format32bppArgb);
		}

		/// <summary>Writes the bitmap to a stream.</summary>
		public void Save(Stream stream, ImageFormat format)
		{
			Sync();
			SKEncodedImageFormat encoded = format != null && format.ToString() == "Jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png;
			using (SKImage image = SKImage.FromBitmap(Native))
			using (SKData data = image.Encode(encoded, 90))
			{
				data.SaveTo(stream);
			}
		}

		/// <summary>Writes the bitmap to a file.</summary>
		public void Save(string fileName, ImageFormat format)
		{
			using (FileStream stream = File.Create(fileName))
			{
				Save(stream, format);
			}
		}

		/// <summary>Writes the bitmap to a file as a PNG.</summary>
		public void Save(string fileName)
		{
			Save(fileName, ImageFormat.Png);
		}

		/// <inheritdoc />
		public override void Dispose()
		{
			if (!disposed)
			{
				disposed = true;
				Native.Dispose();
			}
		}
	}

	/// <summary>Holds the colour palette of an indexed image.</summary>
	public sealed class ColorPalette
	{
		/// <summary>The palette entries.</summary>
		public Color[] Entries { get; }

		internal ColorPalette(Color[] entries)
		{
			Entries = entries;
		}
	}
}
