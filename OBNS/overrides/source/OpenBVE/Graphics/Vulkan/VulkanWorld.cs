// OBNS: not an upstream file. The desktop world layer drawn by Vulkan, composited by OpenGL (OBNS/VULKAN.md).

using System;
using System.Runtime.InteropServices;
using OpenBveApi.Interface;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>
	/// The world layer through Vulkan: the device, the images shared with OpenGL, the frame's commands, and OpenGL's
	/// side of the exchange (importing the images, the semaphores, laying the image over the background and writing
	/// its depth). Used on the main thread, with upstream's GL context current.
	/// </summary>
	internal sealed unsafe class VulkanWorld : IDisposable
	{
		/// <summary>Whether the world layer is drawn by Vulkan this session.</summary>
		internal static bool Enabled => instance != null;

		/// <summary>The renderer, once <see cref="Initialize"/> has made it.</summary>
		internal static VulkanWorld Instance => instance;

		private static VulkanWorld instance;

		private readonly VulkanDevice device;
		private SharedTarget target;

		// The frame's commands: two sets, so a frame's can be recorded while the GPU still has the last one.
		private const int FramesInFlight = 2;
		private ulong commandPool;
		private readonly IntPtr[] commandBuffers = new IntPtr[FramesInFlight];
		private readonly ulong[] fences = new ulong[FramesInFlight];
		private int frame;
		private int debugFrames;

		// OpenGL's side.
		private int glColorMemory, glDepthMemory, glColorTexture, glDepthTexture, glDoneSemaphore, glVulkanDoneSemaphore;
		private int compositeProgram, compositeVao;

		/// <summary>A tint for testing the exchange (OBNS_VULKAN_TINT=1): the cleared image is a faint green, not clear.</summary>
		private readonly bool tint = Environment.GetEnvironmentVariable("OBNS_VULKAN_TINT") == "1";

		private VulkanWorld(VulkanDevice device)
		{
			this.device = device;
		}

		/// <summary>Makes the renderer when Vulkan and OpenGL can share images here and OBNS_VULKAN is not 0; logs which, and why.</summary>
		internal static void Initialize()
		{
			if (instance != null)
			{
				return;
			}

			if (Environment.GetEnvironmentVariable("OBNS_VULKAN") == "0")
			{
				Log("OBNS Vulkan world layer off (OBNS_VULKAN=0); OpenGL draws it");
				return;
			}

			string reason = GLCanShare(out byte[] uuid);
			VulkanDevice device = null;
			if (reason == null)
			{
				device = VulkanDevice.Create(uuid, out reason);
			}

			if (device == null)
			{
				Log("OBNS Vulkan world layer not used: " + reason + "; OpenGL draws it");
				return;
			}

			VulkanWorld world = new VulkanWorld(device);
			try
			{
				world.CreateFrames();
				world.CreateComposite();
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Vulkan world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Vulkan world layer on: " + device.Description);
		}

		private static void Log(string message)
		{
			Program.FileSystem.AppendToLogFile(message);
		}

		/// <summary>Whether OpenGL has what sharing needs; null if it has, with the GPU's UUID, else the reason.</summary>
		private static string GLCanShare(out byte[] uuid)
		{
			uuid = null;
			if (Environment.OSVersion.Platform != PlatformID.Win32NT)
			{
				return "only Windows' handles are supported so far";
			}

			int count = GL.GetInteger(GetPName.NumExtensions);
			bool memory = false, semaphore = false;
			for (int i = 0; i < count; i++)
			{
				string extension = GL.GetString(StringNameIndexed.Extensions, i);
				memory |= extension == "GL_EXT_memory_object_win32";
				semaphore |= extension == "GL_EXT_semaphore_win32";
			}

			if (!memory || !semaphore)
			{
				return "OpenGL has no " + (memory ? "GL_EXT_semaphore_win32" : "GL_EXT_memory_object_win32");
			}

			IntPtr address = ((IGraphicsContextInternal)GraphicsContext.CurrentContext).GetAddress("glGetUnsignedBytei_vEXT");
			if (address == IntPtr.Zero)
			{
				return "OpenGL has no glGetUnsignedBytei_vEXT";
			}

			GetUnsignedBytei getUnsignedBytei = (GetUnsignedBytei)Marshal.GetDelegateForFunctionPointer(address, typeof(GetUnsignedBytei));
			uuid = new byte[16];
			const uint deviceUuid = 0x9597; // GL_DEVICE_UUID_EXT
			fixed (byte* data = uuid)
			{
				getUnsignedBytei(deviceUuid, 0, data);
			}

			return null;
		}

		[UnmanagedFunctionPointer(CallingConvention.Winapi)]
		private delegate void GetUnsignedBytei(uint target, uint index, byte* data);

		private void CreateFrames()
		{
			Vk.CommandPoolCreateInfo poolInfo = new Vk.CommandPoolCreateInfo
			{
				SType = Vk.StructureTypeCommandPoolCreateInfo,
				Flags = Vk.CommandPoolCreateResetCommandBuffer,
				QueueFamilyIndex = device.QueueFamily
			};
			Vk.Check(Vk.vkCreateCommandPool(device.Device, &poolInfo, null, out commandPool), "vkCreateCommandPool");
			Vk.CommandBufferAllocateInfo allocateInfo = new Vk.CommandBufferAllocateInfo
			{
				SType = Vk.StructureTypeCommandBufferAllocateInfo,
				CommandPool = commandPool,
				Level = 0, // primary
				CommandBufferCount = FramesInFlight
			};
			fixed (IntPtr* buffers = commandBuffers)
			{
				Vk.Check(Vk.vkAllocateCommandBuffers(device.Device, &allocateInfo, buffers), "vkAllocateCommandBuffers");
			}

			for (int i = 0; i < FramesInFlight; i++)
			{
				Vk.FenceCreateInfo fenceInfo = new Vk.FenceCreateInfo { SType = Vk.StructureTypeFenceCreateInfo, Flags = Vk.FenceCreateSignaled };
				Vk.Check(Vk.vkCreateFence(device.Device, &fenceInfo, null, out fences[i]), "vkCreateFence");
			}
		}

		/// <summary>The shared images at the viewport's size, made (and imported by OpenGL) when it changes.</summary>
		private void EnsureTarget(int width, int height)
		{
			if (target != null && target.Width == width && target.Height == height)
			{
				return;
			}

			ReleaseTarget();
			target = new SharedTarget(device, (uint)width, (uint)height);

			glColorMemory = ImportMemory(target.ColorHandle, target.ColorSize);
			glDepthMemory = ImportMemory(target.DepthHandle, target.DepthSize);
			glColorTexture = ImportTexture(glColorMemory, (int)All.Rgba8, width, height);
			glDepthTexture = ImportTexture(glDepthMemory, (int)All.DepthComponent32f, width, height);
			glDoneSemaphore = ImportSemaphore(target.GLDoneHandle);
			glVulkanDoneSemaphore = ImportSemaphore(target.VulkanDoneHandle);

			// Windows NT handles are not taken over by the import: they are closed here.
			CloseHandle(target.ColorHandle);
			CloseHandle(target.DepthHandle);
			CloseHandle(target.GLDoneHandle);
			CloseHandle(target.VulkanDoneHandle);
		}

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr handle);

		private static int ImportMemory(IntPtr handle, ulong size)
		{
			GL.Ext.CreateMemoryObjects(1, out int memory);
			int dedicated = 1;
			GL.Ext.MemoryObjectParameter(memory, MemoryObjectParameterName.DedicatedMemoryObjectExt, ref dedicated);
			GL.Ext.ImportMemoryWin32Handle(memory, (long)size, ExternalHandleType.HandleTypeOpaqueWin32Ext, handle);
			return memory;
		}

		private static int ImportTexture(int memory, int format, int width, int height)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texture);
			GL.Ext.TexStorageMem2D(TextureTarget.Texture2D, 1, (ExtMemoryObject)format, width, height, memory, 0);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			return texture;
		}

		private static int ImportSemaphore(IntPtr handle)
		{
			GL.Ext.GenSemaphores(1, out int semaphore);
			GL.Ext.ImportSemaphoreWin32Handle(semaphore, ExternalHandleType.HandleTypeOpaqueWin32Ext, handle);
			return semaphore;
		}

		/// <summary>OpenGL's layout names for the layouts the images are handed over in (<see cref="SharedTarget"/>).</summary>
		private static readonly TextureLayout[] glLayouts = { TextureLayout.LayoutShaderReadOnlyExt, TextureLayout.LayoutDepthStencilReadOnlyExt };

		/// <summary>
		/// Draws the world layer: OpenGL hands the images over, Vulkan draws them, and OpenGL lays the colour over what it
		/// has drawn so far (the background) and writes the depth, for what it draws next. The viewport is the scene's.
		/// </summary>
		internal void DrawWorld(int x, int y, int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				return;
			}

			EnsureTarget(width, height);
			int[] textures = { glColorTexture, glDepthTexture };

			// OpenGL is done with the images (last frame's composite): Vulkan may have them.
			GL.Ext.SignalSemaphore(glDoneSemaphore, 0, (int[])null, 2, textures, glLayouts);
			GL.Flush();

			// Vulkan: this frame's commands, once the GPU has finished the last ones recorded in the same set.
			int current = frame;
			frame = (frame + 1) % FramesInFlight;
			ulong fence = fences[current];
			Vk.Check(Vk.vkWaitForFences(device.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
			Vk.Check(Vk.vkResetFences(device.Device, 1, &fence), "vkResetFences");
			IntPtr commandBuffer = commandBuffers[current];
			Record(commandBuffer);
			Submit(commandBuffer, fence);

			// OpenGL waits for Vulkan, then composites.
			GL.Ext.WaitSemaphore(glVulkanDoneSemaphore, 0, (int[])null, 2, textures, glLayouts);
			if (debugFrames < 3 && Environment.GetEnvironmentVariable("OBNS_VULKAN_DEBUG") == "1")
			{
				// What OpenGL reads of the images: for checking the exchange.
				debugFrames++;
				ErrorCode error = GL.GetError();
				byte[] pixels = new byte[target.Width * target.Height * 4];
				GL.BindTexture(TextureTarget.Texture2D, glColorTexture);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				float[] depths = new float[target.Width * target.Height];
				GL.BindTexture(TextureTarget.Texture2D, glDepthTexture);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.DepthComponent, PixelType.Float, depths);
				GL.BindTexture(TextureTarget.Texture2D, 0);
				int middle = (int)(target.Width * (target.Height / 2) + target.Width / 2);
				Log("OBNS Vulkan debug: GL error " + error + " then " + GL.GetError() + "; colour first " + pixels[0] + "," + pixels[1] + "," + pixels[2] + "," + pixels[3] +
				    ", middle " + pixels[middle * 4] + "," + pixels[middle * 4 + 1] + "," + pixels[middle * 4 + 2] + "," + pixels[middle * 4 + 3] + "; depth first " + depths[0] + ", middle " + depths[middle]);
			}

			Composite();
		}

		private void Record(IntPtr commandBuffer)
		{
			Vk.Check(Vk.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
			Vk.CommandBufferBeginInfo beginInfo = new Vk.CommandBufferBeginInfo { SType = Vk.StructureTypeCommandBufferBeginInfo, Flags = Vk.CommandBufferUsageOneTimeSubmit };
			Vk.Check(Vk.vkBeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer");
			target.AcquireForDrawing(commandBuffer);

			// Cleared to nothing (no coverage), at the far plane.
			Vk.RenderingAttachmentInfo color = new Vk.RenderingAttachmentInfo
			{
				SType = Vk.StructureTypeRenderingAttachmentInfo,
				ImageView = target.ColorView,
				ImageLayout = Vk.ImageLayoutColorAttachmentOptimal,
				LoadOp = Vk.AttachmentLoadOpClear,
				StoreOp = Vk.AttachmentStoreOpStore,
				ClearValue = tint ? new Vk.ClearValue { G = 0.25f, A = 0.25f } : new Vk.ClearValue()
			};
			Vk.RenderingAttachmentInfo depth = new Vk.RenderingAttachmentInfo
			{
				SType = Vk.StructureTypeRenderingAttachmentInfo,
				ImageView = target.DepthView,
				ImageLayout = Vk.ImageLayoutDepthStencilAttachmentOptimal,
				LoadOp = Vk.AttachmentLoadOpClear,
				StoreOp = Vk.AttachmentStoreOpStore,
				ClearValue = new Vk.ClearValue { Depth = 1.0f }
			};
			Vk.RenderingInfo renderingInfo = new Vk.RenderingInfo
			{
				SType = Vk.StructureTypeRenderingInfo,
				RenderArea = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = target.Width, Height = target.Height } },
				LayerCount = 1,
				ColorAttachmentCount = 1,
				PColorAttachments = &color,
				PDepthAttachment = &depth
			};
			Vk.vkCmdBeginRendering(commandBuffer, &renderingInfo);
			Vk.vkCmdEndRendering(commandBuffer);

			target.ReleaseToGL(commandBuffer);
			Vk.Check(Vk.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
		}

		private void Submit(IntPtr commandBuffer, ulong fence)
		{
			ulong wait = target.GLDone, signal = target.VulkanDone;
			uint waitStage = Vk.PipelineStageColorAttachmentOutput | Vk.PipelineStageEarlyFragmentTests;
			Vk.SubmitInfo submit = new Vk.SubmitInfo
			{
				SType = Vk.StructureTypeSubmitInfo,
				WaitSemaphoreCount = 1,
				PWaitSemaphores = &wait,
				PWaitDstStageMask = &waitStage,
				CommandBufferCount = 1,
				PCommandBuffers = &commandBuffer,
				SignalSemaphoreCount = 1,
				PSignalSemaphores = &signal
			};
			Vk.Check(Vk.vkQueueSubmit(device.Queue, 1, &submit, fence), "vkQueueSubmit");
		}

		// --- OpenGL's composite ---

		private const string CompositeVertex = @"#version 330 core
out vec2 uv;
void main()
{
	// One triangle covering the viewport.
	vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	uv = p;
	gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

		private const string CompositeFragment = @"#version 330 core
uniform sampler2D uColor;
uniform sampler2D uDepth;
in vec2 uv;
out vec4 fragColor;
void main()
{
	// Premultiplied colour, its alpha the world's coverage; the depth as Vulkan wrote it.
	fragColor = texture(uColor, uv);
	gl_FragDepth = texture(uDepth, uv).r;
}";

		private void CreateComposite()
		{
			int vertex = CompileShader(ShaderType.VertexShader, CompositeVertex);
			int fragment = CompileShader(ShaderType.FragmentShader, CompositeFragment);
			compositeProgram = GL.CreateProgram();
			GL.AttachShader(compositeProgram, vertex);
			GL.AttachShader(compositeProgram, fragment);
			GL.LinkProgram(compositeProgram);
			GL.DeleteShader(vertex);
			GL.DeleteShader(fragment);
			GL.GetProgram(compositeProgram, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				throw new InvalidOperationException("the composite program did not link: " + GL.GetProgramInfoLog(compositeProgram));
			}

			GL.ProgramUniform1(compositeProgram, GL.GetUniformLocation(compositeProgram, "uColor"), 0);
			GL.ProgramUniform1(compositeProgram, GL.GetUniformLocation(compositeProgram, "uDepth"), 1);
			compositeVao = GL.GenVertexArray();
		}

		private static int CompileShader(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled == 0)
			{
				throw new InvalidOperationException("a composite shader did not compile: " + GL.GetShaderInfoLog(shader));
			}

			return shader;
		}

		/// <summary>Lays the world image over the framebuffer and writes its depth. Leaves upstream's renderer to set its own state again.</summary>
		private void Composite()
		{
			GL.UseProgram(compositeProgram);
			GL.BindVertexArray(compositeVao);
			GL.ActiveTexture(TextureUnit.Texture1);
			GL.BindTexture(TextureTarget.Texture2D, glDepthTexture);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, glColorTexture);
			GL.Enable(EnableCap.Blend);
			GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Always);
			GL.DepthMask(true);
			GL.Disable(EnableCap.CullFace);
			GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
			GL.DepthFunc(DepthFunction.Lequal);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.BindVertexArray(0);
			GL.UseProgram(0);
		}

		private void ReleaseTarget()
		{
			if (target == null)
			{
				return;
			}

			// The GPU may still be reading the images.
			GL.Finish();
			GL.DeleteTexture(glColorTexture);
			GL.DeleteTexture(glDepthTexture);
			GL.Ext.DeleteMemoryObjects(1, ref glColorMemory);
			GL.Ext.DeleteMemoryObjects(1, ref glDepthMemory);
			GL.Ext.DeleteSemaphores(1, ref glDoneSemaphore);
			GL.Ext.DeleteSemaphores(1, ref glVulkanDoneSemaphore);
			target.Dispose();
			target = null;
		}

		public void Dispose()
		{
			if (device.Device != IntPtr.Zero)
			{
				Vk.vkDeviceWaitIdle(device.Device);
			}

			ReleaseTarget();
			if (compositeProgram != 0)
			{
				GL.DeleteProgram(compositeProgram);
				GL.DeleteVertexArray(compositeVao);
				compositeProgram = 0;
			}

			for (int i = 0; i < FramesInFlight; i++)
			{
				if (fences[i] != 0)
				{
					Vk.vkDestroyFence(device.Device, fences[i], null);
					fences[i] = 0;
				}
			}

			if (commandPool != 0)
			{
				Vk.vkDestroyCommandPool(device.Device, commandPool, null);
				commandPool = 0;
			}

			device.Dispose();
			if (instance == this)
			{
				instance = null;
			}
		}
	}
}
