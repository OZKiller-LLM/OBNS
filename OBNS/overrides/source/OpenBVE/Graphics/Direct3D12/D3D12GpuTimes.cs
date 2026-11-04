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

// OBNS: not an upstream file. The GPU's time for each part of a frame Direct3D 12 draws alone, for the frame statistics
// (OBNS_FRAMESTATS=1): the CPU's figures show only that it waits for the GPU, not on what (ARM64/README.md).

using System;
using System.Globalization;

namespace OpenBve.Graphics.Direct3D12
{
	/// <summary>
	/// Timestamps written by the GPU between the parts of a frame (shadows, the world, the cab, the 2D, the copy to the screen),
	/// read back once the GPU has finished that frame's slot, and averaged for the frame statistics.
	/// </summary>
	internal sealed unsafe class D3D12GpuTimes : IDisposable
	{
		/// <summary>The marks of a frame: its start, then the end of each part.</summary>
		internal const int Start = 0, Shadows = 1, World = 2, Cab = 3, TwoD = 4, Screen = 5;

		private const int Marks = 6;

		private static readonly string[] names = { "shadows", "world", "cab", "2D", "to the screen" };

		private IntPtr heap, readback;
		private readonly double milliseconds;
		private readonly bool[] pending;
		private readonly double[] sums = new double[Marks - 1];
		private int frames;

		internal D3D12GpuTimes(D3D12Device device, int slots)
		{
			pending = new bool[slots];
			heap = D12.CreateTimestampHeap(device.Device, (uint)(Marks * slots));
			D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionBuffer, Width = (ulong)(Marks * slots * 8), Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = D12.LayoutRowMajor };
			readback = D12.CreateCommittedResource(device.Device, D12.HeapReadback, D12.HeapFlagNone, desc, D12.StateCopyDest);
			milliseconds = 1000.0 / D12.TimestampFrequency(device.Queue);
		}

		/// <summary>
		/// The start of a frame in a slot whose last frame the GPU has finished (its times are taken first). Only while the frame
		/// statistics are being kept.
		/// </summary>
		internal void Begin(IntPtr list, int slot)
		{
			if (pending[slot])
			{
				pending[slot] = false;
				ulong* times = (ulong*)D12.MapForReading(readback) + slot * Marks;
				try
				{
					bool ordered = true;
					for (int i = 1; i < Marks; i++)
					{
						ordered &= times[i] >= times[i - 1];
					}

					// A frame from a part not drawn, or a clock that jumped: left out.
					if (ordered && times[0] != 0)
					{
						for (int i = 1; i < Marks; i++)
						{
							sums[i - 1] += (times[i] - times[i - 1]) * milliseconds;
						}

						frames++;
					}
				}
				finally
				{
					D12.Unmap(readback);
				}
			}

			D12.EndQuery(list, heap, (uint)(slot * Marks + Start));
		}

		/// <summary>The end of a part of the frame.</summary>
		internal void Mark(IntPtr list, int slot, int mark) => D12.EndQuery(list, heap, (uint)(slot * Marks + mark));

		/// <summary>The end of the frame: its times copied where the CPU can read them once the GPU is done.</summary>
		internal void End(IntPtr list, int slot)
		{
			Mark(list, slot, Screen);
			D12.ResolveQueryData(list, heap, (uint)(slot * Marks), Marks, readback, (ulong)(slot * Marks * 8));
			pending[slot] = true;
		}

		/// <summary>The average GPU time of each part since last asked, for the frame statistics; null with no frames timed.</summary>
		internal string Take()
		{
			if (frames == 0)
			{
				return null;
			}

			string text = "GPU";
			double total = 0.0;
			for (int i = 0; i < names.Length; i++)
			{
				double average = sums[i] / frames;
				total += average;
				text += (i == 0 ? " " : ", ") + names[i] + " " + average.ToString("0.00", CultureInfo.InvariantCulture);
				sums[i] = 0.0;
			}

			text += " ms (" + total.ToString("0.00", CultureInfo.InvariantCulture) + " a frame, " + frames + " frames)";
			frames = 0;
			return text;
		}

		public void Dispose()
		{
			D12.Release(readback);
			D12.Release(heap);
			readback = heap = IntPtr.Zero;
		}
	}
}
