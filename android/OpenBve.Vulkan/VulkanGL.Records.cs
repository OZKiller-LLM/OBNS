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
using System.Numerics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace OpenBve.Vulkan
{
	/*
	 * Recording on several threads. A draw does not go into a command buffer when the renderer
	 * makes it: everything it needs - pipeline, descriptor sets and their offsets, viewport, vertex
	 * and index buffers, the dynamic state of the 1.3 path - is worked out on the render thread
	 * (texture uploads, pipelines and the uniform ring stay there) and kept as a record. At the swap
	 * the frame's records are cut into consecutive chunks, each recorded into a secondary command
	 * buffer by a worker thread, and the frame's command buffer runs them in order inside its render
	 * pass. The commands are the same, in the same order; only who records them changes. On TML's
	 * exterior recording ~6,000 draws took 8-15 ms of the render thread's frame.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int MaxChunks = 8;
		private const int RecordsPerChunk = 400;

		/// <summary>
		/// Runs body(0..count-1) on the host's worker threads and returns when all are done (the port
		/// sets FrameWorkers here); null: one after another on the render thread.
		/// </summary>
		public static Action<int, Action<int>> ParallelRun;

		/// <summary>How many threads ParallelRun uses (the chunks are cut to fit them).</summary>
		public static int ParallelThreads = 1;

		private enum RecordKind : byte
		{
			Draw,
			DrawIndexed,
			Clear
		}

		private struct DrawRecord
		{
			internal RecordKind Kind;
			internal bool Quads;
			internal byte VertexBufferCount;
			internal byte ClearMask;
			internal Pipeline Pipeline;
			internal DescriptorSet UniformSet;
			internal uint Offset0, Offset1;
			internal DescriptorSet TextureSet;
			internal Rect2D Viewport;
			internal fixed ulong Buffers[DefaultLocations];
			internal fixed ulong Offsets[DefaultLocations];
			internal fixed ulong Strides[DefaultLocations];
			internal ulong IndexBuffer, IndexOffset;
			internal IndexType IndexType;
			internal uint First, Count;
			internal int Cull, Topology, DepthTest, DepthWrite, DepthCompare;
			internal Vector4 ClearColor;
		}

		private DrawRecord[] records = new DrawRecord[4096];
		private int recordCount;

		/// <summary>The record PrepareDraw fills in; a draw adds it to the frame (AddDraw).</summary>
		private DrawRecord pending;

		private Rect2D currentViewport;

		// The colour the frame's render pass clears to (the frame's first clear, before any draw).
		private Vector4 passClear;

		// Secondary command buffers: per frame slot, per chunk, each from its own pool (pools are not shared between threads).
		private readonly CommandPool[,] chunkPools = new CommandPool[VulkanContext.Slots, MaxChunks];
		private readonly CommandBuffer[,] chunkBuffers = new CommandBuffer[VulkanContext.Slots, MaxChunks];
		private readonly bool[,] chunkPoolUsed = new bool[VulkanContext.Slots, MaxChunks];

		/// <summary>Chunks the last frame was recorded in, for the log.</summary>
		public int LastFrameChunks { get; private set; }

		/// <summary>Time the render thread waited for the chunks to be recorded, last frame, for the log.</summary>
		public double LastFrameReplayMilliseconds { get; private set; }

		private delegate* unmanaged<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void> cmdPushConstants;
		private delegate* unmanaged<CommandBuffer, uint, uint, Viewport*, void> cmdSetViewport;
		private delegate* unmanaged<CommandBuffer, uint, uint, Rect2D*, void> cmdSetScissor;
		private delegate* unmanaged<CommandBuffer, uint, ClearAttachment*, uint, ClearRect*, void> cmdClearAttachments;
		private delegate* unmanaged<CommandBuffer, CommandBufferBeginInfo*, Result> beginCommandBuffer;
		private delegate* unmanaged<CommandBuffer, Result> endCommandBuffer;

		private void LoadRecordCommands()
		{
			cmdPushConstants = (delegate* unmanaged<CommandBuffer, PipelineLayout, ShaderStageFlags, uint, uint, void*, void>)DeviceProc("vkCmdPushConstants");
			cmdSetViewport = (delegate* unmanaged<CommandBuffer, uint, uint, Viewport*, void>)DeviceProc("vkCmdSetViewport");
			cmdSetScissor = (delegate* unmanaged<CommandBuffer, uint, uint, Rect2D*, void>)DeviceProc("vkCmdSetScissor");
			cmdClearAttachments = (delegate* unmanaged<CommandBuffer, uint, ClearAttachment*, uint, ClearRect*, void>)DeviceProc("vkCmdClearAttachments");
			beginCommandBuffer = (delegate* unmanaged<CommandBuffer, CommandBufferBeginInfo*, Result>)DeviceProc("vkBeginCommandBuffer");
			endCommandBuffer = (delegate* unmanaged<CommandBuffer, Result>)DeviceProc("vkEndCommandBuffer");
		}

		/// <summary>Ensures a frame is being made (the swapchain image taken). False when there is no window to draw on.</summary>
		private bool EnsureFrame()
		{
			if (context.InFrame)
			{
				return true;
			}

			if (!context.BeginFrame())
			{
				return false;
			}

			FrameBegun();
			return true;
		}

		/// <summary>A clear: the render pass's own when nothing has been drawn yet, a record otherwise.</summary>
		private void RecordClear(int mask)
		{
			Vector4 colour = new Vector4(clearRed, clearGreen, clearBlue, clearAlpha);
			if (recordCount == 0)
			{
				if ((mask & GL_COLOR_BUFFER_BIT) != 0)
				{
					passClear = colour;
				}

				// The depth is cleared to 1 by the render pass anyway.
				return;
			}

			ref DrawRecord record = ref NextRecord();
			record = default;
			record.Kind = RecordKind.Clear;
			record.ClearMask = (byte)(((mask & GL_COLOR_BUFFER_BIT) != 0 ? 1 : 0) | ((mask & GL_DEPTH_BUFFER_BIT) != 0 ? 2 : 0));
			record.ClearColor = colour;
		}

		private ref DrawRecord NextRecord()
		{
			if (recordCount == records.Length)
			{
				Array.Resize(ref records, records.Length * 2);
			}

			return ref records[recordCount++];
		}

		/// <summary>Adds the pending record as a draw.</summary>
		private void AddDraw(uint count, uint first, GLBuffer elements, IndexType indexType, bool quads)
		{
			ref DrawRecord record = ref NextRecord();
			record = pending;
			record.First = first;
			record.Count = count;
			record.Quads = quads;
			if (elements != null)
			{
				elements.LastUse = context.RecordingSerial;
				record.Kind = RecordKind.DrawIndexed;
				record.IndexBuffer = elements.Memory.Buffer.Handle;
				record.IndexOffset = elements.Memory.Offset;
				record.IndexType = indexType;
			}
			else
			{
				record.Kind = RecordKind.Draw;
			}

			frameDraws += quads ? (int)(count / 4) : 1;
		}

		/// <summary>
		/// At the swap: begins the render pass, records the frame's draws on the worker threads into
		/// secondary command buffers, and runs them from the frame's command buffer.
		/// </summary>
		private void RecordFrame()
		{
			int slot = context.FrameSlot;
			context.BeginRenderPass(passClear.X, passClear.Y, passClear.Z, passClear.W, 1.0f, true);
			if (recordCount == 0)
			{
				LastFrameChunks = 0;
				return;
			}

			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			int chunks = Math.Max(1, Math.Min(Math.Min(MaxChunks, ParallelThreads * 2), recordCount / RecordsPerChunk));
			int perChunk = (recordCount + chunks - 1) / chunks;
			for (int chunk = 0; chunk < chunks; chunk++)
			{
				EnsureChunkBuffer(slot, chunk);
			}

			Vector4 clip = context.ClipTransform;
			Action<int> record = chunk => RecordChunk(slot, chunk, chunk * perChunk, Math.Min(recordCount, (chunk + 1) * perChunk), clip);
			if (chunks == 1 || ParallelRun == null)
			{
				for (int chunk = 0; chunk < chunks; chunk++)
				{
					record(chunk);
				}
			}
			else
			{
				ParallelRun(chunks, record);
			}

			CommandBuffer* buffers = stackalloc CommandBuffer[chunks];
			for (int chunk = 0; chunk < chunks; chunk++)
			{
				buffers[chunk] = chunkBuffers[slot, chunk];
			}

			vk.CmdExecuteCommands(context.DrawCommands, (uint)chunks, buffers);
			LastFrameChunks = chunks;
			LastFrameReplayMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		}

		/// <summary>The chunk's secondary command buffer for this slot, made the first time.</summary>
		private void EnsureChunkBuffer(int slot, int chunk)
		{
			if (chunkPools[slot, chunk].Handle == 0)
			{
				CommandPoolCreateInfo poolInfo = new CommandPoolCreateInfo
				{
					SType = StructureType.CommandPoolCreateInfo,
					QueueFamilyIndex = context.QueueFamily,
					Flags = CommandPoolCreateFlags.TransientBit
				};
				Check(vk.CreateCommandPool(device, in poolInfo, null, out chunkPools[slot, chunk]), "vkCreateCommandPool");
				CommandBufferAllocateInfo allocate = new CommandBufferAllocateInfo
				{
					SType = StructureType.CommandBufferAllocateInfo,
					CommandPool = chunkPools[slot, chunk],
					Level = CommandBufferLevel.Secondary,
					CommandBufferCount = 1
				};
				Check(vk.AllocateCommandBuffers(device, in allocate, out chunkBuffers[slot, chunk]), "vkAllocateCommandBuffers");
			}

			chunkPoolUsed[slot, chunk] = true;
		}

		/// <summary>A frame slot is being reused (its last submission is done): its chunks' pools start over.</summary>
		private void ResetChunkPools(int slot)
		{
			for (int chunk = 0; chunk < MaxChunks; chunk++)
			{
				if (chunkPoolUsed[slot, chunk])
				{
					vk.ResetCommandPool(device, chunkPools[slot, chunk], 0);
					chunkPoolUsed[slot, chunk] = false;
				}
			}
		}

		/// <summary>Records records [start, end) into a chunk's secondary command buffer (on a worker thread).</summary>
		private void RecordChunk(int slot, int chunk, int start, int end, Vector4 clip)
		{
			CommandBuffer commands = chunkBuffers[slot, chunk];
			Format colorFormat = context.ColorFormat;
			CommandBufferInheritanceRenderingInfo rendering = new CommandBufferInheritanceRenderingInfo
			{
				SType = StructureType.CommandBufferInheritanceRenderingInfo,
				ColorAttachmentCount = 1,
				PColorAttachmentFormats = &colorFormat,
				DepthAttachmentFormat = context.DepthFormat,
				RasterizationSamples = SampleCountFlags.Count1Bit
			};
			CommandBufferInheritanceInfo inheritance = new CommandBufferInheritanceInfo
			{
				SType = StructureType.CommandBufferInheritanceInfo,
				PNext = context.Vulkan13 ? &rendering : null,
				RenderPass = context.Vulkan13 ? default : context.RenderPass,
				Subpass = 0,
				Framebuffer = context.Vulkan13 ? default : context.CurrentFramebuffer
			};
			CommandBufferBeginInfo begin = new CommandBufferBeginInfo
			{
				SType = StructureType.CommandBufferBeginInfo,
				Flags = CommandBufferUsageFlags.OneTimeSubmitBit | CommandBufferUsageFlags.RenderPassContinueBit,
				PInheritanceInfo = &inheritance
			};
			Check(beginCommandBuffer(commands, &begin), "vkBeginCommandBuffer");

			// A secondary command buffer inherits no state: everything is set again.
			cmdPushConstants(commands, pipelineLayout, ShaderStageFlags.VertexBit, 0, 16, &clip);
			Rect2D scissor = new Rect2D(new Offset2D(0, 0), context.ImageExtent);
			cmdSetScissor(commands, 0, 1, &scissor);
			ClearRect clearRect = new ClearRect(scissor, 0, 1);

			Pipeline boundPipeline = default;
			DescriptorSet boundUniform = default, boundTexture = default;
			uint bound0 = uint.MaxValue, bound1 = uint.MaxValue;
			Rect2D boundViewport = default;
			bool viewportSet = false;
			ulong* vertexBuffers = stackalloc ulong[DefaultLocations];
			ulong* vertexOffsets = stackalloc ulong[DefaultLocations];
			ulong* vertexStrides = stackalloc ulong[DefaultLocations];
			for (int i = 0; i < DefaultLocations; i++)
			{
				vertexBuffers[i] = vertexOffsets[i] = vertexStrides[i] = ulong.MaxValue;
			}

			ulong boundIndex = 0, boundIndexAt = ulong.MaxValue;
			IndexType boundIndexKind = IndexType.Uint16;
			int cull = -1, topology = -1, test = -1, write = -1, compare = -1;
			bool dynamic = Dynamic13;
			ClearAttachment* attachments = stackalloc ClearAttachment[2];
			uint* setOffsets = stackalloc uint[2];

			for (int i = start; i < end; i++)
			{
				ref DrawRecord record = ref records[i];
				if (record.Kind == RecordKind.Clear)
				{
					uint count = 0;
					if ((record.ClearMask & 1) != 0)
					{
						attachments[count++] = new ClearAttachment(ImageAspectFlags.ColorBit, 0, new ClearValue(new ClearColorValue(record.ClearColor.X, record.ClearColor.Y, record.ClearColor.Z, record.ClearColor.W)));
					}

					if ((record.ClearMask & 2) != 0)
					{
						attachments[count++] = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(default, new ClearDepthStencilValue(1.0f, 0)));
					}

					cmdClearAttachments(commands, count, attachments, 1, &clearRect);
					continue;
				}

				if (record.Pipeline.Handle != boundPipeline.Handle)
				{
					cmdBindPipeline(commands, PipelineBindPoint.Graphics, record.Pipeline);
					boundPipeline = record.Pipeline;
				}

				if (dynamic)
				{
					if (record.Cull != cull)
					{
						cmdSetCullMode(commands, (CullModeFlags)record.Cull);
						cull = record.Cull;
					}

					if (record.Topology != topology)
					{
						cmdSetPrimitiveTopology(commands, (PrimitiveTopology)record.Topology);
						topology = record.Topology;
					}

					if (record.DepthTest != test)
					{
						cmdSetDepthTestEnable(commands, (uint)record.DepthTest);
						test = record.DepthTest;
					}

					if (record.DepthWrite != write)
					{
						cmdSetDepthWriteEnable(commands, (uint)record.DepthWrite);
						write = record.DepthWrite;
					}

					if (record.DepthCompare != compare)
					{
						cmdSetDepthCompareOp(commands, (CompareOp)record.DepthCompare);
						compare = record.DepthCompare;
					}
				}

				if (!viewportSet || record.Viewport.Offset.X != boundViewport.Offset.X || record.Viewport.Offset.Y != boundViewport.Offset.Y ||
				    record.Viewport.Extent.Width != boundViewport.Extent.Width || record.Viewport.Extent.Height != boundViewport.Extent.Height)
				{
					Viewport viewport = new Viewport(record.Viewport.Offset.X, record.Viewport.Offset.Y, record.Viewport.Extent.Width, record.Viewport.Extent.Height, 0.0f, 1.0f);
					cmdSetViewport(commands, 0, 1, &viewport);
					boundViewport = record.Viewport;
					viewportSet = true;
				}

				if (record.UniformSet.Handle != boundUniform.Handle || record.Offset0 != bound0 || record.Offset1 != bound1)
				{
					DescriptorSet set = record.UniformSet;
					setOffsets[0] = record.Offset0;
					setOffsets[1] = record.Offset1;
					cmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, &set, 2, setOffsets);
					boundUniform = set;
					bound0 = record.Offset0;
					bound1 = record.Offset1;
				}

				if (record.TextureSet.Handle != boundTexture.Handle)
				{
					DescriptorSet set = record.TextureSet;
					cmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, pipelineLayout, 1, 1, &set, 0, null);
					boundTexture = set;
				}

				if (record.VertexBufferCount != 0)
				{
					bool changed = false;
					for (int b = 0; b < DefaultLocations; b++)
					{
						changed |= record.Buffers[b] != vertexBuffers[b] || record.Offsets[b] != vertexOffsets[b] || (dynamic && record.Strides[b] != vertexStrides[b]);
					}

					if (changed)
					{
						fixed (ulong* buffers = record.Buffers, offsets = record.Offsets, strides = record.Strides)
						{
							if (dynamic)
							{
								cmdBindVertexBuffers2(commands, 0, DefaultLocations, (Buffer*)buffers, offsets, null, strides);
							}
							else
							{
								cmdBindVertexBuffers(commands, 0, DefaultLocations, (Buffer*)buffers, offsets);
							}
						}

						for (int b = 0; b < DefaultLocations; b++)
						{
							vertexBuffers[b] = record.Buffers[b];
							vertexOffsets[b] = record.Offsets[b];
							vertexStrides[b] = record.Strides[b];
						}
					}
				}

				if (record.Kind == RecordKind.DrawIndexed)
				{
					if (record.IndexBuffer != boundIndex || record.IndexOffset != boundIndexAt || record.IndexType != boundIndexKind)
					{
						cmdBindIndexBuffer(commands, new Buffer(record.IndexBuffer), record.IndexOffset, record.IndexType);
						boundIndex = record.IndexBuffer;
						boundIndexAt = record.IndexOffset;
						boundIndexKind = record.IndexType;
					}

					if (record.Quads)
					{
						// One fan per quad, as GL.DrawElements does on GLES.
						for (uint quad = 0; quad + 4 <= record.Count; quad += 4)
						{
							cmdDrawIndexed(commands, 4, 1, record.First + quad, 0, 0);
						}
					}
					else
					{
						cmdDrawIndexed(commands, record.Count, 1, record.First, 0, 0);
					}
				}
				else
				{
					cmdDraw(commands, record.Count, 1, record.First, 0);
				}
			}

			Check(endCommandBuffer(commands), "vkEndCommandBuffer");
		}

		private void DisposeRecords()
		{
			for (int slot = 0; slot < VulkanContext.Slots; slot++)
			{
				for (int chunk = 0; chunk < MaxChunks; chunk++)
				{
					if (chunkPools[slot, chunk].Handle != 0)
					{
						vk.DestroyCommandPool(device, chunkPools[slot, chunk], null);
					}
				}
			}
		}
	}
}
