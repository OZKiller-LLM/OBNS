// OBNS: not an upstream file. What the Vulkan world layer's core Vulkan 1.0 tier needs beyond the rest of Vk (OBNS/VULKAN.md):
// render passes and framebuffers (in place of dynamic rendering), the device's 1.0 features, the instance's extensions,
// and the 1.1 queries' KHR forms (VK_KHR_get_physical_device_properties2) through vkGetInstanceProcAddr.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Vulkan
{
	internal static unsafe partial class Vk
	{
		internal const uint StructureTypeFramebufferCreateInfo = 37, StructureTypeRenderPassCreateInfo = 38, StructureTypeRenderPassBeginInfo = 43;

		// VkSubpassContents (the attachment operations are in Vk.cs)
		internal const uint SubpassContentsInline = 0;

		[StructLayout(LayoutKind.Sequential)]
		internal struct AttachmentDescription
		{
			public uint Flags, Format, Samples, LoadOp, StoreOp, StencilLoadOp, StencilStoreOp, InitialLayout, FinalLayout;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct AttachmentReference
		{
			public uint Attachment, Layout;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SubpassDescription
		{
			public uint Flags, PipelineBindPoint;
			public uint InputAttachmentCount;
			public AttachmentReference* PInputAttachments;
			public uint ColorAttachmentCount;
			public AttachmentReference* PColorAttachments;
			public AttachmentReference* PResolveAttachments;
			public AttachmentReference* PDepthStencilAttachment;
			public uint PreserveAttachmentCount;
			public uint* PPreserveAttachments;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderPassCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint AttachmentCount;
			public AttachmentDescription* PAttachments;
			public uint SubpassCount;
			public SubpassDescription* PSubpasses;
			public uint DependencyCount;
			public void* PDependencies;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct FramebufferCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public ulong RenderPass;
			public uint AttachmentCount;
			public ulong* PAttachments;
			public uint Width, Height, Layers;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderPassBeginInfo
		{
			public uint SType;
			public void* PNext;
			public ulong RenderPass;
			public ulong Framebuffer;
			public Rect2D RenderArea;
			public uint ClearValueCount;
			public ClearValue* PClearValues;
		}

		[DllImport(Library)] internal static extern int vkCreateRenderPass(IntPtr device, RenderPassCreateInfo* createInfo, void* allocator, out ulong renderPass);
		[DllImport(Library)] internal static extern void vkDestroyRenderPass(IntPtr device, ulong renderPass, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateFramebuffer(IntPtr device, FramebufferCreateInfo* createInfo, void* allocator, out ulong framebuffer);
		[DllImport(Library)] internal static extern void vkDestroyFramebuffer(IntPtr device, ulong framebuffer, void* allocator);
		[DllImport(Library)] internal static extern void vkCmdBeginRenderPass(IntPtr commandBuffer, RenderPassBeginInfo* beginInfo, uint contents);
		[DllImport(Library)] internal static extern void vkCmdEndRenderPass(IntPtr commandBuffer);
		/// <summary>VkPhysicalDeviceFeatures: 55 VkBool32s.</summary>
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceFeatures(IntPtr physicalDevice, uint* features);
		[DllImport(Library)] internal static extern IntPtr vkGetInstanceProcAddr(IntPtr instance, string name);
		[DllImport(Library)] internal static extern int vkEnumerateInstanceExtensionProperties(IntPtr layerName, ref uint count, byte* properties);

		[UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GetPhysicalDeviceProperties2KHR(IntPtr physicalDevice, PhysicalDeviceProperties2* properties);

		internal static T InstanceFunction<T>(IntPtr instance, string name) where T : class
		{
			IntPtr address = vkGetInstanceProcAddr(instance, name);
			return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
		}

		/// <summary>The instance extensions the loader has (VkExtensionProperties: a 256-byte name and a version).</summary>
		internal static HashSet<string> InstanceExtensions()
		{
			const int size = 260;
			uint count = 0;
			vkEnumerateInstanceExtensionProperties(IntPtr.Zero, ref count, null);
			byte[] buffer = new byte[Math.Max(count, 1) * size];
			HashSet<string> names = new HashSet<string>();
			fixed (byte* properties = buffer)
			{
				vkEnumerateInstanceExtensionProperties(IntPtr.Zero, ref count, properties);
				for (int i = 0; i < count; i++)
				{
					names.Add(Marshal.PtrToStringAnsi((IntPtr)(properties + i * size)));
				}
			}

			return names;
		}

		/// <summary>A render pass of one subpass: a colour attachment (if any) and a depth one, both kept in the layouts the layer's barriers set.</summary>
		internal static ulong CreateRenderPass(IntPtr device, uint colorFormat, uint depthFormat, bool keepDepth)
		{
			AttachmentDescription* attachments = stackalloc AttachmentDescription[2];
			int count = 0;
			AttachmentReference color = new AttachmentReference { Attachment = 0, Layout = ImageLayoutColorAttachmentOptimal };
			if (colorFormat != 0)
			{
				attachments[count++] = new AttachmentDescription
				{
					Format = colorFormat, Samples = 1, LoadOp = AttachmentLoadOpClear, StoreOp = AttachmentStoreOpStore,
					StencilLoadOp = AttachmentLoadOpDontCare, StencilStoreOp = AttachmentStoreOpDontCare,
					InitialLayout = ImageLayoutColorAttachmentOptimal, FinalLayout = ImageLayoutColorAttachmentOptimal
				};
			}

			AttachmentReference depth = new AttachmentReference { Attachment = (uint)count, Layout = ImageLayoutDepthStencilAttachmentOptimal };
			attachments[count++] = new AttachmentDescription
			{
				Format = depthFormat, Samples = 1, LoadOp = AttachmentLoadOpClear, StoreOp = keepDepth ? AttachmentStoreOpStore : AttachmentStoreOpDontCare,
				StencilLoadOp = AttachmentLoadOpDontCare, StencilStoreOp = AttachmentStoreOpDontCare,
				InitialLayout = ImageLayoutDepthStencilAttachmentOptimal, FinalLayout = ImageLayoutDepthStencilAttachmentOptimal
			};
			SubpassDescription subpass = new SubpassDescription
			{
				ColorAttachmentCount = colorFormat != 0 ? 1u : 0u,
				PColorAttachments = colorFormat != 0 ? &color : null,
				PDepthStencilAttachment = &depth
			};
			RenderPassCreateInfo info = new RenderPassCreateInfo { SType = StructureTypeRenderPassCreateInfo, AttachmentCount = (uint)count, PAttachments = attachments, SubpassCount = 1, PSubpasses = &subpass };
			Check(vkCreateRenderPass(device, &info, null, out ulong renderPass), "vkCreateRenderPass");
			return renderPass;
		}

		/// <summary>A framebuffer of a render pass: its attachments' views, at a size.</summary>
		internal static ulong CreateFramebuffer(IntPtr device, ulong renderPass, ulong colorView, ulong depthView, uint width, uint height)
		{
			ulong* views = stackalloc ulong[2];
			uint count = 0;
			if (colorView != 0)
			{
				views[count++] = colorView;
			}

			views[count++] = depthView;
			FramebufferCreateInfo info = new FramebufferCreateInfo { SType = StructureTypeFramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = count, PAttachments = views, Width = width, Height = height, Layers = 1 };
			Check(vkCreateFramebuffer(device, &info, null, out ulong framebuffer), "vkCreateFramebuffer");
			return framebuffer;
		}

		/// <summary>Begins a render pass over the whole framebuffer, its attachments cleared: the colour (if it has one), then depth 1.</summary>
		internal static void BeginRenderPass(IntPtr commandBuffer, ulong renderPass, ulong framebuffer, uint width, uint height, ClearValue? color)
		{
			ClearValue* clears = stackalloc ClearValue[2];
			uint count = 0;
			if (color.HasValue)
			{
				clears[count++] = color.Value;
			}

			clears[count++] = new ClearValue { Depth = 1.0f };
			RenderPassBeginInfo info = new RenderPassBeginInfo
			{
				SType = StructureTypeRenderPassBeginInfo,
				RenderPass = renderPass,
				Framebuffer = framebuffer,
				RenderArea = new Rect2D { Extent = new Extent2D { Width = width, Height = height } },
				ClearValueCount = count,
				PClearValues = clears
			};
			vkCmdBeginRenderPass(commandBuffer, &info, SubpassContentsInline);
		}
	}
}
