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

// OBNS: not an upstream file. The Direct3D 12 world layer's uploads (OBNS/DIRECT3D.md): bytes written into mapped upload
// memory, and the copies into the buffers and textures recorded in a list of their own, sent on the queue before the
// frame's (so the frame sees them). The upload memory is in blocks, each used again once the GPU has done its copies.

using System;
using System.Collections.Generic;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12Uploads : IDisposable
	{
		private sealed class Block
		{
			internal IntPtr Resource;
			internal byte* Data;
			internal ulong Size, Used;
			/// <summary>The uploads fence's value once the copies from it are done (0: none sent).</summary>
			internal ulong Retire;
		}

		private const ulong BlockSize = 64UL << 20;

		/// <summary>Upload memory kept when idle; more is let go of once done with.</summary>
		private const int KeptBlocks = 2;

		/// <summary>Upload memory after which the CPU waits for the GPU's copies rather than making more (loading a route).</summary>
		private const ulong MostBytes = 512UL << 20;

		private readonly D3D12Device device;
		private readonly IntPtr fence;
		private ulong fenceValue;
		private readonly List<Block> blocks = new List<Block>();
		private Block current;
		/// <summary>The blocks written since the last submission (they retire with it).</summary>
		private readonly List<Block> written = new List<Block>();

		private readonly List<KeyValuePair<IntPtr, ulong>> allocators = new List<KeyValuePair<IntPtr, ulong>>();
		private IntPtr list, allocator;
		private bool recording;

		/// <summary>Bytes uploaded since the statistics were last taken.</summary>
		internal long Bytes;

		internal D3D12Uploads(D3D12Device device)
		{
			this.device = device;
			fence = D12.CreateFence(device.Device, false);
		}

		/// <summary>The list the copies are recorded into, open.</summary>
		internal IntPtr List
		{
			get
			{
				if (!recording)
				{
					Open();
				}

				return list;
			}
		}

		private void Open()
		{
			ulong done = D12.CompletedValue(fence);
			allocator = IntPtr.Zero;
			for (int i = 0; i < allocators.Count; i++)
			{
				if (allocators[i].Value <= done)
				{
					allocator = allocators[i].Key;
					allocators.RemoveAt(i);
					D12.ResetAllocator(allocator);
					break;
				}
			}

			if (allocator == IntPtr.Zero)
			{
				allocator = D12.CreateCommandAllocator(device.Device);
			}

			if (list == IntPtr.Zero)
			{
				list = D12.CreateCommandList(device.Device, allocator);
			}
			else
			{
				D12.Reset(list, allocator);
			}

			recording = true;
		}

		private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

		/// <summary>Room for bytes in upload memory, at an alignment: where to write them, and the buffer and offset the copy takes them from.</summary>
		internal byte* Reserve(ulong bytes, ulong alignment, out IntPtr resource, out ulong offset)
		{
			if (!recording)
			{
				Open();
			}

			if (current == null || Align(current.Used, alignment) + bytes > current.Size)
			{
				NextBlock(bytes);
			}

			offset = Align(current.Used, alignment);
			current.Used = offset + bytes;
			if (!written.Contains(current))
			{
				written.Add(current);
			}

			resource = current.Resource;
			Bytes += (long)bytes;
			return current.Data + offset;
		}

		/// <summary>A block with room: one the GPU is done with, else a new one (or, with too many, the oldest once done).</summary>
		private void NextBlock(ulong bytes)
		{
			if (written.Count > 0)
			{
				// What is recorded is sent: the full block retires with it.
				Submit();
				Open();
			}

			while (true)
			{
				ulong done = D12.CompletedValue(fence);
				ulong total = 0;
				Block oldest = null;
				foreach (Block block in blocks)
				{
					total += block.Size;
					if (block.Retire <= done && block.Size >= bytes && !written.Contains(block))
					{
						block.Used = 0;
						current = block;
						return;
					}

					if (block.Size >= bytes && (oldest == null || block.Retire < oldest.Retire))
					{
						oldest = block;
					}
				}

				if (total + Math.Max(BlockSize, bytes) > MostBytes && oldest != null)
				{
					device.WaitFor(fence, oldest.Retire);
					continue;
				}

				ulong size = Math.Max(BlockSize, Align(bytes, 1UL << 20));
				D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionBuffer, Width = size, Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = D12.LayoutRowMajor };
				Block made = new Block { Resource = D12.CreateCommittedResource(device.Device, D12.HeapUpload, D12.HeapFlagNone, desc, D12.StateGenericRead), Size = size };
				made.Data = D12.Map(made.Resource);
				blocks.Add(made);
				current = made;
				return;
			}
		}

		/// <summary>Sends what has been recorded (before the frame's list, on the same queue).</summary>
		internal void Submit()
		{
			if (!recording)
			{
				return;
			}

			D12.Close(list);
			D12.ExecuteCommandList(device.Queue, list);
			fenceValue++;
			D12.Signal(device.Queue, fence, fenceValue);
			foreach (Block block in written)
			{
				block.Retire = fenceValue;
			}

			written.Clear();
			allocators.Add(new KeyValuePair<IntPtr, ulong>(allocator, fenceValue));
			allocator = IntPtr.Zero;
			recording = false;
		}

		/// <summary>Once a frame: upload memory beyond what is kept, and done with, let go of.</summary>
		internal void Trim()
		{
			ulong done = D12.CompletedValue(fence);
			for (int i = blocks.Count - 1; i >= 0 && blocks.Count > KeptBlocks; i--)
			{
				Block block = blocks[i];
				if (block != current && block.Retire <= done && !written.Contains(block))
				{
					D12.Release(block.Resource);
					blocks.RemoveAt(i);
				}
			}
		}

		internal string Describe()
		{
			ulong total = 0;
			foreach (Block block in blocks)
			{
				total += block.Size;
			}

			return "upload memory " + (total >> 20) + " MB";
		}

		public void Dispose()
		{
			if (recording)
			{
				D12.Close(list);
				allocators.Add(new KeyValuePair<IntPtr, ulong>(allocator, 0));
				recording = false;
			}

			try
			{
				device.WaitFor(fence, fenceValue);
			}
			catch (Exception)
			{
				// A removed device: nothing is running.
			}

			foreach (Block block in blocks)
			{
				D12.Release(block.Resource);
			}

			blocks.Clear();
			foreach (KeyValuePair<IntPtr, ulong> pair in allocators)
			{
				D12.Release(pair.Key);
			}

			allocators.Clear();
			D12.Release(list);
			D12.Release(fence);
			list = IntPtr.Zero;
		}
	}
}
