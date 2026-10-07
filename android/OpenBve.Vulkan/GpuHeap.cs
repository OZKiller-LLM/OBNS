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
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace OpenBve.Vulkan
{
	/*
	 * GPU memory in large blocks, handed out in pieces. A driver allows only a few thousand
	 * separate allocations (maxMemoryAllocationCount: 4096 on many phones), and a route has tens of
	 * thousands of meshes and textures, so each block holds many of them. Each block's free space
	 * is a list of ranges in address order, allocated first fit and merged with its neighbours
	 * when freed - simple, and quick enough for a heap that is mostly filled while loading and
	 * freed a little at a time.
	 */

	/// <summary>The free ranges of one block of memory.</summary>
	internal sealed class FreeRanges
	{
		private readonly List<(ulong Offset, ulong Size)> free = new List<(ulong, ulong)>();

		internal FreeRanges(ulong size)
		{
			free.Add((0, size));
		}

		/// <summary>Bytes free in the block.</summary>
		internal ulong FreeBytes { get; private set; }

		/// <summary>Takes a range of this size at this alignment; false when no free range fits.</summary>
		internal bool TryTake(ulong size, ulong alignment, out ulong offset)
		{
			for (int i = 0; i < free.Count; i++)
			{
				(ulong start, ulong length) = free[i];
				ulong aligned = (start + alignment - 1) / alignment * alignment;
				ulong padding = aligned - start;
				if (length < padding + size)
				{
					continue;
				}

				// The padding in front stays free; so does whatever is left behind.
				ulong after = length - padding - size;
				if (padding != 0 && after != 0)
				{
					free[i] = (start, padding);
					free.Insert(i + 1, (aligned + size, after));
				}
				else if (padding != 0)
				{
					free[i] = (start, padding);
				}
				else if (after != 0)
				{
					free[i] = (aligned + size, after);
				}
				else
				{
					free.RemoveAt(i);
				}

				offset = aligned;
				return true;
			}

			offset = 0;
			return false;
		}

		/// <summary>Gives a range back, merging it with free neighbours.</summary>
		internal void Give(ulong offset, ulong size)
		{
			int low = 0, high = free.Count;
			while (low < high)
			{
				int middle = (low + high) / 2;
				if (free[middle].Offset < offset)
				{
					low = middle + 1;
				}
				else
				{
					high = middle;
				}
			}

			int index = low;
			ulong start = offset, end = offset + size;
			if (index > 0 && free[index - 1].Offset + free[index - 1].Size == start)
			{
				index--;
				start = free[index].Offset;
				free.RemoveAt(index);
			}

			if (index < free.Count && free[index].Offset == end)
			{
				end = free[index].Offset + free[index].Size;
				free.RemoveAt(index);
			}

			free.Insert(index, (start, end - start));
		}

		/// <summary>Whether the whole block is free.</summary>
		internal bool Empty(ulong blockSize) => free.Count == 1 && free[0].Offset == 0 && free[0].Size == blockSize;
	}

	/// <summary>
	/// Buffer memory the CPU writes directly (host visible and coherent; on a phone also device
	/// local): vertex, index and uniform data, and staging for textures. Each block is one
	/// VkBuffer, mapped for its whole life.
	/// </summary>
	internal sealed unsafe class BufferHeap : IDisposable
	{
		internal sealed class Block
		{
			internal DeviceMemory Memory;
			internal Buffer Buffer;
			internal byte* Mapped;
			internal ulong Size;
			internal FreeRanges Free;
			internal bool Dedicated;
		}

		/// <summary>A piece of a block: the block's buffer at an offset.</summary>
		internal struct Allocation
		{
			internal Block Block;
			internal ulong Offset;
			internal ulong Size;

			internal bool IsNull => Block == null;

			internal Buffer Buffer => Block.Buffer;

			internal byte* Pointer => Block.Mapped + Offset;
		}

		private const ulong BlockSize = 32UL << 20;
		private const BufferUsageFlags Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.UniformBufferBit |
		                                       BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;

		private readonly Vk vk;
		private readonly Device device;
		private readonly uint memoryType;
		private readonly List<Block> blocks = new List<Block>();

		/// <summary>Bytes in use, for the log.</summary>
		internal ulong UsedBytes { get; private set; }

		/// <summary>Bytes reserved from the driver, for the log.</summary>
		internal ulong ReservedBytes { get; private set; }

		/// <summary>Whether the memory is device local as well (a phone's unified memory).</summary>
		internal bool DeviceLocal { get; }

		internal BufferHeap(VulkanContext context)
		{
			vk = context.Api;
			device = context.Device;
			// The memory types a buffer of this usage may live in.
			BufferCreateInfo probe = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = 256, Usage = Usage, SharingMode = SharingMode.Exclusive };
			vk.CreateBuffer(device, in probe, null, out Buffer buffer);
			vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements requirements);
			vk.DestroyBuffer(device, buffer, null);
			const MemoryPropertyFlags mapped = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
			if (context.TryMemoryType(requirements.MemoryTypeBits, mapped | MemoryPropertyFlags.DeviceLocalBit, out memoryType))
			{
				DeviceLocal = true;
			}
			else
			{
				memoryType = context.MemoryType(requirements.MemoryTypeBits, mapped);
			}
		}

		/// <summary>Takes memory for this many bytes.</summary>
		internal Allocation Allocate(ulong size, ulong alignment = 256)
		{
			size = Math.Max(size, 4);
			foreach (Block candidate in blocks)
			{
				if (!candidate.Dedicated && candidate.Free.TryTake(size, alignment, out ulong offset))
				{
					UsedBytes += size;
					return new Allocation { Block = candidate, Offset = offset, Size = size };
				}
			}

			bool dedicated = size > BlockSize / 2;
			Block block = NewBlock(dedicated ? size : BlockSize);
			block.Dedicated = dedicated;
			if (!block.Free.TryTake(size, alignment, out ulong first))
			{
				throw new InvalidOperationException("a new memory block has no room for " + size + " bytes");
			}

			UsedBytes += size;
			return new Allocation { Block = block, Offset = first, Size = size };
		}

		/// <summary>Gives memory back (only once the GPU is done with it).</summary>
		internal void Free(Allocation allocation)
		{
			if (allocation.IsNull)
			{
				return;
			}

			UsedBytes -= allocation.Size;
			Block block = allocation.Block;
			block.Free.Give(allocation.Offset, allocation.Size);
			if (block.Dedicated && block.Free.Empty(block.Size))
			{
				Release(block);
				blocks.Remove(block);
			}
		}

		private Block NewBlock(ulong size)
		{
			BufferCreateInfo info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = Usage, SharingMode = SharingMode.Exclusive };
			Check(vk.CreateBuffer(device, in info, null, out Buffer buffer), "vkCreateBuffer");
			vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements requirements);
			MemoryAllocateInfo allocateInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryType };
			Result result = vk.AllocateMemory(device, in allocateInfo, null, out DeviceMemory memory);
			if (result != Result.Success)
			{
				vk.DestroyBuffer(device, buffer, null);
				throw new OutOfMemoryException("vkAllocateMemory (" + (size >> 20) + " MB of buffer memory, " + (ReservedBytes >> 20) + " MB already) failed: " + result);
			}

			Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");
			void* mapped;
			Check(vk.MapMemory(device, memory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
			Block block = new Block { Memory = memory, Buffer = buffer, Mapped = (byte*)mapped, Size = size, Free = new FreeRanges(size) };
			blocks.Add(block);
			ReservedBytes += size;
			return block;
		}

		private void Release(Block block)
		{
			vk.UnmapMemory(device, block.Memory);
			vk.DestroyBuffer(device, block.Buffer, null);
			vk.FreeMemory(device, block.Memory, null);
			ReservedBytes -= block.Size;
		}

		private static void Check(Result result, string what)
		{
			if (result != Result.Success)
			{
				throw new InvalidOperationException(what + " failed: " + result);
			}
		}

		public void Dispose()
		{
			foreach (Block block in blocks)
			{
				Release(block);
			}

			blocks.Clear();
		}
	}

	/// <summary>Device memory for images (textures), in blocks per memory type.</summary>
	internal sealed unsafe class ImageHeap : IDisposable
	{
		internal sealed class Block
		{
			internal DeviceMemory Memory;
			internal uint MemoryType;
			internal ulong Size;
			internal FreeRanges Free;
			internal bool Dedicated;
		}

		internal struct Allocation
		{
			internal Block Block;
			internal ulong Offset;
			internal ulong Size;

			internal bool IsNull => Block == null;
		}

		private const ulong BlockSize = 64UL << 20;

		private readonly VulkanContext context;
		private readonly Vk vk;
		private readonly Device device;
		private readonly List<Block> blocks = new List<Block>();

		/// <summary>Bytes in use, for the log.</summary>
		internal ulong UsedBytes { get; private set; }

		/// <summary>Bytes reserved from the driver, for the log.</summary>
		internal ulong ReservedBytes { get; private set; }

		internal ImageHeap(VulkanContext context)
		{
			this.context = context;
			vk = context.Api;
			device = context.Device;
		}

		/// <summary>Takes memory for an image and binds the image to it.</summary>
		internal Allocation AllocateAndBind(Image image)
		{
			vk.GetImageMemoryRequirements(device, image, out MemoryRequirements requirements);
			uint type = context.MemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
			ulong size = requirements.Size;
			ulong alignment = Math.Max(requirements.Alignment, 16);
			Allocation allocation = default;
			foreach (Block candidate in blocks)
			{
				if (!candidate.Dedicated && candidate.MemoryType == type && candidate.Free.TryTake(size, alignment, out ulong offset))
				{
					allocation = new Allocation { Block = candidate, Offset = offset, Size = size };
					break;
				}
			}

			if (allocation.IsNull)
			{
				bool dedicated = size > BlockSize / 2;
				Block block = NewBlock(dedicated ? size : BlockSize, type);
				block.Dedicated = dedicated;
				block.Free.TryTake(size, alignment, out ulong offset);
				allocation = new Allocation { Block = block, Offset = offset, Size = size };
			}

			UsedBytes += size;
			Result bound = vk.BindImageMemory(device, image, allocation.Block.Memory, allocation.Offset);
			if (bound != Result.Success)
			{
				throw new InvalidOperationException("vkBindImageMemory failed: " + bound);
			}

			return allocation;
		}

		internal void Free(Allocation allocation)
		{
			if (allocation.IsNull)
			{
				return;
			}

			UsedBytes -= allocation.Size;
			Block block = allocation.Block;
			block.Free.Give(allocation.Offset, allocation.Size);
			if (block.Dedicated && block.Free.Empty(block.Size))
			{
				vk.FreeMemory(device, block.Memory, null);
				ReservedBytes -= block.Size;
				blocks.Remove(block);
			}
		}

		private Block NewBlock(ulong size, uint type)
		{
			MemoryAllocateInfo allocateInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type };
			Result result = vk.AllocateMemory(device, in allocateInfo, null, out DeviceMemory memory);
			if (result != Result.Success)
			{
				throw new OutOfMemoryException("vkAllocateMemory (" + (size >> 20) + " MB of image memory, " + (ReservedBytes >> 20) + " MB already) failed: " + result);
			}

			Block block = new Block { Memory = memory, MemoryType = type, Size = size, Free = new FreeRanges(size) };
			blocks.Add(block);
			ReservedBytes += size;
			return block;
		}

		public void Dispose()
		{
			foreach (Block block in blocks)
			{
				vk.FreeMemory(device, block.Memory, null);
			}

			blocks.Clear();
		}
	}
}
