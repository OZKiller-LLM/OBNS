// OBNS: not an upstream file. The world layer drawn by Direct3D 12 (OBNS/DIRECT3D.md): Direct3D draws the world and cab
// layers into images whose memory OpenGL imports (as Vulkan's), ordered by a fence both share; OpenGL lays them over its
// frame (WorldComposite). The records and meshes as Direct3D 11's; two frames in flight.

using System;
using OpenTK.Graphics.OpenGL;
using TrainManager.Trains;
using Dx = OpenBve.Graphics.Direct3D11.Dx;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12World : IWorldLayer, IDisposable
	{
		private static D3D12World instance;

		internal static bool Enabled => instance != null && !instance.failed;

		internal static D3D12World Instance => instance;

		private readonly D3D12Device device;
		private WorldComposite composite;
		private D3D12Scene scene;
		private D3D12Uploads uploads;
		private bool failed;

		/// <summary>The copies of upstream's meshes and of the world's textures.</summary>
		internal D3D12Meshes Meshes { get; private set; }
		internal D3D12Textures Textures { get; private set; }

		/// <summary>Frames drawn, from 1 (the textures and meshes free what is let go of by it).</summary>
		private long frameNumber;

		// Each frame in flight's commands, and the shared fence's value once it is drawn.
		private readonly IntPtr[] allocators = new IntPtr[D3D12Scene.FramesInFlight], lists = new IntPtr[D3D12Scene.FramesInFlight];
		private readonly ulong[] drawnValues = new ulong[D3D12Scene.FramesInFlight];

		// The frame's images: the world's colour, its depth as a single-channel colour (what the composite reads), the cab
		// layer's colour, all shared with OpenGL; Direct3D's own depth buffers; their views; OpenGL's textures and memory.
		private uint width, height;
		private IntPtr colorImage, depthImage, cabImage, depthBuffer, cabDepthBuffer;
		private IntPtr targetViews, depthViews;
		private uint targetViewSize, depthViewSize;
		private int glColor, glDepth, glCab, glColorMemory, glDepthMemory, glCabMemory;

		/// <summary>The shared fence's value the frame being made is drawn at (OpenGL waits for it), 0 if none.</summary>
		private ulong pendingValue;

		/// <summary>With OBNS_D3D12_DEBUG=1, a frame whose start takes 10 ms or more is logged with its steps' times.</summary>
		private static readonly bool logSlow = Environment.GetEnvironmentVariable("OBNS_D3D12_DEBUG") == "1";
		private readonly long[] stepTicks = new long[8];
		private static readonly string[] stepNames = { "faces", "textures", "records", "waiting for the GPU", "writing records", "recording", "sending" };

		private void Step(int step)
		{
			stepTicks[step] = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		private void LogSlow()
		{
			double total = (stepTicks[7] - stepTicks[0]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (total < 10.0)
			{
				return;
			}

			System.Text.StringBuilder line = new System.Text.StringBuilder("OBNS Direct3D 12 slow start: " + total.ToString("0.0") + " ms (");
			for (int i = 0; i < stepNames.Length; i++)
			{
				line.Append((i > 0 ? ", " : "") + stepNames[i] + " " + ((stepTicks[i + 1] - stepTicks[i]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0"));
			}

			Log(line + "; " + (instance.uploads.Bytes >> 10) + " KB uploaded since the last statistics)");
		}

		/// <summary>A tint for testing the exchange (OBNS_D3D12_TINT=1): the cleared image is a faint green, not clear.</summary>
		private static readonly bool tint = Environment.GetEnvironmentVariable("OBNS_D3D12_TINT") == "1";

		private D3D12World(D3D12Device device)
		{
			this.device = device;
		}

		private static void Log(string text) => Program.FileSystem.AppendToLogFile(text);

		/// <summary>
		/// Makes the layer when the options choose Direct3D 12 (or OBNS_D3D12=1; 0 never), in a 64-bit Windows program,
		/// where Direct3D 12 can share images with OpenGL; logs which, and why. Where it cannot, Direct3D 11 is tried.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_D3D12");
			if (instance != null || forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Direct3D12)
			{
				return;
			}

			if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
			{
				Log("OBNS Direct3D 12 world layer not used: it needs a 64-bit Windows program");
				return;
			}

			D3D12Device device = D3D12Device.Create(out string reason);
			string test = device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				Log("OBNS Direct3D 12 world layer not used: " + (reason ?? test) + "; Direct3D 11 is tried");
				return;
			}

			D3D12World world = new D3D12World(device);
			try
			{
				world.composite = new WorldComposite();
				world.uploads = new D3D12Uploads(device);
				world.Meshes = new D3D12Meshes(device, world.uploads);
				world.Textures = new D3D12Textures(device, world.uploads);
				world.scene = new D3D12Scene(device, world.Meshes, world.Textures);
				for (int i = 0; i < D3D12Scene.FramesInFlight; i++)
				{
					world.allocators[i] = D12.CreateCommandAllocator(device.Device);
					world.lists[i] = D12.CreateCommandList(device.Device, world.allocators[i]);
					D12.Close(world.lists[i]);
				}

				world.targetViews = D12.CreateDescriptorHeap(device.Device, D12.DescriptorHeapRenderTargets, 3, false);
				world.depthViews = D12.CreateDescriptorHeap(device.Device, D12.DescriptorHeapDepthStencils, 2, false);
				world.targetViewSize = D12.DescriptorSize(device.Device, D12.DescriptorHeapRenderTargets);
				world.depthViewSize = D12.DescriptorSize(device.Device, D12.DescriptorHeapDepthStencils);
				WorldHooks.Install("Direct3D 12", "OBNS_D3D12", world.Meshes, world.Textures);
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Direct3D 12 world layer not used: " + ex.Message + "; Direct3D 11 is tried");
				return;
			}

			instance = world;
			Log("OBNS Direct3D 12 world layer on: " + device.Description + " (the world and cab layers, with shadows; " + world.scene.PipelineCount + " pipeline objects made in " + world.scene.MadeIn.ToString("0") + " ms)");
		}

		public IWorldScene Scene => scene;

		/// <summary>Whether the frame being made has a cab layer (Direct3D draws it whenever it draws the world layer).</summary>
		private bool cabStarted;

		public bool DrawsCab => cabStarted;

		public long LastJoinTicks => 0;

		public void Timed(long gather, long draw)
		{
		}

		public void LoadAllTextures() => Textures.LoadAll();

		public void PreloadTrain(TrainBase train) => Textures.PreloadTrain(train);

		private UIntPtr TargetView(int i) => (UIntPtr)((ulong)D12.CpuStart(targetViews) + (ulong)i * targetViewSize);

		private UIntPtr DepthView(int i) => (UIntPtr)((ulong)D12.CpuStart(depthViews) + (ulong)i * depthViewSize);

		/// <summary>The images at the viewport's size, made (and imported by OpenGL) again when it changes.</summary>
		private void EnsureTarget(int newWidth, int newHeight)
		{
			if (colorImage != IntPtr.Zero && width == newWidth && height == newHeight)
			{
				return;
			}

			ReleaseTarget();
			width = (uint)Math.Max(1, newWidth);
			height = (uint)Math.Max(1, newHeight);
			colorImage = device.CreateSharedImage(width, height, Dx.FormatR8G8B8A8Unorm);
			depthImage = device.CreateSharedImage(width, height, Dx.FormatR32Float);
			cabImage = device.CreateSharedImage(width, height, Dx.FormatR8G8B8A8Unorm);
			D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionTexture2D, Width = width, Height = height, DepthOrArraySize = 1, MipLevels = 1, Format = Dx.FormatD32Float, SampleCount = 1, Flags = D12.ResourceAllowDepthStencil };
			depthBuffer = D12.CreateCommittedResource(device.Device, D12.HeapDefault, D12.HeapFlagNone, desc, D12.StateDepthWrite);
			cabDepthBuffer = D12.CreateCommittedResource(device.Device, D12.HeapDefault, D12.HeapFlagNone, desc, D12.StateDepthWrite);
			D12.CreateRenderTargetView(device.Device, colorImage, TargetView(0));
			D12.CreateRenderTargetView(device.Device, depthImage, TargetView(1));
			D12.CreateRenderTargetView(device.Device, cabImage, TargetView(2));
			D12.CreateDepthStencilView(device.Device, depthBuffer, null, DepthView(0));
			D12.CreateDepthStencilView(device.Device, cabDepthBuffer, null, DepthView(1));
			glColor = device.ImportImage(colorImage, width, height, Dx.FormatR8G8B8A8Unorm, SizedInternalFormat.Rgba8, out glColorMemory);
			glDepth = device.ImportImage(depthImage, width, height, Dx.FormatR32Float, SizedInternalFormat.R32f, out glDepthMemory);
			glCab = device.ImportImage(cabImage, width, height, Dx.FormatR8G8B8A8Unorm, SizedInternalFormat.Rgba8, out glCabMemory);
		}

		/// <summary>
		/// The frame's records are complete: Direct3D records the frame (its shadows, world and cab layers), OpenGL signals
		/// that it is done with the images, and the frame is sent to wait for that on the GPU.
		/// </summary>
		public void Start(int x, int y, int viewportWidth, int viewportHeight)
		{
			pendingValue = 0;
			if (failed)
			{
				return;
			}

			try
			{
				EnsureTarget(viewportWidth, viewportHeight);
				Step(0);
				// The faces resolved (meshes, textures made), what the frames before let go of freed, the textures' uploads recorded.
				scene.Prepare();
				frameNumber++;
				Textures.FrameStarted(frameNumber);
				Meshes.FrameStarted(frameNumber);
				Step(1);
				Textures.Send();
				Step(2);
				cabStarted = scene.HasCab;
				scene.BuildRecords();
				Step(3);

				// This frame's commands and records, once the GPU has drawn the frame that last had them.
				int slot = (int)(frameNumber % D3D12Scene.FramesInFlight);
				device.WaitFor(drawnValues[slot]);
				Step(4);
				scene.Upload(slot, frameNumber);
				Step(5);
				IntPtr list = lists[slot];
				D12.ResetAllocator(allocators[slot]);
				D12.Reset(list, allocators[slot]);
				scene.Bind(list);
				scene.DrawShadows(list);

				// The images taken from OpenGL (their contents are not kept: cleared now).
				D12.ResourceBarrier* barriers = stackalloc D12.ResourceBarrier[3];
				barriers[0] = D12.Transition(colorImage, D12.StateCommon, D12.StateRenderTarget);
				barriers[1] = D12.Transition(depthImage, D12.StateCommon, D12.StateRenderTarget);
				barriers[2] = D12.Transition(cabImage, D12.StateCommon, D12.StateRenderTarget);
				D12.ResourceBarriers(list, barriers, 3);

				// The world layer: cleared to nothing (coverage 0) and the far plane, its colour and its depth as a colour.
				if (tint)
				{
					D12.ClearRenderTargetView(list, TargetView(0), 0.0f, 0.12f, 0.0f, 0.12f);
				}
				else
				{
					D12.ClearRenderTargetView(list, TargetView(0), 0, 0, 0, 0);
				}

				D12.ClearRenderTargetView(list, TargetView(1), 1, 1, 1, 1);
				D12.ClearDepthView(list, DepthView(0), 1.0f);
				UIntPtr* targets = stackalloc UIntPtr[2];
				targets[0] = TargetView(0);
				targets[1] = TargetView(1);
				UIntPtr depth = DepthView(0);
				D12.OMSetRenderTargets(list, 2, targets, &depth);
				scene.Draw(list, WorldScene.WorldLayerIndex, width, height);

				// The cab layer, in its own image (laid over after OpenGL's particles, as upstream draws it), its depth its own.
				D12.ClearRenderTargetView(list, TargetView(2), 0, 0, 0, 0);
				D12.ClearDepthView(list, DepthView(1), 1.0f);
				targets[0] = TargetView(2);
				depth = DepthView(1);
				D12.OMSetRenderTargets(list, 1, targets, &depth);
				scene.Draw(list, WorldScene.CabLayer, width, height);

				// Handed back to OpenGL.
				barriers[0] = D12.Transition(colorImage, D12.StateRenderTarget, D12.StateCommon);
				barriers[1] = D12.Transition(depthImage, D12.StateRenderTarget, D12.StateCommon);
				barriers[2] = D12.Transition(cabImage, D12.StateRenderTarget, D12.StateCommon);
				D12.ResourceBarriers(list, barriers, 3);
				D12.Close(list);
				Step(6);

				// The uploads first, then the frame once OpenGL is done with the images (its last frame's composite).
				uploads.Submit();
				ulong glDone = device.SignalFromGL();
				device.QueueWait(glDone);
				D12.ExecuteCommandList(device.Queue, list);
				drawnValues[slot] = pendingValue = device.QueueSignal();
				uploads.Trim();
				if (logSlow)
				{
					Step(7);
					LogSlow();
				}
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}

		/// <summary>OpenGL waits for the frame to be drawn, and lays the world over its frame.</summary>
		public void Finish()
		{
			if (failed || pendingValue == 0)
			{
				return;
			}

			try
			{
				device.WaitInGL(pendingValue);
				composite.World(glColor, glDepth);
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}

		public void CompositeCab()
		{
			if (!failed && pendingValue != 0 && DrawsCab)
			{
				composite.Cab(glCab, glDepth);
			}
		}

		private void Fail(Exception ex)
		{
			failed = true;
			pendingValue = 0;
			Log("OBNS Direct3D 12 world layer stopped, OpenGL draws it from now: " + ex);
		}

		private void ReleaseTarget()
		{
			if (colorImage != IntPtr.Zero)
			{
				// The frames in flight draw into the images: done with first.
				device.WaitIdle();
			}

			foreach (int texture in new[] { glColor, glDepth, glCab })
			{
				if (texture != 0)
				{
					GL.DeleteTexture(texture);
				}
			}

			foreach (int memory in new[] { glColorMemory, glDepthMemory, glCabMemory })
			{
				if (memory != 0)
				{
					int name = memory;
					GL.Ext.DeleteMemoryObjects(1, ref name);
				}
			}

			glColor = glDepth = glCab = glColorMemory = glDepthMemory = glCabMemory = 0;
			foreach (IntPtr com in new[] { colorImage, depthImage, cabImage, depthBuffer, cabDepthBuffer })
			{
				D12.Release(com);
			}

			colorImage = depthImage = cabImage = depthBuffer = cabDepthBuffer = IntPtr.Zero;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => "Direct3D 12 world layer (" + instance.device.Description + "), OpenGL 4 for the rest";

		/// <summary>The layer's state for the frame statistics.</summary>
		internal static string Summary
		{
			get
			{
				if (!Enabled)
				{
					return "off";
				}

				D3D12Scene scene = instance.scene;
				long uploaded = instance.uploads.Bytes;
				instance.uploads.Bytes = 0;
				return "on (Direct3D 12: " + scene.LastDraws + " draws (" + scene.LastCabDraws + " of them the cab's), " + scene.LastStateChanges + " pipeline and texture changes, " + scene.LastShadowCasters + " shadow casters a cascade; " +
				       (uploaded >> 20) + " MB uploaded; " + instance.Textures.Describe() + "; " + instance.Meshes.Describe() +
				       (scene.SkippedFaces > 0 ? "; " + scene.SkippedFaces + " faces without a copy" : "") + ")";
			}
		}

		public void Dispose()
		{
			ReleaseTarget();
			try
			{
				device.WaitIdle();
			}
			catch (Exception)
			{
				// A removed device: nothing is running.
			}

			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			uploads?.Dispose();
			for (int i = 0; i < D3D12Scene.FramesInFlight; i++)
			{
				D12.Release(lists[i]);
				D12.Release(allocators[i]);
			}

			D12.Release(targetViews);
			D12.Release(depthViews);
			device.Dispose();
		}
	}
}
