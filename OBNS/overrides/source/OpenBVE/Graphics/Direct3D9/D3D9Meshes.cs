// OBNS: not an upstream file. The Direct3D 9.0c world layer's meshes (OBNS/DIRECT3D.md): a copy of upstream's vertex
// buffers (OpenGL keeps its own) and the faces' triangle lists (WorldMeshes), in Direct3D 9 buffers suballocated as the
// other layers' are. Written by locking the range (without waiting: a range is only written while no frame draws it,
// apart from a mesh changing its own vertices, as upstream's do).

using System;
using System.Collections.Generic;
using LibRender2;

namespace OpenBve.Graphics.Direct3D9
{
	/// <summary>Direct3D 9 vertex or index buffers in blocks, suballocated.</summary>
	internal sealed unsafe class D3D9Blocks : IDisposable
	{
		private readonly D3D9Device device;
		private readonly ulong blockSize;
		private readonly bool indices;
		private readonly string name;
		private readonly List<RangeAllocator> allocators = new List<RangeAllocator>();

		/// <summary>The blocks' buffers and sizes, by block.</summary>
		internal readonly List<IntPtr> Buffers = new List<IntPtr>();
		internal readonly List<ulong> Sizes = new List<ulong>();

		internal D3D9Blocks(D3D9Device device, ulong blockSize, bool indices, string name)
		{
			this.device = device;
			this.blockSize = blockSize;
			this.indices = indices;
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

			ulong blockBytes = Math.Max(blockSize, (size + alignment + 65535) / 65536 * 65536);
			Buffers.Add(indices ? D9.CreateIndexBuffer(device.Device, (uint)blockBytes, D9.UsageWriteOnly) : D9.CreateVertexBuffer(device.Device, (uint)blockBytes, D9.UsageWriteOnly));
			Sizes.Add(blockBytes);
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

		/// <summary>Writes bytes into a block.</summary>
		internal void Write(int block, ulong offset, void* data, ulong bytes)
		{
			byte* to = D9.LockBuffer(Buffers[block], (uint)offset, (uint)bytes, D9.LockNoOverwrite);
			Buffer.MemoryCopy(data, to, bytes, bytes);
			D9.UnlockBuffer(Buffers[block]);
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
				D9.Release(buffer);
			}

			Buffers.Clear();
			Sizes.Clear();
			allocators.Clear();
		}
	}

	internal sealed unsafe class D3D9Meshes : WorldMeshes
	{
		/// <summary>Vertex ranges' alignment: a whole vertex (for the draws' base vertex) and 64 bytes.</summary>
		private static readonly ulong vertexAlignment = (ulong)(VertexSize * 64 / Gcd(VertexSize, 64));

		/// <summary>The copies of the vertex buffers.</summary>
		internal readonly D3D9Blocks Vertices;

		/// <summary>The triangle indices.</summary>
		internal readonly D3D9Blocks Indices;

		internal D3D9Meshes(D3D9Device device)
		{
			Vertices = new D3D9Blocks(device, 128UL << 20, false, "vertices");
			Indices = new D3D9Blocks(device, 64UL << 20, true, "indices");
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
