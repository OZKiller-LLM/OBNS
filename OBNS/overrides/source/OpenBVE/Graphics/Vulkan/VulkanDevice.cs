// OBNS: not an upstream file. The desktop world renderer's Vulkan device (OBNS/VULKAN.md).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;

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

		/// <summary>
		/// Whether the textures are one bindless array (descriptor indexing); if not, pages of TexturePageSize textures, a
		/// draw's page bound for it (OBNS_VULKAN_PAGED=1 asks for pages where both work, to try them; =N, pages of at most N).
		/// </summary>
		internal bool Bindless { get; private set; }
		internal int TexturePageSize { get; private set; }

		internal Vk.GetMemoryWin32HandleKHR GetMemoryWin32Handle { get; private set; }
		internal Vk.GetSemaphoreWin32HandleKHR GetSemaphoreWin32Handle { get; private set; }
		private Vk.GetMemoryFdKHR getMemoryFd;
		private Vk.GetSemaphoreFdKHR getSemaphoreFd;

		/// <summary>
		/// Whether memory and semaphores are shared with OpenGL as POSIX file descriptors (Linux) rather than Windows NT
		/// handles. OpenGL takes a descriptor over when it imports it; a Windows handle is closed after (CloseExported).
		/// </summary>
		internal static readonly bool PosixHandles = Environment.OSVersion.Platform == PlatformID.Unix;

		/// <summary>The handle type memory is exported as.</summary>
		internal static uint MemoryHandleType => PosixHandles ? Vk.ExternalMemoryHandleTypeOpaqueFd : Vk.ExternalMemoryHandleTypeOpaqueWin32;

		/// <summary>The handle type semaphores are exported as.</summary>
		internal static uint SemaphoreHandleType => PosixHandles ? Vk.ExternalSemaphoreHandleTypeOpaqueFd : Vk.ExternalSemaphoreHandleTypeOpaqueWin32;

		/// <summary>An exportable allocation's handle for OpenGL: a Windows NT handle, or a file descriptor.</summary>
		internal IntPtr ExportMemory(ulong memory)
		{
			Vk.MemoryGetWin32HandleInfoKHR info = new Vk.MemoryGetWin32HandleInfoKHR
			{
				SType = PosixHandles ? Vk.StructureTypeMemoryGetFdInfoKhr : Vk.StructureTypeMemoryGetWin32HandleInfoKhr,
				Memory = memory,
				HandleType = MemoryHandleType
			};
			if (PosixHandles)
			{
				Vk.Check(getMemoryFd(Device, &info, out int fd), "vkGetMemoryFdKHR");
				return (IntPtr)fd;
			}

			Vk.Check(GetMemoryWin32Handle(Device, &info, out IntPtr handle), "vkGetMemoryWin32HandleKHR");
			return handle;
		}

		/// <summary>An exportable semaphore's handle for OpenGL: a Windows NT handle, or a file descriptor.</summary>
		internal IntPtr ExportSemaphore(ulong semaphore)
		{
			Vk.SemaphoreGetWin32HandleInfoKHR info = new Vk.SemaphoreGetWin32HandleInfoKHR
			{
				SType = PosixHandles ? Vk.StructureTypeSemaphoreGetFdInfoKhr : Vk.StructureTypeSemaphoreGetWin32HandleInfoKhr,
				Semaphore = semaphore,
				HandleType = SemaphoreHandleType
			};
			if (PosixHandles)
			{
				Vk.Check(getSemaphoreFd(Device, &info, out int fd), "vkGetSemaphoreFdKHR");
				return (IntPtr)fd;
			}

			Vk.Check(GetSemaphoreWin32Handle(Device, &info, out IntPtr handle), "vkGetSemaphoreWin32HandleKHR");
			return handle;
		}

		/// <summary>OpenGL imports exported memory (a dedicated allocation if told so); a file descriptor is taken over.</summary>
		internal static void GLImportMemory(int glMemory, ulong size, IntPtr handle)
		{
			if (PosixHandles)
			{
				GL.Ext.ImportMemoryF(glMemory, (long)size, ExternalHandleType.HandleTypeOpaqueFdExt, (int)handle);
			}
			else
			{
				GL.Ext.ImportMemoryWin32Handle(glMemory, (long)size, ExternalHandleType.HandleTypeOpaqueWin32Ext, handle);
			}
		}

		/// <summary>OpenGL imports an exported semaphore; a file descriptor is taken over.</summary>
		internal static void GLImportSemaphore(int glSemaphore, IntPtr handle)
		{
			if (PosixHandles)
			{
				GL.Ext.ImportSemaphoreF(glSemaphore, ExternalHandleType.HandleTypeOpaqueFdExt, (int)handle);
			}
			else
			{
				GL.Ext.ImportSemaphoreWin32Handle(glSemaphore, ExternalHandleType.HandleTypeOpaqueWin32Ext, handle);
			}
		}

		/// <summary>
		/// Closes a Windows handle OpenGL has imported (a file descriptor is OpenGL's once imported). Only after a call that
		/// waits for the driver (glGetError, or using what was imported): a threaded driver may not have done the import yet.
		/// </summary>
		internal static void CloseExported(IntPtr handle)
		{
			if (!PosixHandles)
			{
				CloseHandle(handle);
			}
		}

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		private Vk.PhysicalDeviceMemoryProperties memoryProperties;

		/// <summary>The device extensions the renderer needs (external memory and semaphores are core in Vulkan 1.1).</summary>
		private static readonly string[] requiredExtensions = PosixHandles
			? new[] { "VK_KHR_external_memory_fd", "VK_KHR_external_semaphore_fd" }
			: new[] { "VK_KHR_external_memory_win32", "VK_KHR_external_semaphore_win32" };

		/// <summary>The core Vulkan 1.0 tier's further device extensions: what Vulkan 1.1 made core, that the interop needs.</summary>
		private static readonly string[] core10Extensions = { "VK_KHR_external_memory", "VK_KHR_external_semaphore", "VK_KHR_dedicated_allocation", "VK_KHR_get_memory_requirements2" };

		/// <summary>The core Vulkan 1.0 tier's instance extensions: the device's UUID and external handles' capabilities.</summary>
		private static readonly string[] core10InstanceExtensions = { "VK_KHR_get_physical_device_properties2", "VK_KHR_external_memory_capabilities", "VK_KHR_external_semaphore_capabilities" };

		/// <summary>
		/// The core Vulkan 1.0 tier (OBNS_VULKAN_TIER=1.0, or a device the 1.2/1.3 path cannot use, such as Intel's Broadwell
		/// on Mesa's hasvk): render passes in place of dynamic rendering, texture pages, the 1.0 features and the shaders built
		/// for Vulkan 1.0, and only the extensions OpenGL's interop needs.
		/// </summary>
		internal bool Core10 { get; private set; }

		/// <summary>Each draw recorded as its own vkCmdDrawIndexed (the device lacks multiDrawIndirect or drawIndirectFirstInstance).</summary>
		internal bool DirectDraws { get; private set; }

		/// <summary>
		/// OpenGL and Vulkan are ordered by the CPU waiting for each (glFinish, the frame's fence), not by the semaphores they
		/// share: see <see cref="OrderedByCpu"/>.
		/// </summary>
		internal bool CpuSync { get; private set; }

		private VulkanDevice()
		{
		}

		/// <summary>
		/// Creates the device on the GPU whose UUID OpenGL reports, on the 1.2/1.3 path where the device has all it needs, else
		/// on the core Vulkan 1.0 tier (OBNS_VULKAN_TIER=1.0 or 1.3 chooses one); null, with the reason, when it cannot be used.
		/// </summary>
		internal static VulkanDevice Create(byte[] glDeviceUuid, out string reason)
		{
			string tier = Environment.GetEnvironmentVariable("OBNS_VULKAN_TIER");
			string modernReason = null;
			if (tier != "1.0")
			{
				VulkanDevice modern = TryCreate(glDeviceUuid, false, out modernReason);
				if (modern != null || tier == "1.3")
				{
					reason = modernReason;
					return modern;
				}
			}

			VulkanDevice device = TryCreate(glDeviceUuid, true, out reason);
			if (device == null)
			{
				reason = modernReason == null ? reason : "the 1.2/1.3 path: " + modernReason + "; the core 1.0 tier: " + reason;
				return null;
			}

			device.Description += " (core Vulkan 1.0 tier: " + (modernReason ?? "OBNS_VULKAN_TIER=1.0") + ")";
			return device;
		}

		/// <summary>
		/// Mesa's Intel GPUs from Broadwell back (hasvk, named "... (BDW GT3e)" and so on): semaphores shared with OpenGL hang
		/// Broadwell's GPU within a minute, ten seconds at a time; without them it ran clean, at about 14 fps against 19.
		/// OBNS_VULKAN_CPUSYNC=1 or 0 chooses, for any GPU.
		/// </summary>
		private static bool OrderedByCpu(uint vendor, string deviceName)
		{
			string asked = Environment.GetEnvironmentVariable("OBNS_VULKAN_CPUSYNC");
			if (asked == "0" || asked == "1")
			{
				return asked == "1";
			}

			const uint intel = 0x8086;
			if (!PosixHandles || vendor != intel)
			{
				return false;
			}

			foreach (string family in new[] { "BDW", "CHV", "HSW", "BYT", "IVB" })
			{
				if (deviceName.Contains("(" + family))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Intel's integrated GPUs older than Broadwell, by Mesa's names for them: Vulkan isn't supported on them.</summary>
		private static bool OlderThanBroadwell(string deviceName)
		{
			return deviceName.Contains("(HSW") || deviceName.Contains("(BYT") || deviceName.Contains("(IVB");
		}

		/// <summary>
		/// Whether the computer's GPUs are an Intel integrated one from Broadwell back and no discrete GPU, as Vulkan lists them.
		/// OpenGL is the default on such a computer on Linux (ObnsOptions): on Broadwell it is 3-4 times as fast as Vulkan can be
		/// made to run, and older ones aren't supported.
		/// </summary>
		internal static bool OldIntelWithoutDiscreteGpu()
		{
			IntPtr instance = IntPtr.Zero;
			try
			{
				Vk.ApplicationInfo application = new Vk.ApplicationInfo { SType = Vk.StructureTypeApplicationInfo, ApiVersion = Vk.MakeVersion(1, 0, 0) };
				Vk.InstanceCreateInfo instanceInfo = new Vk.InstanceCreateInfo { SType = Vk.StructureTypeInstanceCreateInfo, PApplicationInfo = &application };
				if (Vk.vkCreateInstance(&instanceInfo, null, out instance) != Vk.Success)
				{
					return false;
				}

				uint count = 0;
				Vk.Check(Vk.vkEnumeratePhysicalDevices(instance, ref count, null), "vkEnumeratePhysicalDevices");
				IntPtr* devices = stackalloc IntPtr[(int)count];
				Vk.Check(Vk.vkEnumeratePhysicalDevices(instance, ref count, devices), "vkEnumeratePhysicalDevices");
				bool oldIntel = false, discrete = false;
				for (uint i = 0; i < count; i++)
				{
					Vk.PhysicalDeviceProperties properties;
					Vk.vkGetPhysicalDeviceProperties(devices[i], &properties);
					discrete |= properties.DeviceType == Vk.PhysicalDeviceTypeDiscreteGpu;
					string deviceName = Marshal.PtrToStringAnsi((IntPtr)properties.DeviceName);
					oldIntel |= deviceName.Contains("(BDW") || deviceName.Contains("(CHV") || OlderThanBroadwell(deviceName);
				}

				return oldIntel && !discrete;
			}
			catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is InvalidOperationException)
			{
				return false;
			}
			finally
			{
				if (instance != IntPtr.Zero)
				{
					Vk.vkDestroyInstance(instance, null);
				}
			}
		}

		private static VulkanDevice TryCreate(byte[] glDeviceUuid, bool core10, out string reason)
		{
			VulkanDevice device = new VulkanDevice { Core10 = core10 };
			try
			{
				reason = device.Initialize(glDeviceUuid);
			}
			catch (DllNotFoundException)
			{
				reason = PosixHandles ? "no Vulkan loader (libvulkan.so.1)" : "no Vulkan loader (vulkan-1)";
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
			// A Vulkan 1.0 loader has no vkEnumerateInstanceVersion.
			uint loaderVersion;
			try
			{
				Vk.vkEnumerateInstanceVersion(out loaderVersion);
			}
			catch (EntryPointNotFoundException)
			{
				loaderVersion = Vk.MakeVersion(1, 0, 0);
			}

			if (!Core10 && loaderVersion < Vk.MakeVersion(1, 3, 0))
			{
				return "the Vulkan loader is version " + VersionText(loaderVersion) + "; 1.3 is needed";
			}

			string[] instanceExtensions = Core10 ? core10InstanceExtensions : new string[0];
			HashSet<string> loaderExtensions = Core10 ? Vk.InstanceExtensions() : null;
			foreach (string extension in instanceExtensions)
			{
				if (!loaderExtensions.Contains(extension))
				{
					return "the Vulkan loader has no " + extension;
				}
			}

			// The instance: Vulkan 1.3, or 1.0 with the extensions the interop needs.
			IntPtr name = Marshal.StringToHGlobalAnsi("OpenBVE (OBNS)");
			IntPtr[] instanceNames = new IntPtr[instanceExtensions.Length];
			try
			{
				for (int i = 0; i < instanceNames.Length; i++)
				{
					instanceNames[i] = Marshal.StringToHGlobalAnsi(instanceExtensions[i]);
				}

				Vk.ApplicationInfo application = new Vk.ApplicationInfo
				{
					SType = Vk.StructureTypeApplicationInfo,
					PApplicationName = (byte*)name,
					PEngineName = (byte*)name,
					ApiVersion = Core10 ? Vk.MakeVersion(1, 0, 0) : Vk.MakeVersion(1, 3, 0)
				};
				fixed (IntPtr* names = instanceNames)
				{
					Vk.InstanceCreateInfo instanceInfo = new Vk.InstanceCreateInfo
					{
						SType = Vk.StructureTypeInstanceCreateInfo,
						PApplicationInfo = &application,
						EnabledExtensionCount = (uint)instanceNames.Length,
						PpEnabledExtensionNames = (byte**)names
					};
					Vk.Check(Vk.vkCreateInstance(&instanceInfo, null, out IntPtr instance), "vkCreateInstance");
					Instance = instance;
				}
			}
			finally
			{
				Marshal.FreeHGlobal(name);
				foreach (IntPtr instanceName in instanceNames)
				{
					Marshal.FreeHGlobal(instanceName);
				}
			}

			// The device's UUID: vkGetPhysicalDeviceProperties2 (1.1), or its KHR form on the 1.0 tier.
			Vk.GetPhysicalDeviceProperties2KHR properties2KHR = null;
			if (Core10)
			{
				properties2KHR = Vk.InstanceFunction<Vk.GetPhysicalDeviceProperties2KHR>(Instance, "vkGetPhysicalDeviceProperties2KHR");
				if (properties2KHR == null)
				{
					return "the Vulkan loader has no vkGetPhysicalDeviceProperties2KHR";
				}
			}

			// The physical device OpenGL is on: the same device UUID.
			uint count = 0;
			Vk.Check(Vk.vkEnumeratePhysicalDevices(Instance, ref count, null), "vkEnumeratePhysicalDevices");
			IntPtr* devices = stackalloc IntPtr[(int)Math.Max(count, 1)];
			Vk.Check(Vk.vkEnumeratePhysicalDevices(Instance, ref count, devices), "vkEnumeratePhysicalDevices");
			List<string> seen = new List<string>();
			uint sampledImagesPerStage = 0, sampledImagesPerSet = 0, resourcesPerStage = 0;
			for (int i = 0; i < count; i++)
			{
				Vk.PhysicalDeviceIDProperties id = new Vk.PhysicalDeviceIDProperties { SType = Vk.StructureTypePhysicalDeviceIdProperties };
				Vk.PhysicalDeviceProperties2 properties = new Vk.PhysicalDeviceProperties2 { SType = Vk.StructureTypePhysicalDeviceProperties2, PNext = &id };
				if (Core10)
				{
					properties2KHR(devices[i], &properties);
				}
				else
				{
					Vk.vkGetPhysicalDeviceProperties2(devices[i], &properties);
				}

				string deviceName = Marshal.PtrToStringAnsi((IntPtr)properties.Properties.DeviceName);
				seen.Add(deviceName);
				// Never a CPU device (llvmpipe), whatever its UUID: OpenGL is not drawing there.
				const uint cpuDevice = 4;
				bool same = glDeviceUuid != null && glDeviceUuid.Length == 16 && properties.Properties.DeviceType != cpuDevice;
				for (int b = 0; same && b < 16; b++)
				{
					same = id.DeviceUuid[b] == glDeviceUuid[b];
				}

				if (same)
				{
					PhysicalDevice = devices[i];
					Description = deviceName + ", Vulkan " + VersionText(properties.Properties.ApiVersion);
					// OBNS_VULKAN=1 still tries it, for testing.
					if (OlderThanBroadwell(deviceName) && Environment.GetEnvironmentVariable("OBNS_VULKAN") != "1")
					{
						return deviceName + ": Vulkan is not supported on Intel's GPUs older than Broadwell";
					}

					CpuSync = OrderedByCpu(properties.Properties.VendorID, deviceName);
					if (CpuSync)
					{
						Description += " (the CPU orders OpenGL and Vulkan)";
					}

					// timestampPeriod: 428 bytes into the limits as they are kept here (720 into VkPhysicalDeviceProperties).
					TimestampPeriod = *(float*)(properties.Properties.LimitsAndSparseProperties + 428);
					// maxPerStageDescriptorSampledImages and maxDescriptorSetSampledImages (80 and 116 into the limits).
					sampledImagesPerStage = *(uint*)(properties.Properties.LimitsAndSparseProperties + 84);
					sampledImagesPerSet = *(uint*)(properties.Properties.LimitsAndSparseProperties + 120);
					// maxPerStageResources (92 into the limits).
					resourcesPerStage = *(uint*)(properties.Properties.LimitsAndSparseProperties + 96);
					if (!Core10 && properties.Properties.ApiVersion < Vk.MakeVersion(1, 3, 0))
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
			string[] deviceExtensions = Core10 ? new string[requiredExtensions.Length + core10Extensions.Length] : requiredExtensions;
			if (Core10)
			{
				requiredExtensions.CopyTo(deviceExtensions, 0);
				core10Extensions.CopyTo(deviceExtensions, requiredExtensions.Length);
			}

			HashSet<string> available = DeviceExtensions(PhysicalDevice);
			foreach (string extension in deviceExtensions)
			{
				if (!available.Contains(extension))
				{
					return "the device has no " + extension;
				}
			}

			// Features: what the renderer uses, if the device has it (the 1.0 tier: VkPhysicalDeviceFeatures alone).
			Vk.PhysicalDeviceVulkan13Features features13 = new Vk.PhysicalDeviceVulkan13Features { SType = Vk.StructureTypePhysicalDeviceVulkan13Features };
			Vk.PhysicalDeviceVulkan12Features features12 = new Vk.PhysicalDeviceVulkan12Features { SType = Vk.StructureTypePhysicalDeviceVulkan12Features, PNext = &features13 };
			Vk.PhysicalDeviceFeatures2 features = new Vk.PhysicalDeviceFeatures2 { SType = Vk.StructureTypePhysicalDeviceFeatures2, PNext = &features12 };
			if (Core10)
			{
				Vk.vkGetPhysicalDeviceFeatures(PhysicalDevice, features.Features);
			}
			else
			{
				Vk.vkGetPhysicalDeviceFeatures2(PhysicalDevice, &features);
			}

			// The 1.2/1.3 path's own needs (the 1.0 tier's textures are in pages, its draws direct where they must be).
			if (!Core10 && features12.Features[Vk.PhysicalDeviceVulkan12Features.ScalarBlockLayout] == 0)
			{
				return "the device lacks scalarBlockLayout";
			}

			// Textures: one bindless array (descriptor indexing), else pages of them a draw's page is bound for (core
			// Vulkan: a fixed array, indexed the same for the whole draw), as on Intel's Broadwell (Mesa's hasvk).
			int[] bindless =
			{
				Vk.PhysicalDeviceVulkan12Features.DescriptorIndexing, Vk.PhysicalDeviceVulkan12Features.ShaderSampledImageArrayNonUniformIndexing,
				Vk.PhysicalDeviceVulkan12Features.DescriptorBindingSampledImageUpdateAfterBind, Vk.PhysicalDeviceVulkan12Features.DescriptorBindingUpdateUnusedWhilePending,
				Vk.PhysicalDeviceVulkan12Features.DescriptorBindingPartiallyBound, Vk.PhysicalDeviceVulkan12Features.DescriptorBindingVariableDescriptorCount,
				Vk.PhysicalDeviceVulkan12Features.RuntimeDescriptorArray
			};
			string pagedAsked = Environment.GetEnvironmentVariable("OBNS_VULKAN_PAGED");
			int.TryParse(pagedAsked, out int pageAsked);
			Bindless = pageAsked <= 0 && !Core10;
			foreach (int feature in bindless)
			{
				Bindless &= features12.Features[feature] != 0;
			}

			// Textures in pages only on the 1.0 tier, unless asked for on the 1.2/1.3 path (OBNS_VULKAN_TIER=1.3 and OBNS_VULKAN_PAGED).
			if (!Core10 && !Bindless && Environment.GetEnvironmentVariable("OBNS_VULKAN_TIER") != "1.3")
			{
				return "the device has no descriptor indexing";
			}

			int[] needed12 = Bindless ? new int[bindless.Length + 1] : new[] { Vk.PhysicalDeviceVulkan12Features.ScalarBlockLayout };
			if (Bindless)
			{
				bindless.CopyTo(needed12, 0);
				needed12[bindless.Length] = Vk.PhysicalDeviceVulkan12Features.ScalarBlockLayout;
			}
			else
			{
				// shaderSampledImageArrayDynamicIndexing (VkPhysicalDeviceFeatures 34); a page as large as the device lets a
				// stage and a pipeline layout have, less the shadow map, and within the resources a stage may have in all
				// (maxPerStageResources: the fragment shader's 4 storage buffers, the shadow map and the 2 colour
				// attachments count too): on Intel's Broadwell (hasvk) pages of 127 hung the GPU, most likely by overrunning
				// the stage's binding table.
				const int sampledImageArrayDynamicIndexing = 34;
				if (features.Features[sampledImageArrayDynamicIndexing] == 0)
				{
					return "the device has neither descriptor indexing nor shaderSampledImageArrayDynamicIndexing";
				}

				const uint otherResources = 4 + 1 + 2;
				uint perStage = sampledImagesPerStage, perSet = sampledImagesPerSet;
				uint fitting = Math.Min(Math.Min(perStage, perSet) - 1, resourcesPerStage > otherResources ? resourcesPerStage - otherResources : 0);
				TexturePageSize = (int)Math.Min(fitting, pageAsked > 1 ? (uint)pageAsked : 1024);
				if (TexturePageSize < 16)
				{
					return "the device allows too few sampled images (" + perStage + " a stage) for pages of textures";
				}

				Description += ", textures in pages of " + TexturePageSize + " (the device: " + perStage + " sampled images and " + resourcesPerStage + " resources a stage)";
			}

			if (!Core10 && (features13.Features[Vk.PhysicalDeviceVulkan13Features.DynamicRendering] == 0 || features13.Features[Vk.PhysicalDeviceVulkan13Features.Synchronization2] == 0 ||
			    features13.Features[Vk.PhysicalDeviceVulkan13Features.ShaderDemoteToHelperInvocation] == 0))
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
			const int multiDrawIndirect = 9, drawIndirectFirstInstance = 10, samplerAnisotropy = 19, imageArrayDynamicIndexing = 34;
			bool indirect = features.Features[multiDrawIndirect] != 0 && features.Features[drawIndirectFirstInstance] != 0;
			if (!Core10 && !indirect)
			{
				return "the device lacks multiDrawIndirect";
			}

			// The 1.0 tier draws each draw directly where the device cannot draw runs of them indirectly.
			DirectDraws = !indirect;
			enable.Features[multiDrawIndirect] = indirect ? 1u : 0u;
			enable.Features[drawIndirectFirstInstance] = indirect ? 1u : 0u;
			enable.Features[samplerAnisotropy] = features.Features[samplerAnisotropy];
			enable.Features[imageArrayDynamicIndexing] = Bindless ? 0u : 1u;

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
			IntPtr[] extensionNames = new IntPtr[deviceExtensions.Length];
			try
			{
				for (int i = 0; i < extensionNames.Length; i++)
				{
					extensionNames[i] = Marshal.StringToHGlobalAnsi(deviceExtensions[i]);
				}

				fixed (IntPtr* names = extensionNames)
				{
					Vk.DeviceCreateInfo deviceInfo = new Vk.DeviceCreateInfo
					{
						SType = Vk.StructureTypeDeviceCreateInfo,
						// The 1.0 tier: VkPhysicalDeviceFeatures (pEnabledFeatures) alone, no 1.1+ structures.
						PNext = Core10 ? null : &enable,
						PEnabledFeatures = Core10 ? enable.Features : null,
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
			if (PosixHandles)
			{
				getMemoryFd = Vk.DeviceFunction<Vk.GetMemoryFdKHR>(Device, "vkGetMemoryFdKHR");
				getSemaphoreFd = Vk.DeviceFunction<Vk.GetSemaphoreFdKHR>(Device, "vkGetSemaphoreFdKHR");
				if (getMemoryFd == null || getSemaphoreFd == null)
				{
					return "the device's file descriptor functions were not found";
				}
			}
			else
			{
				GetMemoryWin32Handle = Vk.DeviceFunction<Vk.GetMemoryWin32HandleKHR>(Device, "vkGetMemoryWin32HandleKHR");
				GetSemaphoreWin32Handle = Vk.DeviceFunction<Vk.GetSemaphoreWin32HandleKHR>(Device, "vkGetSemaphoreWin32HandleKHR");
				if (GetMemoryWin32Handle == null || GetSemaphoreWin32Handle == null)
				{
					return "the device's Win32 handle functions were not found";
				}
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
