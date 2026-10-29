// OBNS: not an upstream file. The images the world layer is drawn into, shared with OpenGL (OBNS/VULKAN.md).

using System;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>
	/// A colour and a depth image whose memory OpenGL imports, and the two semaphores ordering the APIs: OpenGL
	/// signals <see cref="GLDone"/> when it has finished with the images, Vulkan draws and signals
	/// <see cref="VulkanDone"/>, which OpenGL waits on before reading them. The cab layer has a colour image OpenGL
	/// imports too, and a depth image of Vulkan's own (nothing after the cab reads its depth).
	/// </summary>
	internal sealed unsafe class SharedTarget : IDisposable
	{
		internal const uint ColorFormat = Vk.FormatR8G8B8A8Unorm;
		internal const uint DepthFormat = Vk.FormatD32Sfloat;

		/// <summary>The layouts the images are left in for OpenGL, which OpenGL is told of when it waits and signals.</summary>
		internal const uint ColorLayoutForGL = Vk.ImageLayoutShaderReadOnlyOptimal, DepthLayoutForGL = Vk.ImageLayoutDepthStencilReadOnlyOptimal;

		private readonly VulkanDevice device;

		internal readonly uint Width, Height;
		internal ulong ColorImage, DepthImage, ColorView, DepthView, CabColorImage, CabDepthImage, CabColorView, CabDepthView;
		private ulong colorMemory, depthMemory, cabColorMemory, cabDepthMemory;
		internal ulong GLDone, VulkanDone;

		/// <summary>The handles OpenGL imports (Windows NT handles or file descriptors): memory (with its size) and semaphores.</summary>
		internal IntPtr ColorHandle, DepthHandle, CabColorHandle, GLDoneHandle, VulkanDoneHandle;
		internal ulong ColorSize, DepthSize, CabColorSize;

		internal SharedTarget(VulkanDevice device, uint width, uint height)
		{
			this.device = device;
			Width = width;
			Height = height;
			try
			{
				CreateImage(ColorFormat, Vk.ImageUsageColorAttachment | Vk.ImageUsageSampled | Vk.ImageUsageTransferSrc, Vk.ImageAspectColor,
					out ColorImage, out colorMemory, out ColorView, out ColorHandle, out ColorSize);
				CreateImage(DepthFormat, Vk.ImageUsageDepthStencilAttachment | Vk.ImageUsageSampled, Vk.ImageAspectDepth,
					out DepthImage, out depthMemory, out DepthView, out DepthHandle, out DepthSize);
				CreateImage(ColorFormat, Vk.ImageUsageColorAttachment | Vk.ImageUsageSampled, Vk.ImageAspectColor,
					out CabColorImage, out cabColorMemory, out CabColorView, out CabColorHandle, out CabColorSize);
				CreateImage(DepthFormat, Vk.ImageUsageDepthStencilAttachment, Vk.ImageAspectDepth,
					out CabDepthImage, out cabDepthMemory, out CabDepthView, out IntPtr _, out ulong _, false);
				GLDone = CreateSemaphore(out GLDoneHandle);
				VulkanDone = CreateSemaphore(out VulkanDoneHandle);
			}
			catch
			{
				Dispose();
				throw;
			}
		}

		private void CreateImage(uint format, uint usage, uint aspect, out ulong image, out ulong memory, out ulong view, out IntPtr handle, out ulong size, bool shared = true)
		{
			image = 0;
			memory = 0;
			view = 0;
			handle = IntPtr.Zero;
			Vk.ExternalMemoryImageCreateInfo external = new Vk.ExternalMemoryImageCreateInfo
			{
				SType = Vk.StructureTypeExternalMemoryImageCreateInfo,
				HandleTypes = VulkanDevice.MemoryHandleType
			};
			Vk.ImageCreateInfo imageInfo = new Vk.ImageCreateInfo
			{
				SType = Vk.StructureTypeImageCreateInfo,
				PNext = shared ? &external : null,
				ImageType = 1, // 2D
				Format = format,
				Extent = new Vk.Extent3D { Width = Width, Height = Height, Depth = 1 },
				MipLevels = 1,
				ArrayLayers = 1,
				Samples = 1,
				Tiling = 0, // optimal: OpenGL imports it with the same (driver-chosen) tiling
				Usage = usage,
				InitialLayout = Vk.ImageLayoutUndefined
			};
			Vk.Check(Vk.vkCreateImage(device.Device, &imageInfo, null, out image), "vkCreateImage");
			Vk.vkGetImageMemoryRequirements(device.Device, image, out Vk.MemoryRequirements requirements);

			// A dedicated allocation of its own, exportable (OpenGL is told it is dedicated when it imports it).
			Vk.MemoryDedicatedAllocateInfo dedicated = new Vk.MemoryDedicatedAllocateInfo { SType = Vk.StructureTypeMemoryDedicatedAllocateInfo, Image = image };
			Vk.ExportMemoryAllocateInfo export = new Vk.ExportMemoryAllocateInfo
			{
				SType = Vk.StructureTypeExportMemoryAllocateInfo,
				PNext = &dedicated,
				HandleTypes = VulkanDevice.MemoryHandleType
			};
			Vk.MemoryAllocateInfo allocateInfo = new Vk.MemoryAllocateInfo
			{
				SType = Vk.StructureTypeMemoryAllocateInfo,
				PNext = shared ? (void*)&export : null,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = device.MemoryType(requirements.MemoryTypeBits, Vk.MemoryPropertyDeviceLocal)
			};
			Vk.Check(Vk.vkAllocateMemory(device.Device, &allocateInfo, null, out memory), "vkAllocateMemory");
			Vk.Check(Vk.vkBindImageMemory(device.Device, image, memory, 0), "vkBindImageMemory");
			size = requirements.Size;
			if (shared)
			{
				handle = device.ExportMemory(memory);
			}

			Vk.ImageViewCreateInfo viewInfo = new Vk.ImageViewCreateInfo
			{
				SType = Vk.StructureTypeImageViewCreateInfo,
				Image = image,
				ViewType = 1, // 2D
				Format = format,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 }
			};
			Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out view), "vkCreateImageView");
		}

		private ulong CreateSemaphore(out IntPtr handle)
		{
			Vk.ExportSemaphoreCreateInfo export = new Vk.ExportSemaphoreCreateInfo
			{
				SType = Vk.StructureTypeExportSemaphoreCreateInfo,
				HandleTypes = VulkanDevice.SemaphoreHandleType
			};
			Vk.SemaphoreCreateInfo info = new Vk.SemaphoreCreateInfo { SType = Vk.StructureTypeSemaphoreCreateInfo, PNext = &export };
			Vk.Check(Vk.vkCreateSemaphore(device.Device, &info, null, out ulong semaphore), "vkCreateSemaphore");
			handle = device.ExportSemaphore(semaphore);
			return semaphore;
		}

		/// <summary>
		/// Records the barriers that take the images from OpenGL for drawing (their contents are discarded: the world layer
		/// clears them), into the given command buffer.
		/// </summary>
		internal void AcquireForDrawing(IntPtr commandBuffer)
		{
			Vk.ImageMemoryBarrier* barriers = stackalloc Vk.ImageMemoryBarrier[4];
			barriers[0] = Barrier(ColorImage, Vk.ImageAspectColor, 0, Vk.AccessColorAttachmentWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutColorAttachmentOptimal, Vk.QueueFamilyExternal, device.QueueFamily);
			barriers[1] = Barrier(DepthImage, Vk.ImageAspectDepth, 0, Vk.AccessDepthStencilAttachmentRead | Vk.AccessDepthStencilAttachmentWrite,
				Vk.ImageLayoutUndefined, Vk.ImageLayoutDepthStencilAttachmentOptimal, Vk.QueueFamilyExternal, device.QueueFamily);
			barriers[2] = Barrier(CabColorImage, Vk.ImageAspectColor, 0, Vk.AccessColorAttachmentWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutColorAttachmentOptimal, Vk.QueueFamilyExternal, device.QueueFamily);
			barriers[3] = Barrier(CabDepthImage, Vk.ImageAspectDepth, 0, Vk.AccessDepthStencilAttachmentRead | Vk.AccessDepthStencilAttachmentWrite,
				Vk.ImageLayoutUndefined, Vk.ImageLayoutDepthStencilAttachmentOptimal, Vk.QueueFamilyIgnored, Vk.QueueFamilyIgnored);
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageTopOfPipe, Vk.PipelineStageColorAttachmentOutput | Vk.PipelineStageEarlyFragmentTests, 0, 0, null, 0, null, 4, barriers);
		}

		/// <summary>Records the barriers that hand the drawn images to OpenGL, in the layouts it is told of.</summary>
		internal void ReleaseToGL(IntPtr commandBuffer)
		{
			Vk.ImageMemoryBarrier* barriers = stackalloc Vk.ImageMemoryBarrier[3];
			barriers[0] = Barrier(ColorImage, Vk.ImageAspectColor, Vk.AccessColorAttachmentWrite, 0, Vk.ImageLayoutColorAttachmentOptimal, ColorLayoutForGL, device.QueueFamily, Vk.QueueFamilyExternal);
			barriers[1] = Barrier(DepthImage, Vk.ImageAspectDepth, Vk.AccessDepthStencilAttachmentWrite, 0, Vk.ImageLayoutDepthStencilAttachmentOptimal, DepthLayoutForGL, device.QueueFamily, Vk.QueueFamilyExternal);
			barriers[2] = Barrier(CabColorImage, Vk.ImageAspectColor, Vk.AccessColorAttachmentWrite, 0, Vk.ImageLayoutColorAttachmentOptimal, ColorLayoutForGL, device.QueueFamily, Vk.QueueFamilyExternal);
			Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageColorAttachmentOutput | Vk.PipelineStageLateFragmentTests, Vk.PipelineStageBottomOfPipe, 0, 0, null, 0, null, 3, barriers);
		}

		private static Vk.ImageMemoryBarrier Barrier(ulong image, uint aspect, uint srcAccess, uint dstAccess, uint oldLayout, uint newLayout, uint srcFamily, uint dstFamily)
		{
			return new Vk.ImageMemoryBarrier
			{
				SType = Vk.StructureTypeImageMemoryBarrier,
				SrcAccessMask = srcAccess,
				DstAccessMask = dstAccess,
				OldLayout = oldLayout,
				NewLayout = newLayout,
				SrcQueueFamilyIndex = srcFamily,
				DstQueueFamilyIndex = dstFamily,
				Image = image,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 }
			};
		}

		public void Dispose()
		{
			IntPtr d = device.Device;
			if (d == IntPtr.Zero)
			{
				return;
			}

			Vk.vkDeviceWaitIdle(d);
			if (ColorView != 0) Vk.vkDestroyImageView(d, ColorView, null);
			if (DepthView != 0) Vk.vkDestroyImageView(d, DepthView, null);
			if (CabColorView != 0) Vk.vkDestroyImageView(d, CabColorView, null);
			if (CabDepthView != 0) Vk.vkDestroyImageView(d, CabDepthView, null);
			if (ColorImage != 0) Vk.vkDestroyImage(d, ColorImage, null);
			if (DepthImage != 0) Vk.vkDestroyImage(d, DepthImage, null);
			if (CabColorImage != 0) Vk.vkDestroyImage(d, CabColorImage, null);
			if (CabDepthImage != 0) Vk.vkDestroyImage(d, CabDepthImage, null);
			if (colorMemory != 0) Vk.vkFreeMemory(d, colorMemory, null);
			if (depthMemory != 0) Vk.vkFreeMemory(d, depthMemory, null);
			if (cabColorMemory != 0) Vk.vkFreeMemory(d, cabColorMemory, null);
			if (cabDepthMemory != 0) Vk.vkFreeMemory(d, cabDepthMemory, null);
			if (GLDone != 0) Vk.vkDestroySemaphore(d, GLDone, null);
			if (VulkanDone != 0) Vk.vkDestroySemaphore(d, VulkanDone, null);
			ColorView = DepthView = ColorImage = DepthImage = colorMemory = depthMemory = GLDone = VulkanDone = 0;
			CabColorView = CabDepthView = CabColorImage = CabDepthImage = cabColorMemory = cabDepthMemory = 0;
		}
	}
}
