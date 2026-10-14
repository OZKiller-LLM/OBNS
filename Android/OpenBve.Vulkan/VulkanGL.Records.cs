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
using System.Threading;
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
	 *
	 * Pipelining: the recording and the submission run on a thread of their own (the submitter),
	 * with workers of their own for the chunks. At the swap the render thread hands the frame over
	 * and goes on with the next frame's simulation and animated objects (5-15 ms on TML) while the
	 * last one is recorded, submitted and presented; it waits for that to finish only when it begins
	 * drawing the next frame. So the device's queue, the swapchain and the records are never used by
	 * both threads at once, and what a frame used stays untouched until its submission completes
	 * (the serials of VulkanContext.Defer, copy on write for buffers).
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int MaxChunks = 8;
		private const int RecordsPerChunk = 400;

		/// <summary>Whether frames are recorded and submitted on their own thread while the next is prepared (`--ez vulkan_pipelined false`: at the swap, on the render thread).</summary>
		public static bool Pipelined = true;

		/// <summary>Threads that record chunks besides the one submitting (0: it records them all). Set before the device is made.</summary>
		public static int RecordThreads = 2;

		private RecordWorkers workers;
		private Thread submitter;
		private readonly object submitGate = new object();
		private Action submitJob;
		private bool submitBusy;
		private bool stopping;
		private Exception submitFailure;
		private long submitWaitTicks;

		/// <summary>Time the render thread waited for the previous frame's submission, last frame, for the log.</summary>
		public double LastFrameSubmitWaitMilliseconds { get; private set; }

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

			// The last frame's submission must be done before the device is used for the next.
			WaitSubmitter();
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
		/// At the swap: hands the frame over (VulkanContext.DetachFrame) and has it recorded - its
		/// draws in chunks into secondary command buffers, on the workers - submitted and presented,
		/// on the submitter thread (or here when not pipelined).
		/// </summary>
		private void RecordFrame()
		{
			VulkanContext.FrameHandoff handoff = context.DetachFrame();
			int slot = handoff.FrameSlot;
			int count = recordCount;
			Vector4 clear = passClear;
			Vector4 clip = context.ClipTransform;
			int threads = workers == null ? 1 : RecordThreads + 1;
			int chunks = count == 0 ? 0 : Math.Max(1, Math.Min(Math.Min(MaxChunks, threads * 2), count / RecordsPerChunk));
			int perChunk = chunks == 0 ? 0 : (count + chunks - 1) / chunks;
			for (int chunk = 0; chunk < chunks; chunk++)
			{
				EnsureChunkBuffer(slot, chunk);
			}

			LastFrameSubmitWaitMilliseconds = submitWaitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			submitWaitTicks = 0;
			Action job = () =>
			{
				long started = System.Diagnostics.Stopwatch.GetTimestamp();
				context.SubmitFrame(handoff, clear, commands =>
				{
					if (chunks == 0)
					{
						return;
					}

					Action<int> record = chunk => RecordChunk(slot, chunk, chunk * perChunk, Math.Min(count, (chunk + 1) * perChunk), clip, handoff.Framebuffer);
					if (chunks == 1 || workers == null)
					{
						for (int chunk = 0; chunk < chunks; chunk++)
						{
							record(chunk);
						}
					}
					else
					{
						workers.Run(chunks, record);
					}

					CommandBuffer* buffers = stackalloc CommandBuffer[chunks];
					for (int chunk = 0; chunk < chunks; chunk++)
					{
						buffers[chunk] = chunkBuffers[slot, chunk];
					}

					vk.CmdExecuteCommands(commands, (uint)chunks, buffers);
				});
				LastFrameChunks = chunks;
				LastFrameReplayMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			};

			if (Pipelined)
			{
				StartSubmitter();
				lock (submitGate)
				{
					submitJob = job;
					submitBusy = true;
					Monitor.PulseAll(submitGate);
				}
			}
			else
			{
				job();
			}
		}

		private void StartSubmitter()
		{
			if (submitter != null)
			{
				return;
			}

			submitter = new Thread(SubmitLoop) { IsBackground = true, Name = "OpenBVE Vulkan submit" };
			submitter.Start();
		}

		private void SubmitLoop()
		{
			while (true)
			{
				Action job;
				lock (submitGate)
				{
					while (submitJob == null && !stopping)
					{
						Monitor.Wait(submitGate);
					}

					if (submitJob == null)
					{
						return;
					}

					job = submitJob;
				}

				try
				{
					job();
				}
				catch (Exception e)
				{
					submitFailure = e;
				}

				lock (submitGate)
				{
					submitJob = null;
					submitBusy = false;
					Monitor.PulseAll(submitGate);
				}
			}
		}

		/// <summary>
		/// Waits until the last frame handed over has been submitted, rethrowing what went wrong if it
		/// failed. Before anything else uses the device's queue or swapchain.
		/// </summary>
		public void WaitSubmitter()
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			lock (submitGate)
			{
				while (submitBusy)
				{
					Monitor.Wait(submitGate);
				}
			}

			submitWaitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			Exception failure = submitFailure;
			if (failure != null)
			{
				submitFailure = null;
				throw new InvalidOperationException("recording or submitting a Vulkan frame failed: " + failure.Message, failure);
			}
		}

		/// <summary>Ends the submitter thread (the device is going).</summary>
		private void StopSubmitter()
		{
			lock (submitGate)
			{
				stopping = true;
				Monitor.PulseAll(submitGate);
			}

			submitter?.Join(2000);
			submitter = null;
		}

		/*
		 * The chunks' workers: threads of their own, not the frame workers the render thread uses for
		 * animated objects at the same time. Each Run hands out chunk numbers to the workers and the
		 * calling thread, and returns when all are recorded.
		 */
		private sealed class RecordWorkers
		{
			// One object per Run: a worker still finishing the last one only ever sees that one, done.
			private sealed class Job
			{
				internal Action<int> Body;
				internal int Count;
				internal int Next;
				internal int Pending;
				internal Exception Failure;

				internal void Drain()
				{
					while (true)
					{
						int index = Interlocked.Increment(ref Next) - 1;
						if (index >= Count)
						{
							return;
						}

						try
						{
							Body(index);
						}
						catch (Exception e)
						{
							Interlocked.CompareExchange(ref Failure, e, null);
						}
						finally
						{
							Interlocked.Decrement(ref Pending);
						}
					}
				}
			}

			private readonly object gate = new object();
			private int generation;
			private Job current;

			internal RecordWorkers(int threads)
			{
				for (int i = 0; i < threads; i++)
				{
					new Thread(Work) { IsBackground = true, Name = "OpenBVE Vulkan record " + (i + 1) }.Start();
				}
			}

			internal void Run(int total, Action<int> work)
			{
				Job job = new Job { Body = work, Count = total, Pending = total };
				lock (gate)
				{
					current = job;
					generation++;
					Monitor.PulseAll(gate);
				}

				job.Drain();
				SpinWait spin = new SpinWait();
				while (Volatile.Read(ref job.Pending) > 0)
				{
					spin.SpinOnce(-1);
				}

				if (job.Failure != null)
				{
					throw new AggregateException(job.Failure);
				}
			}

			private void Work()
			{
				int seen = 0;
				while (true)
				{
					Job job;
					lock (gate)
					{
						while (generation == seen)
						{
							Monitor.Wait(gate);
						}

						seen = generation;
						job = current;
					}

					job.Drain();
				}
			}
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
		private void RecordChunk(int slot, int chunk, int start, int end, Vector4 clip, Framebuffer framebuffer)
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
				Framebuffer = framebuffer
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
			StopSubmitter();
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
