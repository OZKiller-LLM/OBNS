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

// OBNS: not an upstream file. The Vulkan world layer's meshes (OBNS/VULKAN.md): upstream's vertex buffers, in storage
// shared with OpenGL, read as they are; the faces' triangle lists (WorldMeshes) in Vulkan's own buffers.

using System;
using System.Collections.Generic;
using LibRender2;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanMeshes : WorldMeshes
	{
		/// <summary>Vertex buffers' alignment in the shared heap: a whole vertex (for the draws' vertex offsets) and 64 bytes.</summary>
		private static readonly ulong vertexAlignment = (ulong)(VertexSize * 64 / Gcd(VertexSize, 64));

		private readonly Uploader uploader;

		/// <summary>The vertex buffers' storage, shared with OpenGL.</summary>
		internal readonly SharedHeap Vertices;

		/// <summary>The triangle indices (Vulkan's own).</summary>
		internal readonly GpuPool Indices;

		/// <summary>Whether OpenGL has written vertex buffers since the last frame.</summary>
		private bool written;
		private readonly HashSet<int> writtenBuffers = new HashSet<int>();

		internal VulkanMeshes(VulkanDevice device, Uploader uploader)
		{
			this.uploader = uploader;
			Vertices = new SharedHeap(device, 128UL << 20, Vk.BufferUsageVertexBuffer | Vk.BufferUsageTransferDst | Vk.BufferUsageTransferSrc, "vertices");
			Indices = new GpuPool(device, 64UL << 20, Vk.BufferUsageIndexBuffer | Vk.BufferUsageTransferDst, "indices");
		}

		private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

		/// <summary>Storage for a vertex buffer of so many bytes, in the shared heap.</summary>
		internal override ObnsSharedMemory AllocateVertices(long bytes)
		{
			GpuRange range = Vertices.Allocate((ulong)bytes, vertexAlignment, ~0u);
			return new ObnsSharedMemory { GLMemory = Vertices.GLMemory(range.Block), Offset = range.Offset, Owner = range };
		}

		/// <summary>A vertex buffer's storage is let go of: back to the heap once the frames in flight are done with it.</summary>
		internal override void FreeVertices(ObnsSharedMemory shared)
		{
			base.FreeVertices(shared);
			if (shared != null)
			{
				writtenBuffers.Remove(shared.GLBuffer);
			}
		}

		protected override void FreeVertexRange(GpuRange range) => Vertices.Free(range);

		/// <summary>The upload batch the vertex writes' barrier was recorded in (one per batch).</summary>
		private int vertexBarrierBatch = -1;

		/// <summary>
		/// Writes vertices into a buffer's storage with a copy on Vulkan's queue (ObnsSharing.WriteVertices), submitted
		/// with the uploads before the next frame's draws: after a barrier on the frames before it reading vertices, so no
		/// frame in flight sees half a write, and with no OpenGL access to the storage at all.
		/// </summary>
		internal override bool WriteVertices(ObnsSharedMemory shared, LibRender2.LibRenderVertex[] data, long byteOffset)
		{
			if (!(shared?.Owner is GpuRange range) || data == null || data.Length == 0)
			{
				return false;
			}

			ulong bytes = (ulong)data.Length * (ulong)sizeof(LibRender2.LibRenderVertex);
			ulong staging, stagingOffset;
			fixed (LibRender2.LibRenderVertex* source = data)
			{
				staging = uploader.Stage(source, bytes, out stagingOffset);
			}

			IntPtr commands = uploader.Commands;
			if (vertexBarrierBatch != uploader.Submissions)
			{
				// Earlier frames' vertex reads (all submitted before this batch) done before the copies write.
				vertexBarrierBatch = uploader.Submissions;
				Vk.MemoryBarrier barrier = new Vk.MemoryBarrier { SType = Vk.StructureTypeMemoryBarrier, SrcAccessMask = Vk.AccessVertexAttributeRead, DstAccessMask = Vk.AccessTransferWrite };
				Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageVertexInput, Vk.PipelineStageTransfer, 0, 1, &barrier, 0, null, 0, null);
			}

			Vk.BufferCopy region = new Vk.BufferCopy { SrcOffset = stagingOffset, DstOffset = range.Offset + (ulong)byteOffset, Size = bytes };
			Vk.vkCmdCopyBuffer(commands, staging, Vertices.Buffers[range.Block], 1, &region);
			return true;
		}

		/// <summary>OpenGL has written a vertex buffer (it is handed over at the next frame).</summary>
		internal override void Written(int buffer)
		{
			written = true;
			writtenBuffers.Add(buffer);
		}

		/// <summary>
		/// The vertex buffers written since the last frame, handed over now: their OpenGL names added for OpenGL's
		/// semaphore signal; whether there were any, for the frame's barrier. Then forgotten.
		/// </summary>
		internal bool TakeWritten(List<int> names)
		{
			names.AddRange(writtenBuffers);
			bool any = written;
			written = false;
			writtenBuffers.Clear();
			return any;
		}

		/// <summary>Records making vertex writes OpenGL handed over visible to the frame's vertex input.</summary>
		internal void AcquireWritten(IntPtr commandBuffer, bool handed)
		{
			if (!handed)
			{
				return;
			}

			Vk.MemoryBarrier barrier = new Vk.MemoryBarrier { SType = Vk.StructureTypeMemoryBarrier, DstAccessMask = Vk.AccessVertexAttributeRead };
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageTopOfPipe, Vk.PipelineStageVertexInput, 0, 1, &barrier, 0, null, 0, null);
		}

		protected override GpuRange StoreIndices(uint[] data, ulong bytes)
		{
			GpuRange range = Indices.Allocate(bytes, 4);
			fixed (uint* source = data)
			{
				ulong staging = uploader.Stage(source, bytes, out ulong stagingOffset);
				Vk.BufferCopy region = new Vk.BufferCopy { SrcOffset = stagingOffset, DstOffset = range.Offset, Size = bytes };
				Vk.vkCmdCopyBuffer(uploader.Commands, staging, Indices.Buffers[range.Block], 1, &region);
			}

			return range;
		}

		protected override void FreeIndices(GpuRange range) => Indices.Free(range);

		protected override void Stored()
		{
			if (uploader.Full)
			{
				uploader.Submit(true);
			}
		}

		protected override string DescribeStorage() => Vertices.Describe() + ", " + Indices.Describe();

		public override void Dispose()
		{
			base.Dispose();
			Vertices.Dispose();
			Indices.Dispose();
		}
	}
}
