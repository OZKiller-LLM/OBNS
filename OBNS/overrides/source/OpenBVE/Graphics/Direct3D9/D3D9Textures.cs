// OBNS: not an upstream file. The Direct3D 9.0c world layer's textures (OBNS/DIRECT3D.md): the images of WorldTextures'
// copies, each a Direct3D 9 texture (A8R8G8B8: the pixels written as BGRA, their levels made on every core), bound one
// per draw; written into a system-memory texture of their own and copied by the GPU (UpdateTexture).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Direct3D9
{
	internal sealed unsafe class D3D9Textures : WorldTextures
	{
		internal sealed class D3D9Entry : Entry
		{
			internal IntPtr Texture;
		}

		private readonly D3D9Device device;

		/// <summary>Each texture by its index (none where there is none).</summary>
		private IntPtr[] textures = new IntPtr[1024];

		// Pixels written later, with their levels: into one block of memory (grown as needed), on every core when the uploads
		// are sent, then handed to Direct3D.
		private sealed class Pending
		{
			internal D3D9Entry Entry;
			internal Pixels Pixels;
			internal long Offset;
		}

		private readonly List<Pending> pending = new List<Pending>();
		private IntPtr staging;
		private long stagingSize, stagingUsed;

		/// <summary>Bytes of pixels written later after which they are sent at once (loading makes many).</summary>
		private const long FlushThreshold = 128L << 20;

		private long imageBytes;

		internal D3D9Textures(D3D9Device device) : base("Direct3D 9", "OBNS_D3D9")
		{
			this.device = device;
			MakeWhite();
		}

		/// <summary>A texture (the white one where there is none).</summary>
		internal IntPtr Texture(int index) => index >= 0 && index < textures.Length && textures[index] != IntPtr.Zero ? textures[index] : textures[White];

		protected override Entry NewRecord() => new D3D9Entry();

		protected override void MakeImage(Entry record)
		{
			D3D9Entry entry = (D3D9Entry)record;
			entry.Texture = D9.CreateTexture(device.Device, entry.Width, entry.Height, entry.Levels, 0, D9.FormatA8R8G8B8, D9.PoolDefault);
			imageBytes += ChainBytes(entry.Width, entry.Height, entry.Levels);
			if (logTextures)
			{
				Program.FileSystem.AppendToLogFile("OBNS Direct3D 9 texture " + entry.Index + ": " + entry.Width + "x" + entry.Height + ", " + entry.Levels + " levels");
			}
		}

		protected override void Write(Entry record, Pixels pixels, bool later)
		{
			D3D9Entry entry = (D3D9Entry)record;
			long bytes = ChainBytes(pixels.Width, pixels.Height, entry.Levels);
			if (!later)
			{
				// Taken now (upstream may reuse an animated frame's bytes): written, its levels made, and sent at once.
				IntPtr own = Marshal.AllocHGlobal(new IntPtr(bytes));
				try
				{
					FillChain(entry, pixels, (byte*)own, true);
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

		/// <summary>Every level of a packed chain into the texture: through a system-memory texture, copied by the GPU.</summary>
		private void Send(D3D9Entry entry, byte* chain)
		{
			IntPtr source = D9.CreateTexture(device.Device, entry.Width, entry.Height, entry.Levels, 0, D9.FormatA8R8G8B8, D9.PoolSystemMemory);
			try
			{
				for (int level = 0; level < entry.Levels; level++)
				{
					uint width = LevelSize(entry.Width, level), height = LevelSize(entry.Height, level);
					D9.LockedRect locked = D9.LockRect(source, (uint)level, 0);
					byte* from = chain + LevelOffset(entry.Width, entry.Height, level);
					for (uint y = 0; y < height; y++)
					{
						Buffer.MemoryCopy(from + (long)y * width * 4, locked.Bits + (long)y * locked.Pitch, locked.Pitch, width * 4);
					}

					D9.UnlockRect(source, (uint)level);
				}

				D9.UpdateTexture(device.Device, source, entry.Texture);
			}
			finally
			{
				D9.Release(source);
			}
		}

		protected override void Published(Entry record)
		{
			D3D9Entry entry = (D3D9Entry)record;
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

			FillChains(chains, true);
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
			if (!(record is D3D9Entry entry))
			{
				return;
			}

			if (entry.Index >= 0 && entry.Index < textures.Length && textures[entry.Index] == entry.Texture)
			{
				textures[entry.Index] = IntPtr.Zero;
			}

			if (entry.Texture != IntPtr.Zero)
			{
				D9.Release(entry.Texture);
				imageBytes -= ChainBytes(entry.Width, entry.Height, entry.Levels);
			}

			entry.Texture = IntPtr.Zero;
		}

		protected override string DescribeStorage() => "Direct3D textures " + (imageBytes >> 20) + " MB";

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
