// OBNS: not an upstream file. The Vulkan world layer's shadow maps (OBNS/VULKAN.md): upstream's cascaded shadow maps
// (LibRender2's CascadedShadowMap, an OpenGL depth texture per cascade) as one depth image with a layer per cascade,
// drawn by the shadow pass at the start of the frame and sampled by the world shader with depth comparison.

using System;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanShadows : IDisposable
	{
		internal const uint Format = Vk.FormatD32Sfloat;

		private readonly VulkanDevice device;
		internal ulong Image, Memory, ArrayView, Sampler;
		internal ulong[] LayerViews = new ulong[0];

		/// <summary>A cascade's size in texels (the map is square), and how many cascades it has.</summary>
		internal int Resolution { get; private set; }

		internal int Cascades { get; private set; }

		internal VulkanShadows(VulkanDevice device)
		{
			this.device = device;
			// Comparison with linear filtering: a sample is the 2x2 hardware PCF that upstream's sampler2DShadow gives.
			Vk.SamplerCreateInfo info = new Vk.SamplerCreateInfo
			{
				SType = Vk.StructureTypeSamplerCreateInfo,
				MagFilter = Vk.FilterLinear,
				MinFilter = Vk.FilterLinear,
				MipmapMode = Vk.SamplerMipmapModeNearest,
				AddressModeU = Vk.SamplerAddressModeClampToEdge,
				AddressModeV = Vk.SamplerAddressModeClampToEdge,
				AddressModeW = Vk.SamplerAddressModeClampToEdge,
				MaxAnisotropy = 1.0f,
				CompareEnable = 1,
				CompareOp = Vk.CompareOpLessOrEqual
			};
			Vk.Check(Vk.vkCreateSampler(device.Device, &info, null, out Sampler), "vkCreateSampler (shadows)");
			Create(1, 1);
		}

		/// <summary>Whether the maps need making again for these settings (the caller waits for the GPU, then calls Create).</summary>
		internal bool Differs(int resolution, int cascades) => resolution != Resolution || cascades != Cascades;

		/// <summary>The maps for a resolution and cascade count (a 1x1 single layer when shadows are off).</summary>
		internal void Create(int resolution, int cascades)
		{
			DestroyImage();
			Resolution = resolution;
			Cascades = cascades;
			Vk.ImageCreateInfo imageInfo = new Vk.ImageCreateInfo
			{
				SType = Vk.StructureTypeImageCreateInfo,
				ImageType = 1, // 2D
				Format = Format,
				Extent = new Vk.Extent3D { Width = (uint)resolution, Height = (uint)resolution, Depth = 1 },
				MipLevels = 1,
				ArrayLayers = (uint)cascades,
				Samples = 1,
				Tiling = 0,
				Usage = Vk.ImageUsageDepthStencilAttachment | Vk.ImageUsageSampled,
				InitialLayout = Vk.ImageLayoutUndefined
			};
			Vk.Check(Vk.vkCreateImage(device.Device, &imageInfo, null, out Image), "vkCreateImage (shadows)");
			Vk.vkGetImageMemoryRequirements(device.Device, Image, out Vk.MemoryRequirements requirements);
			Vk.MemoryAllocateInfo allocateInfo = new Vk.MemoryAllocateInfo
			{
				SType = Vk.StructureTypeMemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyDeviceLocal)
			};
			Vk.Check(Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out Memory), "vkAllocateMemory (shadows)");
			Vk.Check(Vk.vkBindImageMemory(device.Device, Image, Memory, 0), "vkBindImageMemory (shadows)");

			Vk.ImageViewCreateInfo viewInfo = new Vk.ImageViewCreateInfo
			{
				SType = Vk.StructureTypeImageViewCreateInfo,
				Image = Image,
				ViewType = 5, // 2D array
				Format = Format,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectDepth, LevelCount = 1, LayerCount = (uint)cascades }
			};
			Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out ArrayView), "vkCreateImageView (shadows)");
			LayerViews = new ulong[cascades];
			for (int i = 0; i < cascades; i++)
			{
				viewInfo.ViewType = 1; // 2D
				viewInfo.SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectDepth, LevelCount = 1, BaseArrayLayer = (uint)i, LayerCount = 1 };
				Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out LayerViews[i]), "vkCreateImageView (shadow cascade)");
			}
		}

		/// <summary>
		/// Records the barrier before the shadow pass: the last frame's sampling done, the maps' contents discarded (the
		/// pass clears every layer).
		/// </summary>
		internal void BeginWriting(IntPtr commandBuffer)
		{
			Vk.ImageMemoryBarrier barrier = Barrier(Vk.AccessShaderRead, Vk.AccessDepthStencilAttachmentRead | Vk.AccessDepthStencilAttachmentWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutDepthStencilAttachmentOptimal);
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageFragmentShader, Vk.PipelineStageEarlyFragmentTests | Vk.PipelineStageLateFragmentTests, 0, 0, null, 0, null, 1, &barrier);
		}

		/// <summary>Records the barrier after the shadow pass: the maps written, for the world's fragment shader to read.</summary>
		internal void EndWriting(IntPtr commandBuffer)
		{
			Vk.ImageMemoryBarrier barrier = Barrier(Vk.AccessDepthStencilAttachmentWrite, Vk.AccessShaderRead, Vk.ImageLayoutDepthStencilAttachmentOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageLateFragmentTests, Vk.PipelineStageFragmentShader, 0, 0, null, 0, null, 1, &barrier);
		}

		private Vk.ImageMemoryBarrier Barrier(uint srcAccess, uint dstAccess, uint oldLayout, uint newLayout)
		{
			return new Vk.ImageMemoryBarrier
			{
				SType = Vk.StructureTypeImageMemoryBarrier,
				SrcAccessMask = srcAccess,
				DstAccessMask = dstAccess,
				OldLayout = oldLayout,
				NewLayout = newLayout,
				SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
				DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
				Image = Image,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectDepth, LevelCount = 1, LayerCount = (uint)Cascades }
			};
		}

		private void DestroyImage()
		{
			IntPtr d = device.Device;
			foreach (ulong view in LayerViews)
			{
				Vk.vkDestroyImageView(d, view, null);
			}

			LayerViews = new ulong[0];
			if (ArrayView != 0) Vk.vkDestroyImageView(d, ArrayView, null);
			if (Image != 0) Vk.vkDestroyImage(d, Image, null);
			if (Memory != 0) Vk.vkFreeMemory(d, Memory, null);
			ArrayView = Image = Memory = 0;
		}

		public void Dispose()
		{
			DestroyImage();
			if (Sampler != 0)
			{
				Vk.vkDestroySampler(device.Device, Sampler, null);
				Sampler = 0;
			}
		}
	}
}
