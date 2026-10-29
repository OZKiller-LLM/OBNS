// OBNS: not an upstream file. GPU memory for the Vulkan world layer (OBNS/VULKAN.md): blocks suballocated for meshes
// and textures (a route has thousands; devices allow a few thousand allocations in all), and the uploads into them.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>First-fit ranges within one block, merged again when freed.</summary>
	internal sealed class RangeAllocator
	{
		private readonly List<ulong> freeOffsets = new List<ulong>();
		private readonly List<ulong> freeSizes = new List<ulong>();
		internal readonly ulong Size;
		internal ulong Used { get; private set; }

		internal RangeAllocator(ulong size)
		{
			Size = size;
			freeOffsets.Add(0);
			freeSizes.Add(size);
		}

		/// <summary>An offset for size bytes aligned to alignment (any positive number), or ulong.MaxValue if none fits.</summary>
		internal ulong Allocate(ulong size, ulong alignment, out ulong taken)
		{
			for (int i = 0; i < freeOffsets.Count; i++)
			{
				ulong start = freeOffsets[i];
				ulong aligned = (start + alignment - 1) / alignment * alignment;
				ulong end = freeOffsets[i] + freeSizes[i];
				if (aligned + size > end)
				{
					continue;
				}

				// The range taken includes the alignment padding, so it is given back whole.
				taken = aligned + size - start;
				if (aligned + size == end)
				{
					freeOffsets.RemoveAt(i);
					freeSizes.RemoveAt(i);
				}
				else
				{
					freeOffsets[i] = aligned + size;
					freeSizes[i] = end - (aligned + size);
				}

				Used += taken;
				return aligned;
			}

			taken = 0;
			return ulong.MaxValue;
		}

		/// <summary>Gives back a range (its start before alignment, and the size taken).</summary>
		internal void Free(ulong start, ulong taken)
		{
			Used -= taken;
			int i = freeOffsets.BinarySearch(start);
			i = i < 0 ? ~i : i;
			freeOffsets.Insert(i, start);
			freeSizes.Insert(i, taken);
			// Merge with the next and previous ranges.
			if (i + 1 < freeOffsets.Count && freeOffsets[i] + freeSizes[i] == freeOffsets[i + 1])
			{
				freeSizes[i] += freeSizes[i + 1];
				freeOffsets.RemoveAt(i + 1);
				freeSizes.RemoveAt(i + 1);
			}

			if (i > 0 && freeOffsets[i - 1] + freeSizes[i - 1] == freeOffsets[i])
			{
				freeSizes[i - 1] += freeSizes[i];
				freeOffsets.RemoveAt(i);
				freeSizes.RemoveAt(i);
			}
		}
	}

	/// <summary>A suballocated range: its block, its aligned offset, and what to give back.</summary>
	internal struct GpuRange
	{
		internal int Block;
		internal ulong Offset;
		internal ulong Start;
		internal ulong Taken;
		internal bool Valid => Taken != 0;
	}

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
			foreach (RangeAllocator allocator in allocators)
			{
				total += allocator.Size;
				used += allocator.Used;
			}

			return name + " " + (used >> 20) + " of " + (total >> 20) + " MB in " + allocators.Count + " blocks";
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
				if ((typeBits & (1u << (int)memoryTypes[i])) == 0)
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
				allocators[range.Block].Free(range.Start, range.Taken);
			}
		}

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
			Vk.Check(Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out ulong memory), "vkAllocateMemory (" + name + ", " + (size >> 20) + " MB)");
			if (buffer != 0)
			{
				Vk.Check(Vk.vkBindBufferMemory(device.Device, buffer, memory, 0), "vkBindBufferMemory");
			}

			memories.Add(memory);
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

				Vk.vkFreeMemory(device.Device, memories[i], null);
			}

			memories.Clear();
			memoryTypes.Clear();
			Buffers.Clear();
			allocators.Clear();
		}
	}

	/// <summary>A host-visible, coherent buffer, mapped while it lives.</summary>
	internal sealed unsafe class HostBuffer : IDisposable
	{
		private readonly VulkanDevice device;
		internal readonly ulong Buffer;
		private readonly ulong memory;
		internal readonly byte* Data;
		internal readonly ulong Size;

		internal HostBuffer(VulkanDevice device, ulong size, uint usage)
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
				MemoryTypeIndex = device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyHostVisible | Vk.MemoryPropertyHostCoherent)
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
