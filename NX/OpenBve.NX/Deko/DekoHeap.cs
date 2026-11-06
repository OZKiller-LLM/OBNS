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

namespace OpenBve.NX.Deko
{
	/// <summary>
	/// Memory for DekoGL: deko3d memory blocks of 64 MB (or one of its own for something larger), each handed out in
	/// pieces, first fit, the free pieces kept in order and joined again when freed - as Android's GpuHeap does on Vulkan.
	/// Two heaps: buffers (the CPU writes them: vertices, indices, uniforms, staging) and images (the GPU's alone).
	/// </summary>
	internal sealed unsafe class DekoHeap
	{
		internal const uint BlockSize = 64u << 20;

		internal sealed class Block
		{
			internal IntPtr Handle;
			internal byte* Cpu;
			internal ulong Gpu;
			internal uint Size;
			/// <summary>Free pieces, by offset: (offset, size).</summary>
			internal readonly SortedList<uint, uint> Free = new SortedList<uint, uint>();
		}

		/// <summary>A piece of a block. <see cref="Pointer"/> is null in the image heap.</summary>
		internal struct Allocation
		{
			internal Block Block;
			internal uint Offset;
			internal uint Size;
			internal bool IsNull => Block == null;
			internal byte* Pointer => Block.Cpu == null ? null : Block.Cpu + Offset;
			internal ulong Gpu => Block.Gpu + Offset;
		}

		private readonly bool images;
		private readonly List<Block> blocks = new List<Block>();

		/// <summary>Bytes handed out, and in blocks, for the log.</summary>
		internal ulong UsedBytes { get; private set; }

		internal ulong ReservedBytes { get; private set; }

		internal DekoHeap(bool images)
		{
			this.images = images;
		}

		private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) / alignment * alignment;

		internal Allocation Allocate(ulong bytes, uint alignment = 256)
		{
			uint size = AlignUp((uint)Math.Max(1UL, bytes), 256);
			alignment = Math.Max(alignment, 256u);
			foreach (Block block in blocks)
			{
				if (TryTake(block, size, alignment, out uint offset))
				{
					UsedBytes += size;
					return new Allocation { Block = block, Offset = offset, Size = size };
				}
			}

			uint blockSize = Math.Max(BlockSize, AlignUp(size + alignment, 4096));
			IntPtr handle = DekoNative.Memory(blockSize, images ? 1 : 0, out IntPtr cpu, out ulong gpu);
			if (handle == IntPtr.Zero)
			{
				throw new OutOfMemoryException("deko3d: no memory block of " + (blockSize >> 20) + " MB for " + (images ? "images" : "buffers"));
			}

			Block added = new Block { Handle = handle, Cpu = (byte*)cpu, Gpu = gpu, Size = blockSize };
			added.Free.Add(0, blockSize);
			blocks.Add(added);
			ReservedBytes += blockSize;
			if (!TryTake(added, size, alignment, out uint first))
			{
				throw new OutOfMemoryException("deko3d: a new block could not hold " + size + " bytes");
			}

			UsedBytes += size;
			return new Allocation { Block = added, Offset = first, Size = size };
		}

		private static bool TryTake(Block block, uint size, uint alignment, out uint offset)
		{
			// The GPU address's alignment is what counts: blocks start 4 KB aligned.
			for (int i = 0; i < block.Free.Count; i++)
			{
				uint start = block.Free.Keys[i], length = block.Free.Values[i];
				uint aligned = (uint)(AlignUp64(block.Gpu + start, alignment) - block.Gpu);
				if (aligned + size > start + length)
				{
					continue;
				}

				block.Free.RemoveAt(i);
				if (aligned > start)
				{
					block.Free.Add(start, aligned - start);
				}

				uint end = aligned + size;
				if (end < start + length)
				{
					block.Free.Add(end, start + length - end);
				}

				offset = aligned;
				return true;
			}

			offset = 0;
			return false;
		}

		private static ulong AlignUp64(ulong value, uint alignment) => (value + alignment - 1) / alignment * alignment;

		internal void Free(Allocation allocation)
		{
			if (allocation.IsNull)
			{
				return;
			}

			Block block = allocation.Block;
			uint start = allocation.Offset, size = allocation.Size;
			UsedBytes -= size;
			// Joined with the free pieces either side.
			int index = 0;
			while (index < block.Free.Count && block.Free.Keys[index] < start)
			{
				index++;
			}

			if (index < block.Free.Count && start + size == block.Free.Keys[index])
			{
				size += block.Free.Values[index];
				block.Free.RemoveAt(index);
			}

			if (index > 0 && block.Free.Keys[index - 1] + block.Free.Values[index - 1] == start)
			{
				uint previous = block.Free.Keys[index - 1];
				size += start - previous;
				start = previous;
				block.Free.RemoveAt(index - 1);
			}

			block.Free.Add(start, size);
		}

		internal void Dispose()
		{
			foreach (Block block in blocks)
			{
				DekoNative.MemoryFree(block.Handle);
			}

			blocks.Clear();
		}
	}
}
