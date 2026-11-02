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

// OBNS: not an upstream file. Vulkan's swap chain, for a game drawn wholly by Vulkan (OBNS/RENDERERS.md).

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Vulkan
{
	internal static unsafe partial class Vk
	{
		internal const uint StructureTypeWin32SurfaceCreateInfoKHR = 1000009000, StructureTypeSwapchainCreateInfoKHR = 1000001000, StructureTypePresentInfoKHR = 1000001001;
		internal const uint ImageLayoutPresentSrc = 1000001002;
		internal const uint PresentModeImmediate = 0, PresentModeMailbox = 1, PresentModeFifo = 2;
		internal const uint FormatB8G8R8A8Unorm = 44;
		internal const int SuboptimalKHR = 1000001003, ErrorOutOfDateKHR = -1000001004;

		[StructLayout(LayoutKind.Sequential)]
		internal struct Win32SurfaceCreateInfoKHR
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public IntPtr Hinstance;
			public IntPtr Hwnd;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SurfaceCapabilitiesKHR
		{
			public uint MinImageCount, MaxImageCount;
			public Extent2D CurrentExtent, MinImageExtent, MaxImageExtent;
			public uint MaxImageArrayLayers, SupportedTransforms, CurrentTransform, SupportedCompositeAlpha, SupportedUsageFlags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SurfaceFormatKHR
		{
			public uint Format, ColorSpace;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SwapchainCreateInfoKHR
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public ulong Surface;
			public uint MinImageCount, ImageFormat, ImageColorSpace;
			public Extent2D ImageExtent;
			public uint ImageArrayLayers, ImageUsage, ImageSharingMode, QueueFamilyIndexCount;
			public uint* PQueueFamilyIndices;
			public uint PreTransform, CompositeAlpha, PresentMode, Clipped;
			public ulong OldSwapchain;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PresentInfoKHR
		{
			public uint SType;
			public void* PNext;
			public uint WaitSemaphoreCount;
			public ulong* PWaitSemaphores;
			public uint SwapchainCount;
			public ulong* PSwapchains;
			public uint* PImageIndices;
			public int* PResults;
		}

		[DllImport(Library)] internal static extern int vkCreateWin32SurfaceKHR(IntPtr instance, Win32SurfaceCreateInfoKHR* createInfo, void* allocator, out ulong surface);
		[DllImport(Library)] internal static extern void vkDestroySurfaceKHR(IntPtr instance, ulong surface, void* allocator);
		[DllImport(Library)] internal static extern int vkGetPhysicalDeviceSurfaceSupportKHR(IntPtr physicalDevice, uint queueFamily, ulong surface, out uint supported);
		[DllImport(Library)] internal static extern int vkGetPhysicalDeviceSurfaceCapabilitiesKHR(IntPtr physicalDevice, ulong surface, out SurfaceCapabilitiesKHR capabilities);
		[DllImport(Library)] internal static extern int vkGetPhysicalDeviceSurfaceFormatsKHR(IntPtr physicalDevice, ulong surface, ref uint count, SurfaceFormatKHR* formats);
		[DllImport(Library)] internal static extern int vkGetPhysicalDeviceSurfacePresentModesKHR(IntPtr physicalDevice, ulong surface, ref uint count, uint* modes);
		[DllImport(Library)] internal static extern int vkCreateSwapchainKHR(IntPtr device, SwapchainCreateInfoKHR* createInfo, void* allocator, out ulong swapchain);
		[DllImport(Library)] internal static extern void vkDestroySwapchainKHR(IntPtr device, ulong swapchain, void* allocator);
		[DllImport(Library)] internal static extern int vkGetSwapchainImagesKHR(IntPtr device, ulong swapchain, ref uint count, ulong* images);
		[DllImport(Library)] internal static extern int vkAcquireNextImageKHR(IntPtr device, ulong swapchain, ulong timeout, ulong semaphore, ulong fence, out uint index);
		[DllImport(Library)] internal static extern int vkQueuePresentKHR(IntPtr queue, PresentInfoKHR* presentInfo);
		[DllImport(Library)] internal static extern void vkCmdCopyImageToBuffer(IntPtr commandBuffer, ulong image, uint layout, ulong buffer, uint regionCount, BufferImageCopy* regions);
		[DllImport("kernel32.dll")] internal static extern IntPtr GetModuleHandle(IntPtr name);
	}

	/// <summary>The window's surface and swap chain: made for the surface's size, and made again when it changes.</summary>
	internal sealed unsafe class VulkanPresenter : IDisposable
	{
		private readonly VulkanDevice device;
		private readonly ulong surface;
		private ulong swapchain;
		private ulong[] images = new ulong[0];
		private ulong[] rendered = new ulong[0];
		private ulong[] acquired = new ulong[0];
		private int nextAcquire;
		private uint format, colorSpace;
		private bool stale;
		private bool vsync;

		internal uint Width { get; private set; }
		internal uint Height { get; private set; }

		/// <summary>The swap chain's current image, once <see cref="Acquire"/> has given one.</summary>
		internal ulong Image => images[index];

		/// <summary>The semaphore the frame's commands wait for, and the one they signal for the present.</summary>
		internal ulong AcquiredSemaphore { get; private set; }

		internal ulong RenderedSemaphore => rendered[index];

		private uint index;

		internal VulkanPresenter(VulkanDevice device, IntPtr window, bool vsync)
		{
			this.device = device;
			this.vsync = vsync;
			Vk.Win32SurfaceCreateInfoKHR info = new Vk.Win32SurfaceCreateInfoKHR
			{
				SType = Vk.StructureTypeWin32SurfaceCreateInfoKHR,
				Hinstance = Vk.GetModuleHandle(IntPtr.Zero),
				Hwnd = window
			};
			Vk.Check(Vk.vkCreateWin32SurfaceKHR(device.Instance, &info, null, out surface), "vkCreateWin32SurfaceKHR");
			Vk.Check(Vk.vkGetPhysicalDeviceSurfaceSupportKHR(device.PhysicalDevice, device.QueueFamily, surface, out uint supported), "vkGetPhysicalDeviceSurfaceSupportKHR");
			if (supported == 0)
			{
				throw new InvalidOperationException("the device's graphics queue cannot present to the window");
			}

			uint count = 0;
			Vk.Check(Vk.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, ref count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
			Vk.SurfaceFormatKHR* formats = stackalloc Vk.SurfaceFormatKHR[(int)Math.Max(count, 1)];
			Vk.Check(Vk.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, ref count, formats), "vkGetPhysicalDeviceSurfaceFormatsKHR");
			format = formats[0].Format;
			colorSpace = formats[0].ColorSpace;
			for (uint i = 0; i < count; i++)
			{
				// Plain values, as OpenGL's framebuffer has them.
				if (formats[i].Format == Vk.FormatB8G8R8A8Unorm || formats[i].Format == Vk.FormatR8G8B8A8Unorm)
				{
					format = formats[i].Format;
					colorSpace = formats[i].ColorSpace;
					break;
				}
			}
		}

		/// <summary>Gets the next image; false if there's nothing to present to now (minimized, or the chain is being made again).</summary>
		internal bool Acquire()
		{
			Vk.Check(Vk.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device.PhysicalDevice, surface, out Vk.SurfaceCapabilitiesKHR capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
			uint width = capabilities.CurrentExtent.Width, height = capabilities.CurrentExtent.Height;
			if (width == 0 || height == 0)
			{
				return false;
			}

			if (swapchain == 0 || stale || (width != uint.MaxValue && (width != Width || height != Height)))
			{
				Make(capabilities);
			}

			ulong semaphore = acquired[nextAcquire];
			int result = Vk.vkAcquireNextImageKHR(device.Device, swapchain, ulong.MaxValue, semaphore, 0, out index);
			if (result == Vk.ErrorOutOfDateKHR)
			{
				stale = true;
				return false;
			}

			if (result != Vk.SuboptimalKHR)
			{
				Vk.Check(result, "vkAcquireNextImageKHR");
			}

			AcquiredSemaphore = semaphore;
			nextAcquire = (nextAcquire + 1) % acquired.Length;
			return true;
		}

		/// <summary>Presents the image, once the frame's commands signal <see cref="RenderedSemaphore"/>.</summary>
		internal void Present()
		{
			ulong wait = RenderedSemaphore, chain = swapchain;
			uint imageIndex = index;
			Vk.PresentInfoKHR info = new Vk.PresentInfoKHR
			{
				SType = Vk.StructureTypePresentInfoKHR,
				WaitSemaphoreCount = 1,
				PWaitSemaphores = &wait,
				SwapchainCount = 1,
				PSwapchains = &chain,
				PImageIndices = &imageIndex
			};
			int result;
			lock (device.QueueLock)
			{
				result = Vk.vkQueuePresentKHR(device.Queue, &info);
			}

			if (result == Vk.ErrorOutOfDateKHR || result == Vk.SuboptimalKHR)
			{
				stale = true;
			}
			else
			{
				Vk.Check(result, "vkQueuePresentKHR");
			}
		}

		private void Make(Vk.SurfaceCapabilitiesKHR capabilities)
		{
			// The old chain's images and semaphores may still be in use.
			Vk.vkDeviceWaitIdle(device.Device);
			uint width = capabilities.CurrentExtent.Width == uint.MaxValue ? Math.Max(Width, 1) : capabilities.CurrentExtent.Width;
			uint height = capabilities.CurrentExtent.Height == uint.MaxValue ? Math.Max(Height, 1) : capabilities.CurrentExtent.Height;
			uint presentMode = Vk.PresentModeFifo;
			if (!vsync)
			{
				uint count = 0;
				Vk.Check(Vk.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, ref count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
				uint* modes = stackalloc uint[(int)Math.Max(count, 1)];
				Vk.Check(Vk.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, ref count, modes), "vkGetPhysicalDeviceSurfacePresentModesKHR");
				for (uint i = 0; i < count; i++)
				{
					if (modes[i] == Vk.PresentModeImmediate)
					{
						presentMode = Vk.PresentModeImmediate;
						break;
					}
				}
			}

			uint imageCount = capabilities.MinImageCount + 1;
			if (capabilities.MaxImageCount != 0 && imageCount > capabilities.MaxImageCount)
			{
				imageCount = capabilities.MaxImageCount;
			}

			// Opaque (1) if the window can have it, else whatever it has.
			uint compositeAlpha = (capabilities.SupportedCompositeAlpha & 1) != 0 ? 1u : capabilities.SupportedCompositeAlpha & (~capabilities.SupportedCompositeAlpha + 1);
			Vk.SwapchainCreateInfoKHR info = new Vk.SwapchainCreateInfoKHR
			{
				SType = Vk.StructureTypeSwapchainCreateInfoKHR,
				Surface = surface,
				MinImageCount = imageCount,
				ImageFormat = format,
				ImageColorSpace = colorSpace,
				ImageExtent = new Vk.Extent2D { Width = width, Height = height },
				ImageArrayLayers = 1,
				ImageUsage = Vk.ImageUsageTransferDst,
				PreTransform = capabilities.CurrentTransform,
				CompositeAlpha = compositeAlpha,
				PresentMode = presentMode,
				Clipped = 1,
				OldSwapchain = swapchain
			};
			Vk.Check(Vk.vkCreateSwapchainKHR(device.Device, &info, null, out ulong made), "vkCreateSwapchainKHR");
			DestroyChain();
			swapchain = made;
			Width = width;
			Height = height;
			stale = false;
			uint imagesMade = 0;
			Vk.Check(Vk.vkGetSwapchainImagesKHR(device.Device, swapchain, ref imagesMade, null), "vkGetSwapchainImagesKHR");
			images = new ulong[imagesMade];
			fixed (ulong* to = images)
			{
				Vk.Check(Vk.vkGetSwapchainImagesKHR(device.Device, swapchain, ref imagesMade, to), "vkGetSwapchainImagesKHR");
			}

			rendered = new ulong[imagesMade];
			acquired = new ulong[imagesMade + 1];
			nextAcquire = 0;
			Vk.SemaphoreCreateInfo semaphoreInfo = new Vk.SemaphoreCreateInfo { SType = Vk.StructureTypeSemaphoreCreateInfo };
			for (int i = 0; i < rendered.Length; i++)
			{
				Vk.Check(Vk.vkCreateSemaphore(device.Device, &semaphoreInfo, null, out rendered[i]), "vkCreateSemaphore");
			}

			for (int i = 0; i < acquired.Length; i++)
			{
				Vk.Check(Vk.vkCreateSemaphore(device.Device, &semaphoreInfo, null, out acquired[i]), "vkCreateSemaphore");
			}
		}

		private void DestroyChain()
		{
			foreach (ulong semaphore in rendered)
			{
				Vk.vkDestroySemaphore(device.Device, semaphore, null);
			}

			foreach (ulong semaphore in acquired)
			{
				Vk.vkDestroySemaphore(device.Device, semaphore, null);
			}

			rendered = new ulong[0];
			acquired = new ulong[0];
			if (swapchain != 0)
			{
				Vk.vkDestroySwapchainKHR(device.Device, swapchain, null);
				swapchain = 0;
			}
		}

		public void Dispose()
		{
			if (device.Device != IntPtr.Zero)
			{
				Vk.vkDeviceWaitIdle(device.Device);
				DestroyChain();
			}

			Vk.vkDestroySurfaceKHR(device.Instance, surface, null);
		}
	}
}
