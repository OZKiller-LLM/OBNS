// OBNS: not an upstream file. The desktop world renderer's Vulkan device (OBNS/VULKAN.md).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>The Vulkan instance and device the world layer is drawn with: the GPU OpenGL is drawing on.</summary>
	internal sealed unsafe class VulkanDevice : IDisposable
	{
		internal IntPtr Instance { get; private set; }
		internal IntPtr PhysicalDevice { get; private set; }
		internal IntPtr Device { get; private set; }
		internal IntPtr Queue { get; private set; }
		internal uint QueueFamily { get; private set; }

		/// <summary>Held while submitting to the queue: the frame's thread and the main thread (uploads) both submit.</summary>
		internal readonly object QueueLock = new object();

		/// <summary>The device's name and Vulkan version, for the log.</summary>
		internal string Description { get; private set; }

		/// <summary>Nanoseconds per timestamp tick (VkPhysicalDeviceLimits.timestampPeriod), and whether the queue has timestamps.</summary>
		internal float TimestampPeriod { get; private set; }
		internal bool Timestamps { get; private set; }

		internal Vk.GetMemoryWin32HandleKHR GetMemoryWin32Handle { get; private set; }
		internal Vk.GetSemaphoreWin32HandleKHR GetSemaphoreWin32Handle { get; private set; }

		private Vk.PhysicalDeviceMemoryProperties memoryProperties;

		/// <summary>The device extensions the renderer needs (external memory and semaphores are core in Vulkan 1.1).</summary>
		private static readonly string[] requiredExtensions = { "VK_KHR_external_memory_win32", "VK_KHR_external_semaphore_win32" };

		private VulkanDevice()
		{
		}

		/// <summary>Creates the device on the GPU whose UUID OpenGL reports; null, with the reason, when it cannot be used.</summary>
		internal static VulkanDevice Create(byte[] glDeviceUuid, out string reason)
		{
			VulkanDevice device = new VulkanDevice();
			try
			{
				reason = device.Initialize(glDeviceUuid);
			}
			catch (DllNotFoundException)
			{
				reason = "no Vulkan loader (vulkan-1)";
			}
			catch (EntryPointNotFoundException ex)
			{
				reason = "the Vulkan loader is too old (" + ex.Message + ")";
			}
			catch (Exception ex)
			{
				reason = ex.Message;
			}

			if (reason != null)
			{
				device.Dispose();
				return null;
			}

			return device;
		}

		private string Initialize(byte[] glDeviceUuid)
		{
			Vk.vkEnumerateInstanceVersion(out uint loaderVersion);
			if (loaderVersion < Vk.MakeVersion(1, 3, 0))
			{
				return "the Vulkan loader is version " + VersionText(loaderVersion) + "; 1.3 is needed";
			}

			// The instance.
			IntPtr name = Marshal.StringToHGlobalAnsi("OpenBVE (OBNS)");
			try
			{
				Vk.ApplicationInfo application = new Vk.ApplicationInfo
				{
					SType = Vk.StructureTypeApplicationInfo,
					PApplicationName = (byte*)name,
					PEngineName = (byte*)name,
					ApiVersion = Vk.MakeVersion(1, 3, 0)
				};
				Vk.InstanceCreateInfo instanceInfo = new Vk.InstanceCreateInfo
				{
					SType = Vk.StructureTypeInstanceCreateInfo,
					PApplicationInfo = &application
				};
				Vk.Check(Vk.vkCreateInstance(&instanceInfo, null, out IntPtr instance), "vkCreateInstance");
				Instance = instance;
			}
			finally
			{
				Marshal.FreeHGlobal(name);
			}

			// The physical device OpenGL is on: the same device UUID.
			uint count = 0;
			Vk.Check(Vk.vkEnumeratePhysicalDevices(Instance, ref count, null), "vkEnumeratePhysicalDevices");
			IntPtr* devices = stackalloc IntPtr[(int)Math.Max(count, 1)];
			Vk.Check(Vk.vkEnumeratePhysicalDevices(Instance, ref count, devices), "vkEnumeratePhysicalDevices");
			List<string> seen = new List<string>();
			for (int i = 0; i < count; i++)
			{
				Vk.PhysicalDeviceIDProperties id = new Vk.PhysicalDeviceIDProperties { SType = Vk.StructureTypePhysicalDeviceIdProperties };
				Vk.PhysicalDeviceProperties2 properties = new Vk.PhysicalDeviceProperties2 { SType = Vk.StructureTypePhysicalDeviceProperties2, PNext = &id };
				Vk.vkGetPhysicalDeviceProperties2(devices[i], &properties);
				string deviceName = Marshal.PtrToStringAnsi((IntPtr)properties.Properties.DeviceName);
				seen.Add(deviceName);
				bool same = glDeviceUuid != null && glDeviceUuid.Length == 16;
				for (int b = 0; same && b < 16; b++)
				{
					same = id.DeviceUuid[b] == glDeviceUuid[b];
				}

				if (same)
				{
					PhysicalDevice = devices[i];
					Description = deviceName + ", Vulkan " + VersionText(properties.Properties.ApiVersion);
					// timestampPeriod: 428 bytes into the limits as they are kept here (720 into VkPhysicalDeviceProperties).
					TimestampPeriod = *(float*)(properties.Properties.LimitsAndSparseProperties + 428);
					if (properties.Properties.ApiVersion < Vk.MakeVersion(1, 3, 0))
					{
						return deviceName + " has Vulkan " + VersionText(properties.Properties.ApiVersion) + "; 1.3 is needed";
					}

					break;
				}
			}

			if (PhysicalDevice == IntPtr.Zero)
			{
				return "no Vulkan device is the GPU OpenGL is on (Vulkan has " + (seen.Count == 0 ? "none" : string.Join(", ", seen)) + ")";
			}

			// Extensions.
			HashSet<string> available = DeviceExtensions(PhysicalDevice);
			foreach (string extension in requiredExtensions)
			{
				if (!available.Contains(extension))
				{
					return "the device has no " + extension;
				}
			}

			// Features: what the renderer uses, if the device has it.
			Vk.PhysicalDeviceVulkan13Features features13 = new Vk.PhysicalDeviceVulkan13Features { SType = Vk.StructureTypePhysicalDeviceVulkan13Features };
			Vk.PhysicalDeviceVulkan12Features features12 = new Vk.PhysicalDeviceVulkan12Features { SType = Vk.StructureTypePhysicalDeviceVulkan12Features, PNext = &features13 };
			Vk.PhysicalDeviceFeatures2 features = new Vk.PhysicalDeviceFeatures2 { SType = Vk.StructureTypePhysicalDeviceFeatures2, PNext = &features12 };
			Vk.vkGetPhysicalDeviceFeatures2(PhysicalDevice, &features);
			int[] needed12 =
			{
				Vk.PhysicalDeviceVulkan12Features.DescriptorIndexing, Vk.PhysicalDeviceVulkan12Features.ShaderSampledImageArrayNonUniformIndexing,
				Vk.PhysicalDeviceVulkan12Features.DescriptorBindingSampledImageUpdateAfterBind, Vk.PhysicalDeviceVulkan12Features.DescriptorBindingUpdateUnusedWhilePending,
				Vk.PhysicalDeviceVulkan12Features.DescriptorBindingPartiallyBound, Vk.PhysicalDeviceVulkan12Features.DescriptorBindingVariableDescriptorCount,
				Vk.PhysicalDeviceVulkan12Features.RuntimeDescriptorArray, Vk.PhysicalDeviceVulkan12Features.ScalarBlockLayout
			};
			foreach (int feature in needed12)
			{
				if (features12.Features[feature] == 0)
				{
					return "the device lacks Vulkan 1.2 feature " + feature + " (descriptor indexing)";
				}
			}

			if (features13.Features[Vk.PhysicalDeviceVulkan13Features.DynamicRendering] == 0 || features13.Features[Vk.PhysicalDeviceVulkan13Features.Synchronization2] == 0 ||
			    features13.Features[Vk.PhysicalDeviceVulkan13Features.ShaderDemoteToHelperInvocation] == 0)
			{
				return "the device lacks dynamic rendering";
			}

			// Only those enabled (the rest left off).
			Vk.PhysicalDeviceVulkan13Features enable13 = new Vk.PhysicalDeviceVulkan13Features { SType = Vk.StructureTypePhysicalDeviceVulkan13Features };
			enable13.Features[Vk.PhysicalDeviceVulkan13Features.DynamicRendering] = 1;
			enable13.Features[Vk.PhysicalDeviceVulkan13Features.Synchronization2] = 1;
			// glslang compiles discard (Vulkan 1.3) to a demote, which needs this.
			enable13.Features[Vk.PhysicalDeviceVulkan13Features.ShaderDemoteToHelperInvocation] = 1;
			Vk.PhysicalDeviceVulkan12Features enable12 = new Vk.PhysicalDeviceVulkan12Features { SType = Vk.StructureTypePhysicalDeviceVulkan12Features, PNext = &enable13 };
			foreach (int feature in needed12)
			{
				enable12.Features[feature] = 1;
			}

			Vk.PhysicalDeviceFeatures2 enable = new Vk.PhysicalDeviceFeatures2 { SType = Vk.StructureTypePhysicalDeviceFeatures2, PNext = &enable12 };
			// VkPhysicalDeviceFeatures: indirect draws of many commands with their own first instance (9, 10), which the
			// renderer needs, and samplerAnisotropy (19) when the device has it.
			const int multiDrawIndirect = 9, drawIndirectFirstInstance = 10, samplerAnisotropy = 19;
			if (features.Features[multiDrawIndirect] == 0 || features.Features[drawIndirectFirstInstance] == 0)
			{
				return "the device lacks multiDrawIndirect";
			}

			enable.Features[multiDrawIndirect] = 1;
			enable.Features[drawIndirectFirstInstance] = 1;
			enable.Features[samplerAnisotropy] = features.Features[samplerAnisotropy];

			// A graphics queue.
			uint familyCount = 0;
			Vk.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref familyCount, null);
			Vk.QueueFamilyProperties* families = stackalloc Vk.QueueFamilyProperties[(int)Math.Max(familyCount, 1)];
			Vk.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, ref familyCount, families);
			QueueFamily = uint.MaxValue;
			for (uint i = 0; i < familyCount; i++)
			{
				if ((families[i].QueueFlags & Vk.QueueGraphics) != 0)
				{
					QueueFamily = i;
					Timestamps = families[i].TimestampValidBits != 0;
					break;
				}
			}

			if (QueueFamily == uint.MaxValue)
			{
				return "the device has no graphics queue";
			}

			// The device.
			float priority = 1.0f;
			Vk.DeviceQueueCreateInfo queueInfo = new Vk.DeviceQueueCreateInfo
			{
				SType = Vk.StructureTypeDeviceQueueCreateInfo,
				QueueFamilyIndex = QueueFamily,
				QueueCount = 1,
				PQueuePriorities = &priority
			};
			IntPtr[] extensionNames = new IntPtr[requiredExtensions.Length];
			try
			{
				for (int i = 0; i < extensionNames.Length; i++)
				{
					extensionNames[i] = Marshal.StringToHGlobalAnsi(requiredExtensions[i]);
				}

				fixed (IntPtr* names = extensionNames)
				{
					Vk.DeviceCreateInfo deviceInfo = new Vk.DeviceCreateInfo
					{
						SType = Vk.StructureTypeDeviceCreateInfo,
						PNext = &enable,
						QueueCreateInfoCount = 1,
						PQueueCreateInfos = &queueInfo,
						EnabledExtensionCount = (uint)extensionNames.Length,
						PpEnabledExtensionNames = (byte**)names
					};
					Vk.Check(Vk.vkCreateDevice(PhysicalDevice, &deviceInfo, null, out IntPtr created), "vkCreateDevice");
					Device = created;
				}
			}
			finally
			{
				foreach (IntPtr extensionName in extensionNames)
				{
					Marshal.FreeHGlobal(extensionName);
				}
			}

			Vk.vkGetDeviceQueue(Device, QueueFamily, 0, out IntPtr queue);
			Queue = queue;
			GetMemoryWin32Handle = Vk.DeviceFunction<Vk.GetMemoryWin32HandleKHR>(Device, "vkGetMemoryWin32HandleKHR");
			GetSemaphoreWin32Handle = Vk.DeviceFunction<Vk.GetSemaphoreWin32HandleKHR>(Device, "vkGetSemaphoreWin32HandleKHR");
			if (GetMemoryWin32Handle == null || GetSemaphoreWin32Handle == null)
			{
				return "the device's Win32 handle functions were not found";
			}

			Vk.PhysicalDeviceMemoryProperties memory;
			Vk.vkGetPhysicalDeviceMemoryProperties(PhysicalDevice, &memory);
			memoryProperties = memory;
			return null;
		}

		private static HashSet<string> DeviceExtensions(IntPtr physicalDevice)
		{
			// VkExtensionProperties: a 256-byte name and a uint version.
			const int size = 260;
			uint count = 0;
			Vk.vkEnumerateDeviceExtensionProperties(physicalDevice, IntPtr.Zero, ref count, null);
			byte[] buffer = new byte[Math.Max(count, 1) * size];
			HashSet<string> names = new HashSet<string>();
			fixed (byte* properties = buffer)
			{
				Vk.vkEnumerateDeviceExtensionProperties(physicalDevice, IntPtr.Zero, ref count, properties);
				for (int i = 0; i < count; i++)
				{
					names.Add(Marshal.PtrToStringAnsi((IntPtr)(properties + i * size)));
				}
			}

			return names;
		}

		/// <summary>A memory type allowed by a resource's type bits with all the properties asked for.</summary>
		internal uint MemoryType(uint typeBits, uint properties)
		{
			Vk.PhysicalDeviceMemoryProperties memory = memoryProperties;
			for (uint i = 0; i < memory.MemoryTypeCount; i++)
			{
				// Each VkMemoryType is its property flags, then its heap index.
				if ((typeBits & (1u << (int)i)) != 0 && (memory.MemoryTypes[2 * i] & properties) == properties)
				{
					return i;
				}
			}

			throw new InvalidOperationException("no memory type with properties 0x" + properties.ToString("X") + " for type bits 0x" + typeBits.ToString("X"));
		}

		private static string VersionText(uint version) => (version >> 22) + "." + ((version >> 12) & 0x3FF) + "." + (version & 0xFFF);

		public void Dispose()
		{
			if (Device != IntPtr.Zero)
			{
				Vk.vkDeviceWaitIdle(Device);
				Vk.vkDestroyDevice(Device, null);
				Device = IntPtr.Zero;
			}

			if (Instance != IntPtr.Zero)
			{
				Vk.vkDestroyInstance(Instance, null);
				Instance = IntPtr.Zero;
			}
		}
	}
}
