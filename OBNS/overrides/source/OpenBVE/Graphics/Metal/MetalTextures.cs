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

// OBNS: not an upstream file. The Metal world layer's textures (OBNS/METAL.md): the images of WorldTextures' copies, each
// a Metal texture in shared or managed memory (RGBA8; its levels made on every core), written directly (replaceRegion) while
// Metal draws nothing, and bound one per draw.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Metal
{
	internal sealed unsafe class MetalTextures : WorldTextures
	{
		internal sealed class MetalEntry : Entry
		{
			internal IntPtr Texture;
		}

		private static readonly IntPtr selTexture2D = Mtl.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), selSetUsage = Mtl.Sel("setUsage:"),
			selSetStorageMode = Mtl.Sel("setStorageMode:"), selSetMipmapLevelCount = Mtl.Sel("setMipmapLevelCount:"), selNewTexture = Mtl.Sel("newTextureWithDescriptor:"),
			selReplaceRegion = Mtl.Sel("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");

		private readonly MetalDevice device;

		/// <summary>Each texture by its index (none where there is none).</summary>
		private IntPtr[] textures = new IntPtr[1024];

		// Pixels written later, with their levels: into one block of memory (grown as needed), on every core when the uploads
		// are sent, then copied into the textures.
		private sealed class Pending
		{
			internal MetalEntry Entry;
			internal Pixels Pixels;
			internal long Offset;
		}

		private readonly List<Pending> pending = new List<Pending>();
		private IntPtr staging;
		private long stagingSize, stagingUsed;

		/// <summary>Bytes of pixels written later after which they are sent at once (loading makes many).</summary>
		private const long FlushThreshold = 128L << 20;

		private long imageBytes;

		internal MetalTextures(MetalDevice device) : base("Metal", "OBNS_METAL")
		{
			this.device = device;
			MakeWhite();
		}

		/// <summary>A texture (the white one where there is none).</summary>
		internal IntPtr Texture(int index) => index >= 0 && index < textures.Length && textures[index] != IntPtr.Zero ? textures[index] : textures[White];

		protected override Entry NewRecord() => new MetalEntry();

		protected override void MakeImage(Entry record)
		{
			MetalEntry entry = (MetalEntry)record;
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				IntPtr desc = Mtl.Id(Mtl.Class("MTLTextureDescriptor"), selTexture2D, Mtl.PixelRGBA8Unorm, entry.Width, entry.Height, (byte)0);
				Mtl.Void(desc, selSetMipmapLevelCount, (ulong)entry.Levels);
				Mtl.Void(desc, selSetUsage, Mtl.UsageShaderRead);
				Mtl.Void(desc, selSetStorageMode, device.TextureStorage);
				entry.Texture = Mtl.Id(device.Device, selNewTexture, desc);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}

			if (entry.Texture == IntPtr.Zero)
			{
				throw new InvalidOperationException("Metal could not make a texture of " + entry.Width + "x" + entry.Height);
			}

			imageBytes += ChainBytes(entry.Width, entry.Height, entry.Levels);
			if (logTextures)
			{
				Program.FileSystem.AppendToLogFile("OBNS Metal texture " + entry.Index + ": " + entry.Width + "x" + entry.Height + ", " + entry.Levels + " levels");
			}
		}

		protected override void Write(Entry record, Pixels pixels, bool later)
		{
			MetalEntry entry = (MetalEntry)record;
			long bytes = ChainBytes(pixels.Width, pixels.Height, entry.Levels);
			if (!later)
			{
				// Taken now (upstream may reuse an animated frame's bytes): written, its levels made, and sent at once.
				IntPtr own = Marshal.AllocHGlobal(new IntPtr(bytes));
				try
				{
					FillChain(entry, pixels, (byte*)own, false);
					Send(entry, (byte*)own);
				}
				finally
				{
					Marshal.FreeHGlobal(own);
				}

				return;
			}

			if (stagingUsed + bytes > stagingSize)
			{
				Flush();
				if (bytes > stagingSize)
				{
					if (staging != IntPtr.Zero)
					{
						Marshal.FreeHGlobal(staging);
					}

					stagingSize = Math.Max(bytes, 64L << 20);
					staging = Marshal.AllocHGlobal(new IntPtr(stagingSize));
				}
			}

			pending.Add(new Pending { Entry = entry, Pixels = pixels, Offset = stagingUsed });
			stagingUsed += (bytes + 15) / 16 * 16;
		}

		/// <summary>Every level of a packed chain into the texture.</summary>
		private void Send(MetalEntry entry, byte* chain)
		{
			for (int level = 0; level < entry.Levels; level++)
			{
				uint width = LevelSize(entry.Width, level), height = LevelSize(entry.Height, level);
				Mtl.Region region = new Mtl.Region { Width = width, Height = height, Depth = 1 };
				Mtl.Void(entry.Texture, selReplaceRegion, region, (ulong)level, chain + LevelOffset(entry.Width, entry.Height, level), (ulong)width * 4);
			}
		}

		protected override void Published(Entry record)
		{
			MetalEntry entry = (MetalEntry)record;
			if (entry.Index >= textures.Length)
			{
				Array.Resize(ref textures, Math.Max(entry.Index + 1, textures.Length * 2));
			}

			textures[entry.Index] = entry.Texture;
		}

		protected override void Unpublished(Entry entry)
		{
			if (entry.Index >= 0 && entry.Index < textures.Length)
			{
				textures[entry.Index] = IntPtr.Zero;
			}
		}

		protected override void Uploaded()
		{
			if (stagingUsed >= FlushThreshold)
			{
				Flush();
			}
		}

		/// <summary>The pixels written later: written on every core with their levels, then sent.</summary>
		protected override void Flush()
		{
			if (pending.Count == 0)
			{
				return;
			}

			byte* block = (byte*)staging;
			Chain[] chains = new Chain[pending.Count];
			for (int i = 0; i < chains.Length; i++)
			{
				chains[i] = new Chain { Entry = pending[i].Entry, Pixels = pending[i].Pixels, Data = block + pending[i].Offset };
			}

			FillChains(chains, false);
			foreach (Pending item in pending)
			{
				if (item.Entry.Texture != IntPtr.Zero)
				{
					Send(item.Entry, block + item.Offset);
				}
			}

			pending.Clear();
			stagingUsed = 0;
		}

		/// <summary>Called each frame before the draws: the uploads made since the last are sent.</summary>
		internal void Send() => Flush();

		protected override void Destroy(Entry record)
		{
			if (!(record is MetalEntry entry))
			{
				return;
			}

			if (entry.Index >= 0 && entry.Index < textures.Length && textures[entry.Index] == entry.Texture)
			{
				textures[entry.Index] = IntPtr.Zero;
			}

			if (entry.Texture != IntPtr.Zero)
			{
				Mtl.Release(entry.Texture);
				imageBytes -= ChainBytes(entry.Width, entry.Height, entry.Levels);
			}

			entry.Texture = IntPtr.Zero;
		}

		protected override string DescribeStorage() => "Metal textures " + (imageBytes >> 20) + " MB";

		public override void Dispose()
		{
			pending.Clear();
			base.Dispose();
			if (staging != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(staging);
				staging = IntPtr.Zero;
			}
		}
	}
}
