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

// OBNS: not an upstream file. The Metal world layer's meshes (OBNS/METAL.md): a copy of upstream's vertex buffers (OpenGL
// keeps its own) and the faces' triangle lists (WorldMeshes), in Metal buffers suballocated as the other layers' are. The
// buffers are in memory the CPU shares (Apple silicon's unified memory), written directly: only while Metal draws nothing
// (each frame is waited for before upstream goes on to change anything).

using System;
using System.Collections.Generic;
using LibRender2;

namespace OpenBve.Graphics.Metal
{
	/// <summary>Metal buffers in blocks, suballocated.</summary>
	internal sealed unsafe class MetalBlocks : IDisposable
	{
		private static readonly IntPtr selNewBuffer = Mtl.Sel("newBufferWithLength:options:"), selContents = Mtl.Sel("contents");

		private readonly MetalDevice device;
		private readonly ulong blockSize;
		private readonly string name;
		private readonly List<RangeAllocator> allocators = new List<RangeAllocator>();

		/// <summary>The blocks' buffers and their memory, by block.</summary>
		internal readonly List<IntPtr> Buffers = new List<IntPtr>();
		private readonly List<IntPtr> contents = new List<IntPtr>();

		internal MetalBlocks(MetalDevice device, ulong blockSize, string name)
		{
			this.device = device;
			this.blockSize = blockSize;
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
			IntPtr buffer = Mtl.Id(device.Device, selNewBuffer, blockBytes, Mtl.ResourceShared);
			if (buffer == IntPtr.Zero)
			{
				throw new InvalidOperationException("Metal could not make a buffer of " + (blockBytes >> 20) + " MB");
			}

			Buffers.Add(buffer);
			contents.Add(Mtl.Id(buffer, selContents));
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
			Buffer.MemoryCopy(data, (byte*)contents[block] + offset, bytes, bytes);
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
				Mtl.Release(buffer);
			}

			Buffers.Clear();
			contents.Clear();
			allocators.Clear();
		}
	}

	internal sealed unsafe class MetalMeshes : WorldMeshes
	{
		/// <summary>Vertex ranges' alignment: a whole vertex (for the draws' base vertex) and 64 bytes.</summary>
		private static readonly ulong vertexAlignment = (ulong)(VertexSize * 64 / Gcd(VertexSize, 64));

		/// <summary>The copies of the vertex buffers.</summary>
		internal readonly MetalBlocks Vertices;

		/// <summary>The triangle indices.</summary>
		internal readonly MetalBlocks Indices;

		internal MetalMeshes(MetalDevice device)
		{
			Vertices = new MetalBlocks(device, 128UL << 20, "vertices");
			Indices = new MetalBlocks(device, 64UL << 20, "indices");
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
