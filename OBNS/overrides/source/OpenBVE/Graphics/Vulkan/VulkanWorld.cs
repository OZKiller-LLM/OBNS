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

// OBNS: not an upstream file. The desktop world layer drawn by Vulkan, composited by OpenGL (OBNS/VULKAN.md).

using System;
using System.Runtime.InteropServices;
using LibRender2;
using LibRender2.Textures;
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
	internal sealed unsafe class VulkanWorld : IWorldLayer, IDisposable
	{
		/// <summary>Whether the world layer is drawn by Vulkan (this session, unless a frame has failed).</summary>
		internal static bool Enabled => instance != null && !instance.failed;

		/// <summary>The renderer, once <see cref="Initialize"/> has made it.</summary>
		internal static VulkanWorld Instance => instance;

		private static VulkanWorld instance;

		private readonly VulkanDevice device;
		private SharedTarget target;
		private Uploader uploader;

		/// <summary>The world layer's draws for the frame being drawn.</summary>
		internal VulkanScene Scene { get; private set; }

		IWorldScene IWorldLayer.Scene => Scene;

		public void LoadAllTextures() => Scene.Textures.LoadAll();

		public void PreloadTrain(global::TrainManager.Trains.TrainBase train) => Scene.Textures.PreloadTrain(train);

		/// <summary>Frames drawn, from 1 (the textures and meshes free what is let go of by it).</summary>
		private long frameNumber;

		// The frame's commands: two sets, so a frame's can be recorded while the GPU still has the last one.
		private const int FramesInFlight = 2;
		private ulong commandPool;
		private readonly IntPtr[] commandBuffers = new IntPtr[FramesInFlight];
		private readonly ulong[] fences = new ulong[FramesInFlight];
		private int frame;
		private int debugFrames;

		// OpenGL's side.
		private int glColorMemory, glDepthMemory, glColorTexture, glDepthTexture, glDoneSemaphore, glVulkanDoneSemaphore;
		private WorldComposite composite;

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

			// The renderer chosen in the options; OBNS_VULKAN=0 or 1 overrides it (for testing).
			string forced = Environment.GetEnvironmentVariable("OBNS_VULKAN");
			if (forced == "0" || forced != "1" && ObnsOptions.Renderer == ObnsOptions.RendererKind.OpenGL)
			{
				Log("OBNS Vulkan world layer off (" + (forced == "0" ? "OBNS_VULKAN=0" : ObnsOptions.OpenGLByDefault ? "OpenGL is the default on Intel's GPUs from Broadwell back without a discrete GPU" : "the OpenGL renderer is chosen in the options") + "); OpenGL draws it");
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
				world.composite = new WorldComposite();
				world.uploader = new Uploader(device);
				world.Scene = new VulkanScene(device, world.uploader, Program.Renderer);
				world.Hook();
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Vulkan world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Vulkan world layer on: " + device.Description);
			if (Environment.GetEnvironmentVariable("OBNS_VULKAN_DEBUG") == "1")
			{
				Step.Watch();
			}
		}

		/// <summary>Has upstream's VAOs and textures copied as they are made, updated and let go of (OBNS's LibRender2 copies).</summary>
		private void Hook()
		{
			WorldHooks.Install("Vulkan", "OBNS_VULKAN", Scene.Meshes, Scene.Textures, () => "upload waits " + Uploader.WaitMilliseconds + " ms");
		}

		// Time gathering the faces and drawing (recording, submitting, compositing), since the statistics were last taken.
		private long gatherTicks, drawTicks, timedFrames;

		/// <summary>Adds a frame's times: gathering its faces and drawing them.</summary>
		public void Timed(long gather, long draw)
		{
			gatherTicks += gather;
			drawTicks += draw;
			timedFrames++;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay: Vulkan's world layer, or OpenGL alone.</summary>
		internal static string RendererName
		{
			get
			{
				if (Enabled && Program.Renderer != null && !Program.Renderer.ShadowsEnabled && !Program.Renderer.OptionWireFrame && !Program.Renderer.OptionNormals)
				{
					return "Vulkan world layer (" + instance.device.Description + "), OpenGL 4 for the rest";
				}

				return "openGL 4 (new)" + (Enabled ? " (Vulkan world layer paused: shadows or a debug view)" : "");
			}
		}

		/// <summary>The world layer's state for the frame statistics.</summary>
		internal static string Summary
		{
			get
			{
				VulkanWorld world = instance;
				if (world?.Scene == null)
				{
					return "off";
				}

				double frames = Math.Max(1, world.timedFrames);
				string times = string.Format(System.Globalization.CultureInfo.InvariantCulture, "gathering {0:0.00} ms ({2} objects, {3:0.00} ms), drawing {1:0.00} ms a frame",
					world.gatherTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames, world.drawTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames,
					world.Scene.LastObjects, world.Scene.ObjectTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames);
				world.gatherTicks = world.drawTicks = world.timedFrames = 0;
				world.Scene.ObjectTicks = 0;
				return "on (" + times + "; OpenGL's loaded textures " + Program.Renderer.TextureManager.GetNumberOfLoadedTextures() + "; " + world.Scene.LastDraws + " draws (" + world.Scene.LastCabDraws + " of them the cab's) in " + world.Scene.LastIndirectCalls + " indirect calls; " + world.Scene.Textures.Describe() + "; " + world.Scene.Meshes.Describe() +
				       (world.Scene.SkippedFaces > 0 ? "; " + world.Scene.SkippedFaces + " faces without a copy" : "") + ")";
			}
		}

		private static void Log(string message)
		{
			Program.FileSystem.AppendToLogFile(message);
		}

		/// <summary>Whether OpenGL has what sharing needs; null if it has, with the GPU's UUID, else the reason.</summary>
		private static string GLCanShare(out byte[] uuid)
		{
			uuid = null;
			if (Environment.OSVersion.Platform != PlatformID.Win32NT && !VulkanDevice.PosixHandles)
			{
				return "only Windows' handles and Linux's file descriptors are supported so far";
			}

			// Windows shares NT handles, Linux file descriptors.
			string memoryExtension = VulkanDevice.PosixHandles ? "GL_EXT_memory_object_fd" : "GL_EXT_memory_object_win32";
			string semaphoreExtension = VulkanDevice.PosixHandles ? "GL_EXT_semaphore_fd" : "GL_EXT_semaphore_win32";
			int count = GL.GetInteger(GetPName.NumExtensions);
			bool memory = false, semaphore = false;
			for (int i = 0; i < count; i++)
			{
				string extension = GL.GetString(StringNameIndexed.Extensions, i);
				memory |= extension == memoryExtension;
				semaphore |= extension == semaphoreExtension;
			}

			if (!memory || !semaphore)
			{
				return "OpenGL has no " + (memory ? semaphoreExtension : memoryExtension);
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
			if (device.Core10)
			{
				// The core Vulkan 1.0 tier's framebuffers of the images, for the world's and the cab's render passes.
				worldFramebuffer = Vk.CreateFramebuffer(device.Device, Scene.WorldRenderPass, target.ColorView, target.DepthView, target.Width, target.Height);
				cabFramebuffer = Vk.CreateFramebuffer(device.Device, Scene.CabRenderPass, target.CabColorView, target.CabDepthView, target.Width, target.Height);
			}

			glColorMemory = ImportMemory(target.ColorHandle, target.ColorSize);
			glDepthMemory = ImportMemory(target.DepthHandle, target.DepthSize);
			glCabColorMemory = ImportMemory(target.CabColorHandle, target.CabColorSize);
			glColorTexture = ImportTexture(glColorMemory, (int)All.Rgba8, width, height);
			glDepthTexture = ImportTexture(glDepthMemory, (int)All.DepthComponent32f, width, height);
			glCabColorTexture = ImportTexture(glCabColorMemory, (int)All.Rgba8, width, height);
			glDoneSemaphore = ImportSemaphore(target.GLDoneHandle);
			glVulkanDoneSemaphore = ImportSemaphore(target.VulkanDoneHandle);

			// Windows NT handles are not taken over by the import: closed now the textures are made from them.
			VulkanDevice.CloseExported(target.ColorHandle);
			VulkanDevice.CloseExported(target.DepthHandle);
			VulkanDevice.CloseExported(target.CabColorHandle);
			VulkanDevice.CloseExported(target.GLDoneHandle);
			VulkanDevice.CloseExported(target.VulkanDoneHandle);
		}

		private static int ImportMemory(IntPtr handle, ulong size)
		{
			GL.Ext.CreateMemoryObjects(1, out int memory);
			int dedicated = 1;
			GL.Ext.MemoryObjectParameter(memory, MemoryObjectParameterName.DedicatedMemoryObjectExt, ref dedicated);
			VulkanDevice.GLImportMemory(memory, size, handle);
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
			VulkanDevice.GLImportSemaphore(semaphore, handle);
			return semaphore;
		}

		private readonly System.Collections.Generic.List<int> signalBuffers = new System.Collections.Generic.List<int>(), signalTextures = new System.Collections.Generic.List<int>();
		private readonly System.Collections.Generic.List<TextureLayout> signalLayouts = new System.Collections.Generic.List<TextureLayout>();

		/// <summary>OpenGL's layout names for the layouts the images are handed over in (<see cref="SharedTarget"/>).</summary>
		private static readonly TextureLayout[] glLayouts = { TextureLayout.LayoutShaderReadOnlyExt, TextureLayout.LayoutDepthStencilReadOnlyExt, TextureLayout.LayoutShaderReadOnlyExt };

		/// <summary>The cab layer's image, made with the frame's world layer; laid over by CompositeCab.</summary>
		private int glCabColorMemory, glCabColorTexture;

		/// <summary>Whether the frame being made has a cab layer (main thread: known once its faces are given).</summary>
		private bool cabStarted;

		/// <summary>Whether Vulkan draws the frame's cab layer (it does whenever it draws the world layer).</summary>
		public bool DrawsCab => cabStarted;

		// The frame started and not laid over yet: its slot, what OpenGL handed over with it, and the thread making it.
		private bool started, failed;

		/// <summary>The main thread's wait for the last frame's thread (OBNS_GPUSTATS).</summary>
		public long LastJoinTicks { get; private set; }

		// The frames' timestamps (OBNS_GPUSTATS): two a set of commands, start and end.
		private ulong queryPool;
		private readonly bool[] stamped = new bool[FramesInFlight];
		private int startedSlot;
		private bool handedVertices;
		private readonly FrameThread frameThread = new FrameThread();
		private Action recordFrame;

		/// <summary>
		/// Starts the frame's world layer (on the main thread, its faces given): what needs OpenGL or the shared tables is
		/// done here; OpenGL hands the images over, with what it has written into shared storage; then the frame is made
		/// and submitted on its own thread, while OpenGL goes on (the background). The viewport is the scene's.
		/// </summary>
		public void Start(int x, int y, int width, int height)
		{
			started = false;
			if (width <= 0 || height <= 0 || failed)
			{
				return;
			}

			EnsureTarget(width, height);
			Step.Main = "preparing the faces";
			Scene.Prepare();
			Step.Main = "submitting uploads";

			// What the frames three or more before this one let go of can be freed: they are done (each frame's set
			// of commands is waited for before it is used again, two frames later).
			frameNumber++;
			Scene.Textures.FrameStarted(frameNumber);
			Scene.Meshes.FrameStarted(frameNumber);

			// Uploads first, on the same queue (their pixels written on every core).
			uploader.Submit(false, true);

			// OpenGL is done with the images (last frame's composite), and hands over what it has written into shared
			// storage since the last frame: Vulkan may have them.
			signalBuffers.Clear();
			handedVertices = Scene.Meshes.TakeWritten(signalBuffers);
			signalTextures.Clear();
			signalTextures.Add(glColorTexture);
			signalTextures.Add(glDepthTexture);
			signalTextures.Add(glCabColorTexture);
			cabStarted = Scene.HasCab;
			signalLayouts.Clear();
			signalLayouts.AddRange(glLayouts);
			while (signalLayouts.Count < signalTextures.Count)
			{
				signalLayouts.Add(TextureLayout.LayoutShaderReadOnlyExt);
			}

			Step.Main = "OpenGL's signal";
			if (cpuSync)
			{
				// OpenGL's work finished on the CPU's side instead.
				GL.Finish();
			}
			else
			{
				GL.Ext.SignalSemaphore(glDoneSemaphore, signalBuffers.Count, signalBuffers.ToArray(), signalTextures.Count, signalTextures.ToArray(), signalLayouts.ToArray());
				GL.Flush();
			}

			startedSlot = frame;
			frame = (frame + 1) % FramesInFlight;
			started = true;
			recordFrame = recordFrame ?? RecordFrame;
			frameThread.Start(recordFrame);
			Step.Main = "OpenGL's background (frame being made)";
		}

		/// <summary>The frame's records, commands and submission (on the frame's thread).</summary>
		private void RecordFrame()
		{
			Step.Thread = "making the records";
			Scene.BuildRecords();
			Step.Thread = "waiting for the slot's fence";
			// This frame's set of commands and buffers, once the GPU has finished the frame that last had them.
			ulong fence = fences[startedSlot];
			Vk.Check(Vk.vkWaitForFences(device.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
			Vk.Check(Vk.vkResetFences(device.Device, 1, &fence), "vkResetFences");
			// Pages of textures (no descriptor indexing): the slot's sets are free to take the writes made since.
			Scene.TakeTextureWrites(startedSlot);
			if (stamped[startedSlot])
			{
				// The slot's last frame is done: its time on the GPU.
				ulong* times = stackalloc ulong[2];
				if (Vk.vkGetQueryPoolResults(device.Device, queryPool, (uint)startedSlot * 2, 2, (UIntPtr)16, times, 8, Vk.QueryResult64) == Vk.Success)
				{
					GpuStats.VulkanFrame((times[1] - times[0]) * device.TimestampPeriod / 1e6);
				}
			}

			IntPtr commandBuffer = commandBuffers[startedSlot];
			Step.Thread = "recording";
			Record(commandBuffer, startedSlot);
			Step.Thread = "submitting";
			Submit(commandBuffer, fence);
			Step.Thread = "done";
		}

		/// <summary>
		/// Lays the frame's world layer over what OpenGL has drawn (the background) and writes its depth, for what it
		/// draws next. OpenGL's wait is queued only once Vulkan's frame is submitted (its signal must be submitted first).
		/// </summary>
		public void Finish()
		{
			if (!started)
			{
				return;
			}

			started = false;
			long joinStart = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				Step.Main = "waiting for the frame's thread";
				frameThread.Wait();
				LastJoinTicks = System.Diagnostics.Stopwatch.GetTimestamp() - joinStart;
			}
			catch (Exception ex)
			{
				// Not submitted: OpenGL must not wait for it. OpenGL draws the world layer from the next frame.
				failed = true;
				Log("OBNS Vulkan world layer stopped, OpenGL draws it from now: a frame failed: " + ex);
				return;
			}

			int[] textures = { glColorTexture, glDepthTexture, glCabColorTexture };
			Step.Main = "OpenGL's wait";
			if (cpuSync)
			{
				// Vulkan's frame waited for on the CPU's side instead.
				ulong fence = fences[startedSlot];
				Vk.Check(Vk.vkWaitForFences(device.Device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
			}
			else
			{
				GL.Ext.WaitSemaphore(glVulkanDoneSemaphore, 0, (int[])null, 3, textures, glLayouts);
			}
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

			Step.Main = "compositing";
			Composite();
			Step.Main = "after the world layer";
			Step.Frames++;
		}

		/// <summary>
		/// For finding hangs (OBNS_VULKAN_DEBUG=1): the step each thread is at, logged by a watchdog when frames stop
		/// coming for three seconds.
		/// </summary>
		internal static unsafe class Step
		{
			internal static volatile string Main = "starting", Thread = "idle", Uploads = "idle";
			internal static long Frames;

			/// <summary>Whether Vulkan's device is lost (its last frames' fences asked, without waiting).</summary>
			private static string DeviceState()
			{
				VulkanWorld world = instance;
				if (world == null)
				{
					return "no device";
				}

				System.Text.StringBuilder text = new System.Text.StringBuilder("Vulkan fences:");
				for (int i = 0; i < FramesInFlight; i++)
				{
					ulong fence = world.fences[i];
					int result = Vk.vkWaitForFences(world.device.Device, 1, &fence, 1, 0);
					text.Append(' ').Append(result == Vk.Success ? "done" : result == Vk.Timeout ? "pending" : result == -4 ? "DEVICE LOST" : "result " + result);
				}

				return text.ToString();
			}

			internal static void Watch()
			{
				new System.Threading.Thread(() =>
				{
					long last = -1;
					int stuck = 0;
					while (true)
					{
						System.Threading.Thread.Sleep(1000);
						long frames = System.Threading.Interlocked.Read(ref Frames);
						if (frames == last && frames > 0)
						{
							if (++stuck == 3)
							{
								Log("OBNS Vulkan watchdog: no frame for 3 s; main thread: " + Main + "; frame's thread: " + Thread + "; uploads: " + Uploads + "; " + DeviceState());
							}
						}
						else
						{
							stuck = 0;
						}

						last = frames;
					}
				}) { IsBackground = true, Name = "OBNS Vulkan watchdog" }.Start();
			}
		}

		/// <summary>A thread that makes one frame at a time, started and waited for by the main thread.</summary>
		private sealed class FrameThread
		{
			private readonly System.Threading.AutoResetEvent go = new System.Threading.AutoResetEvent(false);
			private readonly System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(true);
			private Action work;
			private Exception failure;

			internal FrameThread()
			{
				new System.Threading.Thread(Loop) { IsBackground = true, Name = "OBNS Vulkan frame" }.Start();
			}

			private void Loop()
			{
				while (true)
				{
					go.WaitOne();
					try
					{
						work();
					}
					catch (Exception ex)
					{
						failure = ex;
					}
					finally
					{
						done.Set();
					}
				}
			}

			internal void Start(Action frameWork)
			{
				work = frameWork;
				failure = null;
				done.Reset();
				go.Set();
			}

			/// <summary>Waits for the frame, rethrowing what made it fail.</summary>
			internal void Wait()
			{
				done.Wait();
				if (failure != null)
				{
					throw new InvalidOperationException(failure.Message, failure);
				}
			}
		}

		private void Record(IntPtr commandBuffer, int slot)
		{
			Vk.Check(Vk.vkResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer");
			Vk.CommandBufferBeginInfo beginInfo = new Vk.CommandBufferBeginInfo { SType = Vk.StructureTypeCommandBufferBeginInfo, Flags = Vk.CommandBufferUsageOneTimeSubmit };
			Vk.Check(Vk.vkBeginCommandBuffer(commandBuffer, &beginInfo), "vkBeginCommandBuffer");
			if (GpuStats.Enabled && device.Timestamps)
			{
				if (queryPool == 0)
				{
					Vk.QueryPoolCreateInfo queryInfo = new Vk.QueryPoolCreateInfo { SType = Vk.StructureTypeQueryPoolCreateInfo, QueryType = Vk.QueryTypeTimestamp, QueryCount = FramesInFlight * 2 };
					Vk.Check(Vk.vkCreateQueryPool(device.Device, &queryInfo, null, out queryPool), "vkCreateQueryPool");
				}

				Vk.vkCmdResetQueryPool(commandBuffer, queryPool, (uint)slot * 2, 2);
				Vk.vkCmdWriteTimestamp(commandBuffer, Vk.PipelineStageTopOfPipe, queryPool, (uint)slot * 2);
			}

			target.AcquireForDrawing(commandBuffer);
			// What OpenGL wrote into shared storage since the last frame.
			Scene.Meshes.AcquireWritten(commandBuffer, handedVertices);

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
			Scene.Upload(slot);
			// The shadow maps first (cleared only, with shadows off), then the world layer that samples them.
			Scene.RecordShadows(commandBuffer, slot);
			BeginLayer(commandBuffer, &renderingInfo, Scene.WorldRenderPass, worldFramebuffer, color.ClearValue);
			if (!Scene.Empty(VulkanScene.WorldLayerIndex))
			{
				Scene.Draw(commandBuffer, slot, target.Width, target.Height, VulkanScene.WorldLayerIndex);
			}

			EndLayer(commandBuffer);

			// The cab layer, in its own image (laid over after OpenGL's particles, as upstream draws it), its depth its own.
			color.ImageView = target.CabColorView;
			color.ClearValue = new Vk.ClearValue();
			depth.ImageView = target.CabDepthView;
			depth.StoreOp = Vk.AttachmentStoreOpDontCare;
			BeginLayer(commandBuffer, &renderingInfo, Scene.CabRenderPass, cabFramebuffer, color.ClearValue);
			if (!Scene.Empty(VulkanScene.CabLayer))
			{
				Scene.Draw(commandBuffer, slot, target.Width, target.Height, VulkanScene.CabLayer);
			}

			EndLayer(commandBuffer);

			target.ReleaseToGL(commandBuffer);
			if (queryPool != 0)
			{
				Vk.vkCmdWriteTimestamp(commandBuffer, Vk.PipelineStageBottomOfPipe, queryPool, (uint)slot * 2 + 1);
				stamped[slot] = true;
			}

			Vk.Check(Vk.vkEndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
		}

		/// <summary>The APIs ordered by the CPU waiting for each (glFinish, the frame's fence) rather than by the semaphores they share.</summary>
		private bool cpuSync => device.CpuSync;

		/// <summary>Begins a layer's drawing: its render pass on the core Vulkan 1.0 tier, dynamic rendering on the 1.2/1.3 path.</summary>
		private void BeginLayer(IntPtr commandBuffer, Vk.RenderingInfo* renderingInfo, ulong renderPass, ulong framebuffer, Vk.ClearValue clear)
		{
			if (device.Core10)
			{
				Vk.BeginRenderPass(commandBuffer, renderPass, framebuffer, target.Width, target.Height, clear);
			}
			else
			{
				Vk.vkCmdBeginRendering(commandBuffer, renderingInfo);
			}
		}

		private void EndLayer(IntPtr commandBuffer)
		{
			if (device.Core10)
			{
				Vk.vkCmdEndRenderPass(commandBuffer);
			}
			else
			{
				Vk.vkCmdEndRendering(commandBuffer);
			}
		}

		private void Submit(IntPtr commandBuffer, ulong fence)
		{
			ulong wait = target.GLDone, signal = target.VulkanDone;
			uint waitStage = Vk.PipelineStageColorAttachmentOutput | Vk.PipelineStageEarlyFragmentTests;
			Vk.SubmitInfo submit = new Vk.SubmitInfo
			{
				SType = Vk.StructureTypeSubmitInfo,
				WaitSemaphoreCount = cpuSync ? 0u : 1u,
				PWaitSemaphores = &wait,
				PWaitDstStageMask = &waitStage,
				CommandBufferCount = 1,
				PCommandBuffers = &commandBuffer,
				SignalSemaphoreCount = cpuSync ? 0u : 1u,
				PSignalSemaphores = &signal
			};
			lock (device.QueueLock)
			{
				Vk.Check(Vk.vkQueueSubmit(device.Queue, 1, &submit, fence), "vkQueueSubmit");
			}
		}

		// --- OpenGL's composite (WorldComposite, shared with the other APIs' layers) ---

		/// <summary>Lays the frame's cab layer over the framebuffer, where upstream draws it (after the particles).</summary>
		public void CompositeCab()
		{
			if (cabStarted)
			{
				composite.Cab(glCabColorTexture, glDepthTexture);
			}
		}

		/// <summary>Lays the world image over the framebuffer and writes its depth.</summary>
		private void Composite() => composite.World(glColorTexture, glDepthTexture);

		/// <summary>The core Vulkan 1.0 tier's framebuffers of the world's and the cab's images (0 on the 1.2/1.3 path).</summary>
		private ulong worldFramebuffer, cabFramebuffer;

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
			GL.DeleteTexture(glCabColorTexture);
			GL.Ext.DeleteMemoryObjects(1, ref glColorMemory);
			GL.Ext.DeleteMemoryObjects(1, ref glDepthMemory);
			GL.Ext.DeleteMemoryObjects(1, ref glCabColorMemory);
			GL.Ext.DeleteSemaphores(1, ref glDoneSemaphore);
			GL.Ext.DeleteSemaphores(1, ref glVulkanDoneSemaphore);
			// (SharedTarget waits for the device too: the framebuffers are not in use.)
			Vk.vkDeviceWaitIdle(device.Device);
			if (worldFramebuffer != 0) Vk.vkDestroyFramebuffer(device.Device, worldFramebuffer, null);
			if (cabFramebuffer != 0) Vk.vkDestroyFramebuffer(device.Device, cabFramebuffer, null);
			worldFramebuffer = cabFramebuffer = 0;
			target.Dispose();
			target = null;
		}

		public void Dispose()
		{
			if (started)
			{
				// A frame being made is waited for, and not laid over.
				started = false;
				try
				{
					frameThread.Wait();
				}
				catch (Exception)
				{
				}
			}

			if (device.Device != IntPtr.Zero)
			{
				Vk.vkDeviceWaitIdle(device.Device);
			}

			ReleaseTarget();
			Scene?.Dispose();
			uploader?.Dispose();
			composite?.Dispose();
			composite = null;

			for (int i = 0; i < FramesInFlight; i++)
			{
				if (fences[i] != 0)
				{
					Vk.vkDestroyFence(device.Device, fences[i], null);
					fences[i] = 0;
				}
			}

			if (queryPool != 0)
			{
				Vk.vkDestroyQueryPool(device.Device, queryPool, null);
				queryPool = 0;
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
