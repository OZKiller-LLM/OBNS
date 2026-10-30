// OBNS: not an upstream file. The Vulkan API the desktop's world renderer uses (OBNS/VULKAN.md), bound by hand:
// only what is used, no package dependencies, so it runs under .NET Framework and Mono alike. Structures follow
// vulkan_core.h; their field names are Silk.NET's, so their layouts can be checked against it.

using System;
using System.Runtime.InteropServices;
using System.Security;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>Vulkan's functions, structures and constants, as far as the world renderer needs them.</summary>
	[SuppressUnmanagedCodeSecurity]
	internal static unsafe partial class Vk
	{
		private const string Library = "vulkan-1";

		// --- constants ---

		internal const int Success = 0, NotReady = 1, Timeout = 2, Incomplete = 5;
		internal const uint QueueFamilyIgnored = ~0u, QueueFamilyExternal = ~0u - 1;
		internal const uint WholeSize = ~0u;

		internal static uint MakeVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;

		// Structure types.
		internal const uint StructureTypeApplicationInfo = 0;
		internal const uint StructureTypeInstanceCreateInfo = 1;
		internal const uint StructureTypeDeviceQueueCreateInfo = 2;
		internal const uint StructureTypeDeviceCreateInfo = 3;
		internal const uint StructureTypeSubmitInfo = 4;
		internal const uint StructureTypeMemoryAllocateInfo = 5;
		internal const uint StructureTypeFenceCreateInfo = 8;
		internal const uint StructureTypeSemaphoreCreateInfo = 9;
		internal const uint StructureTypeImageCreateInfo = 14;
		internal const uint StructureTypeImageViewCreateInfo = 15;
		internal const uint StructureTypeCommandPoolCreateInfo = 39;
		internal const uint StructureTypeCommandBufferAllocateInfo = 40;
		internal const uint StructureTypeCommandBufferBeginInfo = 42;
		internal const uint StructureTypeImageMemoryBarrier = 45;
		internal const uint StructureTypePhysicalDeviceFeatures2 = 1000059000;
		internal const uint StructureTypePhysicalDeviceProperties2 = 1000059001;
		internal const uint StructureTypePhysicalDeviceIdProperties = 1000071004;
		internal const uint StructureTypeExternalMemoryImageCreateInfo = 1000072001;
		internal const uint StructureTypeExportMemoryAllocateInfo = 1000072002;
		internal const uint StructureTypeMemoryGetWin32HandleInfoKhr = 1000073003;
		internal const uint StructureTypeExportSemaphoreCreateInfo = 1000077000;
		internal const uint StructureTypeSemaphoreGetWin32HandleInfoKhr = 1000078003;
		internal const uint StructureTypeMemoryDedicatedAllocateInfo = 1000127001;
		internal const uint StructureTypePhysicalDeviceVulkan12Features = 51;
		internal const uint StructureTypePhysicalDeviceVulkan13Features = 53;
		internal const uint StructureTypeRenderingInfo = 1000044000;
		internal const uint StructureTypeRenderingAttachmentInfo = 1000044001;

		// Formats.
		internal const uint FormatR8G8B8A8Unorm = 37;
		internal const uint FormatD32Sfloat = 126;

		// Image layouts.
		internal const uint ImageLayoutUndefined = 0;
		internal const uint ImageLayoutGeneral = 1;
		internal const uint ImageLayoutColorAttachmentOptimal = 2;
		internal const uint ImageLayoutDepthStencilAttachmentOptimal = 3;
		internal const uint ImageLayoutDepthStencilReadOnlyOptimal = 4;
		internal const uint ImageLayoutShaderReadOnlyOptimal = 5;
		internal const uint ImageLayoutTransferDstOptimal = 7;

		// Image usage, aspects.
		internal const uint ImageUsageTransferSrc = 0x1, ImageUsageTransferDst = 0x2, ImageUsageSampled = 0x4, ImageUsageColorAttachment = 0x10, ImageUsageDepthStencilAttachment = 0x20;
		internal const uint ImageAspectColor = 0x1, ImageAspectDepth = 0x2;

		// Memory properties.
		internal const uint MemoryPropertyDeviceLocal = 0x1, MemoryPropertyHostVisible = 0x2, MemoryPropertyHostCoherent = 0x4;

		// External handles.
		internal const uint ExternalMemoryHandleTypeOpaqueWin32 = 0x2;
		internal const uint ExternalSemaphoreHandleTypeOpaqueWin32 = 0x2;
		internal const uint ExternalMemoryHandleTypeOpaqueFd = 0x1;
		internal const uint ExternalSemaphoreHandleTypeOpaqueFd = 0x1;
		internal const uint StructureTypeMemoryGetFdInfoKhr = 1000074002;
		internal const uint StructureTypeSemaphoreGetFdInfoKhr = 1000079001;

		// Pipeline stages, access.
		internal const uint PipelineStageTopOfPipe = 0x1, PipelineStageEarlyFragmentTests = 0x100, PipelineStageLateFragmentTests = 0x200,
			PipelineStageColorAttachmentOutput = 0x400, PipelineStageBottomOfPipe = 0x2000, PipelineStageAllCommands = 0x10000;
		internal const uint AccessColorAttachmentWrite = 0x100, AccessDepthStencilAttachmentRead = 0x200, AccessDepthStencilAttachmentWrite = 0x400, AccessShaderRead = 0x20;

		// Attachments.
		internal const uint AttachmentLoadOpLoad = 0, AttachmentLoadOpClear = 1, AttachmentLoadOpDontCare = 2;
		internal const uint AttachmentStoreOpStore = 0, AttachmentStoreOpDontCare = 1;

		// Queues, command pools and buffers.
		internal const uint QueueGraphics = 0x1;
		internal const uint CommandPoolCreateResetCommandBuffer = 0x2;
		internal const uint CommandBufferUsageOneTimeSubmit = 0x1;
		internal const uint FenceCreateSignaled = 0x1;

		// Physical device types.
		internal const uint PhysicalDeviceTypeIntegratedGpu = 1, PhysicalDeviceTypeDiscreteGpu = 2;

		// --- structures ---

		[StructLayout(LayoutKind.Sequential)]
		internal struct ApplicationInfo
		{
			public uint SType;
			public void* PNext;
			public byte* PApplicationName;
			public uint ApplicationVersion;
			public byte* PEngineName;
			public uint EngineVersion;
			public uint ApiVersion;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct InstanceCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public ApplicationInfo* PApplicationInfo;
			public uint EnabledLayerCount;
			public byte** PpEnabledLayerNames;
			public uint EnabledExtensionCount;
			public byte** PpEnabledExtensionNames;
		}

		/// <summary>VkPhysicalDeviceProperties: the fields read here, then the rest (limits, sparse properties) as bytes.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceProperties
		{
			public uint ApiVersion;
			public uint DriverVersion;
			public uint VendorID;
			public uint DeviceID;
			public uint DeviceType;
			public fixed byte DeviceName[256];
			public fixed byte PipelineCacheUuid[16];
			/// <summary>Padding to VkPhysicalDeviceLimits' 8-byte alignment, the limits (504 bytes) and VkPhysicalDeviceSparseProperties (20).</summary>
			public fixed byte LimitsAndSparseProperties[532];
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceProperties2
		{
			public uint SType;
			public void* PNext;
			public PhysicalDeviceProperties Properties;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceIDProperties
		{
			public uint SType;
			public void* PNext;
			public fixed byte DeviceUuid[16];
			public fixed byte DriverUuid[16];
			public fixed byte DeviceLuid[8];
			public uint DeviceNodeMask;
			public uint DeviceLuidvalid;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Extent3D
		{
			public uint Width, Height, Depth;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct QueueFamilyProperties
		{
			public uint QueueFlags;
			public uint QueueCount;
			public uint TimestampValidBits;
			public Extent3D MinImageTransferGranularity;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryType
		{
			public uint PropertyFlags;
			public uint HeapIndex;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryHeap
		{
			public ulong Size;
			public uint Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceMemoryProperties
		{
			public uint MemoryTypeCount;
			/// <summary>32 VkMemoryType (two uints each).</summary>
			public fixed uint MemoryTypes[64];
			public uint MemoryHeapCount;
			/// <summary>16 VkMemoryHeap (a ulong and a uint, padded to 16 bytes each).</summary>
			public fixed ulong MemoryHeaps[32];
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DeviceQueueCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint QueueFamilyIndex;
			public uint QueueCount;
			public float* PQueuePriorities;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DeviceCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint QueueCreateInfoCount;
			public DeviceQueueCreateInfo* PQueueCreateInfos;
			public uint EnabledLayerCount;
			public byte** PpEnabledLayerNames;
			public uint EnabledExtensionCount;
			public byte** PpEnabledExtensionNames;
			public void* PEnabledFeatures;
		}

		/// <summary>VkPhysicalDeviceVulkan12Features: 47 VkBool32s, by the indices below.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceVulkan12Features
		{
			public uint SType;
			public void* PNext;
			public fixed uint Features[47];

			internal const int DescriptorIndexing = 9, ShaderSampledImageArrayNonUniformIndexing = 14, DescriptorBindingSampledImageUpdateAfterBind = 21,
				DescriptorBindingUpdateUnusedWhilePending = 26, DescriptorBindingPartiallyBound = 27, DescriptorBindingVariableDescriptorCount = 28,
				RuntimeDescriptorArray = 29, ScalarBlockLayout = 31, TimelineSemaphore = 37;
		}

		/// <summary>VkPhysicalDeviceVulkan13Features: 15 VkBool32s, by the indices below.</summary>
		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceVulkan13Features
		{
			public uint SType;
			public void* PNext;
			public fixed uint Features[15];

			internal const int ShaderDemoteToHelperInvocation = 5, Synchronization2 = 9, DynamicRendering = 12;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PhysicalDeviceFeatures2
		{
			public uint SType;
			public void* PNext;
			/// <summary>VkPhysicalDeviceFeatures: 55 VkBool32s.</summary>
			public fixed uint Features[55];
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint ImageType;
			public uint Format;
			public Extent3D Extent;
			public uint MipLevels;
			public uint ArrayLayers;
			public uint Samples;
			public uint Tiling;
			public uint Usage;
			public uint SharingMode;
			public uint QueueFamilyIndexCount;
			public uint* PQueueFamilyIndices;
			public uint InitialLayout;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ExternalMemoryImageCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint HandleTypes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryRequirements
		{
			public ulong Size;
			public ulong Alignment;
			public uint MemoryTypeBits;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public ulong AllocationSize;
			public uint MemoryTypeIndex;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ExportMemoryAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public uint HandleTypes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryDedicatedAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public ulong Image;
			public ulong Buffer;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryGetWin32HandleInfoKHR
		{
			public uint SType;
			public void* PNext;
			public ulong Memory;
			public uint HandleType;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ComponentMapping
		{
			public uint R, G, B, A;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageSubresourceRange
		{
			public uint AspectMask;
			public uint BaseMipLevel;
			public uint LevelCount;
			public uint BaseArrayLayer;
			public uint LayerCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageViewCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public ulong Image;
			public uint ViewType;
			public uint Format;
			public ComponentMapping Components;
			public ImageSubresourceRange SubresourceRange;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SemaphoreCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ExportSemaphoreCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint HandleTypes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SemaphoreGetWin32HandleInfoKHR
		{
			public uint SType;
			public void* PNext;
			public ulong Semaphore;
			public uint HandleType;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct FenceCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct CommandPoolCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint QueueFamilyIndex;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct CommandBufferAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public ulong CommandPool;
			public uint Level;
			public uint CommandBufferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct CommandBufferBeginInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public void* PInheritanceInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageMemoryBarrier
		{
			public uint SType;
			public void* PNext;
			public uint SrcAccessMask;
			public uint DstAccessMask;
			public uint OldLayout;
			public uint NewLayout;
			public uint SrcQueueFamilyIndex;
			public uint DstQueueFamilyIndex;
			public ulong Image;
			public ImageSubresourceRange SubresourceRange;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SubmitInfo
		{
			public uint SType;
			public void* PNext;
			public uint WaitSemaphoreCount;
			public ulong* PWaitSemaphores;
			public uint* PWaitDstStageMask;
			public uint CommandBufferCount;
			public IntPtr* PCommandBuffers;
			public uint SignalSemaphoreCount;
			public ulong* PSignalSemaphores;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Offset2D
		{
			public int X, Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Extent2D
		{
			public uint Width, Height;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Rect2D
		{
			public Offset2D Offset;
			public Extent2D Extent;
		}

		/// <summary>VkClearValue: a colour (four floats) or a depth and stencil value.</summary>
		[StructLayout(LayoutKind.Explicit, Size = 16)]
		internal struct ClearValue
		{
			[FieldOffset(0)] public float R;
			[FieldOffset(4)] public float G;
			[FieldOffset(8)] public float B;
			[FieldOffset(12)] public float A;
			[FieldOffset(0)] public float Depth;
			[FieldOffset(4)] public uint Stencil;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderingAttachmentInfo
		{
			public uint SType;
			public void* PNext;
			public ulong ImageView;
			public uint ImageLayout;
			public uint ResolveMode;
			public ulong ResolveImageView;
			public uint ResolveImageLayout;
			public uint LoadOp;
			public uint StoreOp;
			public ClearValue ClearValue;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderingInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public Rect2D RenderArea;
			public uint LayerCount;
			public uint ViewMask;
			public uint ColorAttachmentCount;
			public RenderingAttachmentInfo* PColorAttachments;
			public RenderingAttachmentInfo* PDepthAttachment;
			public RenderingAttachmentInfo* PStencilAttachment;
		}

		// --- functions: the loader's exports ---

		[DllImport(Library)] internal static extern int vkCreateInstance(InstanceCreateInfo* createInfo, void* allocator, out IntPtr instance);
		[DllImport(Library)] internal static extern void vkDestroyInstance(IntPtr instance, void* allocator);
		[DllImport(Library)] internal static extern int vkEnumerateInstanceVersion(out uint apiVersion);
		[DllImport(Library)] internal static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr* devices);
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceProperties(IntPtr device, PhysicalDeviceProperties* properties);
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceProperties2(IntPtr physicalDevice, PhysicalDeviceProperties2* properties);
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceFeatures2(IntPtr physicalDevice, PhysicalDeviceFeatures2* features);
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceQueueFamilyProperties(IntPtr physicalDevice, ref uint count, QueueFamilyProperties* properties);
		[DllImport(Library)] internal static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr physicalDevice, PhysicalDeviceMemoryProperties* properties);
		[DllImport(Library)] internal static extern int vkEnumerateDeviceExtensionProperties(IntPtr physicalDevice, IntPtr layerName, ref uint count, byte* properties);
		[DllImport(Library)] internal static extern int vkCreateDevice(IntPtr physicalDevice, DeviceCreateInfo* createInfo, void* allocator, out IntPtr device);
		[DllImport(Library)] internal static extern void vkDestroyDevice(IntPtr device, void* allocator);
		[DllImport(Library)] internal static extern IntPtr vkGetDeviceProcAddr(IntPtr device, string name);
		[DllImport(Library)] internal static extern void vkGetDeviceQueue(IntPtr device, uint queueFamilyIndex, uint queueIndex, out IntPtr queue);
		[DllImport(Library)] internal static extern int vkDeviceWaitIdle(IntPtr device);

		[DllImport(Library)] internal static extern int vkCreateImage(IntPtr device, ImageCreateInfo* createInfo, void* allocator, out ulong image);
		[DllImport(Library)] internal static extern void vkDestroyImage(IntPtr device, ulong image, void* allocator);
		[DllImport(Library)] internal static extern void vkGetImageMemoryRequirements(IntPtr device, ulong image, out MemoryRequirements requirements);
		[DllImport(Library)] internal static extern int vkAllocateMemory(IntPtr device, MemoryAllocateInfo* allocateInfo, void* allocator, out ulong memory);
		[DllImport(Library)] internal static extern void vkFreeMemory(IntPtr device, ulong memory, void* allocator);
		[DllImport(Library)] internal static extern int vkBindImageMemory(IntPtr device, ulong image, ulong memory, ulong offset);
		[DllImport(Library)] internal static extern int vkCreateImageView(IntPtr device, ImageViewCreateInfo* createInfo, void* allocator, out ulong view);
		[DllImport(Library)] internal static extern void vkDestroyImageView(IntPtr device, ulong view, void* allocator);

		[DllImport(Library)] internal static extern int vkCreateSemaphore(IntPtr device, SemaphoreCreateInfo* createInfo, void* allocator, out ulong semaphore);
		[DllImport(Library)] internal static extern void vkDestroySemaphore(IntPtr device, ulong semaphore, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateFence(IntPtr device, FenceCreateInfo* createInfo, void* allocator, out ulong fence);
		[DllImport(Library)] internal static extern void vkDestroyFence(IntPtr device, ulong fence, void* allocator);
		[DllImport(Library)] internal static extern int vkWaitForFences(IntPtr device, uint count, ulong* fences, uint waitAll, ulong timeout);
		[DllImport(Library)] internal static extern int vkResetFences(IntPtr device, uint count, ulong* fences);

		[DllImport(Library)] internal static extern int vkCreateCommandPool(IntPtr device, CommandPoolCreateInfo* createInfo, void* allocator, out ulong pool);
		[DllImport(Library)] internal static extern void vkDestroyCommandPool(IntPtr device, ulong pool, void* allocator);
		[DllImport(Library)] internal static extern int vkAllocateCommandBuffers(IntPtr device, CommandBufferAllocateInfo* allocateInfo, IntPtr* commandBuffers);
		[DllImport(Library)] internal static extern int vkBeginCommandBuffer(IntPtr commandBuffer, CommandBufferBeginInfo* beginInfo);
		[DllImport(Library)] internal static extern int vkEndCommandBuffer(IntPtr commandBuffer);
		[DllImport(Library)] internal static extern int vkResetCommandBuffer(IntPtr commandBuffer, uint flags);
		[DllImport(Library)] internal static extern void vkCmdBeginRendering(IntPtr commandBuffer, RenderingInfo* renderingInfo);
		[DllImport(Library)] internal static extern void vkCmdEndRendering(IntPtr commandBuffer);
		[DllImport(Library)] internal static extern int vkQueueSubmit(IntPtr queue, uint submitCount, SubmitInfo* submits, ulong fence);

		// --- functions: device extensions, through vkGetDeviceProcAddr ---

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		internal delegate int GetMemoryWin32HandleKHR(IntPtr device, MemoryGetWin32HandleInfoKHR* info, out IntPtr handle);

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		internal delegate int GetSemaphoreWin32HandleKHR(IntPtr device, SemaphoreGetWin32HandleInfoKHR* info, out IntPtr handle);

		// VkMemoryGetFdInfoKHR and VkSemaphoreGetFdInfoKHR are laid out as the Win32 forms (sType, pNext, object, type).
		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		internal delegate int GetMemoryFdKHR(IntPtr device, MemoryGetWin32HandleInfoKHR* info, out int fd);

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		internal delegate int GetSemaphoreFdKHR(IntPtr device, SemaphoreGetWin32HandleInfoKHR* info, out int fd);

		/// <summary>A device function, or null when the device does not have it.</summary>
		internal static T DeviceFunction<T>(IntPtr device, string name) where T : class
		{
			IntPtr address = vkGetDeviceProcAddr(device, name);
			return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
		}

		/// <summary>Throws when a call failed.</summary>
		internal static void Check(int result, string call)
		{
			if (result < 0)
			{
				throw new InvalidOperationException(call + " failed (VkResult " + result + ")");
			}
		}
	}
}
