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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using OpenBve.Android;
using OpenBveApi.Textures;
using ApiTexture = OpenBveApi.Textures.Texture;
using PixelFormat = OpenBveApi.Textures.PixelFormat;

namespace OpenBve.NX.Deko
{
	/*
	 * The Switch's compressed texture cache (AndroidHost.CompressedTextures; OBNS/SWITCH.md, step 4, item E). A streamed
	 * texture decoded once - at the texture size limit, after its parameters (clip region, colour key) - is compressed on a
	 * thread of its own into the GPU's block formats with its mipmaps (NX/runner/source/obns_bc.c) and kept in one file
	 * under UserData/TextureCache; from then on it is read back instead of decoded, and takes 4 bits a texel (BC1:
	 * opaque) or 8 (BC3: partly transparent, and cut-outs - colour-keyed or one-bit alpha - with their hidden colours
	 * spread in from the edges and their mipmaps' alpha kept to 0 or 255) instead of 16 or 32. Kept for the same file
	 * (path, size, date) read the same way (parameters, size limit); FormatVersion starts them all again.
	 * Cut-outs were BC1 with one-bit alpha until 2026-11-07: its transparent texels are black, which filtering blended
	 * into the edges (dark fringes and specks round the trees); such files are read as missing and made again.
	 */
	internal sealed unsafe class CompressedTextures : ICompressedTextureCache
	{
		private const int FormatVersion = 1;
		private const uint Magic = 0x4F425443; // "OBTC"

		/// <summary>Textures waiting to be compressed at most; more are left (they are decoded again next time).</summary>
		private const int MaxQueued = 8;

		/// <summary>Smaller ones aren't worth a file each.</summary>
		private const int MinSide = 16;

		private readonly DekoGL deko;
		private readonly string folder;
		private readonly Queue<(string Path, string Key, int Width, int Height, int Format, TextureTransparencyType Transparency, byte[] Rgba)> queue =
			new Queue<(string, string, int, int, int, TextureTransparencyType, byte[])>();
		private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
		private int hits, misses, stored, dropped, failures;
		private long compressTicks, storedBytes;

		public CompressedTextures(DekoGL deko, string folder)
		{
			this.deko = deko;
			this.folder = folder;
			Directory.CreateDirectory(folder);
			new Thread(Compress) { IsBackground = true, Name = "texture compression" }.Start();
		}

		public string Describe()
		{
			lock (queue)
			{
				return "compressed textures: " + hits + " read back, " + misses + " not kept yet, " + stored + " stored (" + (storedBytes >> 20) + " MB, " +
				       (compressTicks * 1000 / Stopwatch.Frequency) + " ms compressing), " + dropped + " skipped (queue full), " + failures + " failures";
			}
		}

		private static string Key(PathOrigin origin)
		{
			StringBuilder key = new StringBuilder();
			key.Append(origin.Path.ToLowerInvariant()).Append('|').Append(origin.FileSize).Append('|').Append(origin.LastModificationTime.Ticks);
			TextureParameters parameters = origin.Parameters;
			if (parameters?.ClipRegion != null)
			{
				TextureClipRegion clip = parameters.ClipRegion;
				key.Append("|clip ").Append(clip.Left).Append(',').Append(clip.Top).Append(',').Append(clip.Width).Append(',').Append(clip.Height);
			}

			if (parameters?.TransparentColor != null)
			{
				OpenBveApi.Colors.Color24 colour = parameters.TransparentColor.Value;
				key.Append("|key ").Append(colour.R).Append(',').Append(colour.G).Append(',').Append(colour.B);
			}

			if (parameters != null && parameters.FirstColorTransparent)
			{
				key.Append("|first");
			}

			key.Append("|max ").Append(TextureSizeLimit.MaxSize).Append("|v").Append(FormatVersion);
			return key.ToString();
		}

		private string FileFor(string key)
		{
			// FNV-1a: the Switch's runtime has no native crypto, and nothing here needs it.
			ulong hash = 14695981039346656037UL;
			foreach (char c in key)
			{
				hash = (hash ^ (byte)c) * 1099511628211UL;
				hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
			}

			return Path.Combine(folder, hash.ToString("x16") + ".btc");
		}

		public bool TryRead(PathOrigin origin, out CompressedTexture texture)
		{
			texture = null;
			if (origin?.Path == null || origin.Parameters?.TransparencyTexture != null)
			{
				return false;
			}

			try
			{
				string key = Key(origin);
				string file = FileFor(key);
				if (!File.Exists(file))
				{
					Interlocked.Increment(ref misses);
					return false;
				}

				using (BinaryReader reader = new BinaryReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16), Encoding.UTF8))
				{
					if (reader.ReadUInt32() != Magic || reader.ReadString() != key)
					{
						Interlocked.Increment(ref misses);
						return false;
					}

					CompressedTexture read = new CompressedTexture
					{
						Format = reader.ReadInt32(),
						Width = reader.ReadInt32(),
						Height = reader.ReadInt32(),
						Levels = reader.ReadInt32(),
						Transparency = (TextureTransparencyType)reader.ReadInt32()
					};
					int length = reader.ReadInt32();
					if (read.Format == DekoNative.FormatBC1Alpha)
					{
						// A cut-out kept the old way (see above): decoded, and kept again as BC3.
						Interlocked.Increment(ref misses);
						return false;
					}

					if (length <= 0 || length != DekoNative.BcSize(read.Width, read.Height, read.Format, read.Levels))
					{
						Interlocked.Increment(ref failures);
						return false;
					}

					read.Data = reader.ReadBytes(length);
					if (read.Data.Length != length)
					{
						Interlocked.Increment(ref failures);
						return false;
					}

					texture = read;
					Interlocked.Increment(ref hits);
					return true;
				}
			}
			catch (Exception)
			{
				Interlocked.Increment(ref failures);
				return false;
			}
		}

		public void Store(PathOrigin origin, ApiTexture decoded)
		{
			if (origin?.Path == null || origin.Parameters?.TransparencyTexture != null || decoded == null || decoded.MultipleFrames ||
			    decoded.Width < MinSide || decoded.Height < MinSide || (decoded.PixelFormat != PixelFormat.RGBAlpha && decoded.PixelFormat != PixelFormat.RGB))
			{
				return;
			}

			string key;
			try
			{
				key = Key(origin);
			}
			catch (Exception)
			{
				return;
			}

			lock (queue)
			{
				if (queued.Contains(key))
				{
					return;
				}

				if (queue.Count >= MaxQueued)
				{
					dropped++;
					return;
				}
			}

			// A copy as RGBA: the decoded texture goes on to its upload (and may be released).
			byte[] bytes = decoded.Bytes;
			int texels = decoded.Width * decoded.Height;
			byte[] rgba;
			if (decoded.PixelFormat == PixelFormat.RGB)
			{
				if (bytes == null || bytes.Length < texels * 3)
				{
					return;
				}

				rgba = new byte[texels * 4];
				for (int i = 0, j = 0; i < texels; i++, j += 3)
				{
					rgba[4 * i] = bytes[j];
					rgba[4 * i + 1] = bytes[j + 1];
					rgba[4 * i + 2] = bytes[j + 2];
					rgba[4 * i + 3] = 255;
				}
			}
			else
			{
				if (bytes == null || bytes.Length < texels * 4)
				{
					return;
				}

				rgba = (byte[])bytes.Clone();
			}

			TextureTransparencyType transparency = decoded.GetTransparencyType();
			// Partial: alpha only 0 or 255 (a cut-out) - BC3 with the cut-out flag (NX/runner/source/obns_bc.c).
			int format = transparency == TextureTransparencyType.Opaque ? DekoNative.FormatBC1 :
				transparency == TextureTransparencyType.Partial ? DekoNative.FormatBC3 | DekoNative.BcCutout : DekoNative.FormatBC3;
			lock (queue)
			{
				if (!queued.Add(key))
				{
					return;
				}

				queue.Enqueue((FileFor(key), key, decoded.Width, decoded.Height, format, transparency, rgba));
				Monitor.Pulse(queue);
			}
		}

		public bool Upload(CompressedTexture texture)
		{
			deko.CompressedImage(texture.Format, texture.Width, texture.Height, texture.Levels, texture.Data);
			return true;
		}

		/// <summary>The compressing thread: one texture at a time, each written beside and then put in place.</summary>
		private void Compress()
		{
			while (true)
			{
				(string Path, string Key, int Width, int Height, int Format, TextureTransparencyType Transparency, byte[] Rgba) job;
				lock (queue)
				{
					while (queue.Count == 0)
					{
						Monitor.Wait(queue);
					}

					job = queue.Dequeue();
				}

				try
				{
					long started = Stopwatch.GetTimestamp();
					int levels = DekoNative.BcLevels(job.Width, job.Height);
					int stored = job.Format & 0xFF;
					long size = DekoNative.BcSize(job.Width, job.Height, stored, levels);
					byte[] data = new byte[size];
					int result;
					fixed (byte* rgba = job.Rgba)
					fixed (byte* output = data)
					{
						result = DekoNative.BcCompress(rgba, job.Width, job.Height, job.Format, levels, output);
					}

					long took = Stopwatch.GetTimestamp() - started;
					if (result != 0)
					{
						throw new InvalidOperationException("obns_bc_compress: " + result);
					}

					string temporary = job.Path + ".new";
					using (BinaryWriter writer = new BinaryWriter(new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16), Encoding.UTF8))
					{
						writer.Write(Magic);
						writer.Write(job.Key);
						writer.Write(stored);
						writer.Write(job.Width);
						writer.Write(job.Height);
						writer.Write(levels);
						writer.Write((int)job.Transparency);
						writer.Write(data.Length);
						writer.Write(data);
					}

					// The Switch's file system doesn't rename over an existing file.
					if (File.Exists(job.Path))
					{
						File.Delete(job.Path);
					}

					File.Move(temporary, job.Path);
					lock (queue)
					{
						stored++;
						storedBytes += data.Length;
						compressTicks += took;
					}
				}
				catch (Exception)
				{
					lock (queue)
					{
						failures++;
					}
				}
				finally
				{
					lock (queue)
					{
						queued.Remove(job.Key);
					}
				}
			}
		}
	}
}
