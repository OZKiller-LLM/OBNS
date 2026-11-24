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

// OBNS: not an upstream file. GPU memory for the Vulkan world layer (OBNS/VULKAN.md): blocks suballocated for meshes
// and textures (a route has thousands; devices allow a few thousand allocations in all), and the uploads into them.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>Device memory in blocks, each optionally one buffer over the whole block, suballocated.</summary>
	internal sealed unsafe class GpuPool : IDisposable
	{
		private readonly VulkanDevice device;
		private readonly ulong blockSize;
		private readonly uint bufferUsage;
		private readonly string name;
		private readonly List<ulong> memories = new List<ulong>();
		/// <summary>Each block's memory type: a resource is only put in a block whose type it allows.</summary>
		private readonly List<uint> memoryTypes = new List<uint>();
		private readonly List<RangeAllocator> allocators = new List<RangeAllocator>();

		/// <summary>The blocks' buffers (buffer pools only), by block.</summary>
		internal readonly List<ulong> Buffers = new List<ulong>();

		/// <summary>A pool of buffers (with usage) or, with usage 0, of memory for images.</summary>
		internal GpuPool(VulkanDevice device, ulong blockSize, uint bufferUsage, string name)
		{
			this.device = device;
			this.blockSize = blockSize;
			this.bufferUsage = bufferUsage;
			this.name = name;
		}

		internal ulong Memory(int block) => memories[block];

		/// <summary>Bytes in blocks, and bytes in use, for the log.</summary>
		internal string Describe()
		{
			ulong total = 0, used = 0;
			int blocks = 0;
			foreach (RangeAllocator allocator in allocators)
			{
				if (allocator == null)
				{
					continue;
				}

				total += allocator.Size;
				used += allocator.Used;
				blocks++;
			}

			return name + " " + (used >> 20) + " of " + (total >> 20) + " MB in " + blocks + " blocks" + (SystemBlocks > 0 ? ", " + SystemBlocks + " of them in system memory" : "") + (BlocksReleased > 0 ? " (" + BlocksReleased + " released when empty)" : "");
		}

		/// <summary>A range of the pool, with the memory type bits a resource allows (images), making a block when none has room.</summary>
		internal GpuRange Allocate(ulong size, ulong alignment, uint typeBits = ~0u)
		{
			if (alignment == 0)
			{
				alignment = 1;
			}

			for (int i = 0; i < allocators.Count; i++)
			{
				if (allocators[i] == null || (typeBits & (1u << (int)memoryTypes[i])) == 0)
				{
					// (An image may only go in some memory types: on NVIDIA, mipmapped images and others differ.)
					continue;
				}

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
				RangeAllocator allocator = allocators[range.Block];
				allocator.Free(range.Start, range.Taken);
				/*
				 * A block of images left empty is given back (a pool of images only: a buffer pool's blocks are bound to
				 * buffers the meshes name). Freed ranges are reused as they are, but a block lived for the rest of the game:
				 * on a 16 GB Windows ARM64 laptop (Adreno X1-45, memory shared with the CPU) the Tuen Ma Line's textures grew
				 * to 17 blocks, 4.1 GB, and the driver ran out of host memory (2026-11-09). With unused textures unloaded
				 * (Potato mode, or the option), what a stretch of the line no longer needs can go back to the system. The
				 * first block is kept, for the textures every game has. The images in it are destroyed already (Destroy),
				 * after the frames that drew them.
				 */
				if (bufferUsage == 0 && allocator.Used == 0 && range.Block > 0)
				{
					if (!systemMemory[range.Block])
					{
						deviceLocalBytes -= Math.Min(deviceLocalBytes, allocator.Size);
					}

					Vk.vkFreeMemory(device.Device, memories[range.Block], null);
					memories[range.Block] = 0;
					allocators[range.Block] = null;
					BlocksReleased++;
				}
			}
		}

		/// <summary>Blocks of images given back to the system because they were left empty, for the log.</summary>
		internal int BlocksReleased;

		/// <summary>Blocks of images put in system memory because the GPU's was full, for the log.</summary>
		internal int SystemBlocks;

		/// <summary>VK_ERROR_OUT_OF_DEVICE_MEMORY.</summary>
		private const int OutOfDeviceMemory = -2;

		/// <summary>The most of the GPU's own memory a pool of images takes before its blocks go in system memory (0: no limit).</summary>
		internal ulong DeviceLocalLimit;

		private ulong deviceLocalBytes;

		/// <summary>Whether each block is in system memory.</summary>
		private readonly List<bool> systemMemory = new List<bool>();

		private int AddBlock(ulong size, uint typeBits)
		{
			ulong buffer = 0;
			Vk.MemoryRequirements requirements;
			if (bufferUsage != 0)
			{
				Vk.BufferCreateInfo bufferInfo = new Vk.BufferCreateInfo { SType = Vk.StructureTypeBufferCreateInfo, Size = size, Usage = bufferUsage };
				Vk.Check(Vk.vkCreateBuffer(device.Device, &bufferInfo, null, out buffer), "vkCreateBuffer");
				Vk.vkGetBufferMemoryRequirements(device.Device, buffer, out requirements);
			}
			else
			{
				requirements = new Vk.MemoryRequirements { Size = size, MemoryTypeBits = typeBits };
			}

			uint memoryType = device.MemoryType(requirements.MemoryTypeBits & typeBits, Vk.MemoryPropertyDeviceLocal);
			Vk.MemoryAllocateInfo allocateInfo = new Vk.MemoryAllocateInfo
			{
				SType = Vk.StructureTypeMemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = memoryType
			};
			// Past its share of the GPU's memory, a pool of images takes system memory at once: the rest of the GPU's is for what
			// must be there (the frame's targets: with every texture of the Tuen Ma Line in it, one failed 33 s into the drive).
			bool overShare = bufferUsage == 0 && DeviceLocalLimit > 0 && deviceLocalBytes + requirements.Size > DeviceLocalLimit;
			ulong memory = 0;
			int result = overShare ? OutOfDeviceMemory : Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out memory);
			bool inSystemMemory = false;

			if (result == OutOfDeviceMemory && bufferUsage == 0)
			{
				/*
				 * The GPU's memory full: images in system memory, which the GPU reads over the bus (slower to draw from, but
				 * drawn), as Direct3D's and OpenGL's drivers do of themselves. Every texture of the Tuen Ma Line loaded in
				 * advance came to 6.4 GB, and on an 8 GB card the next block failed (the user, 2026-11-25: load ALL of them).
				 */
				uint system = device.SystemMemoryType(requirements.MemoryTypeBits & typeBits);
				if (system != uint.MaxValue)
				{
					allocateInfo.MemoryTypeIndex = system;
					result = Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out memory);
					if (result >= 0)
					{
						memoryType = system;
						inSystemMemory = true;
						SystemBlocks++;
						if (SystemBlocks == 1)
						{
							Program.FileSystem.AppendToLogFile("OBNS Vulkan: " + (overShare ? "the " + name + "' share of the GPU's memory (" + (DeviceLocalLimit >> 20) + " MB) is taken" : "the GPU's memory is full") + "; more " + name + " go in system memory (" + (size >> 20) + " MB blocks)");
						}
					}
				}
			}

			Vk.Check(result, "vkAllocateMemory (" + name + ", " + (size >> 20) + " MB)");
			if (bufferUsage == 0 && !inSystemMemory)
			{
				deviceLocalBytes += requirements.Size;
			}
			if (buffer != 0)
			{
				Vk.Check(Vk.vkBindBufferMemory(device.Device, buffer, memory, 0), "vkBindBufferMemory");
			}

			// A slot a released block left (images only, which name their block by its index) is taken again.
			int slot = bufferUsage == 0 ? allocators.IndexOf(null) : -1;
			if (slot >= 0)
			{
				systemMemory[slot] = inSystemMemory;
				memories[slot] = memory;
				memoryTypes[slot] = memoryType;
				Buffers[slot] = buffer;
				allocators[slot] = new RangeAllocator(size);
				return slot;
			}

			memories.Add(memory);
			systemMemory.Add(inSystemMemory);
			memoryTypes.Add(memoryType);
			Buffers.Add(buffer);
			allocators.Add(new RangeAllocator(size));
			return memories.Count - 1;
		}

		public void Dispose()
		{
			for (int i = 0; i < memories.Count; i++)
			{
				if (Buffers[i] != 0)
				{
					Vk.vkDestroyBuffer(device.Device, Buffers[i], null);
				}

				// (A block released when it was left empty is 0.)
				if (memories[i] != 0)
				{
					Vk.vkFreeMemory(device.Device, memories[i], null);
				}
			}

			memories.Clear();
			systemMemory.Clear();
			memoryTypes.Clear();
			Buffers.Clear();
			allocators.Clear();
		}
	}

	/// <summary>A host-visible, coherent buffer, mapped while it lives; one read back by the CPU in cached memory where there is some.</summary>
	internal sealed unsafe class HostBuffer : IDisposable
	{
		private readonly VulkanDevice device;
		internal readonly ulong Buffer;
		private readonly ulong memory;
		internal readonly byte* Data;
		internal readonly ulong Size;

		internal HostBuffer(VulkanDevice device, ulong size, uint usage, bool readBack = false)
		{
			this.device = device;
			Size = size;
			Vk.BufferCreateInfo bufferInfo = new Vk.BufferCreateInfo { SType = Vk.StructureTypeBufferCreateInfo, Size = size, Usage = usage };
			Vk.Check(Vk.vkCreateBuffer(device.Device, &bufferInfo, null, out Buffer), "vkCreateBuffer");
			Vk.vkGetBufferMemoryRequirements(device.Device, Buffer, out Vk.MemoryRequirements requirements);
			Vk.MemoryAllocateInfo allocateInfo = new Vk.MemoryAllocateInfo
			{
				SType = Vk.StructureTypeMemoryAllocateInfo,
				AllocationSize = requirements.Size,
				// Uncached (write-combined) memory is read by the CPU a great deal slower: a snapshot took about half a second so.
				MemoryTypeIndex = readBack && device.HasMemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyHostVisible | Vk.MemoryPropertyHostCoherent | Vk.MemoryPropertyHostCached)
					? device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyHostVisible | Vk.MemoryPropertyHostCoherent | Vk.MemoryPropertyHostCached)
					: device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyHostVisible | Vk.MemoryPropertyHostCoherent)
			};
			Vk.Check(Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out memory), "vkAllocateMemory (host, " + (size >> 20) + " MB)");
			Vk.Check(Vk.vkBindBufferMemory(device.Device, Buffer, memory, 0), "vkBindBufferMemory");
			Vk.Check(Vk.vkMapMemory(device.Device, memory, 0, ulong.MaxValue, 0, out IntPtr mapped), "vkMapMemory");
			Data = (byte*)mapped;
		}

		public void Dispose()
		{
			Vk.vkUnmapMemory(device.Device, memory);
			Vk.vkDestroyBuffer(device.Device, Buffer, null);
			Vk.vkFreeMemory(device.Device, memory, null);
		}
	}

	/// <summary>
	/// Uploads into device memory: data is staged in host-visible buffers and the copies recorded into a command buffer
	/// of the uploader's own, submitted before the frame's (queue order, and a barrier at its end, make the data visible
	/// to the frame). Staging is kept until the GPU has finished the upload.
	/// </summary>
	internal sealed unsafe class Uploader : IDisposable
	{
		private const ulong ChunkSize = 16 << 20;

		/// <summary>Staged bytes after which an upload is submitted and waited for at once (loading makes many).</summary>
		private const ulong FlushThreshold = 128 << 20;

		private readonly VulkanDevice device;
		private ulong commandPool;

		private sealed class Batch
		{
			internal IntPtr CommandBuffer;
			internal ulong Fence;
			internal readonly List<HostBuffer> Chunks = new List<HostBuffer>();
			internal bool Recording, Submitted;
		}

		private readonly List<Batch> batches = new List<Batch>();

		// Staging chunks kept for reuse once their upload is done (allocating and mapping one costs about a millisecond,
		// and a frame making textures stages several), up to this many.
		private const int KeptChunks = 8;
		private readonly Stack<HostBuffer> keptChunks = new Stack<HostBuffer>();

		private Batch current;
		private HostBuffer chunk;
		private ulong chunkUsed, staged;

		internal Uploader(VulkanDevice device)
		{
			this.device = device;
			Vk.CommandPoolCreateInfo poolInfo = new Vk.CommandPoolCreateInfo
			{
				SType = Vk.StructureTypeCommandPoolCreateInfo,
				Flags = Vk.CommandPoolCreateResetCommandBuffer,
				QueueFamilyIndex = device.QueueFamily
			};
			Vk.Check(Vk.vkCreateCommandPool(device.Device, &poolInfo, null, out commandPool), "vkCreateCommandPool");
		}

		/// <summary>The command buffer the uploads are being recorded into, begun if need be.</summary>
		internal IntPtr Commands
		{
			get
			{
				if (current == null)
				{
					current = FreeBatch();
					Vk.CommandBufferBeginInfo beginInfo = new Vk.CommandBufferBeginInfo { SType = Vk.StructureTypeCommandBufferBeginInfo, Flags = Vk.CommandBufferUsageOneTimeSubmit };
					Vk.Check(Vk.vkResetCommandBuffer(current.CommandBuffer, 0), "vkResetCommandBuffer");
					Vk.Check(Vk.vkBeginCommandBuffer(current.CommandBuffer, &beginInfo), "vkBeginCommandBuffer");
					current.Recording = true;
				}

				return current.CommandBuffer;
			}
		}

		private Batch FreeBatch()
		{
			foreach (Batch batch in batches)
			{
				if (batch.Submitted)
				{
					ulong fence = batch.Fence;
					if (Vk.vkWaitForFences(device.Device, 1, &fence, 1, 0) != Vk.Success)
					{
						continue;
					}

					Release(batch);
				}

				if (!batch.Recording && !batch.Submitted)
				{
					return batch;
				}
			}

			Batch made = new Batch();
			Vk.CommandBufferAllocateInfo allocateInfo = new Vk.CommandBufferAllocateInfo
			{
				SType = Vk.StructureTypeCommandBufferAllocateInfo,
				CommandPool = commandPool,
				CommandBufferCount = 1
			};
			IntPtr commandBuffer;
			Vk.Check(Vk.vkAllocateCommandBuffers(device.Device, &allocateInfo, &commandBuffer), "vkAllocateCommandBuffers");
			made.CommandBuffer = commandBuffer;
			Vk.FenceCreateInfo fenceInfo = new Vk.FenceCreateInfo { SType = Vk.StructureTypeFenceCreateInfo };
			Vk.Check(Vk.vkCreateFence(device.Device, &fenceInfo, null, out made.Fence), "vkCreateFence");
			batches.Add(made);
			return made;
		}

		private void Release(Batch batch)
		{
			ulong fence = batch.Fence;
			Vk.vkResetFences(device.Device, 1, &fence);
			foreach (HostBuffer staging in batch.Chunks)
			{
				if (staging.Size == ChunkSize && keptChunks.Count < KeptChunks)
				{
					keptChunks.Push(staging);
				}
				else
				{
					staging.Dispose();
				}
			}

			batch.Chunks.Clear();
			batch.Submitted = false;
		}

		/// <summary>Copies bytes into staging; returns the staging buffer and offset to copy from.</summary>
		internal ulong Stage(void* data, ulong size, out ulong offset)
		{
			ulong buffer = Reserve(size, out offset, out byte* target);
			Buffer.MemoryCopy(data, target, size, size);
			return buffer;
		}

		/// <summary>Staging for bytes the caller writes (before the uploads are submitted); returns the staging buffer and offset to copy from.</summary>
		internal ulong Reserve(ulong size, out ulong offset, out byte* target)
		{
			IntPtr commands = Commands;
			const ulong alignment = 16;
			if (chunk == null || chunkUsed + size > chunk.Size)
			{
				chunk = size <= ChunkSize && keptChunks.Count > 0 ? keptChunks.Pop() : new HostBuffer(device, Math.Max(ChunkSize, size), Vk.BufferUsageTransferSrc);
				current.Chunks.Add(chunk);
				chunkUsed = 0;
			}

			offset = chunkUsed;
			target = chunk.Data + offset;
			chunkUsed = (chunkUsed + size + alignment - 1) / alignment * alignment;
			staged += size;
			return chunk.Buffer;
		}

		// Writes into reserved staging, made when the uploads are submitted.
		private readonly List<Action> fills = new List<Action>();
		private Action<int, int> runFills;
		private static readonly bool logFills = Environment.GetEnvironmentVariable("OBNS_VULKAN_DEBUG") == "1";

		/// <summary>Writes staging when the uploads are submitted (on every core for the frame's submit).</summary>
		internal void FillLater(Action fill)
		{
			fills.Add(fill);
		}

		private void RunFills(bool parallel)
		{
			if (fills.Count == 0)
			{
				return;
			}

			long fillStart = System.Diagnostics.Stopwatch.GetTimestamp();
			if (parallel)
			{
				if (runFills == null)
				{
					runFills = (start, end) =>
					{
						for (int i = start; i < end; i++)
						{
							fills[i]();
						}
					};
				}

				OpenBve.FrameWorkers.Shared.Run(fills.Count, runFills);
			}
			else
			{
				foreach (Action fill in fills)
				{
					fill();
				}
			}

			if (logFills)
			{
				Program.FileSystem.AppendToLogFile(string.Format(System.Globalization.CultureInfo.InvariantCulture, "OBNS Vulkan staging written: {0} pieces, {1:0.00} ms{2}", fills.Count, (System.Diagnostics.Stopwatch.GetTimestamp() - fillStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, parallel ? " on every core" : ""));
			}

			fills.Clear();
		}

		/// <summary>Whether so much is staged that it should be submitted now.</summary>
		internal bool Full => staged >= FlushThreshold;

		/// <summary>Time spent waiting for uploads to finish, for the log.</summary>
		internal static long WaitMilliseconds;

		/// <summary>
		/// Submits what has been recorded, ending with a barrier making it visible to everything after it on the queue;
		/// with wait, until the GPU has done it (and the staging is freed). Staging still to be written is written first:
		/// with parallel, on every core (only from the render thread between frames' work, as FrameWorkers runs one job).
		/// </summary>
		/// <summary>How many times the uploads have been submitted (a new batch begins after each).</summary>
		internal int Submissions { get; private set; }

		internal void Submit(bool wait, bool parallel = false)
		{
			Submissions++;
			if (current == null)
			{
				return;
			}

			RunFills(parallel);

			Vk.MemoryBarrier barrier = new Vk.MemoryBarrier
			{
				SType = Vk.StructureTypeMemoryBarrier,
				SrcAccessMask = Vk.AccessTransferWrite,
				DstAccessMask = Vk.AccessVertexAttributeRead | Vk.AccessIndexRead | Vk.AccessShaderRead | Vk.AccessTransferRead | Vk.AccessTransferWrite
			};
			Vk.vkCmdPipelineBarrier(current.CommandBuffer, Vk.PipelineStageTransfer, Vk.PipelineStageAllCommands, 0, 1, &barrier, 0, null, 0, null);
			Vk.Check(Vk.vkEndCommandBuffer(current.CommandBuffer), "vkEndCommandBuffer");
			IntPtr commandBuffer = current.CommandBuffer;
			Vk.SubmitInfo submit = new Vk.SubmitInfo { SType = Vk.StructureTypeSubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
			lock (device.QueueLock)
			{
				Vk.Check(Vk.vkQueueSubmit(device.Queue, 1, &submit, current.Fence), "vkQueueSubmit (uploads)");
			}
			current.Recording = false;
			current.Submitted = true;
			if (wait)
			{
				ulong fence = current.Fence;
				long start = System.Diagnostics.Stopwatch.GetTimestamp();
				VulkanWorld.Step.Uploads = "waiting for an upload";
				Vk.Check(Vk.vkWaitForFences(device.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences (uploads)");
				VulkanWorld.Step.Uploads = "idle";
				WaitMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000 / System.Diagnostics.Stopwatch.Frequency;
				Release(current);
			}

			current = null;
			chunk = null;
			chunkUsed = 0;
			staged = 0;
		}

		public void Dispose()
		{
			Vk.vkDeviceWaitIdle(device.Device);
			foreach (Batch batch in batches)
			{
				foreach (HostBuffer staging in batch.Chunks)
				{
					staging.Dispose();
				}

				Vk.vkDestroyFence(device.Device, batch.Fence, null);
			}

			batches.Clear();
			while (keptChunks.Count > 0)
			{
				keptChunks.Pop().Dispose();
			}

			if (commandPool != 0)
			{
				Vk.vkDestroyCommandPool(device.Device, commandPool, null);
				commandPool = 0;
			}
		}
	}
}
