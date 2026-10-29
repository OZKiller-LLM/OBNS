// OBNS: not an upstream file. The Vulkan world layer's textures for devices without descriptor indexing (OBNS/VULKAN.md),
// such as Intel's Broadwell on Mesa's hasvk: pages of a fixed number of textures, each page a descriptor set of its own
// per frame in flight, which the draws using its textures bind (set 1). A set cannot be written while a frame using it
// may still run there, so writes wait in a queue and each frame's sets take them once the frame's slot is free.

using System;
using System.Collections.Generic;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanTexturePages : IDisposable
	{
		private const int PagesPerPool = 16;

		private readonly VulkanDevice device;
		private readonly int framesInFlight;

		/// <summary>The textures in a page: texture i is element i % PageSize of page i / PageSize.</summary>
		internal readonly int PageSize;

		/// <summary>A page's set layout: PageSize sampled images for the fragment shader.</summary>
		internal readonly ulong Layout;

		private readonly List<ulong> pools = new List<ulong>();
		private int poolPages = PagesPerPool;

		/// <summary>Each page's sets, one per frame in flight.</summary>
		private readonly List<ulong[]> sets = new List<ulong[]>();

		/// <summary>Writes (texture, view) not yet taken by every frame's sets, and how far each frame's sets have taken them.</summary>
		private readonly List<KeyValuePair<int, ulong>> writes = new List<KeyValuePair<int, ulong>>();
		private readonly int[] taken;

		/// <summary>The white texture's view: what a page's elements hold until a texture is written there, and after it goes.</summary>
		internal ulong WhiteView;

		internal VulkanTexturePages(VulkanDevice device, int pageSize, int framesInFlight)
		{
			this.device = device;
			this.framesInFlight = framesInFlight;
			PageSize = pageSize;
			taken = new int[framesInFlight];
			Vk.DescriptorSetLayoutBinding binding = new Vk.DescriptorSetLayoutBinding { Binding = 0, DescriptorType = Vk.DescriptorTypeSampledImage, DescriptorCount = (uint)pageSize, StageFlags = Vk.ShaderStageFragment };
			Vk.DescriptorSetLayoutCreateInfo layoutInfo = new Vk.DescriptorSetLayoutCreateInfo
			{
				SType = Vk.StructureTypeDescriptorSetLayoutCreateInfo,
				BindingCount = 1,
				PBindings = &binding
			};
			Vk.Check(Vk.vkCreateDescriptorSetLayout(device.Device, &layoutInfo, null, out Layout), "vkCreateDescriptorSetLayout (texture pages)");
		}

		/// <summary>Points a texture's element at a view, for every frame's sets (main thread; taken by Take).</summary>
		internal void Write(int index, ulong view)
		{
			lock (writes)
			{
				writes.Add(new KeyValuePair<int, ulong>(index, view));
			}
		}

		/// <summary>A frame slot's sets take the writes waiting (once the slot's last frame is done, before recording it).</summary>
		internal void Take(int slot)
		{
			lock (writes)
			{
				for (int i = taken[slot]; i < writes.Count; i++)
				{
					int index = writes[i].Key;
					WriteElement(Page(index / PageSize)[slot], index % PageSize, writes[i].Value);
				}

				taken[slot] = writes.Count;
				bool all = true;
				foreach (int count in taken)
				{
					all &= count == writes.Count;
				}

				if (all)
				{
					writes.Clear();
					Array.Clear(taken, 0, taken.Length);
				}
			}
		}

		/// <summary>A page's set for a frame slot (a page no texture has been written to: the first page).</summary>
		internal ulong Set(int page, int slot) => page < sets.Count && sets[page] != null ? sets[page][slot] : sets[0][slot];

		/// <summary>A page's sets, made (every element white) the first time.</summary>
		private ulong[] Page(int page)
		{
			while (sets.Count <= page)
			{
				sets.Add(null);
			}

			if (sets[page] != null)
			{
				return sets[page];
			}

			if (poolPages == PagesPerPool)
			{
				Vk.DescriptorPoolSize size = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeSampledImage, DescriptorCount = (uint)(PageSize * framesInFlight * PagesPerPool) };
				Vk.DescriptorPoolCreateInfo poolInfo = new Vk.DescriptorPoolCreateInfo
				{
					SType = Vk.StructureTypeDescriptorPoolCreateInfo,
					MaxSets = (uint)(framesInFlight * PagesPerPool),
					PoolSizeCount = 1,
					PPoolSizes = &size
				};
				Vk.Check(Vk.vkCreateDescriptorPool(device.Device, &poolInfo, null, out ulong pool), "vkCreateDescriptorPool (texture pages)");
				pools.Add(pool);
				poolPages = 0;
			}

			ulong[] pageSets = new ulong[framesInFlight];
			ulong* layouts = stackalloc ulong[framesInFlight];
			for (int i = 0; i < framesInFlight; i++)
			{
				layouts[i] = Layout;
			}

			Vk.DescriptorSetAllocateInfo allocateInfo = new Vk.DescriptorSetAllocateInfo
			{
				SType = Vk.StructureTypeDescriptorSetAllocateInfo,
				DescriptorPool = pools[pools.Count - 1],
				DescriptorSetCount = (uint)framesInFlight,
				PSetLayouts = layouts
			};
			fixed (ulong* allocated = pageSets)
			{
				Vk.Check(Vk.vkAllocateDescriptorSets(device.Device, &allocateInfo, allocated), "vkAllocateDescriptorSets (texture pages)");
			}

			poolPages++;

			// Every element white: a bound set's elements must all be valid without descriptor indexing.
			Vk.DescriptorImageInfo* images = stackalloc Vk.DescriptorImageInfo[PageSize];
			for (int i = 0; i < PageSize; i++)
			{
				images[i] = new Vk.DescriptorImageInfo { ImageView = WhiteView, ImageLayout = Vk.ImageLayoutShaderReadOnlyOptimal };
			}

			foreach (ulong set in pageSets)
			{
				Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = set,
					DescriptorCount = (uint)PageSize,
					DescriptorType = Vk.DescriptorTypeSampledImage,
					PImageInfo = images
				};
				Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
			}

			sets[page] = pageSets;
			return pageSets;
		}

		private void WriteElement(ulong set, int element, ulong view)
		{
			Vk.DescriptorImageInfo image = new Vk.DescriptorImageInfo { ImageView = view, ImageLayout = Vk.ImageLayoutShaderReadOnlyOptimal };
			Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
			{
				SType = Vk.StructureTypeWriteDescriptorSet,
				DstSet = set,
				DstArrayElement = (uint)element,
				DescriptorCount = 1,
				DescriptorType = Vk.DescriptorTypeSampledImage,
				PImageInfo = &image
			};
			Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
		}

		public void Dispose()
		{
			foreach (ulong pool in pools)
			{
				Vk.vkDestroyDescriptorPool(device.Device, pool, null);
			}

			pools.Clear();
			sets.Clear();
			if (Layout != 0)
			{
				Vk.vkDestroyDescriptorSetLayout(device.Device, Layout, null);
			}
		}
	}
}
