// OBNS: not an upstream file. The Vulkan world layer's textures (OBNS/VULKAN.md): the images of WorldTextures' copies,
// in one bindless array the draws index (or pages of one, VulkanTexturePages).

using System;
using OpenBveApi.Graphics;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanTextures : WorldTextures
	{
		private sealed class VulkanEntry : Entry
		{
			internal ulong Image, View;
			internal GpuRange Memory;
		}

		private readonly VulkanDevice device;
		private readonly Uploader uploader;
		private readonly GpuPool memory;

		private readonly ulong[] descriptorSets;
		private readonly uint textureBinding;

		/// <summary>The pages the textures are written to, where the device has no descriptor indexing (else null).</summary>
		private readonly VulkanTexturePages pages;

		/// <summary>The samplers by OpenGL wrap mode (ClampClamp, ClampRepeat, RepeatClamp, RepeatRepeat).</summary>
		internal readonly ulong[] Samplers = new ulong[4];

		internal VulkanTextures(VulkanDevice device, Uploader uploader, ulong[] descriptorSets, uint textureBinding, VulkanTexturePages pages, InterpolationMode interpolation, int anisotropy) : base("Vulkan", "OBNS_VULKAN")
		{
			this.device = device;
			this.uploader = uploader;
			this.descriptorSets = descriptorSets;
			this.textureBinding = textureBinding;
			this.pages = pages;
			memory = new GpuPool(device, 256UL << 20, 0, "textures");
			CreateSamplers(interpolation, anisotropy);
			MakeWhite();
			if (pages != null)
			{
				pages.WhiteView = ((VulkanEntry)WhiteEntry).View;
			}
		}

		private void CreateSamplers(InterpolationMode interpolation, int anisotropy)
		{
			uint mag = Vk.FilterLinear, min = Vk.FilterLinear, mip = Vk.SamplerMipmapModeLinear;
			float maxLod = 1000.0f;
			switch (interpolation)
			{
				case InterpolationMode.NearestNeighbor:
					mag = min = Vk.FilterNearest;
					mip = Vk.SamplerMipmapModeNearest;
					maxLod = 0.0f;
					break;
				case InterpolationMode.Bilinear:
					maxLod = 0.0f;
					break;
				case InterpolationMode.NearestNeighborMipmapped:
					mag = min = Vk.FilterNearest;
					mip = Vk.SamplerMipmapModeNearest;
					break;
				case InterpolationMode.BilinearMipmapped:
					// As upstream: GL_NEAREST_MIPMAP_LINEAR.
					min = Vk.FilterNearest;
					break;
			}

			bool anisotropic = interpolation == InterpolationMode.AnisotropicFiltering && anisotropy > 0;
			for (int wrap = 0; wrap < 4; wrap++)
			{
				Vk.SamplerCreateInfo info = new Vk.SamplerCreateInfo
				{
					SType = Vk.StructureTypeSamplerCreateInfo,
					MagFilter = mag,
					MinFilter = min,
					MipmapMode = mip,
					// OpenGlTextureWrapMode: RepeatClamp (2) repeats S, ClampRepeat (1) repeats T.
					AddressModeU = (wrap & 2) != 0 ? Vk.SamplerAddressModeRepeat : Vk.SamplerAddressModeClampToEdge,
					AddressModeV = (wrap & 1) != 0 ? Vk.SamplerAddressModeRepeat : Vk.SamplerAddressModeClampToEdge,
					AddressModeW = Vk.SamplerAddressModeClampToEdge,
					AnisotropyEnable = anisotropic ? 1u : 0u,
					MaxAnisotropy = anisotropic ? anisotropy : 1.0f,
					MaxLod = maxLod
				};
				Vk.Check(Vk.vkCreateSampler(device.Device, &info, null, out Samplers[wrap]), "vkCreateSampler");
			}
		}

		protected override Entry NewRecord() => new VulkanEntry();

		protected override void MakeImage(Entry record)
		{
			VulkanEntry entry = (VulkanEntry)record;
			uint width = entry.Width, height = entry.Height;
			Vk.ImageCreateInfo imageInfo = new Vk.ImageCreateInfo
			{
				SType = Vk.StructureTypeImageCreateInfo,
				ImageType = 1,
				Format = Vk.FormatR8G8B8A8Unorm,
				Extent = new Vk.Extent3D { Width = width, Height = height, Depth = 1 },
				MipLevels = entry.Levels,
				ArrayLayers = 1,
				Samples = 1,
				Usage = Vk.ImageUsageSampled | Vk.ImageUsageTransferDst | Vk.ImageUsageTransferSrc,
				InitialLayout = Vk.ImageLayoutUndefined
			};
			Vk.Check(Vk.vkCreateImage(device.Device, &imageInfo, null, out entry.Image), "vkCreateImage (texture)");
			Vk.vkGetImageMemoryRequirements(device.Device, entry.Image, out Vk.MemoryRequirements requirements);
			entry.Memory = memory.Allocate(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits);
			Vk.Check(Vk.vkBindImageMemory(device.Device, entry.Image, memory.Memory(entry.Memory.Block), entry.Memory.Offset), "vkBindImageMemory (texture)");
			Vk.ImageViewCreateInfo viewInfo = new Vk.ImageViewCreateInfo
			{
				SType = Vk.StructureTypeImageViewCreateInfo,
				Image = entry.Image,
				ViewType = 1,
				Format = Vk.FormatR8G8B8A8Unorm,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, LevelCount = entry.Levels, LayerCount = 1 }
			};
			Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out entry.View), "vkCreateImageView (texture)");
			if (logTextures)
			{
				Program.FileSystem.AppendToLogFile("OBNS Vulkan texture " + entry.Index + ": " + width + "x" + height + ", " + entry.Levels + " levels, " + requirements.Size + " bytes at block " + entry.Memory.Block + " offset " + entry.Memory.Offset + " (alignment " + requirements.Alignment + ")");
			}
		}

		/// <summary>
		/// The pixels uploaded with every mipmap level (made on the GPU); with later, written into staging when the uploads
		/// are submitted.
		/// </summary>
		protected override void Write(Entry record, Pixels pixels, bool later)
		{
			VulkanEntry entry = (VulkanEntry)record;
			uint width = pixels.Width, height = pixels.Height;

			// The first level staged and copied; the others made from it on the GPU, each from the one before (image blits,
			// as OpenGL's mipmap generation does).
			ulong stagedBytes = (ulong)width * height * 4;
			ulong stagingOffset;
			ulong staging;
			staging = uploader.Reserve(stagedBytes, out stagingOffset, out byte* target);
			int count = (int)(width * height);
			if (later)
			{
				for (int first = 0; first < count; first += FillPiece)
				{
					int start = first, pieceCount = Math.Min(FillPiece, count - first);
					uploader.FillLater(() => pixels.Fill(target, start, pieceCount));
				}
			}
			else
			{
				pixels.Fill(target, 0, count);
			}

			IntPtr commands = uploader.Commands;
			// Discarding what was there (a new image, or an animated one's last frame: frames reading it are earlier on the queue).
			Vk.ImageMemoryBarrier toTransfer = ImageBarrier(entry.Image, 0, entry.Levels, Vk.AccessShaderRead, Vk.AccessTransferWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutTransferDstOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageFragmentShader | Vk.PipelineStageTransfer, Vk.PipelineStageTransfer, 0, 0, null, 0, null, 1, &toTransfer);
			Vk.BufferImageCopy copy = new Vk.BufferImageCopy
			{
				BufferOffset = stagingOffset,
				ImageSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, LayerCount = 1 },
				ImageExtent = new Vk.Extent3D { Width = width, Height = height, Depth = 1 }
			};
			Vk.vkCmdCopyBufferToImage(commands, staging, entry.Image, Vk.ImageLayoutTransferDstOptimal, 1, &copy);

			int levelWidth = (int)width, levelHeight = (int)height;
			for (uint level = 1; level < entry.Levels; level++)
			{
				Vk.ImageMemoryBarrier toSource = ImageBarrier(entry.Image, level - 1, 1, Vk.AccessTransferWrite, Vk.AccessTransferRead, Vk.ImageLayoutTransferDstOptimal, Vk.ImageLayoutTransferSrcOptimal);
				Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTransfer, Vk.PipelineStageTransfer, 0, 0, null, 0, null, 1, &toSource);
				int nextWidth = Math.Max(1, levelWidth / 2), nextHeight = Math.Max(1, levelHeight / 2);
				Vk.ImageBlit blit = new Vk.ImageBlit
				{
					SrcSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, MipLevel = level - 1, LayerCount = 1 },
					SrcOffset1 = new Vk.Offset3D { X = levelWidth, Y = levelHeight, Z = 1 },
					DstSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, MipLevel = level, LayerCount = 1 },
					DstOffset1 = new Vk.Offset3D { X = nextWidth, Y = nextHeight, Z = 1 }
				};
				Vk.vkCmdBlitImage(commands, entry.Image, Vk.ImageLayoutTransferSrcOptimal, entry.Image, Vk.ImageLayoutTransferDstOptimal, 1, &blit, Vk.FilterLinear);
				levelWidth = nextWidth;
				levelHeight = nextHeight;
			}

			// Levels before the last are transfer sources now; the last is still a transfer destination.
			Vk.ImageMemoryBarrier* toRead = stackalloc Vk.ImageMemoryBarrier[2];
			int barriers = 0;
			if (entry.Levels > 1)
			{
				toRead[barriers++] = ImageBarrier(entry.Image, 0, entry.Levels - 1, Vk.AccessTransferRead, Vk.AccessShaderRead, Vk.ImageLayoutTransferSrcOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			}

			toRead[barriers++] = ImageBarrier(entry.Image, entry.Levels - 1, 1, Vk.AccessTransferWrite, Vk.AccessShaderRead, Vk.ImageLayoutTransferDstOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTransfer, Vk.PipelineStageFragmentShader, 0, 0, null, 0, null, (uint)barriers, toRead);
		}

		protected override void Uploaded()
		{
			if (uploader.Full)
			{
				uploader.Submit(true);
			}
		}

		protected override void Flush() => uploader.Submit(true);

		private static Vk.ImageMemoryBarrier ImageBarrier(ulong image, uint baseLevel, uint levels, uint srcAccess, uint dstAccess, uint oldLayout, uint newLayout)
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
				Image = image,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, BaseMipLevel = baseLevel, LevelCount = levels, LayerCount = 1 }
			};
		}

		/// <summary>Points the array's element at a texture, in every frame's descriptor set (update-after-bind), or its page's.</summary>
		protected override void Published(Entry record)
		{
			VulkanEntry entry = (VulkanEntry)record;
			if (pages != null)
			{
				pages.Write(entry.Index, entry.View);
				return;
			}

			Vk.DescriptorImageInfo image = new Vk.DescriptorImageInfo { ImageView = entry.View, ImageLayout = Vk.ImageLayoutShaderReadOnlyOptimal };
			foreach (ulong set in descriptorSets)
			{
				Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = set,
					DstBinding = textureBinding,
					DstArrayElement = (uint)entry.Index,
					DescriptorCount = 1,
					DescriptorType = Vk.DescriptorTypeSampledImage,
					PImageInfo = &image
				};
				Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
			}
		}

		/// <summary>Pages: its element white again before it is destroyed (every element of a bound set must be valid).</summary>
		protected override void Unpublished(Entry entry) => pages?.Write(entry.Index, ((VulkanEntry)WhiteEntry).View);

		protected override void Destroy(Entry record)
		{
			if (!(record is VulkanEntry entry))
			{
				return;
			}

			if (entry.View != 0) Vk.vkDestroyImageView(device.Device, entry.View, null);
			if (entry.Image != 0) Vk.vkDestroyImage(device.Device, entry.Image, null);
			memory.Free(entry.Memory);
			entry.View = entry.Image = 0;
		}

		protected override string DescribeStorage() => memory.Describe();

		public override void Dispose()
		{
			base.Dispose();
			foreach (ulong sampler in Samplers)
			{
				if (sampler != 0) Vk.vkDestroySampler(device.Device, sampler, null);
			}

			memory.Dispose();
		}
	}
}
