// OBNS: not an upstream file. The Direct3D 11 world layer's meshes (OBNS/DIRECT3D.md): a copy of upstream's vertex
// buffers (OpenGL keeps its own) and the faces' triangle lists (WorldMeshes), in Direct3D buffers suballocated as
// Vulkan's are, so the draws name blocks and offsets the same way.

using System;
using System.Collections.Generic;
using LibRender2;

namespace OpenBve.Graphics.Direct3D11
{
	/// <summary>Direct3D buffers in blocks, suballocated.</summary>
	internal sealed unsafe class D3D11Blocks : IDisposable
	{
		private readonly D3D11Device device;
		private readonly ulong blockSize;
		private readonly uint bindFlags;
		private readonly string name;
		private readonly List<RangeAllocator> allocators = new List<RangeAllocator>();

		/// <summary>The blocks' buffers, by block.</summary>
		internal readonly List<IntPtr> Buffers = new List<IntPtr>();

		internal D3D11Blocks(D3D11Device device, ulong blockSize, uint bindFlags, string name)
		{
			this.device = device;
			this.blockSize = blockSize;
			this.bindFlags = bindFlags;
			this.name = name;
		}

		/// <summary>A range, making a block when none has room.</summary>
		internal GpuRange Allocate(ulong size, ulong alignment)
		{
			for (int i = 0; i < allocators.Count; i++)
			{
				ulong offset = allocators[i].Allocate(size, alignment, out ulong taken);
				if (offset != ulong.MaxValue)
				{
					return new GpuRange { Block = i, Offset = offset, Start = offset + size - taken, Taken = taken };
				}
			}

			ulong blockBytes = Math.Max(blockSize, (size + alignment + 15) / 16 * 16);
			Dx.BufferDesc desc = new Dx.BufferDesc { ByteWidth = (uint)blockBytes, Usage = Dx.UsageDefault, BindFlags = bindFlags };
			Buffers.Add(Dx.CreateBuffer(device.Device, desc, null));
			allocators.Add(new RangeAllocator(blockBytes));
			int block = allocators.Count - 1;
			ulong first = allocators[block].Allocate(size, alignment, out ulong firstTaken);
			return new GpuRange { Block = block, Offset = first, Start = first + size - firstTaken, Taken = firstTaken };
		}

		internal void Free(GpuRange range)
		{
			if (range.Valid)
			{
				allocators[range.Block].Free(range.Start, range.Taken);
			}
		}

		/// <summary>Writes bytes into a block (ordered with the draws by Direct3D).</summary>
		internal void Write(int block, ulong offset, void* data, ulong bytes)
		{
			Dx.UpdateBuffer(device.Context, Buffers[block], offset, data, bytes);
		}

		internal string Describe()
		{
			ulong total = 0, used = 0;
			foreach (RangeAllocator allocator in allocators)
			{
				total += allocator.Size;
				used += allocator.Used;
			}

			return name + " " + (used >> 20) + " of " + (total >> 20) + " MB in " + allocators.Count + " blocks";
		}

		public void Dispose()
		{
			foreach (IntPtr buffer in Buffers)
			{
				Dx.Release(buffer);
			}

			Buffers.Clear();
			allocators.Clear();
		}
	}

	internal sealed unsafe class D3D11Meshes : WorldMeshes
	{
		/// <summary>Vertex ranges' alignment: a whole vertex (for the draws' base vertex) and 64 bytes.</summary>
		private static readonly ulong vertexAlignment = (ulong)(VertexSize * 64 / Gcd(VertexSize, 64));

		/// <summary>The copies of the vertex buffers.</summary>
		internal readonly D3D11Blocks Vertices;

		/// <summary>The triangle indices.</summary>
		internal readonly D3D11Blocks Indices;

		internal D3D11Meshes(D3D11Device device)
		{
			Vertices = new D3D11Blocks(device, 128UL << 20, Dx.BindVertexBuffer, "vertices");
			Indices = new D3D11Blocks(device, 64UL << 20, Dx.BindIndexBuffer, "indices");
		}

		private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

		/// <summary>A range for a copy of a vertex buffer of so many bytes (OpenGL keeps its own storage).</summary>
		internal override ObnsSharedMemory AllocateVertices(long bytes)
		{
			GpuRange range = Vertices.Allocate((ulong)bytes, vertexAlignment);
			return new ObnsSharedMemory { Offset = range.Offset, Owner = range, Mirror = true };
		}

		protected override void FreeVertexRange(GpuRange range) => Vertices.Free(range);

		/// <summary>Writes vertices into the copy (OpenGL writes its own as well).</summary>
		internal override bool WriteVertices(ObnsSharedMemory shared, LibRenderVertex[] data, long byteOffset)
		{
			if (!(shared?.Owner is GpuRange range) || data == null || data.Length == 0)
			{
				return false;
			}

			fixed (LibRenderVertex* source = data)
			{
				Vertices.Write(range.Block, range.Offset + (ulong)byteOffset, source, (ulong)data.Length * (ulong)sizeof(LibRenderVertex));
			}

			return true;
		}

		protected override GpuRange StoreIndices(uint[] data, ulong bytes)
		{
			GpuRange range = Indices.Allocate(bytes, 4);
			fixed (uint* source = data)
			{
				Indices.Write(range.Block, range.Offset, source, bytes);
			}

			return range;
		}

		protected override void FreeIndices(GpuRange range) => Indices.Free(range);

		protected override string DescribeStorage() => Vertices.Describe() + ", " + Indices.Describe();

		public override void Dispose()
		{
			base.Dispose();
			Vertices.Dispose();
			Indices.Dispose();
		}
	}
}
