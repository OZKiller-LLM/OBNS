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
	internal sealed unsafe class VulkanWorld : IDisposable
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

			// The renderer chosen in the options; OBNS_VULKAN=0 or 1 overrides it (for testing).
			string forced = Environment.GetEnvironmentVariable("OBNS_VULKAN");
			if (forced == "0" || forced != "1" && ObnsOptions.Renderer == ObnsOptions.RendererKind.OpenGL)
			{
				Log("OBNS Vulkan world layer off (" + (forced == "0" ? "OBNS_VULKAN=0" : "the OpenGL renderer is chosen in the options") + "); OpenGL draws it");
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
			ObnsSharing.Error = message => Log("OBNS Vulkan world layer: OpenGL error on shared storage: " + message);
			// One copy of each vertex buffer, in storage both APIs use.
			ObnsSharing.AllocateBuffer = bytes => Guard(() => Scene.Meshes.AllocateVertices(bytes), 4);
			ObnsSharing.FreeBuffer = shared => Guard(() => { Scene.Meshes.FreeVertices(shared); return null; }, 5);
			ObnsSharing.BufferWritten = buffer => Scene.Meshes.Written(buffer);
			// Textures are not shared (OpenGL draws far more slowly with textures over shared memory): Vulkan keeps its
			// own copy of the world's (VulkanTextures), and of animated ones' frames as OpenGL uploads them.
			ObnsSharing.AnimatedFrame = (handle, decoded, bytes, opaque) => Guard(() => { Scene.Textures.AnimatedFrame(handle, decoded, bytes, opaque); return null; }, 2);
			ObnsSharing.TextureUnloading = handle => Guard(() => { Scene.Textures.Unloading(handle); return null; }, 3);

			// The meshes' faces, indexed for Vulkan's triangle lists.
			VAOExtensions.ObnsMeshUploaded = mesh => Guard(() => { Scene.Meshes.Uploaded(mesh); return null; }, 0);
			VAOExtensions.ObnsVaoDisposed = vao => Guard(() => { Scene.Meshes.Disposed(vao); return null; }, 1);
		}

		private static bool hookFailed;

		// The hooks' calls and time, logged every 5 seconds while they are busy (OBNS_VULKAN_DEBUG=1).
		private static readonly bool logHooks = Environment.GetEnvironmentVariable("OBNS_VULKAN_DEBUG") == "1";
		private static readonly long[] hookCalls = new long[6], hookTicks = new long[6];
		private static readonly System.Diagnostics.Stopwatch hookClock = System.Diagnostics.Stopwatch.StartNew();
		private static long lastHookLog;
		private static readonly string[] hookNames = { "meshes indexed", "VAOs disposed", "animated texture frames", "textures unloaded", "vertex buffers made", "vertex buffers freed" };

		/// <summary>
		/// Runs a hook, so a failure is logged (once) and does not break upstream's drawing: storage not given is left to
		/// OpenGL (null).
		/// </summary>
		private static ObnsSharedMemory Guard(Func<ObnsSharedMemory> action, int hook)
		{
			long start = hookClock.ElapsedTicks;
			ObnsSharedMemory result = null;
			try
			{
				result = action();
			}
			catch (Exception ex)
			{
				if (!hookFailed)
				{
					hookFailed = true;
					Log("OBNS Vulkan world layer: a copy failed: " + ex);
				}
			}

			long now = hookClock.ElapsedTicks;
			hookCalls[hook]++;
			hookTicks[hook] += now - start;
			if (logHooks && now - lastHookLog > 5 * System.Diagnostics.Stopwatch.Frequency)
			{
				lastHookLog = now;
				System.Text.StringBuilder line = new System.Text.StringBuilder("OBNS Vulkan copies:");
				for (int i = 0; i < hookCalls.Length; i++)
				{
					line.Append(' ').Append(hookCalls[i]).Append(' ').Append(hookNames[i]).Append(" (").Append((hookTicks[i] * 1000 / System.Diagnostics.Stopwatch.Frequency)).Append(" ms)").Append(i < hookCalls.Length - 1 ? "," : "");
				}

				line.Append("; upload waits ").Append(Uploader.WaitMilliseconds).Append(" ms; ").Append(instance?.Scene?.Meshes.Describe()).Append("; ").Append(instance?.Scene?.Textures.Describe());
				Log(line.ToString());
			}

			return result;
		}

		// Time gathering the faces and drawing (recording, submitting, compositing), since the statistics were last taken.
		private long gatherTicks, drawTicks, timedFrames;

		/// <summary>Adds a frame's times: gathering its faces and drawing them.</summary>
		internal void Timed(long gather, long draw)
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
				return "on (" + times + "; OpenGL's loaded textures " + Program.Renderer.TextureManager.GetNumberOfLoadedTextures() + "; " + world.Scene.LastDraws + " draws in " + world.Scene.LastIndirectCalls + " indirect calls; " + world.Scene.Textures.Describe() + "; " + world.Scene.Meshes.Describe() +
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

		private readonly System.Collections.Generic.List<int> signalBuffers = new System.Collections.Generic.List<int>(), signalTextures = new System.Collections.Generic.List<int>();
		private readonly System.Collections.Generic.List<TextureLayout> signalLayouts = new System.Collections.Generic.List<TextureLayout>();

		/// <summary>OpenGL's layout names for the layouts the images are handed over in (<see cref="SharedTarget"/>).</summary>
		private static readonly TextureLayout[] glLayouts = { TextureLayout.LayoutShaderReadOnlyExt, TextureLayout.LayoutDepthStencilReadOnlyExt };

		// The frame started and not laid over yet: its slot, what OpenGL handed over with it, and the thread making it.
		private bool started, failed;

		/// <summary>The main thread's wait for the last frame's thread (OBNS_GPUSTATS).</summary>
		internal long LastJoinTicks;

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
		internal void Start(int x, int y, int width, int height)
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
			signalLayouts.Clear();
			signalLayouts.AddRange(glLayouts);
			while (signalLayouts.Count < signalTextures.Count)
			{
				signalLayouts.Add(TextureLayout.LayoutShaderReadOnlyExt);
			}

			Step.Main = "OpenGL's signal";
			GL.Ext.SignalSemaphore(glDoneSemaphore, signalBuffers.Count, signalBuffers.ToArray(), signalTextures.Count, signalTextures.ToArray(), signalLayouts.ToArray());
			GL.Flush();

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
		internal void Finish()
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

			int[] textures = { glColorTexture, glDepthTexture };
			Step.Main = "OpenGL's wait";
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
			Vk.vkCmdBeginRendering(commandBuffer, &renderingInfo);
			if (!Scene.Empty)
			{
				Scene.Record(commandBuffer, slot, target.Width, target.Height);
			}

			Vk.vkCmdEndRendering(commandBuffer);

			target.ReleaseToGL(commandBuffer);
			if (queryPool != 0)
			{
				Vk.vkCmdWriteTimestamp(commandBuffer, Vk.PipelineStageBottomOfPipe, queryPool, (uint)slot * 2 + 1);
				stamped[slot] = true;
			}

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
			lock (device.QueueLock)
			{
				Vk.Check(Vk.vkQueueSubmit(device.Queue, 1, &submit, fence), "vkQueueSubmit");
			}
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
