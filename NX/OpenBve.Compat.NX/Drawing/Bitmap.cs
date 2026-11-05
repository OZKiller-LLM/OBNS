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

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>Base class for the image shims.</summary>
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

		// Frames, as on Android: the first frame of an animated image only.

		/// <summary>The image's frame dimensions: the time dimension only.</summary>
		public Guid[] FrameDimensionsList => new[] { Imaging.FrameDimension.Time.Guid };

		/// <summary>The number of frames in a dimension: one.</summary>
		public int GetFrameCount(Imaging.FrameDimension dimension) => 1;

		/// <summary>Selects a frame: there is only the first.</summary>
		public int SelectActiveFrame(Imaging.FrameDimension dimension, int frameIndex) => 0;
	}

	/// <summary>The Switch's replacement for System.Drawing.Bitmap.</summary>
	/// <remarks>
	/// Pixels are unpremultiplied BGRA8888, the layout OpenBVE's texture pipeline expects from Format32bppArgb, as on
	/// Android. They live in native memory, outside the managed heap the collector scans, and are freed on Dispose (or
	/// by the finaliser of a bitmap nobody disposed).
	/// </remarks>
	public sealed unsafe class Bitmap : Image
	{
		private IntPtr pixels;
		private readonly bool fromDecoder;
		private readonly int width, height;
		private bool disposed;

		/// <summary>The first pixel; rows are <see cref="Stride"/> bytes apart.</summary>
		internal IntPtr Pixels => pixels;

		/// <summary>Bytes from one row to the next.</summary>
		internal int Stride => width * 4;

		/// <summary>Drawing goes straight into the pixels here, so there is never anything to bring across first.</summary>
		internal void Sync()
		{
		}

		private Bitmap(int width, int height, IntPtr pixels, bool fromDecoder)
		{
			this.width = width;
			this.height = height;
			this.pixels = pixels;
			this.fromDecoder = fromDecoder;
		}

		private static IntPtr Allocate(int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				throw new ArgumentException("A bitmap needs a positive size: " + width + " x " + height);
			}

			return (IntPtr)NativeMemory.AllocZeroed((nuint)width * (nuint)height * 4);
		}

		/// <summary>Creates a blank (transparent) bitmap.</summary>
		public Bitmap(int width, int height) : this(width, height, Allocate(width, height), false)
		{
		}

		/// <summary>Creates a blank bitmap. The pixel format is accepted for source compatibility and always treated as 32bpp BGRA.</summary>
		public Bitmap(int width, int height, PixelFormat format) : this(width, height)
		{
		}

		/// <summary>Loads a bitmap from a file.</summary>
		public Bitmap(string fileName) : this(DecodeFile(fileName))
		{
		}

		/// <summary>Loads a bitmap from a stream.</summary>
		public Bitmap(Stream stream) : this(Decode(ReadAll(stream), "<stream>"))
		{
		}

		/// <summary>A copy of an image.</summary>
		public Bitmap(Image original) : this(CopyOf(original))
		{
		}

		// Takes over a decoded or copied image's pixels.
		private Bitmap(Bitmap source) : this(source.width, source.height, source.pixels, source.fromDecoder)
		{
			source.pixels = IntPtr.Zero;
			source.disposed = true;
			GC.SuppressFinalize(source);
		}

		private static Bitmap CopyOf(Image original)
		{
			if (!(original is Bitmap bitmap))
			{
				throw new ArgumentException("Only Bitmap is supported by the Switch graphics shim.", nameof(original));
			}

			Bitmap copy = new Bitmap(bitmap.width, bitmap.height);
			Buffer.MemoryCopy((void*)bitmap.pixels, (void*)copy.pixels, (long)bitmap.Stride * bitmap.height, (long)bitmap.Stride * bitmap.height);
			return copy;
		}

		private static byte[] ReadAll(Stream stream)
		{
			if (stream is MemoryStream memory && memory.TryGetBuffer(out ArraySegment<byte> segment) && segment.Offset == 0 && memory.Position == 0 && segment.Count == memory.Length)
			{
				return segment.Array.Length == segment.Count ? segment.Array : memory.ToArray();
			}

			using (MemoryStream copy = new MemoryStream())
			{
				stream.CopyTo(copy);
				return copy.ToArray();
			}
		}

		private static Bitmap DecodeFile(string fileName)
		{
			return Decode(File.ReadAllBytes(fileName), fileName);
		}

		/*
		 * The size hint for images decoded on this thread, as on Android (set by the host around a texture decode when a
		 * texture size limit is in effect): an image at least twice the limit is halved here, in native memory, before any
		 * caller copies its pixels. Filtered (a 2 x 2 box, weighted by alpha, as Skia's linear and mipmapped resize comes
		 * to at exact halves) or nearest-pixel when the texture is colour-keyed, so the key colour survives exactly.
		 */
		[ThreadStatic] public static int DecodeMaxSize;
		[ThreadStatic] public static bool DecodeNearest;

		private static Bitmap Decode(byte[] data, string source)
		{
			Bitmap full = DecodeFull(data, source);
			int max = DecodeMaxSize;
			while (max > 0 && (full.width >= 2 * max || full.height >= 2 * max) && full.width >= 2 && full.height >= 2)
			{
				Bitmap half = full.Halved(DecodeNearest);
				full.Dispose();
				full = half;
			}

			return full;
		}

		private static Bitmap DecodeFull(byte[] data, string source)
		{
			if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
			{
				return BmpDecoder.Decode(data, source);
			}

			fixed (byte* p = data)
			{
				int result = Native.ImageDecode(p, data.Length, out int w, out int h, out IntPtr decoded);
				if (result != 0 || decoded == IntPtr.Zero)
				{
					throw new ArgumentException((result == 1 ? "The image's format isn't one the Switch build reads (PNG, JPEG, GIF, BMP): " : "The image could not be decoded: ") + source);
				}

				return new Bitmap(w, h, decoded, true);
			}
		}

		/// <summary>Creates a bitmap holding a copy of BGRA pixels made elsewhere (BmpDecoder).</summary>
		internal static Bitmap FromBgra(int width, int height, byte[] bgra)
		{
			Bitmap bitmap = new Bitmap(width, height);
			Marshal.Copy(bgra, 0, bitmap.pixels, width * height * 4);
			return bitmap;
		}

		private Bitmap Halved(bool nearest)
		{
			int w = Math.Max(1, width / 2), h = Math.Max(1, height / 2);
			Bitmap half = new Bitmap(w, h);
			byte* s = (byte*)pixels, d = (byte*)half.pixels;
			int stride = Stride;
			for (int y = 0; y < h; y++)
			{
				byte* row0 = s + (2 * y) * stride, row1 = s + Math.Min(2 * y + 1, height - 1) * stride;
				byte* o = d + y * w * 4;
				for (int x = 0; x < w; x++, o += 4)
				{
					int x0 = 2 * x * 4, x1 = Math.Min(2 * x + 1, width - 1) * 4;
					if (nearest)
					{
						*(uint*)o = *(uint*)(row0 + x0);
						continue;
					}

					int a0 = row0[x0 + 3], a1 = row0[x1 + 3], a2 = row1[x0 + 3], a3 = row1[x1 + 3];
					int alpha = a0 + a1 + a2 + a3;
					if (alpha == 0)
					{
						*(uint*)o = 0;
						continue;
					}

					for (int c = 0; c < 3; c++)
					{
						o[c] = (byte)((row0[x0 + c] * a0 + row0[x1 + c] * a1 + row1[x0 + c] * a2 + row1[x1 + c] * a3 + alpha / 2) / alpha);
					}

					o[3] = (byte)((alpha + 2) / 4);
				}
			}

			return half;
		}

		/// <inheritdoc />
		public override int Width => width;

		/// <inheritdoc />
		public override int Height => height;

		/// <summary>The pixel format: always 32bpp BGRA, whatever the source, as LockBits hands it back.</summary>
		public PixelFormat PixelFormat => PixelFormat.Format32bppArgb;

		private byte[] indexedPixels;
		private int indexedStride;
		private ColorPalette palette = new ColorPalette(new Color[0]);

		/// <summary>
		/// The colour palette: empty unless this bitmap was built from indexed data, as the TGA parser does (it reads the
		/// palette back, fills it in and assigns it again, at which point the indices are expanded through it).
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
		public Bitmap(int width, int height, int stride, PixelFormat format, IntPtr scan0) : this(width, height)
		{
			switch (format)
			{
				case PixelFormat.Format8bppIndexed:
					// Indices are kept until a palette arrives; 256 black entries stand in until then, as GDI+ has.
					indexedStride = stride;
					indexedPixels = new byte[stride * height];
					Marshal.Copy(scan0, indexedPixels, 0, indexedPixels.Length);
					palette = new ColorPalette(new Color[256]);
					ExpandIndexed();
					break;
				case PixelFormat.Format32bppArgb:
				case PixelFormat.Format32bppPArgb:
				case PixelFormat.Format32bppRgb:
					CopyRows(scan0, stride, 4, Convert32);
					break;
				case PixelFormat.Format24bppRgb:
					CopyRows(scan0, stride, 3, Convert24);
					break;
				case PixelFormat.Format16bppRgb555:
					CopyRows(scan0, stride, 2, Convert555);
					break;
				case PixelFormat.Format16bppArgb1555:
					CopyRows(scan0, stride, 2, Convert1555);
					break;
				case PixelFormat.Format16bppRgb565:
					CopyRows(scan0, stride, 2, Convert565);
					break;
				default:
					throw new NotSupportedException("Unsupported source pixel format: " + format);
			}
		}

		private delegate void PixelConverter(byte* source, byte* destination);

		private void CopyRows(IntPtr scan0, int stride, int bytesPerPixel, PixelConverter converter)
		{
			for (int y = 0; y < height; y++)
			{
				byte* row = (byte*)scan0 + (long)y * stride;
				byte* target = (byte*)pixels + (long)y * Stride;
				for (int x = 0; x < width; x++)
				{
					converter(row + x * bytesPerPixel, target + x * 4);
				}
			}
		}

		// BGRA, the order GDI+ keeps 32bpp ARGB in memory.
		private static void Convert32(byte* s, byte* d)
		{
			*(uint*)d = *(uint*)s;
		}

		private static void Convert24(byte* s, byte* d)
		{
			d[0] = s[0];
			d[1] = s[1];
			d[2] = s[2];
			d[3] = 255;
		}

		private static void Convert555(byte* s, byte* d)
		{
			Unpack555(s, d);
			d[3] = 255;
		}

		private static void Convert1555(byte* s, byte* d)
		{
			Unpack555(s, d);
			d[3] = (s[1] & 0x80) != 0 ? (byte)255 : (byte)0;
		}

		private static void Unpack555(byte* s, byte* d)
		{
			int value = s[0] | (s[1] << 8);
			int r = (value >> 10) & 0x1F, g = (value >> 5) & 0x1F, b = value & 0x1F;
			// The high bits replicated into the low ones, so 31 maps to 255, not 248.
			d[0] = (byte)((b << 3) | (b >> 2));
			d[1] = (byte)((g << 3) | (g >> 2));
			d[2] = (byte)((r << 3) | (r >> 2));
		}

		private static void Convert565(byte* s, byte* d)
		{
			int value = s[0] | (s[1] << 8);
			int r = (value >> 11) & 0x1F, g = (value >> 5) & 0x3F, b = value & 0x1F;
			d[0] = (byte)((b << 3) | (b >> 2));
			d[1] = (byte)((g << 2) | (g >> 4));
			d[2] = (byte)((r << 3) | (r >> 2));
			d[3] = 255;
		}

		private void ExpandIndexed()
		{
			Color[] entries = palette.Entries;
			for (int y = 0; y < height; y++)
			{
				byte* target = (byte*)pixels + (long)y * Stride;
				for (int x = 0; x < width; x++, target += 4)
				{
					int index = indexedPixels[y * indexedStride + x];
					Color color = index < entries.Length ? entries[index] : Color.Black;
					target[0] = color.B;
					target[1] = color.G;
					target[2] = color.R;
					target[3] = color.A;
				}
			}
		}

		/// <summary>Returns a pointer to the pixels.</summary>
		public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
		{
			return new BitmapData
			{
				Scan0 = pixels,
				Stride = Stride,
				Width = width,
				Height = height,
				PixelFormat = PixelFormat.Format32bppArgb
			};
		}

		/// <summary>Releases pixel data from LockBits: the pixels are always addressable, so nothing to do.</summary>
		public void UnlockBits(BitmapData data)
		{
		}

		/// <summary>Gets the colour of a pixel.</summary>
		public Color GetPixel(int x, int y)
		{
			byte* p = (byte*)pixels + (long)y * Stride + x * 4;
			return Color.FromArgb(p[3], p[2], p[1], p[0]);
		}

		/// <summary>Sets the colour of a pixel.</summary>
		public void SetPixel(int x, int y, Color color)
		{
			byte* p = (byte*)pixels + (long)y * Stride + x * 4;
			p[0] = color.B;
			p[1] = color.G;
			p[2] = color.R;
			p[3] = color.A;
		}

		/// <summary>A copy of a rectangle of this bitmap; any part outside it is transparent.</summary>
		public Bitmap Clone(Rectangle rect, PixelFormat format)
		{
			Bitmap copy = new Bitmap(rect.Width, rect.Height);
			for (int y = 0; y < rect.Height; y++)
			{
				int sy = rect.Y + y;
				if (sy < 0 || sy >= height)
				{
					continue;
				}

				int x0 = Math.Max(0, -rect.X), x1 = Math.Min(rect.Width, width - rect.X);
				if (x1 > x0)
				{
					Buffer.MemoryCopy((byte*)pixels + (long)sy * Stride + (rect.X + x0) * 4, (byte*)copy.pixels + (long)y * copy.Stride + x0 * 4, (x1 - x0) * 4, (x1 - x0) * 4);
				}
			}

			return copy;
		}

		/// <summary>A copy of this bitmap.</summary>
		public object Clone()
		{
			return Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb);
		}

		/// <summary>Writes the bitmap to a stream, as a PNG or (for <see cref="ImageFormat.Jpeg"/>) a JPEG.</summary>
		public void Save(Stream stream, ImageFormat format)
		{
			string temporary = Path.Combine(Path.GetTempPath(), "obns-save-" + Guid.NewGuid().ToString("N"));
			try
			{
				Save(temporary, format);
				using (FileStream file = File.OpenRead(temporary))
				{
					file.CopyTo(stream);
				}
			}
			finally
			{
				File.Delete(temporary);
			}
		}

		/// <summary>Writes the bitmap to a file, as a PNG or (for <see cref="ImageFormat.Jpeg"/>) a JPEG.</summary>
		public void Save(string fileName, ImageFormat format)
		{
			bool jpeg = format != null && format.ToString() == "Jpeg";
			if (Native.ImageSave(fileName, jpeg ? 1 : 0, pixels, width, height, Stride) != 0)
			{
				throw new IOException("The image could not be written: " + fileName);
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
			Free();
			GC.SuppressFinalize(this);
		}

		~Bitmap()
		{
			Free();
		}

		private void Free()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			if (pixels != IntPtr.Zero)
			{
				if (fromDecoder)
				{
					Native.ImageFree(pixels);
				}
				else
				{
					NativeMemory.Free((void*)pixels);
				}

				pixels = IntPtr.Zero;
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
