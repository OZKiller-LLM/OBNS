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

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>
	/// Windows bitmaps, decoded in C#: BVE and OpenBVE content uses them more than any other format. Palettes of 1, 4
	/// and 8 bits (with RLE4 and RLE8), 16 bits (555, 565 or bit fields), 24 and 32 bits (alpha only where the header
	/// gives an alpha mask, as Skia reads them on Android), rows bottom-up or top-down.
	/// </summary>
	internal static class BmpDecoder
	{
		private const int BI_RGB = 0, BI_RLE8 = 1, BI_RLE4 = 2, BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;

		internal static Bitmap Decode(byte[] d, string source)
		{
			try
			{
				return DecodeCore(d);
			}
			catch (Exception e) when (!(e is OutOfMemoryException))
			{
				throw new ArgumentException("The bitmap could not be decoded (" + e.Message + "): " + source);
			}
		}

		private static int I32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
		private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);

		private static Bitmap DecodeCore(byte[] d)
		{
			int dataOffset = I32(d, 10);
			int headerSize = I32(d, 14);
			int width, height, bpp, compression = BI_RGB, colours = 0;
			uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
			if (headerSize == 12)
			{
				// OS/2 BITMAPCOREHEADER: 16-bit sizes, palette entries of 3 bytes.
				width = U16(d, 18);
				height = (short)U16(d, 20);
				bpp = U16(d, 24);
			}
			else
			{
				width = I32(d, 18);
				height = I32(d, 22);
				bpp = U16(d, 28);
				compression = I32(d, 30);
				colours = I32(d, 46);
				if (headerSize >= 52 || compression == BI_BITFIELDS || compression == BI_ALPHABITFIELDS)
				{
					// The masks follow a 40-byte header, or are part of a V2 header and later.
					int m = 14 + 40;
					rMask = (uint)I32(d, m);
					gMask = (uint)I32(d, m + 4);
					bMask = (uint)I32(d, m + 8);
					if (headerSize >= 56 || compression == BI_ALPHABITFIELDS)
					{
						aMask = (uint)I32(d, m + 12);
					}
				}
			}

			bool topDown = height < 0;
			height = Math.Abs(height);
			if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
			{
				throw new FormatException("size " + width + " x " + height);
			}

			byte[] palette = null;
			int paletteEntrySize = headerSize == 12 ? 3 : 4;
			if (bpp <= 8)
			{
				int count = colours != 0 ? colours : 1 << bpp;
				palette = new byte[256 * 4];
				int p = 14 + headerSize + ((compression == BI_BITFIELDS && headerSize == 40) ? 12 : 0);
				for (int i = 0; i < count && i < 256 && p + i * paletteEntrySize + 2 < d.Length; i++)
				{
					palette[i * 4] = d[p + i * paletteEntrySize];
					palette[i * 4 + 1] = d[p + i * paletteEntrySize + 1];
					palette[i * 4 + 2] = d[p + i * paletteEntrySize + 2];
					palette[i * 4 + 3] = 255;
				}
			}

			byte[] o = new byte[width * height * 4];
			if (compression == BI_RLE8 || compression == BI_RLE4)
			{
				DecodeRle(d, dataOffset, width, height, compression == BI_RLE4, palette, o, topDown);
				return Bitmap.FromBgra(width, height, o);
			}

			int stride = ((width * bpp + 31) / 32) * 4;
			if (bpp == 16 && rMask == 0)
			{
				rMask = 0x7C00;
				gMask = 0x03E0;
				bMask = 0x001F;
			}
			else if (bpp == 32 && rMask == 0)
			{
				rMask = 0x00FF0000;
				gMask = 0x0000FF00;
				bMask = 0x000000FF;
			}

			Channel r = new Channel(rMask), g = new Channel(gMask), b = new Channel(bMask), a = new Channel(aMask);
			for (int y = 0; y < height; y++)
			{
				int row = dataOffset + (topDown ? y : height - 1 - y) * stride;
				int t = y * width * 4;
				for (int x = 0; x < width; x++, t += 4)
				{
					switch (bpp)
					{
						case 1:
						case 2:
						case 4:
						case 8:
						{
							int bit = x * bpp;
							int value = (d[row + bit / 8] >> (8 - bpp - bit % 8)) & ((1 << bpp) - 1);
							o[t] = palette[value * 4];
							o[t + 1] = palette[value * 4 + 1];
							o[t + 2] = palette[value * 4 + 2];
							o[t + 3] = 255;
							break;
						}
						case 24:
							o[t] = d[row + x * 3];
							o[t + 1] = d[row + x * 3 + 1];
							o[t + 2] = d[row + x * 3 + 2];
							o[t + 3] = 255;
							break;
						case 16:
						case 32:
						{
							uint v = bpp == 16 ? (uint)U16(d, row + x * 2) : (uint)I32(d, row + x * 4);
							o[t] = b.Get(v);
							o[t + 1] = g.Get(v);
							o[t + 2] = r.Get(v);
							o[t + 3] = aMask != 0 ? a.Get(v) : (byte)255;
							break;
						}
						default:
							throw new FormatException(bpp + " bits a pixel");
					}
				}
			}

			return Bitmap.FromBgra(width, height, o);
		}

		/// <summary>One colour channel of a bit-field pixel, scaled to 8 bits.</summary>
		private readonly struct Channel
		{
			private readonly uint mask;
			private readonly int shift, bits;

			internal Channel(uint mask)
			{
				this.mask = mask;
				shift = 0;
				bits = 0;
				if (mask == 0)
				{
					return;
				}

				while (((mask >> shift) & 1) == 0)
				{
					shift++;
				}

				while (((mask >> (shift + bits)) & 1) != 0 && shift + bits < 32)
				{
					bits++;
				}
			}

			internal byte Get(uint value)
			{
				if (bits == 0)
				{
					return 0;
				}

				uint v = (value & mask) >> shift;
				return bits >= 8 ? (byte)(v >> (bits - 8)) : (byte)((v * 255 + ((1u << bits) - 1) / 2) / ((1u << bits) - 1));
			}
		}

		private static void DecodeRle(byte[] d, int p, int width, int height, bool four, byte[] palette, byte[] o, bool topDown)
		{
			int x = 0, y = 0;
			void Put(int index)
			{
				if (x < width && y < height)
				{
					int t = ((topDown ? y : height - 1 - y) * width + x) * 4;
					o[t] = palette[index * 4];
					o[t + 1] = palette[index * 4 + 1];
					o[t + 2] = palette[index * 4 + 2];
					o[t + 3] = 255;
				}

				x++;
			}

			while (p + 1 < d.Length && y < height)
			{
				int count = d[p++], value = d[p++];
				if (count > 0)
				{
					for (int i = 0; i < count; i++)
					{
						Put(four ? ((i & 1) == 0 ? value >> 4 : value & 15) : value);
					}

					continue;
				}

				switch (value)
				{
					case 0:
						x = 0;
						y++;
						break;
					case 1:
						return;
					case 2:
						x += d[p++];
						y += d[p++];
						break;
					default:
						for (int i = 0; i < value; i++)
						{
							int b = four ? d[p + i / 2] : d[p + i];
							Put(four ? ((i & 1) == 0 ? b >> 4 : b & 15) : b);
						}

						int used = four ? (value + 1) / 2 : value;
						p += used + (used & 1);
						break;
				}
			}
		}
	}
}
