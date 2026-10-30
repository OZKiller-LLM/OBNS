// OBNS: not an upstream file. Ranges of GPU memory for the world layer (OBNS/VULKAN.md, OBNS/DIRECT3D.md), whichever
// API draws it: blocks suballocated for meshes and textures (a route has thousands; devices allow a few thousand
// allocations in all).

using System.Collections.Generic;

namespace OpenBve.Graphics
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
}
