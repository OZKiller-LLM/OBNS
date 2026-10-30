// OBNS: not an upstream file. The Direct3D 12 world layer's textures (OBNS/DIRECT3D.md): the images of WorldTextures'
// copies, each a Direct3D texture whose view is in the shader-visible heap at the texture's index (bound one per draw).
// Direct3D 12 makes no mipmap levels itself: they are made here, on every core, as the pixels are written.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dx = OpenBve.Graphics.Direct3D11.Dx;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12Textures : WorldTextures
	{
		internal sealed class D3D12Entry : Entry
		{
			internal IntPtr Texture;
			/// <summary>Whether it has been written (it is read by the pixel shaders between writes).</summary>
			internal bool Readable;
		}

		/// <summary>Views in the heap after the textures' (the shadow maps').</summary>
		internal const int ExtraViews = 4;

		private readonly D3D12Device device;
		private readonly D3D12Uploads uploads;

		/// <summary>The shader-visible heap of views: the textures' by index, then the extra ones.</summary>
		internal readonly IntPtr Heap;
		private readonly UIntPtr cpuStart;
		private readonly ulong gpuStart;
		private readonly uint viewSize;

		/// <summary>Whether each index's view is a texture's that may be drawn.</summary>
		private bool[] published = new bool[1024];

		// Pixels written later, with their levels: into one block of memory (grown as needed), on every core when the uploads
		// are sent, then copied into upload memory.
		private sealed class Pending
		{
			internal D3D12Entry Entry;
			internal Pixels Pixels;
			internal long Offset;
		}

		private readonly List<Pending> pending = new List<Pending>();
		private IntPtr staging;
		private long stagingSize, stagingUsed;

		/// <summary>Bytes of pixels written later after which they are sent at once (loading makes many).</summary>
		private const long FlushThreshold = 128L << 20;

		private long imageBytes;

		internal D3D12Textures(D3D12Device device, D3D12Uploads uploads) : base("Direct3D 12", "OBNS_D3D12")
		{
			this.device = device;
			this.uploads = uploads;
			Heap = D12.CreateDescriptorHeap(device.Device, D12.DescriptorHeapResources, Capacity + ExtraViews, true);
			cpuStart = D12.CpuStart(Heap);
			gpuStart = D12.GpuStart(Heap);
			viewSize = D12.DescriptorSize(device.Device, D12.DescriptorHeapResources);
			MakeWhite();
		}

		/// <summary>A view's place in the heap, for writing it.</summary>
		internal UIntPtr CpuView(int index) => (UIntPtr)((ulong)cpuStart + (ulong)index * viewSize);

		/// <summary>A texture's view for the shaders (the white one's where there is none).</summary>
		internal ulong View(int index) => gpuStart + (ulong)(index >= 0 && index < published.Length && published[index] ? index : White) * viewSize;

		/// <summary>An extra view's place for the shaders.</summary>
		internal ulong ExtraView(int extra) => gpuStart + (ulong)(Capacity + extra) * viewSize;

		/// <summary>An extra view's place, for writing it.</summary>
		internal UIntPtr ExtraCpuView(int extra) => CpuView((int)Capacity + extra);

		protected override Entry NewRecord() => new D3D12Entry();

		protected override void MakeImage(Entry record)
		{
			D3D12Entry entry = (D3D12Entry)record;
			D12.ResourceDesc desc = new D12.ResourceDesc
			{
				Dimension = D12.DimensionTexture2D,
				Width = entry.Width,
				Height = entry.Height,
				DepthOrArraySize = 1,
				MipLevels = (ushort)entry.Levels,
				Format = Dx.FormatR8G8B8A8Unorm,
				SampleCount = 1
			};
			entry.Texture = D12.CreateCommittedResource(device.Device, D12.HeapDefault, D12.HeapFlagNone, desc, D12.StateCopyDest);
			entry.Readable = false;
			imageBytes += ChainBytes(entry.Width, entry.Height, entry.Levels);
			if (logTextures)
			{
				Program.FileSystem.AppendToLogFile("OBNS Direct3D 12 texture " + entry.Index + ": " + entry.Width + "x" + entry.Height + ", " + entry.Levels + " levels");
			}
		}

		private static uint LevelSize(uint size, int level) => Math.Max(1u, size >> level);

		/// <summary>The bytes of every level, packed one after another.</summary>
		private static long ChainBytes(uint width, uint height, uint levels)
		{
			long bytes = 0;
			for (int level = 0; level < levels; level++)
			{
				bytes += (long)LevelSize(width, level) * LevelSize(height, level) * 4;
			}

			return bytes;
		}

		private static long LevelOffset(uint width, uint height, int level)
		{
			long offset = 0;
			for (int k = 0; k < level; k++)
			{
				offset += (long)LevelSize(width, k) * LevelSize(height, k) * 4;
			}

			return offset;
		}

		protected override void Write(Entry record, Pixels pixels, bool later)
		{
			D3D12Entry entry = (D3D12Entry)record;
			long bytes = ChainBytes(pixels.Width, pixels.Height, entry.Levels);
			if (!later)
			{
				// Taken now (upstream may reuse an animated frame's bytes): written, its levels made, and sent at once.
				IntPtr own = Marshal.AllocHGlobal(new IntPtr(bytes));
				try
				{
					pixels.Fill((byte*)own, 0, (int)(pixels.Width * pixels.Height));
					for (int level = 1; level < entry.Levels; level++)
					{
						MakeLevelRows((byte*)own, entry.Width, entry.Height, level, 0, LevelSize(entry.Height, level));
					}

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

		/// <summary>
		/// Rows [first, end) of a level made from the level above it (packed in the chain): each texel the mean of the 2x2
		/// above, as Direct3D 11's GenerateMips (and OpenGL's) box filter.
		/// </summary>
		private static void MakeLevelRows(byte* chain, uint width, uint height, int level, uint first, uint end)
		{
			uint sourceWidth = LevelSize(width, level - 1), sourceHeight = LevelSize(height, level - 1);
			uint targetWidth = LevelSize(width, level);
			byte* source = chain + LevelOffset(width, height, level - 1);
			byte* target = chain + LevelOffset(width, height, level);
			for (uint y = first; y < end; y++)
			{
				byte* row0 = source + (long)Math.Min(2 * y, sourceHeight - 1) * sourceWidth * 4;
				byte* row1 = source + (long)Math.Min(2 * y + 1, sourceHeight - 1) * sourceWidth * 4;
				byte* to = target + (long)y * targetWidth * 4;
				for (uint x = 0; x < targetWidth; x++)
				{
					uint x0 = Math.Min(2 * x, sourceWidth - 1) * 4, x1 = Math.Min(2 * x + 1, sourceWidth - 1) * 4;
					for (int c = 0; c < 4; c++)
					{
						to[x * 4 + c] = (byte)((row0[x0 + c] + row0[x1 + c] + row1[x0 + c] + row1[x1 + c] + 2) >> 2);
					}
				}
			}
		}

		/// <summary>Copies every level of a packed chain into the texture (through upload memory, rows aligned as Direct3D needs).</summary>
		private void Send(D3D12Entry entry, byte* chain)
		{
			D12.ResourceBarrier* barrier = stackalloc D12.ResourceBarrier[1];
			if (entry.Readable)
			{
				barrier[0] = D12.Transition(entry.Texture, D12.StatePixelShaderResource, D12.StateCopyDest);
				D12.ResourceBarriers(uploads.List, barrier, 1);
			}

			for (int level = 0; level < entry.Levels; level++)
			{
				uint width = LevelSize(entry.Width, level), height = LevelSize(entry.Height, level);
				uint pitch = (width * 4 + D12.PitchAlignment - 1) / D12.PitchAlignment * D12.PitchAlignment;
				byte* to = uploads.Reserve((ulong)pitch * height, D12.PlacementAlignment, out IntPtr source, out ulong offset);
				byte* from = chain + LevelOffset(entry.Width, entry.Height, level);
				for (uint y = 0; y < height; y++)
				{
					Buffer.MemoryCopy(from + (long)y * width * 4, to + (long)y * pitch, pitch, width * 4);
				}

				D12.TextureCopyLocation destination = new D12.TextureCopyLocation { Resource = entry.Texture, Type = D12.CopySubresource, SubresourceIndex = (uint)level };
				D12.TextureCopyLocation footprint = new D12.TextureCopyLocation { Resource = source, Type = D12.CopyFootprint, Offset = offset, Format = Dx.FormatR8G8B8A8Unorm, Width = width, Height = height, Depth = 1, RowPitch = pitch };
				D12.CopyTextureRegion(uploads.List, destination, footprint);
			}

			barrier[0] = D12.Transition(entry.Texture, D12.StateCopyDest, D12.StatePixelShaderResource);
			D12.ResourceBarriers(uploads.List, barrier, 1);
			entry.Readable = true;
		}

		protected override void Published(Entry record)
		{
			D3D12Entry entry = (D3D12Entry)record;
			if (entry.Index >= published.Length)
			{
				Array.Resize(ref published, Math.Max(entry.Index + 1, published.Length * 2));
			}

			D12.ShaderResourceViewDesc view = new D12.ShaderResourceViewDesc { Format = Dx.FormatR8G8B8A8Unorm, ViewDimension = D12.SrvTexture2D, Shader4ComponentMapping = D12.DefaultComponentMapping, MipLevels = entry.Levels };
			D12.CreateShaderResourceView(device.Device, entry.Texture, &view, CpuView(entry.Index));
			published[entry.Index] = true;
		}

		protected override void Unpublished(Entry entry)
		{
			// The view stays as it is until the texture is destroyed (frames in flight may read it); new draws are given white.
			if (entry.Index >= 0 && entry.Index < published.Length)
			{
				published[entry.Index] = false;
			}
		}

		protected override void Uploaded()
		{
			if (stagingUsed >= FlushThreshold)
			{
				Flush();
			}
		}

		/// <summary>The pixels written later: written on every core, their levels made on every core (a level at a time), then sent.</summary>
		protected override void Flush()
		{
			if (pending.Count == 0)
			{
				return;
			}

			byte* block = (byte*)staging;
			List<KeyValuePair<Pending, int>> pieces = new List<KeyValuePair<Pending, int>>();
			foreach (Pending item in pending)
			{
				int count = (int)(item.Pixels.Width * item.Pixels.Height);
				for (int first = 0; first < count; first += FillPiece)
				{
					pieces.Add(new KeyValuePair<Pending, int>(item, first));
				}
			}

			Parallel.For(0, pieces.Count, i =>
			{
				Pending item = pieces[i].Key;
				int first = pieces[i].Value;
				int count = (int)(item.Pixels.Width * item.Pixels.Height);
				item.Pixels.Fill(block + item.Offset, first, Math.Min(FillPiece, count - first));
			});

			// Each level from the one above: the rows shared out in pieces of about FillPiece texels.
			uint most = 0;
			foreach (Pending item in pending)
			{
				most = Math.Max(most, item.Entry.Levels);
			}

			for (int level = 1; level < most; level++)
			{
				pieces.Clear();
				foreach (Pending item in pending)
				{
					if (item.Entry.Levels <= level)
					{
						continue;
					}

					uint rows = LevelSize(item.Entry.Height, level), rowsPerPiece = (uint)Math.Max(1, FillPiece / (int)LevelSize(item.Entry.Width, level));
					for (uint first = 0; first < rows; first += rowsPerPiece)
					{
						pieces.Add(new KeyValuePair<Pending, int>(item, (int)first));
					}
				}

				int current = level;
				Parallel.For(0, pieces.Count, i =>
				{
					D3D12Entry entry = pieces[i].Key.Entry;
					uint first = (uint)pieces[i].Value, rows = LevelSize(entry.Height, current), rowsPerPiece = (uint)Math.Max(1, FillPiece / (int)LevelSize(entry.Width, current));
					MakeLevelRows(block + pieces[i].Key.Offset, entry.Width, entry.Height, current, first, Math.Min(rows, first + rowsPerPiece));
				});
			}

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

		/// <summary>Called each frame before the draws: the uploads made since the last are recorded.</summary>
		internal void Send() => Flush();

		protected override void Destroy(Entry record)
		{
			if (!(record is D3D12Entry entry))
			{
				return;
			}

			if (entry.Texture != IntPtr.Zero)
			{
				if (entry.Index >= 0 && entry.Index < published.Length)
				{
					published[entry.Index] = false;
				}

				D12.Release(entry.Texture);
				imageBytes -= ChainBytes(entry.Width, entry.Height, entry.Levels);
			}

			entry.Texture = IntPtr.Zero;
		}

		protected override string DescribeStorage() => "Direct3D textures " + (imageBytes >> 20) + " MB, " + uploads.Describe();

		public override void Dispose()
		{
			pending.Clear();
			base.Dispose();
			D12.Release(Heap);
			if (staging != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(staging);
				staging = IntPtr.Zero;
			}
		}
	}
}
