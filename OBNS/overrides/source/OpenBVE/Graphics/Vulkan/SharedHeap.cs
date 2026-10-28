// OBNS: not an upstream file. Device memory both APIs use (OBNS/VULKAN.md): exportable blocks that OpenGL imports as
// memory objects, suballocated for vertex buffers and textures, so the GPU holds one copy of each.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class SharedHeap : IDisposable
	{
		private readonly VulkanDevice device;
		private readonly ulong blockSize;
		private readonly uint bufferUsage;
		private readonly string name;
		private readonly List<ulong> memories = new List<ulong>();
		private readonly List<RangeAllocator> allocators = new List<RangeAllocator>();
		private readonly List<int> glMemories = new List<int>();

		/// <summary>The blocks' buffers over the whole block (a buffer heap's), by block.</summary>
		internal readonly List<ulong> Buffers = new List<ulong>();

		/// <summary>A heap of buffer storage (with the buffers' usage) or, with usage 0, of image storage.</summary>
		internal SharedHeap(VulkanDevice device, ulong blockSize, uint bufferUsage, string name)
		{
			this.device = device;
			this.blockSize = blockSize;
			this.bufferUsage = bufferUsage;
			this.name = name;
		}

		internal ulong Memory(int block) => memories[block];

		/// <summary>A block size in bytes: the environment variable's megabytes if set (for measuring), else the default's.</summary>
		internal static ulong BlockSize(string variable, int defaultMegabytes)
		{
			return (ulong)(int.TryParse(Environment.GetEnvironmentVariable(variable), out int megabytes) && megabytes > 0 ? megabytes : defaultMegabytes) << 20;
		}

		/// <summary>The block's OpenGL memory object.</summary>
		internal int GLMemory(int block) => glMemories[block];

		internal string Describe()
		{
			ulong total = 0, used = 0;
			foreach (RangeAllocator allocator in allocators)
			{
				total += allocator.Size;
				used += allocator.Used;
			}

			return name + " " + (used >> 20) + " of " + (total >> 20) + " MB in " + allocators.Count + " shared blocks";
		}

		/// <summary>A range, in a block allowed by the type bits (images give theirs), making a block when none has room.</summary>
		internal GpuRange Allocate(ulong size, ulong alignment, uint typeBits)
		{
			if (alignment == 0)
			{
				alignment = 1;
			}

			for (int i = 0; i < allocators.Count; i++)
			{
				ulong offset = allocators[i].Allocate(size, alignment, out ulong taken);
				if (offset != ulong.MaxValue)
				{
					return new GpuRange { Block = i, Offset = offset, Start = offset + size - taken, Taken = taken };
				}
			}

			int block = AddBlock(Math.Max(blockSize, size + alignment), typeBits);
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

		private int AddBlock(ulong size, uint typeBits)
		{
			ulong buffer = 0;
			Vk.MemoryRequirements requirements;
			if (bufferUsage != 0)
			{
				Vk.ExternalMemoryBufferCreateInfo external = new Vk.ExternalMemoryBufferCreateInfo
				{
					SType = Vk.StructureTypeExternalMemoryBufferCreateInfo,
					HandleTypes = Vk.ExternalMemoryHandleTypeOpaqueWin32
				};
				Vk.BufferCreateInfo bufferInfo = new Vk.BufferCreateInfo { SType = Vk.StructureTypeBufferCreateInfo, PNext = &external, Size = size, Usage = bufferUsage };
				Vk.Check(Vk.vkCreateBuffer(device.Device, &bufferInfo, null, out buffer), "vkCreateBuffer (shared)");
				Vk.vkGetBufferMemoryRequirements(device.Device, buffer, out requirements);
			}
			else
			{
				requirements = new Vk.MemoryRequirements { Size = size, MemoryTypeBits = typeBits };
			}

			Vk.ExportMemoryAllocateInfo export = new Vk.ExportMemoryAllocateInfo
			{
				SType = Vk.StructureTypeExportMemoryAllocateInfo,
				HandleTypes = Vk.ExternalMemoryHandleTypeOpaqueWin32
			};
			Vk.MemoryAllocateInfo allocateInfo = new Vk.MemoryAllocateInfo
			{
				SType = Vk.StructureTypeMemoryAllocateInfo,
				PNext = &export,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyDeviceLocal)
			};
			Vk.Check(Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out ulong memory), "vkAllocateMemory (" + name + ", " + (size >> 20) + " MB)");
			if (buffer != 0)
			{
				Vk.Check(Vk.vkBindBufferMemory(device.Device, buffer, memory, 0), "vkBindBufferMemory (shared)");
			}

			// OpenGL imports the block once; the handle is not taken over by the import, so it is closed after.
			Vk.MemoryGetWin32HandleInfoKHR handleInfo = new Vk.MemoryGetWin32HandleInfoKHR
			{
				SType = Vk.StructureTypeMemoryGetWin32HandleInfoKhr,
				Memory = memory,
				HandleType = Vk.ExternalMemoryHandleTypeOpaqueWin32
			};
			Vk.Check(device.GetMemoryWin32Handle(device.Device, &handleInfo, out IntPtr handle), "vkGetMemoryWin32HandleKHR (shared)");
			LibRender2.ObnsSharing.CheckError("before importing a shared block (left by an earlier call)");
			GL.Ext.CreateMemoryObjects(1, out int glMemory);
			GL.Ext.ImportMemoryWin32Handle(glMemory, (long)requirements.Size, ExternalHandleType.HandleTypeOpaqueWin32Ext, handle);
			LibRender2.ObnsSharing.CheckError("importing a shared " + name + " block of " + (requirements.Size >> 20) + " MB");
			CloseHandle(handle);

			memories.Add(memory);
			Buffers.Add(buffer);
			glMemories.Add(glMemory);
			allocators.Add(new RangeAllocator(size));
			return memories.Count - 1;
		}

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		public void Dispose()
		{
			for (int i = 0; i < memories.Count; i++)
			{
				int glMemory = glMemories[i];
				GL.Ext.DeleteMemoryObjects(1, ref glMemory);
				if (Buffers[i] != 0)
				{
					Vk.vkDestroyBuffer(device.Device, Buffers[i], null);
				}

				Vk.vkFreeMemory(device.Device, memories[i], null);
			}

			memories.Clear();
			Buffers.Clear();
			glMemories.Clear();
			allocators.Clear();
		}
	}
}
