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
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace OpenBve.Vulkan
{
	/*
	 * The Vulkan device and the window's swapchain, for the native renderer: instance, Android
	 * surface, device and graphics queue, swapchain with a depth buffer, one render pass, and a
	 * frame loop with two frames in flight (VulkanGL records into it). The window can go and come back
	 * (DetachWindow / AttachWindow, as when the app moves to another display) and change size
	 * (the swapchain is rebuilt when the driver reports it out of date).
	 */
	public sealed unsafe class VulkanContext : IDisposable
	{
		private const int FramesInFlight = 2;

		private readonly Vk vk;
		private Instance instance;
		private KhrSurface khrSurface;
		private KhrAndroidSurface khrAndroidSurface;
		private KhrSwapchain khrSwapchain;
		private PhysicalDevice physicalDevice;
		private Device device;
		private Queue queue;
		private uint queueFamily;

		private SurfaceKHR surface;
		private SwapchainKHR swapchain;
		private Format colorFormat;
		private Format depthFormat;
		private Extent2D extent;
		private Image[] images = Array.Empty<Image>();
		private ImageView[] views = Array.Empty<ImageView>();
		private Framebuffer[] framebuffers = Array.Empty<Framebuffer>();
		private Image depthImage;
		private DeviceMemory depthMemory;
		private ImageView depthView;
		private RenderPass renderPass;

		private CommandPool commandPool;
		private readonly CommandBuffer[] commandBuffers = new CommandBuffer[FramesInFlight];
		private readonly CommandBuffer[] uploadBuffers = new CommandBuffer[FramesInFlight];
		private readonly ulong[] slotSerial = new ulong[FramesInFlight];
		private readonly System.Collections.Generic.List<(ulong Serial, Action Action)> deferred = new System.Collections.Generic.List<(ulong, Action)>();
		private bool slotReady;
		private bool uploadsRecording;
		private bool renderPassStarted;
		private readonly Semaphore[] imageAvailable = new Semaphore[FramesInFlight];
		private readonly Semaphore[] renderFinished = new Semaphore[FramesInFlight];
		private readonly Fence[] inFlight = new Fence[FramesInFlight];
		private int frame;
		private uint imageIndex;
		private bool frameStarted;
		private bool rebuild;

		/// <summary>The device and driver, for the log.</summary>
		public string Description { get; private set; }

		/// <summary>The swapchain's size in pixels.</summary>
		public uint Width => (Rotation % 180) == 0 ? extent.Width : extent.Height;

		/// <summary>The swapchain's size in pixels.</summary>
		public uint Height => (Rotation % 180) == 0 ? extent.Height : extent.Width;

		/// <summary>
		/// Clockwise degrees (0, 90, 180, 270) the upright frame must be turned to match the
		/// swapchain images, which are in the display's own orientation (pre-rotation): the
		/// compositor then shows them without turning them itself, which on a phone held sideways
		/// otherwise costs a GPU composition pass every frame.
		/// </summary>
		public int Rotation { get; private set; }

		/// <summary>The command buffer the frame draws into, between BeginFrame and EndFrame.</summary>
		public CommandBuffer DrawCommands => commandBuffers[frame];

		/// <summary>The frame slot being (or about to be) recorded, 0 to FramesInFlight - 1.</summary>
		public int FrameSlot => frame;

		/// <summary>How many frames may be in flight at once.</summary>
		public const int Slots = FramesInFlight;

		/// <summary>Whether a frame is being recorded (BeginFrame without EndFrame yet).</summary>
		public bool InFrame => frameStarted;

		/// <summary>Whether the frame's render pass has begun (BeginRenderPass).</summary>
		public bool InRenderPass => renderPassStarted;

		/// <summary>
		/// The serial the submission now being recorded will have: whatever is used now may be freed
		/// once <see cref="CompletedSerial"/> reaches it (see <see cref="Defer"/>).
		/// </summary>
		public ulong RecordingSerial { get; private set; } = 1;

		/// <summary>The serial of the latest submission the GPU has finished with.</summary>
		public ulong CompletedSerial { get; private set; }

		/// <summary>The physical device.</summary>
		public PhysicalDevice PhysicalDevice => physicalDevice;

		/// <summary>The largest anisotropy samplers may use (1 when the device has no anisotropic filtering).</summary>
		public float MaxAnisotropy { get; private set; } = 1.0f;

		/// <summary>The device's limits.</summary>
		public PhysicalDeviceLimits Limits { get; private set; }

		/// <summary>The swapchain images' size: the display's own orientation (see Rotation).</summary>
		public Extent2D ImageExtent => extent;

		/// <summary>The Vulkan API, for the code that records into the frame.</summary>
		public Vk Api => vk;

		/// <summary>The logical device.</summary>
		public Device Device => device;

		/// <summary>The render pass every frame draws in.</summary>
		public RenderPass RenderPass => renderPass;

		/// <summary>Creates the device on an ANativeWindow* (the window can be replaced later).</summary>
		public VulkanContext(IntPtr nativeWindow)
		{
			vk = Vk.GetApi();
			CreateInstance();
			CreateSurface(nativeWindow);
			PickDevice();
			CreateDevice();
			CreateFrameResources();
			CreateSwapchain();
		}

		private static void Check(Result result, string what)
		{
			if (result != Result.Success)
			{
				throw new InvalidOperationException(what + " failed: " + result);
			}
		}

		private void CreateInstance()
		{
			IntPtr name = SilkMarshal.StringToPtr("OpenBVE");
			string[] extensions = { KhrSurface.ExtensionName, KhrAndroidSurface.ExtensionName };
			IntPtr extensionNames = SilkMarshal.StringArrayToPtr(extensions);
			try
			{
				ApplicationInfo application = new ApplicationInfo
				{
					SType = StructureType.ApplicationInfo,
					PApplicationName = (byte*)name,
					PEngineName = (byte*)name,
					ApiVersion = Vk.Version11
				};
				InstanceCreateInfo info = new InstanceCreateInfo
				{
					SType = StructureType.InstanceCreateInfo,
					PApplicationInfo = &application,
					EnabledExtensionCount = (uint)extensions.Length,
					PpEnabledExtensionNames = (byte**)extensionNames
				};
				Check(vk.CreateInstance(in info, null, out instance), "vkCreateInstance");
			}
			finally
			{
				SilkMarshal.Free(name);
				SilkMarshal.Free(extensionNames);
			}

			if (!vk.TryGetInstanceExtension(instance, out khrSurface) || !vk.TryGetInstanceExtension(instance, out khrAndroidSurface))
			{
				throw new InvalidOperationException("the Vulkan driver has no Android surface extension");
			}
		}

		private void CreateSurface(IntPtr nativeWindow)
		{
			AndroidSurfaceCreateInfoKHR info = new AndroidSurfaceCreateInfoKHR
			{
				SType = StructureType.AndroidSurfaceCreateInfoKhr,
				Window = (nint*)nativeWindow
			};
			Check(khrAndroidSurface.CreateAndroidSurface(instance, in info, null, out surface), "vkCreateAndroidSurfaceKHR");
		}

		private void PickDevice()
		{
			uint count = 0;
			Check(vk.EnumeratePhysicalDevices(instance, ref count, null), "vkEnumeratePhysicalDevices");
			if (count == 0)
			{
				throw new InvalidOperationException("no Vulkan device");
			}

			PhysicalDevice[] devices = new PhysicalDevice[count];
			fixed (PhysicalDevice* p = devices)
			{
				Check(vk.EnumeratePhysicalDevices(instance, ref count, p), "vkEnumeratePhysicalDevices");
			}

			foreach (PhysicalDevice candidate in devices)
			{
				uint families = 0;
				vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, null);
				QueueFamilyProperties[] properties = new QueueFamilyProperties[families];
				fixed (QueueFamilyProperties* p = properties)
				{
					vk.GetPhysicalDeviceQueueFamilyProperties(candidate, ref families, p);
				}

				for (uint i = 0; i < families; i++)
				{
					khrSurface.GetPhysicalDeviceSurfaceSupport(candidate, i, surface, out Bool32 present);
					if ((properties[i].QueueFlags & QueueFlags.GraphicsBit) != 0 && present)
					{
						physicalDevice = candidate;
						queueFamily = i;
						vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties deviceProperties);
						Description = "Vulkan " + Version(deviceProperties.ApiVersion) + " (" + SilkMarshal.PtrToString((nint)deviceProperties.DeviceName) +
						              ", driver " + deviceProperties.DriverVersion.ToString("X") + ")";
						return;
					}
				}
			}

			throw new InvalidOperationException("no Vulkan device can draw to this window");
		}

		private static string Version(uint version) => (version >> 22) + "." + ((version >> 12) & 0x3FF) + "." + (version & 0xFFF);

		private void CreateDevice()
		{
			float priority = 1.0f;
			DeviceQueueCreateInfo queueInfo = new DeviceQueueCreateInfo
			{
				SType = StructureType.DeviceQueueCreateInfo,
				QueueFamilyIndex = queueFamily,
				QueueCount = 1,
				PQueuePriorities = &priority
			};
			string[] extensions = { KhrSwapchain.ExtensionName };
			IntPtr extensionNames = SilkMarshal.StringArrayToPtr(extensions);
			try
			{
				vk.GetPhysicalDeviceFeatures(physicalDevice, out PhysicalDeviceFeatures supported);
				vk.GetPhysicalDeviceProperties(physicalDevice, out PhysicalDeviceProperties properties);
				Limits = properties.Limits;
				PhysicalDeviceFeatures features = default;
				if (supported.SamplerAnisotropy)
				{
					features.SamplerAnisotropy = true;
					MaxAnisotropy = properties.Limits.MaxSamplerAnisotropy;
				}

				DeviceCreateInfo info = new DeviceCreateInfo
				{
					SType = StructureType.DeviceCreateInfo,
					QueueCreateInfoCount = 1,
					PQueueCreateInfos = &queueInfo,
					EnabledExtensionCount = (uint)extensions.Length,
					PpEnabledExtensionNames = (byte**)extensionNames,
					PEnabledFeatures = &features
				};
				Check(vk.CreateDevice(physicalDevice, in info, null, out device), "vkCreateDevice");
			}
			finally
			{
				SilkMarshal.Free(extensionNames);
			}

			vk.GetDeviceQueue(device, queueFamily, 0, out queue);
			if (!vk.TryGetDeviceExtension(instance, device, out khrSwapchain))
			{
				throw new InvalidOperationException("the Vulkan device has no swapchain extension");
			}

			depthFormat = PickDepthFormat();
		}

		private Format PickDepthFormat()
		{
			foreach (Format candidate in new[] { Format.D24UnormS8Uint, Format.D32Sfloat, Format.X8D24UnormPack32, Format.D16Unorm })
			{
				vk.GetPhysicalDeviceFormatProperties(physicalDevice, candidate, out FormatProperties properties);
				if ((properties.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
				{
					return candidate;
				}
			}

			throw new InvalidOperationException("no depth format");
		}

		private void CreateFrameResources()
		{
			CommandPoolCreateInfo poolInfo = new CommandPoolCreateInfo
			{
				SType = StructureType.CommandPoolCreateInfo,
				QueueFamilyIndex = queueFamily,
				Flags = CommandPoolCreateFlags.ResetCommandBufferBit
			};
			Check(vk.CreateCommandPool(device, in poolInfo, null, out commandPool), "vkCreateCommandPool");
			CommandBufferAllocateInfo allocateInfo = new CommandBufferAllocateInfo
			{
				SType = StructureType.CommandBufferAllocateInfo,
				CommandPool = commandPool,
				Level = CommandBufferLevel.Primary,
				CommandBufferCount = FramesInFlight
			};
			fixed (CommandBuffer* p = commandBuffers)
			{
				Check(vk.AllocateCommandBuffers(device, in allocateInfo, p), "vkAllocateCommandBuffers");
			}

			fixed (CommandBuffer* p = uploadBuffers)
			{
				Check(vk.AllocateCommandBuffers(device, in allocateInfo, p), "vkAllocateCommandBuffers");
			}

			SemaphoreCreateInfo semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
			FenceCreateInfo fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
			for (int i = 0; i < FramesInFlight; i++)
			{
				Check(vk.CreateSemaphore(device, in semaphoreInfo, null, out imageAvailable[i]), "vkCreateSemaphore");
				Check(vk.CreateSemaphore(device, in semaphoreInfo, null, out renderFinished[i]), "vkCreateSemaphore");
				Check(vk.CreateFence(device, in fenceInfo, null, out inFlight[i]), "vkCreateFence");
			}
		}

		private void CreateSwapchain()
		{
			Check(khrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out SurfaceCapabilitiesKHR capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
			uint formatCount = 0;
			khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, null);
			SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
			fixed (SurfaceFormatKHR* p = formats)
			{
				khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, ref formatCount, p);
			}

			SurfaceFormatKHR chosen = formats.Length > 0 ? formats[0] : new SurfaceFormatKHR(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr);
			foreach (SurfaceFormatKHR format in formats)
			{
				if (format.Format == Format.R8G8B8A8Unorm || format.Format == Format.B8G8R8A8Unorm)
				{
					chosen = format;
					break;
				}
			}

			colorFormat = chosen.Format;
			extent = capabilities.CurrentExtent.Width != uint.MaxValue ? capabilities.CurrentExtent : new Extent2D(capabilities.MinImageExtent.Width, capabilities.MinImageExtent.Height);
			// Pre-rotation: the images take the display's own orientation and the frame is turned
			// to fit (Rotation); Android reports the window's (turned) size as the current extent.
			SurfaceTransformFlagsKHR transform = capabilities.CurrentTransform;
			if ((capabilities.SupportedTransforms & transform) == 0)
			{
				transform = SurfaceTransformFlagsKHR.IdentityBitKhr;
			}

			switch (transform)
			{
				case SurfaceTransformFlagsKHR.Rotate90BitKhr:
					Rotation = 90;
					break;
				case SurfaceTransformFlagsKHR.Rotate180BitKhr:
					Rotation = 180;
					break;
				case SurfaceTransformFlagsKHR.Rotate270BitKhr:
					Rotation = 270;
					break;
				default:
					transform = SurfaceTransformFlagsKHR.IdentityBitKhr;
					Rotation = 0;
					break;
			}

			if (Rotation % 180 != 0)
			{
				extent = new Extent2D(extent.Height, extent.Width);
			}

			uint imageCount = Math.Max(capabilities.MinImageCount, 3);
			if (capabilities.MaxImageCount > 0)
			{
				imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
			}

			CompositeAlphaFlagsKHR alpha = (capabilities.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0
				? CompositeAlphaFlagsKHR.OpaqueBitKhr
				: CompositeAlphaFlagsKHR.InheritBitKhr;
			SwapchainKHR old = swapchain;
			SwapchainCreateInfoKHR info = new SwapchainCreateInfoKHR
			{
				SType = StructureType.SwapchainCreateInfoKhr,
				Surface = surface,
				MinImageCount = imageCount,
				ImageFormat = colorFormat,
				ImageColorSpace = chosen.ColorSpace,
				ImageExtent = extent,
				ImageArrayLayers = 1,
				ImageUsage = ImageUsageFlags.ColorAttachmentBit,
				ImageSharingMode = SharingMode.Exclusive,
				PreTransform = transform,
				CompositeAlpha = alpha,
				PresentMode = PresentModeKHR.FifoKhr,
				Clipped = true,
				OldSwapchain = old
			};
			Check(khrSwapchain.CreateSwapchain(device, in info, null, out swapchain), "vkCreateSwapchainKHR");
			if (old.Handle != 0)
			{
				khrSwapchain.DestroySwapchain(device, old, null);
			}

			uint count = 0;
			khrSwapchain.GetSwapchainImages(device, swapchain, ref count, null);
			images = new Image[count];
			fixed (Image* p = images)
			{
				khrSwapchain.GetSwapchainImages(device, swapchain, ref count, p);
			}

			if (renderPass.Handle == 0)
			{
				CreateRenderPass();
			}

			CreateDepthBuffer();
			views = new ImageView[count];
			framebuffers = new Framebuffer[count];
			ImageView* attachments = stackalloc ImageView[2];
			for (int i = 0; i < count; i++)
			{
				views[i] = CreateView(images[i], colorFormat, ImageAspectFlags.ColorBit);
				attachments[0] = views[i];
				attachments[1] = depthView;
				FramebufferCreateInfo framebufferInfo = new FramebufferCreateInfo
				{
					SType = StructureType.FramebufferCreateInfo,
					RenderPass = renderPass,
					AttachmentCount = 2,
					PAttachments = attachments,
					Width = extent.Width,
					Height = extent.Height,
					Layers = 1
				};
				Check(vk.CreateFramebuffer(device, in framebufferInfo, null, out framebuffers[i]), "vkCreateFramebuffer");
			}
		}

		private ImageView CreateView(Image image, Format format, ImageAspectFlags aspect)
		{
			ImageViewCreateInfo info = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = format,
				SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1)
			};
			Check(vk.CreateImageView(device, in info, null, out ImageView view), "vkCreateImageView");
			return view;
		}

		private void CreateRenderPass()
		{
			AttachmentDescription* attachments = stackalloc AttachmentDescription[2];
			attachments[0] = new AttachmentDescription
			{
				Format = colorFormat,
				Samples = SampleCountFlags.Count1Bit,
				LoadOp = AttachmentLoadOp.Clear,
				StoreOp = AttachmentStoreOp.Store,
				StencilLoadOp = AttachmentLoadOp.DontCare,
				StencilStoreOp = AttachmentStoreOp.DontCare,
				InitialLayout = ImageLayout.Undefined,
				FinalLayout = ImageLayout.PresentSrcKhr
			};
			attachments[1] = new AttachmentDescription
			{
				Format = depthFormat,
				Samples = SampleCountFlags.Count1Bit,
				LoadOp = AttachmentLoadOp.Clear,
				StoreOp = AttachmentStoreOp.DontCare,
				StencilLoadOp = AttachmentLoadOp.DontCare,
				StencilStoreOp = AttachmentStoreOp.DontCare,
				InitialLayout = ImageLayout.Undefined,
				FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
			};
			AttachmentReference color = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
			AttachmentReference depth = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
			SubpassDescription subpass = new SubpassDescription
			{
				PipelineBindPoint = PipelineBindPoint.Graphics,
				ColorAttachmentCount = 1,
				PColorAttachments = &color,
				PDepthStencilAttachment = &depth
			};
			SubpassDependency dependency = new SubpassDependency
			{
				SrcSubpass = Vk.SubpassExternal,
				DstSubpass = 0,
				SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
				DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
				DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
			};
			RenderPassCreateInfo info = new RenderPassCreateInfo
			{
				SType = StructureType.RenderPassCreateInfo,
				AttachmentCount = 2,
				PAttachments = attachments,
				SubpassCount = 1,
				PSubpasses = &subpass,
				DependencyCount = 1,
				PDependencies = &dependency
			};
			Check(vk.CreateRenderPass(device, in info, null, out renderPass), "vkCreateRenderPass");
		}

		private void CreateDepthBuffer()
		{
			ImageCreateInfo info = new ImageCreateInfo
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = depthFormat,
				Extent = new Extent3D(extent.Width, extent.Height, 1),
				MipLevels = 1,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = ImageUsageFlags.DepthStencilAttachmentBit,
				SharingMode = SharingMode.Exclusive,
				InitialLayout = ImageLayout.Undefined
			};
			Check(vk.CreateImage(device, in info, null, out depthImage), "vkCreateImage");
			vk.GetImageMemoryRequirements(device, depthImage, out MemoryRequirements requirements);
			MemoryAllocateInfo allocateInfo = new MemoryAllocateInfo
			{
				SType = StructureType.MemoryAllocateInfo,
				AllocationSize = requirements.Size,
				MemoryTypeIndex = MemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
			};
			Check(vk.AllocateMemory(device, in allocateInfo, null, out depthMemory), "vkAllocateMemory");
			Check(vk.BindImageMemory(device, depthImage, depthMemory, 0), "vkBindImageMemory");
			ImageAspectFlags aspect = depthFormat == Format.D24UnormS8Uint ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit : ImageAspectFlags.DepthBit;
			depthView = CreateView(depthImage, depthFormat, aspect);
		}

		/// <summary>A memory type index with these properties, among those the resource accepts, if there is one.</summary>
		public bool TryMemoryType(uint acceptable, MemoryPropertyFlags wanted, out uint type)
		{
			vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties properties);
			for (int i = 0; i < properties.MemoryTypeCount; i++)
			{
				if ((acceptable & (1u << i)) != 0 && (properties.MemoryTypes[i].PropertyFlags & wanted) == wanted)
				{
					type = (uint)i;
					return true;
				}
			}

			type = 0;
			return false;
		}

		/// <summary>A memory type index with these properties, among those the resource accepts.</summary>
		public uint MemoryType(uint acceptable, MemoryPropertyFlags wanted)
		{
			vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties properties);
			for (int i = 0; i < properties.MemoryTypeCount; i++)
			{
				if ((acceptable & (1u << i)) != 0 && (properties.MemoryTypes[i].PropertyFlags & wanted) == wanted)
				{
					return (uint)i;
				}
			}

			throw new InvalidOperationException("no memory type with " + wanted);
		}

		private void DestroySwapchainResources(bool swapchainToo)
		{
			foreach (Framebuffer framebuffer in framebuffers)
			{
				vk.DestroyFramebuffer(device, framebuffer, null);
			}

			foreach (ImageView view in views)
			{
				vk.DestroyImageView(device, view, null);
			}

			framebuffers = Array.Empty<Framebuffer>();
			views = Array.Empty<ImageView>();
			if (depthView.Handle != 0)
			{
				vk.DestroyImageView(device, depthView, null);
				vk.DestroyImage(device, depthImage, null);
				vk.FreeMemory(device, depthMemory, null);
				depthView = default;
				depthImage = default;
				depthMemory = default;
			}

			if (swapchainToo && swapchain.Handle != 0)
			{
				khrSwapchain.DestroySwapchain(device, swapchain, null);
				swapchain = default;
			}
		}

		private void RebuildSwapchain()
		{
			WaitIdle();
			DestroySwapchainResources(false);
			CreateSwapchain();
			rebuild = false;
			Rebuilds++;
		}

		/// <summary>How many times the swapchain was rebuilt (size or rotation changed), for the log.</summary>
		public int Rebuilds { get; private set; }

		/*
		 * Frames and uploads. Each frame slot has two command buffers, submitted together: uploads
		 * (texture copies and mipmaps, which cannot be recorded inside a render pass) and then the
		 * frame's drawing. Uploads may start before the frame does - a texture loaded between two
		 * frames goes into the next slot's upload buffer - and while the game loads, with no frames
		 * at all, FlushUploads submits them on their own. Every submission has a serial, and
		 * anything the GPU may still be reading is freed only once that serial completes (Defer).
		 */

		/// <summary>Waits until the current slot's previous submission is done, and frees what it held.</summary>
		private void EnsureSlotReady()
		{
			if (slotReady)
			{
				return;
			}

			Fence fence = inFlight[frame];
			vk.WaitForFences(device, 1, in fence, true, ulong.MaxValue);
			Completed(slotSerial[frame]);
			slotReady = true;
		}

		private void Completed(ulong serial)
		{
			if (serial > CompletedSerial)
			{
				CompletedSerial = serial;
			}

			int kept = 0;
			for (int i = 0; i < deferred.Count; i++)
			{
				if (deferred[i].Serial <= CompletedSerial)
				{
					deferred[i].Action();
				}
				else
				{
					deferred[kept++] = deferred[i];
				}
			}

			deferred.RemoveRange(kept, deferred.Count - kept);
		}

		/// <summary>Runs an action (a release) once the GPU is done with everything recorded so far.</summary>
		public void Defer(Action action)
		{
			deferred.Add((RecordingSerial, action));
		}

		/// <summary>Waits for the GPU to finish everything, and runs every deferred release.</summary>
		public void WaitIdle()
		{
			vk.DeviceWaitIdle(device);
			Completed(RecordingSerial - 1);
		}

		/// <summary>The command buffer for uploads (copies, mipmaps): recorded outside the render pass, run before the frame's drawing.</summary>
		public CommandBuffer BeginUploads()
		{
			EnsureSlotReady();
			CommandBuffer commands = uploadBuffers[frame];
			if (!uploadsRecording)
			{
				vk.ResetCommandBuffer(commands, 0);
				CommandBufferBeginInfo begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
				Check(vk.BeginCommandBuffer(commands, in begin), "vkBeginCommandBuffer");
				uploadsRecording = true;
			}

			return commands;
		}

		/// <summary>
		/// Outside a frame: submits the uploads recorded so far and waits for them, so that what
		/// they used can be freed (while loading, with no frames to carry them).
		/// </summary>
		public void FlushUploads()
		{
			if (!uploadsRecording || frameStarted)
			{
				return;
			}

			CommandBuffer commands = uploadBuffers[frame];
			Check(vk.EndCommandBuffer(commands), "vkEndCommandBuffer");
			uploadsRecording = false;
			Fence fence = inFlight[frame];
			vk.ResetFences(device, 1, in fence);
			SubmitInfo submit = new SubmitInfo
			{
				SType = StructureType.SubmitInfo,
				CommandBufferCount = 1,
				PCommandBuffers = &commands
			};
			Check(vk.QueueSubmit(queue, 1, in submit, fence), "vkQueueSubmit");
			slotSerial[frame] = RecordingSerial++;
			vk.WaitForFences(device, 1, in fence, true, ulong.MaxValue);
			Completed(slotSerial[frame]);
		}

		/// <summary>
		/// Starts a frame: waits for this frame slot and takes a swapchain image. The render pass
		/// begins separately (BeginRenderPass), with the clear values. False when there is nothing
		/// to draw on (no window, or it was rebuilt); try again later then.
		/// </summary>
		public bool BeginFrame()
		{
			if (frameStarted)
			{
				return true;
			}

			if (surface.Handle == 0)
			{
				return false;
			}

			if (rebuild)
			{
				RebuildSwapchain();
			}

			EnsureSlotReady();
			Result acquired = khrSwapchain.AcquireNextImage(device, swapchain, ulong.MaxValue, imageAvailable[frame], default, ref imageIndex);
			if (acquired == Result.ErrorOutOfDateKhr)
			{
				RebuildSwapchain();
				return false;
			}

			if (acquired != Result.Success && acquired != Result.SuboptimalKhr)
			{
				Check(acquired, "vkAcquireNextImageKHR");
			}

			CommandBuffer commands = commandBuffers[frame];
			vk.ResetCommandBuffer(commands, 0);
			CommandBufferBeginInfo begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
			Check(vk.BeginCommandBuffer(commands, in begin), "vkBeginCommandBuffer");
			frameStarted = true;
			renderPassStarted = false;
			return true;
		}

		/// <summary>Begins the frame's render pass, clearing to these values.</summary>
		public void BeginRenderPass(float red, float green, float blue, float alpha, float depth)
		{
			if (!frameStarted || renderPassStarted)
			{
				return;
			}

			ClearValue* clear = stackalloc ClearValue[2];
			clear[0] = new ClearValue(new ClearColorValue(red, green, blue, alpha));
			clear[1] = new ClearValue(default, new ClearDepthStencilValue(depth, 0));
			RenderPassBeginInfo pass = new RenderPassBeginInfo
			{
				SType = StructureType.RenderPassBeginInfo,
				RenderPass = renderPass,
				Framebuffer = framebuffers[imageIndex],
				RenderArea = new Rect2D(new Offset2D(0, 0), extent),
				ClearValueCount = 2,
				PClearValues = clear
			};
			vk.CmdBeginRenderPass(commandBuffers[frame], in pass, SubpassContents.Inline);
			renderPassStarted = true;
		}

		/// <summary>Ends the render pass, submits the frame (its uploads first) and presents it.</summary>
		public void EndFrame()
		{
			if (!frameStarted)
			{
				return;
			}

			if (!renderPassStarted)
			{
				BeginRenderPass(0.0f, 0.0f, 0.0f, 1.0f, 1.0f);
			}

			frameStarted = false;
			renderPassStarted = false;
			CommandBuffer commands = commandBuffers[frame];
			vk.CmdEndRenderPass(commands);
			Check(vk.EndCommandBuffer(commands), "vkEndCommandBuffer");
			CommandBuffer* buffers = stackalloc CommandBuffer[2];
			uint bufferCount = 0;
			if (uploadsRecording)
			{
				Check(vk.EndCommandBuffer(uploadBuffers[frame]), "vkEndCommandBuffer");
				buffers[bufferCount++] = uploadBuffers[frame];
				uploadsRecording = false;
			}

			buffers[bufferCount++] = commands;
			Semaphore wait = imageAvailable[frame];
			Semaphore signal = renderFinished[frame];
			PipelineStageFlags stage = PipelineStageFlags.ColorAttachmentOutputBit;
			SubmitInfo submit = new SubmitInfo
			{
				SType = StructureType.SubmitInfo,
				WaitSemaphoreCount = 1,
				PWaitSemaphores = &wait,
				PWaitDstStageMask = &stage,
				CommandBufferCount = bufferCount,
				PCommandBuffers = buffers,
				SignalSemaphoreCount = 1,
				PSignalSemaphores = &signal
			};
			Fence fence = inFlight[frame];
			vk.ResetFences(device, 1, in fence);
			Check(vk.QueueSubmit(queue, 1, in submit, fence), "vkQueueSubmit");
			slotSerial[frame] = RecordingSerial++;
			SwapchainKHR chain = swapchain;
			uint index = imageIndex;
			PresentInfoKHR present = new PresentInfoKHR
			{
				SType = StructureType.PresentInfoKhr,
				WaitSemaphoreCount = 1,
				PWaitSemaphores = &signal,
				SwapchainCount = 1,
				PSwapchains = &chain,
				PImageIndices = &index
			};
			Result presented = khrSwapchain.QueuePresent(queue, in present);
			if (presented == Result.ErrorOutOfDateKhr || presented == Result.SuboptimalKhr)
			{
				rebuild = true;
			}
			else if (presented != Result.Success)
			{
				Check(presented, "vkQueuePresentKHR");
			}

			frame = (frame + 1) % FramesInFlight;
			slotReady = false;
		}

		/*
		 * Pre-rotation, for the code that draws: the frame is laid out upright (Width x Height, as
		 * the game sees the window) and turned into the swapchain images' own orientation.
		 */

		/// <summary>
		/// The 2x2 matrix (row major: x' = X*x + Y*y, y' = Z*x + W*y) that takes OpenGL clip space x
		/// and y to the swapchain's Vulkan clip space: Y flipped (Vulkan's points down), then turned
		/// by Rotation.
		/// </summary>
		public System.Numerics.Vector4 ClipTransform
		{
			get
			{
				switch (Rotation)
				{
					case 90:
						// (x, y) -> (-y', x) with y' = -y
						return new System.Numerics.Vector4(0.0f, 1.0f, 1.0f, 0.0f);
					case 180:
						return new System.Numerics.Vector4(-1.0f, 0.0f, 0.0f, 1.0f);
					case 270:
						return new System.Numerics.Vector4(0.0f, -1.0f, -1.0f, 0.0f);
					default:
						return new System.Numerics.Vector4(1.0f, 0.0f, 0.0f, -1.0f);
				}
			}
		}

		/// <summary>
		/// An OpenGL window rectangle (origin bottom left, upright) as a rectangle of the swapchain
		/// image (origin top left, turned by Rotation).
		/// </summary>
		public Rect2D ToImageRect(int x, int y, int width, int height)
		{
			int logicalWidth = (int)Width, logicalHeight = (int)Height;
			// upright, origin top left
			int top = logicalHeight - (y + height);
			switch (Rotation)
			{
				case 90:
					return new Rect2D(new Offset2D(logicalHeight - top - height, x), new Extent2D((uint)Math.Max(0, height), (uint)Math.Max(0, width)));
				case 180:
					return new Rect2D(new Offset2D(logicalWidth - x - width, logicalHeight - top - height), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
				case 270:
					return new Rect2D(new Offset2D(top, logicalWidth - x - width), new Extent2D((uint)Math.Max(0, height), (uint)Math.Max(0, width)));
				default:
					return new Rect2D(new Offset2D(x, top), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
			}
		}

		/// <summary>Lets go of the window (it is going away); the device and everything on it stay.</summary>
		public void DetachWindow()
		{
			vk.DeviceWaitIdle(device);
			DestroySwapchainResources(true);
			if (surface.Handle != 0)
			{
				khrSurface.DestroySurface(instance, surface, null);
				surface = default;
			}
		}

		/// <summary>Draws on a new window (ANativeWindow*) from now on.</summary>
		public void AttachWindow(IntPtr nativeWindow)
		{
			CreateSurface(nativeWindow);
			khrSurface.GetPhysicalDeviceSurfaceSupport(physicalDevice, queueFamily, surface, out Bool32 present);
			if (!present)
			{
				throw new InvalidOperationException("the Vulkan queue cannot present to the new window");
			}

			CreateSwapchain();
		}

		/// <summary>Asks for the swapchain to be rebuilt before the next frame (the window changed size).</summary>
		public void WindowResized()
		{
			rebuild = true;
		}

		public void Dispose()
		{
			if (device.Handle != 0)
			{
				WaitIdle();
				DestroySwapchainResources(true);
				for (int i = 0; i < FramesInFlight; i++)
				{
					vk.DestroySemaphore(device, imageAvailable[i], null);
					vk.DestroySemaphore(device, renderFinished[i], null);
					vk.DestroyFence(device, inFlight[i], null);
				}

				vk.DestroyCommandPool(device, commandPool, null);
				if (renderPass.Handle != 0)
				{
					vk.DestroyRenderPass(device, renderPass, null);
				}

				vk.DestroyDevice(device, null);
				device = default;
			}

			if (surface.Handle != 0)
			{
				khrSurface.DestroySurface(instance, surface, null);
				surface = default;
			}

			if (instance.Handle != 0)
			{
				vk.DestroyInstance(instance, null);
				instance = default;
			}
		}
	}
}
