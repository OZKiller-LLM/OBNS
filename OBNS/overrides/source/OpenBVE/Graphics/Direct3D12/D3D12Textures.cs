// OBNS: not an upstream file. The Direct3D 12 world layer's textures (OBNS/DIRECT3D.md): the images of WorldTextures'
// copies, each a Direct3D texture whose view is in the shader-visible heap at the texture's index (bound one per draw).
// Direct3D 12 makes no mipmap levels itself: they are made on every core as the pixels are written (WorldTextures).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
