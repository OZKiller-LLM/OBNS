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
using System.Threading;
using ApiTexture = OpenBveApi.Textures.Texture;

namespace OpenBve.Android
{
	/*
	 * A cap on texture size, for phones with little RAM. On a phone the GPU's memory is system
	 * RAM, and on the Tuen Ma Line the textures were ~1.7 GB of the game's 3.4 GB in the exterior
	 * view - 177 of them 1024 px or larger (a 2048 px texture with its mipmaps is ~21 MB) - which is
	 * what ended the game on 4 GB phones. A texture over the cap is halved (2x2 averaging) until it
	 * fits, as it is decoded: after upstream's ApplyParameters, so clip regions and colour keys work
	 * in the file's own pixels; panel layouts ask the plugins for the file's dimensions, not these.
	 * 0 = no cap. Set from the Texture size option (automatic: 1024 px at 4 GB of RAM or less,
	 * 2048 px at 6 GB - a Galaxy S20 FE, 6 GB, was still ended in that exterior view uncapped).
	 */
	internal static class TextureSizeLimit
	{
		/// <summary>The largest width or height kept; 0 for no limit.</summary>
		public static int MaxSize;

		private static int reduced;
		private static long bytesSaved;

		/// <summary>Textures reduced and the memory that saved (base level), for the log.</summary>
		public static string Describe() => MaxSize <= 0 ? "no texture size limit" :
			"texture size limit " + MaxSize + " px: " + reduced + " reduced, ~" + (Interlocked.Read(ref bytesSaved) * 4 / 3 >> 20) + " MB saved";

		/// <summary>The automatic limit for a phone with this much RAM.</summary>
		public static int Automatic(long totalRamBytes) => totalRamBytes <= 0 ? 0 : totalRamBytes <= 4608L << 20 ? 1024 : totalRamBytes <= 6656L << 20 ? 2048 : 0;

		/// <summary>Returns the texture, halved until it fits the limit. Thread-safe (textures decode on worker threads).</summary>
		public static ApiTexture Apply(ApiTexture texture)
		{
			int max = MaxSize;
			if (max <= 0 || texture == null || texture.MultipleFrames || texture.Palette != null || texture.Bytes == null ||
			    (texture.Width <= max && texture.Height <= max))
			{
				return texture;
			}

			int channels = (int)texture.PixelFormat;
			if (channels < 1 || channels > 4 || texture.Bytes.Length < texture.Width * texture.Height * channels)
			{
				return texture;
			}

			int width = texture.Width, height = texture.Height;
			byte[] pixels = texture.Bytes;
			long before = (long)width * height * channels;
			// Halving while that still leaves it at least the cap (exact 2x2 averages), then one
			// area-averaged step to fit the cap exactly (so 768 px means 768, not 512).
			while ((width >= 2 * max || height >= 2 * max) && (width > 1 || height > 1))
			{
				int w = System.Math.Max(1, width / 2), h = System.Math.Max(1, height / 2);
				byte[] next = new byte[w * h * channels];
				for (int y = 0; y < h; y++)
				{
					int y0 = System.Math.Min(2 * y, height - 1), y1 = System.Math.Min(2 * y + 1, height - 1);
					for (int x = 0; x < w; x++)
					{
						int x0 = System.Math.Min(2 * x, width - 1), x1 = System.Math.Min(2 * x + 1, width - 1);
						int a = (y0 * width + x0) * channels, b = (y0 * width + x1) * channels;
						int c = (y1 * width + x0) * channels, d = (y1 * width + x1) * channels;
						int o = (y * w + x) * channels;
						for (int k = 0; k < channels; k++)
						{
							next[o + k] = (byte)((pixels[a + k] + pixels[b + k] + pixels[c + k] + pixels[d + k] + 2) >> 2);
						}
					}
				}

				pixels = next;
				width = w;
				height = h;
			}

			if (width > max || height > max)
			{
				double scale = (double)max / System.Math.Max(width, height);
				int w = System.Math.Max(1, (int)System.Math.Round(width * scale)), h = System.Math.Max(1, (int)System.Math.Round(height * scale));
				byte[] next = new byte[w * h * channels];
				int[] sum = new int[channels];
				for (int y = 0; y < h; y++)
				{
					int sy0 = y * height / h, sy1 = System.Math.Max(sy0 + 1, (y + 1) * height / h);
					for (int x = 0; x < w; x++)
					{
						int sx0 = x * width / w, sx1 = System.Math.Max(sx0 + 1, (x + 1) * width / w);
						System.Array.Clear(sum, 0, channels);
						for (int sy = sy0; sy < sy1; sy++)
						{
							int row = sy * width;
							for (int sx = sx0; sx < sx1; sx++)
							{
								int s = (row + sx) * channels;
								for (int k = 0; k < channels; k++)
								{
									sum[k] += pixels[s + k];
								}
							}
						}

						int n = (sy1 - sy0) * (sx1 - sx0), o = (y * w + x) * channels;
						for (int k = 0; k < channels; k++)
						{
							next[o + k] = (byte)((sum[k] + n / 2) / n);
						}
					}
				}

				pixels = next;
				width = w;
				height = h;
			}

			Interlocked.Increment(ref reduced);
			Interlocked.Add(ref bytesSaved, before - (long)width * height * channels);
			return new ApiTexture(width, height, texture.PixelFormat, pixels, (OpenBveApi.Colors.Color24[])null)
			{
				CompatibleTransparencyMode = texture.CompatibleTransparencyMode
			};
		}
	}
}
