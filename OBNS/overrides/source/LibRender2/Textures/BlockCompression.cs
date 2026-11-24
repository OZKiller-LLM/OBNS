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

// OBNS: not an upstream file. Potato mode's compressed textures for OpenGL (the user's choice, 2026-11-11, for a Broadwell
// MacBook Pro, whose GPU shares the CPU's memory and its bandwidth): BC1 (4 bits a texel) for opaque textures and BC3
// (8 bits) for those with alpha, against 32 bits uncompressed, with their mipmap chains made here. The encoder is the
// Switch's (NX/runner/source/obns_bc.c, written for OBNS), in C#: the block's principal colour axis, its extremes pulled
// in by 1/16, each texel the nearest of the palette. A cut-out (alpha only 0 or 255) is BC3 with its hidden colours spread
// in from the edges and its mipmaps' alpha kept to 0 or 255, so filtering doesn't darken its edges. Encoding a texture
// takes longer than decoding it, so each result is kept on disk, named by a hash of the decoded pixels: a texture is
// encoded once, on its first load.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using OpenBveApi.Textures;

namespace LibRender2.Textures
{
	public static class ObnsBlockCompression
	{
		/// <summary>Whether OpenGL's textures are compressed (Potato mode, set as a game starts).</summary>
		public static bool Enabled;

		/// <summary>The folder the compressed textures are kept in (Potato mode sets it); none: encoded on each load.</summary>
		public static string CacheFolder;

		/// <summary>The cache files' format: a change to the encoder or the file makes the old ones unused.</summary>
		private const int Version = 1;

		/// <summary>A compressed texture: its size, format and mipmap levels, the first the largest.</summary>
		public sealed class Image
		{
			public int Width;
			public int Height;
			/// <summary>BC3 (with alpha) rather than BC1 (opaque).</summary>
			public bool Bc3;
			public byte[][] Levels;
		}

		/// <summary>Worker decodes' compressed textures, for their upload (TextureManager).</summary>
		private static readonly ConditionalWeakTable<Texture, Image> prepared = new ConditionalWeakTable<Texture, Image>();

		private static int encoded, read;
		private static long savedBytes;

		/// <summary>How many textures were encoded and read from the cache since the last call, for the log.</summary>
		public static string TakeCounts()
		{
			string text = encoded + " encoded, " + read + " from the cache, " + (savedBytes >> 20) + " MB less than uncompressed";
			encoded = read = 0;
			savedBytes = 0;
			return text;
		}

		/// <summary>
		/// On a decode worker: the texture compressed ahead of its upload, so the main thread only sends it to the GPU. Its
		/// pixels are kept as they are (the upload uses them if this fails).
		/// </summary>
		public static void Prepare(Texture texture)
		{
			if (!Enabled || texture == null || texture.MultipleFrames)
			{
				return;
			}

			try
			{
				Image image = Compress(texture, texture.GetTransparencyType());
				if (image != null)
				{
					lock (prepared)
					{
						prepared.Remove(texture);
						prepared.Add(texture, image);
					}
				}
			}
			catch
			{
				// Uploaded uncompressed.
			}
		}

		/// <summary>The texture's compressed image: the one a worker made, else made now (while a game loads).</summary>
		public static Image Get(Texture texture, TextureTransparencyType transparency)
		{
			lock (prepared)
			{
				if (prepared.TryGetValue(texture, out Image image))
				{
					prepared.Remove(texture);
					return image;
				}
			}

			try
			{
				return Compress(texture, transparency);
			}
			catch
			{
				return null;
			}
		}

		/// <summary>The texture compressed, from the cache or encoded (and then kept there); null for a texture it can't take.</summary>
		private static Image Compress(Texture texture, TextureTransparencyType transparency)
		{
			int w = texture.Width, h = texture.Height;
			byte[] rgba = ToRgba(texture);
			if (rgba == null || w <= 0 || h <= 0)
			{
				return null;
			}

			bool bc3 = transparency != TextureTransparencyType.Opaque;
			bool cutout = transparency == TextureTransparencyType.Partial;
			string file = null;
			if (!string.IsNullOrEmpty(CacheFolder))
			{
				ulong hash = Hash(rgba, w, h, bc3, cutout);
				file = Path.Combine(CacheFolder, hash.ToString("x16") + ".bc");
				Image cached = ReadCache(file, w, h, bc3);
				if (cached != null)
				{
					System.Threading.Interlocked.Increment(ref read);
					System.Threading.Interlocked.Add(ref savedBytes, (long)w * h * 4 * 4 / 3 - Size(cached));
					return cached;
				}
			}

			Image image = new Image { Width = w, Height = h, Bc3 = bc3, Levels = Encode(rgba, w, h, bc3, cutout) };
			System.Threading.Interlocked.Increment(ref encoded);
			System.Threading.Interlocked.Add(ref savedBytes, (long)w * h * 4 * 4 / 3 - Size(image));
			if (file != null)
			{
				WriteCache(file, image);
			}

			return image;
		}

		private static long Size(Image image)
		{
			long size = 0;
			foreach (byte[] level in image.Levels)
			{
				size += level.Length;
			}

			return size;
		}

		// --- the cache ---

		private static Image ReadCache(string file, int w, int h, bool bc3)
		{
			try
			{
				if (!File.Exists(file))
				{
					return null;
				}

				using (BinaryReader reader = new BinaryReader(File.OpenRead(file)))
				{
					if (reader.ReadInt32() != 0x3143424F || reader.ReadInt32() != Version || reader.ReadInt32() != w || reader.ReadInt32() != h || reader.ReadBoolean() != bc3)
					{
						return null;
					}

					int count = reader.ReadInt32();
					if (count != Levels(w, h))
					{
						return null;
					}

					byte[][] levels = new byte[count][];
					int lw = w, lh = h;
					for (int l = 0; l < count; l++)
					{
						int size = LevelSize(lw, lh, bc3);
						levels[l] = reader.ReadBytes(size);
						if (levels[l].Length != size)
						{
							return null;
						}

						lw = Math.Max(1, lw / 2);
						lh = Math.Max(1, lh / 2);
					}

					return new Image { Width = w, Height = h, Bc3 = bc3, Levels = levels };
				}
			}
			catch
			{
				return null;
			}
		}

		private static void WriteCache(string file, Image image)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file));
				// Written whole under another name, then moved: another worker or a later game never reads half of one.
				string partial = file + "." + System.Threading.Thread.CurrentThread.ManagedThreadId + ".part";
				using (BinaryWriter writer = new BinaryWriter(File.Create(partial)))
				{
					writer.Write(0x3143424F); // "OBC1"
					writer.Write(Version);
					writer.Write(image.Width);
					writer.Write(image.Height);
					writer.Write(image.Bc3);
					writer.Write(image.Levels.Length);
					foreach (byte[] level in image.Levels)
					{
						writer.Write(level);
					}
				}

				if (File.Exists(file))
				{
					File.Delete(partial);
				}
				else
				{
					File.Move(partial, file);
				}
			}
			catch
			{
				// Encoded again next time.
			}
		}

		/// <summary>A 64-bit hash of the decoded pixels and how they are compressed.</summary>
		private static unsafe ulong Hash(byte[] rgba, int w, int h, bool bc3, bool cutout)
		{
			const ulong prime1 = 0x9E3779B185EBCA87UL, prime2 = 0xC2B2AE3D27D4EB4FUL;
			ulong hash = prime2 ^ (ulong)w * prime1 ^ (ulong)h << 32 ^ (bc3 ? 1UL : 0UL) ^ (cutout ? 2UL : 0UL) ^ (ulong)Version << 8;
			fixed (byte* bytes = rgba)
			{
				ulong* words = (ulong*)bytes;
				int count = rgba.Length / 8;
				for (int i = 0; i < count; i++)
				{
					hash ^= words[i] * prime1;
					hash = (hash << 31 | hash >> 33) * prime2;
				}

				for (int i = count * 8; i < rgba.Length; i++)
				{
					hash = (hash ^ bytes[i]) * prime1;
				}
			}

			hash ^= hash >> 29;
			hash *= prime1;
			hash ^= hash >> 32;
			return hash;
		}

		/// <summary>
		/// The texture resampled (bilinear) to the next whole number of 4x4 blocks across and down, as RGBA: Direct3D's and
		/// Metal's compressed images must be whole blocks, and about two in five of the Tuen Ma Line's textures are not
		/// (2026-11-25). A cut-out's alpha is kept to 0 or 255. Faces sample a texture by fractions of its size, so it is
		/// drawn as before. Null if its pixels can't be read.
		/// </summary>
		public static Texture ToWholeBlocks(Texture texture, TextureTransparencyType transparency)
		{
			int w = texture.Width, h = texture.Height;
			byte[] rgba = ToRgba(texture);
			if (rgba == null || w <= 0 || h <= 0)
			{
				return null;
			}

			int nw = (w + 3) / 4 * 4, nh = (h + 3) / 4 * 4;
			bool cutout = transparency == TextureTransparencyType.Partial;
			byte[] result = new byte[nw * nh * 4];
			for (int y = 0; y < nh; y++)
			{
				// The target pixel's centre in the source's pixels.
				double sy = Math.Max(0.0, (y + 0.5) * h / nh - 0.5);
				int y0 = Math.Min(h - 1, (int)sy), y1 = Math.Min(h - 1, y0 + 1);
				double fy = sy - y0;
				for (int x = 0; x < nw; x++)
				{
					double sx = Math.Max(0.0, (x + 0.5) * w / nw - 0.5);
					int x0 = Math.Min(w - 1, (int)sx), x1 = Math.Min(w - 1, x0 + 1);
					double fx = sx - x0;
					int a = (y0 * w + x0) * 4, b = (y0 * w + x1) * 4, c = (y1 * w + x0) * 4, d = (y1 * w + x1) * 4, o = (y * nw + x) * 4;
					for (int k = 0; k < 4; k++)
					{
						double top = rgba[a + k] + (rgba[b + k] - rgba[a + k]) * fx, bottom = rgba[c + k] + (rgba[d + k] - rgba[c + k]) * fx;
						result[o + k] = (byte)Math.Round(top + (bottom - top) * fy);
					}

					if (cutout)
					{
						result[o + 3] = result[o + 3] >= 128 ? (byte)255 : (byte)0;
					}
				}
			}

			return new Texture(nw, nh, PixelFormat.RGBAlpha, result, (OpenBveApi.Colors.Color24[])null);
		}

		// --- the pixels as RGBA ---

		/// <summary>The texture's pixels, tightly packed RGBA, whatever its format; null for one it doesn't know.</summary>
		private static byte[] ToRgba(Texture texture)
		{
			byte[] bytes = texture.Bytes;
			int count = texture.Width * texture.Height;
			if (bytes == null || count <= 0)
			{
				return null;
			}

			byte[] rgba;
			switch (texture.PixelFormat)
			{
				case PixelFormat.RGBAlpha:
					return bytes.Length >= count * 4 ? bytes : null;
				case PixelFormat.RGB:
					if (bytes.Length < count * 3)
					{
						return null;
					}

					rgba = new byte[count * 4];
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = bytes[p * 3];
						rgba[p * 4 + 1] = bytes[p * 3 + 1];
						rgba[p * 4 + 2] = bytes[p * 3 + 2];
						rgba[p * 4 + 3] = 255;
					}

					return rgba;
				case PixelFormat.Grayscale:
					if (bytes.Length < count)
					{
						return null;
					}

					rgba = new byte[count * 4];
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = bytes[p];
						rgba[p * 4 + 3] = 255;
					}

					return rgba;
				case PixelFormat.GrayscaleAlpha:
					if (bytes.Length < count * 2)
					{
						return null;
					}

					rgba = new byte[count * 4];
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = bytes[p * 2];
						rgba[p * 4 + 3] = bytes[p * 2 + 1];
					}

					return rgba;
				case PixelFormat.Paletted:
					OpenBveApi.Colors.Color32[] palette = texture.Palette32;
					if (bytes.Length < count || palette == null)
					{
						return null;
					}

					rgba = new byte[count * 4];
					for (int p = 0; p < count; p++)
					{
						int index = bytes[p];
						OpenBveApi.Colors.Color32 c = index < palette.Length ? palette[index] : new OpenBveApi.Colors.Color32(0, 0, 0, 255);
						rgba[p * 4] = c.R;
						rgba[p * 4 + 1] = c.G;
						rgba[p * 4 + 2] = c.B;
						rgba[p * 4 + 3] = c.A;
					}

					return rgba;
				default:
					return null;
			}
		}

		// --- the encoder (obns_bc.c's) ---

		/// <summary>The mipmap chain's level count for a size, down to 1x1.</summary>
		public static int Levels(int w, int h)
		{
			int levels = 1, m = Math.Max(w, h);
			while (m > 1)
			{
				m >>= 1;
				levels++;
			}

			return levels;
		}

		/// <summary>One level's bytes: its 4x4 blocks, 16 bytes each for BC3 and 8 for BC1.</summary>
		public static int LevelSize(int w, int h, bool bc3)
		{
			return (w + 3) / 4 * ((h + 3) / 4) * (bc3 ? 16 : 8);
		}

		/// <summary>The image and its mipmaps, each level's blocks row by row.</summary>
		private static byte[][] Encode(byte[] rgba, int w, int h, bool bc3, bool cutout)
		{
			int count = Levels(w, h);
			byte[][] levels = new byte[count][];
			byte[] level = rgba;
			if (bc3)
			{
				// A copy whose hidden colours are spread in from what can be seen (the texture's own pixels are left as they are).
				level = (byte[])rgba.Clone();
				Bleed(level, w, h, 16);
			}

			byte[] texels = new byte[64];
			for (int l = 0; l < count; l++)
			{
				levels[l] = new byte[LevelSize(w, h, bc3)];
				CompressLevel(level, w, h, bc3, levels[l], texels);
				if (l + 1 == count)
				{
					break;
				}

				int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
				level = Halve(level, w, h, nw, nh, cutout);
				w = nw;
				h = nh;
			}

			return levels;
		}

		private static void CompressLevel(byte[] rgba, int w, int h, bool bc3, byte[] output, byte[] texels)
		{
			int offset = 0;
			for (int by = 0; by < (h + 3) / 4; by++)
			{
				for (int bx = 0; bx < (w + 3) / 4; bx++)
				{
					for (int y = 0; y < 4; y++)
					{
						for (int x = 0; x < 4; x++)
						{
							// Past the edge: the nearest texel inside.
							int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
							Buffer.BlockCopy(rgba, (sy * w + sx) * 4, texels, (y * 4 + x) * 4, 4);
						}
					}

					if (bc3)
					{
						AlphaBlock(texels, output, offset);
						ColourBlock(texels, output, offset + 8, true);
						offset += 16;
					}
					else
					{
						ColourBlock(texels, output, offset, false);
						offset += 8;
					}
				}
			}
		}

		private static ushort To565(float r, float g, float b)
		{
			int r5 = Clamp((int)(r * 31.0f / 255.0f + 0.5f), 0, 31);
			int g6 = Clamp((int)(g * 63.0f / 255.0f + 0.5f), 0, 63);
			int b5 = Clamp((int)(b * 31.0f / 255.0f + 0.5f), 0, 31);
			return (ushort)(r5 << 11 | g6 << 5 | b5);
		}

		private static void From565(ushort v, int[] c, int at)
		{
			int r = v >> 11 & 31, g = v >> 5 & 63, b = v & 31;
			c[at] = r << 3 | r >> 2;
			c[at + 1] = g << 2 | g >> 4;
			c[at + 2] = b << 3 | b >> 2;
		}

		private static int Clamp(int v, int lo, int hi)
		{
			return v < lo ? lo : v > hi ? hi : v;
		}

		/// <summary>
		/// The colour block for 16 RGBA texels, four colours (BC1 opaque, or BC3's colour block, whose endpoints then come
		/// from the texels that can be seen, every texel still taking the nearest of the four).
		/// </summary>
		private static void ColourBlock(byte[] texels, byte[] output, int at, bool seenOnly)
		{
			int[] used = new int[16];
			int n = 0;
			for (int i = 0; i < 16; i++)
			{
				if (!seenOnly || texels[i * 4 + 3] != 0)
				{
					used[n++] = i;
				}
			}

			if (n == 0)
			{
				// Nothing seen in the block: its colours from all of it (spread in from the edges, see Bleed).
				for (int i = 0; i < 16; i++)
				{
					used[i] = i;
				}

				n = 16;
			}

			float m0 = 0, m1 = 0, m2 = 0;
			for (int k = 0; k < n; k++)
			{
				m0 += texels[used[k] * 4];
				m1 += texels[used[k] * 4 + 1];
				m2 += texels[used[k] * 4 + 2];
			}

			m0 /= n;
			m1 /= n;
			m2 /= n;
			float c00 = 0, c01 = 0, c02 = 0, c11 = 0, c12 = 0, c22 = 0;
			for (int k = 0; k < n; k++)
			{
				float r = texels[used[k] * 4] - m0, g = texels[used[k] * 4 + 1] - m1, b = texels[used[k] * 4 + 2] - m2;
				c00 += r * r;
				c01 += r * g;
				c02 += r * b;
				c11 += g * g;
				c12 += g * b;
				c22 += b * b;
			}

			float a0 = 0.9f, a1 = 1.0f, a2 = 0.7f;
			for (int it = 0; it < 4; it++)
			{
				float x = c00 * a0 + c01 * a1 + c02 * a2;
				float y = c01 * a0 + c11 * a1 + c12 * a2;
				float z = c02 * a0 + c12 * a1 + c22 * a2;
				float m = x * x + y * y + z * z;
				if (m < 1e-6f)
				{
					break;
				}

				float inv = 1.0f / (float)Math.Sqrt(m);
				a0 = x * inv;
				a1 = y * inv;
				a2 = z * inv;
			}

			float lo = 1e9f, hi = -1e9f;
			for (int k = 0; k < n; k++)
			{
				int t = used[k] * 4;
				float d = (texels[t] - m0) * a0 + (texels[t + 1] - m1) * a1 + (texels[t + 2] - m2) * a2;
				if (d < lo)
				{
					lo = d;
				}

				if (d > hi)
				{
					hi = d;
				}
			}

			float inset = (hi - lo) / 16.0f;
			lo += inset;
			hi -= inset;
			ushort c0 = To565(m0 + a0 * hi, m1 + a1 * hi, m2 + a2 * hi);
			ushort c1 = To565(m0 + a0 * lo, m1 + a1 * lo, m2 + a2 * lo);
			if (c0 < c1)
			{
				ushort s = c0;
				c0 = c1;
				c1 = s;
			}
			else if (c0 == c1)
			{
				// One colour: every index 0.
				output[at] = (byte)c0;
				output[at + 1] = (byte)(c0 >> 8);
				output[at + 2] = (byte)c1;
				output[at + 3] = (byte)(c1 >> 8);
				output[at + 4] = output[at + 5] = output[at + 6] = output[at + 7] = 0;
				return;
			}

			int[] p = new int[12];
			From565(c0, p, 0);
			From565(c1, p, 3);
			for (int c = 0; c < 3; c++)
			{
				p[6 + c] = (2 * p[c] + p[3 + c]) / 3;
				p[9 + c] = (p[c] + 2 * p[3 + c]) / 3;
			}

			uint indices = 0;
			for (int i = 0; i < 16; i++)
			{
				int t = i * 4, best = 0, bestD = int.MaxValue;
				for (int j = 0; j < 4; j++)
				{
					int dr = texels[t] - p[j * 3], dg = texels[t + 1] - p[j * 3 + 1], db = texels[t + 2] - p[j * 3 + 2];
					int d = dr * dr + dg * dg + db * db;
					if (d < bestD)
					{
						bestD = d;
						best = j;
					}
				}

				indices |= (uint)best << (2 * i);
			}

			output[at] = (byte)c0;
			output[at + 1] = (byte)(c0 >> 8);
			output[at + 2] = (byte)c1;
			output[at + 3] = (byte)(c1 >> 8);
			output[at + 4] = (byte)indices;
			output[at + 5] = (byte)(indices >> 8);
			output[at + 6] = (byte)(indices >> 16);
			output[at + 7] = (byte)(indices >> 24);
		}

		/// <summary>BC3's alpha block: eight levels between the block's least and greatest alpha.</summary>
		private static void AlphaBlock(byte[] texels, byte[] output, int at)
		{
			int lo = 255, hi = 0;
			for (int i = 0; i < 16; i++)
			{
				int a = texels[i * 4 + 3];
				if (a < lo)
				{
					lo = a;
				}

				if (a > hi)
				{
					hi = a;
				}
			}

			output[at] = (byte)hi;
			output[at + 1] = (byte)lo;
			int[] level = new int[8];
			level[0] = hi;
			level[1] = lo;
			for (int j = 1; j < 7; j++)
			{
				level[j + 1] = ((7 - j) * hi + j * lo) / 7;
			}

			ulong bits = 0;
			for (int i = 0; i < 16; i++)
			{
				int a = texels[i * 4 + 3], best = 0, bestD = int.MaxValue;
				if (hi != lo)
				{
					for (int j = 0; j < 8; j++)
					{
						int d = Math.Abs(a - level[j]);
						if (d < bestD)
						{
							bestD = d;
							best = j;
						}
					}
				}

				bits |= (ulong)best << (3 * i);
			}

			for (int k = 0; k < 6; k++)
			{
				output[at + 2 + k] = (byte)(bits >> (8 * k));
			}
		}

		/// <summary>
		/// Half size, each texel the average of four; colours weighted by alpha, so transparent texels' colours don't bleed
		/// into the edges. <paramref name="binary"/>: alpha kept to 0 or 255 (a cut-out's).
		/// </summary>
		private static byte[] Halve(byte[] src, int w, int h, int nw, int nh, bool binary)
		{
			byte[] dst = new byte[nw * nh * 4];
			int[] q = new int[4];
			for (int y = 0; y < nh; y++)
			{
				for (int x = 0; x < nw; x++)
				{
					int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1), y0 = Math.Min(2 * y, h - 1), y1 = Math.Min(2 * y + 1, h - 1);
					q[0] = (y0 * w + x0) * 4;
					q[1] = (y0 * w + x1) * 4;
					q[2] = (y1 * w + x0) * 4;
					q[3] = (y1 * w + x1) * 4;
					int alpha = src[q[0] + 3] + src[q[1] + 3] + src[q[2] + 3] + src[q[3] + 3];
					int o = (y * nw + x) * 4;
					for (int c = 0; c < 3; c++)
					{
						int sum = alpha > 0
							? src[q[0] + c] * src[q[0] + 3] + src[q[1] + c] * src[q[1] + 3] + src[q[2] + c] * src[q[2] + 3] + src[q[3] + c] * src[q[3] + 3]
							: src[q[0] + c] + src[q[1] + c] + src[q[2] + c] + src[q[3] + c];
						dst[o + c] = (byte)(alpha > 0 ? (sum + alpha / 2) / alpha : (sum + 2) / 4);
					}

					int a = (alpha + 2) / 4;
					dst[o + 3] = (byte)(binary ? (a >= 128 ? 255 : 0) : a);
				}
			}

			return dst;
		}

		/// <summary>
		/// The colours of transparent texels (a colour key's, or black) spread in from the texels that can be seen, a texel
		/// at a time (up to <paramref name="passes"/>), the rest given the image's average: filtering at the edges then
		/// blends in the leaves' own colours rather than dark ones. Alpha is left as it is.
		/// </summary>
		private static void Bleed(byte[] rgba, int w, int h, int passes)
		{
			int count = w * h;
			bool[] known = new bool[count];
			long s0 = 0, s1 = 0, s2 = 0, seen = 0;
			for (int i = 0; i < count; i++)
			{
				known[i] = rgba[i * 4 + 3] != 0;
				if (known[i])
				{
					s0 += rgba[i * 4];
					s1 += rgba[i * 4 + 1];
					s2 += rgba[i * 4 + 2];
					seen++;
				}
			}

			if (seen == 0 || seen == count)
			{
				return;
			}

			// Only the texels next to what is known change in a pass: those are kept in a list rather than the image scanned.
			List<int> edge = new List<int>();
			for (int i = 0; i < count; i++)
			{
				if (!known[i] && HasKnownNeighbour(known, i % w, i / w, w, h))
				{
					edge.Add(i);
				}
			}

			bool[] queued = new bool[count];
			for (int pass = 0; pass < passes && edge.Count > 0; pass++)
			{
				List<int> next = new List<int>();
				int[] colours = new int[edge.Count * 3];
				for (int e = 0; e < edge.Count; e++)
				{
					int i = edge[e], x = i % w, y = i / w, r = 0, g = 0, b = 0, k = 0;
					for (int dy = -1; dy <= 1; dy++)
					{
						for (int dx = -1; dx <= 1; dx++)
						{
							int nx = x + dx, ny = y + dy;
							if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
							{
								continue;
							}

							int j = ny * w + nx;
							if (!known[j])
							{
								continue;
							}

							r += rgba[j * 4];
							g += rgba[j * 4 + 1];
							b += rgba[j * 4 + 2];
							k++;
						}
					}

					colours[e * 3] = (r + k / 2) / k;
					colours[e * 3 + 1] = (g + k / 2) / k;
					colours[e * 3 + 2] = (b + k / 2) / k;
				}

				for (int e = 0; e < edge.Count; e++)
				{
					int i = edge[e];
					rgba[i * 4] = (byte)colours[e * 3];
					rgba[i * 4 + 1] = (byte)colours[e * 3 + 1];
					rgba[i * 4 + 2] = (byte)colours[e * 3 + 2];
					known[i] = true;
				}

				foreach (int i in edge)
				{
					int x = i % w, y = i / w;
					for (int dy = -1; dy <= 1; dy++)
					{
						for (int dx = -1; dx <= 1; dx++)
						{
							int nx = x + dx, ny = y + dy;
							if (nx < 0 || ny < 0 || nx >= w || ny >= h)
							{
								continue;
							}

							int j = ny * w + nx;
							if (!known[j] && !queued[j])
							{
								queued[j] = true;
								next.Add(j);
							}
						}
					}
				}

				foreach (int j in next)
				{
					queued[j] = false;
				}

				edge = next;
			}

			byte r0 = (byte)(s0 / seen), g0 = (byte)(s1 / seen), b0 = (byte)(s2 / seen);
			for (int i = 0; i < count; i++)
			{
				if (!known[i])
				{
					rgba[i * 4] = r0;
					rgba[i * 4 + 1] = g0;
					rgba[i * 4 + 2] = b0;
				}
			}
		}

		private static bool HasKnownNeighbour(bool[] known, int x, int y, int w, int h)
		{
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					int nx = x + dx, ny = y + dy;
					if ((dx | dy) != 0 && nx >= 0 && ny >= 0 && nx < w && ny < h && known[ny * w + nx])
					{
						return true;
					}
				}
			}

			return false;
		}
	}
}
