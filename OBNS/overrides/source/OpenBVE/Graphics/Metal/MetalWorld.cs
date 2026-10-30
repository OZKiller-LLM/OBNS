// OBNS: not an upstream file. The world layer drawn by Metal on macOS (OBNS/METAL.md): Metal draws the world and cab layers
// into IOSurfaces OpenGL binds as rectangle textures, OpenGL lays them over its frame (WorldComposite). Two sets of images,
// a frame each in turn: OpenGL's fence after its composite says when a set may be drawn into again; OpenGL waits for
// Metal's frame (its command buffer completed) before laying it over.

using System;
using OpenTK.Graphics.OpenGL;
using TrainManager.Trains;

namespace OpenBve.Graphics.Metal
{
	internal sealed class MetalWorld : IWorldLayer, IDisposable
	{
		private static MetalWorld instance;

		internal static bool Enabled => instance != null && !instance.failed;

		internal static MetalWorld Instance => instance;

		/// <summary>The selectors, in a class of their own: looked up only once Metal is used (only on macOS).</summary>
		private static class S
		{
			internal static readonly IntPtr selCommandBuffer = Mtl.Sel("commandBuffer"), selCommit = Mtl.Sel("commit"),
				selRenderPassDescriptor = Mtl.Sel("renderPassDescriptor"), selColorAttachments = Mtl.Sel("colorAttachments"),
				selObjectAtIndex = Mtl.Sel("objectAtIndexedSubscript:"), selDepthAttachment = Mtl.Sel("depthAttachment"),
				selSetTexture = Mtl.Sel("setTexture:"), selSetLoadAction = Mtl.Sel("setLoadAction:"), selSetStoreAction = Mtl.Sel("setStoreAction:"),
				selSetClearColor = Mtl.Sel("setClearColor:"), selSetClearDepth = Mtl.Sel("setClearDepth:"),
				selRenderEncoder = Mtl.Sel("renderCommandEncoderWithDescriptor:"), selEndEncoding = Mtl.Sel("endEncoding"),
				selTexture2D = Mtl.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), selSetUsage = Mtl.Sel("setUsage:"),
				selSetStorageMode = Mtl.Sel("setStorageMode:"), selNewTexture = Mtl.Sel("newTextureWithDescriptor:");
		}

		private readonly MetalDevice device;
		private WorldComposite composite;
		private MetalScene scene;
		private bool failed;

		/// <summary>The copies of upstream's meshes and of the world's textures.</summary>
		internal MetalMeshes Meshes { get; private set; }
		internal MetalTextures Textures { get; private set; }

		/// <summary>Frames drawn, from 1 (the textures and meshes free what is let go of by it).</summary>
		private long frameNumber;

		/// <summary>A frame's images: the world's colour, its depth as a colour, the cab's colour (shared); Metal's depth buffers.</summary>
		private sealed class ImageSet : IDisposable
		{
			internal MetalDevice.SharedImage Color, Depth, Cab;
			internal IntPtr DepthBuffer, CabDepthBuffer;
			/// <summary>OpenGL's fence after its last use of the set (0: none).</summary>
			internal IntPtr Fence;

			public void Dispose()
			{
				Color?.Dispose();
				Depth?.Dispose();
				Cab?.Dispose();
				Mtl.Release(DepthBuffer);
				Mtl.Release(CabDepthBuffer);
				DepthBuffer = CabDepthBuffer = IntPtr.Zero;
				if (Fence != IntPtr.Zero)
				{
					GL.DeleteSync(Fence);
					Fence = IntPtr.Zero;
				}
			}
		}

		private readonly ImageSet[] sets = new ImageSet[2];
		private uint width, height;
		/// <summary>The set the frame being made is drawn into, and its command buffer (committed, retained; none if none).</summary>
		private int current;
		private IntPtr pending;

		/// <summary>A tint for testing the exchange (OBNS_METAL_TINT=1): the cleared image is a faint green, not clear.</summary>
		private static readonly bool tint = Environment.GetEnvironmentVariable("OBNS_METAL_TINT") == "1";

		private MetalWorld(MetalDevice device)
		{
			this.device = device;
		}

		private static void Log(string text) => Program.FileSystem.AppendToLogFile(text);

		/// <summary>
		/// Makes the layer on macOS when the options choose Metal (or OBNS_METAL=1; 0 never), where Metal can share images
		/// with OpenGL; logs which, and why. Where it cannot, OpenGL draws the world.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_METAL");
			if (instance != null || forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Metal)
			{
				return;
			}

			if (Program.CurrentHost.Platform != OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				Log("OBNS Metal world layer not used: Metal is macOS's only");
				return;
			}

			MetalDevice device = MetalDevice.Create(out string reason);
			string test = device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				Log("OBNS Metal world layer not used: " + (reason ?? test) + "; OpenGL draws it");
				return;
			}

			MetalWorld world = new MetalWorld(device);
			try
			{
				world.composite = new WorldComposite(true);
				world.Meshes = new MetalMeshes(device);
				world.Textures = new MetalTextures(device);
				world.scene = new MetalScene(device, world.Meshes, world.Textures);
				WorldHooks.Install("Metal", "OBNS_METAL", world.Meshes, world.Textures);
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Metal world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Metal world layer on: " + device.Description + " (the world and cab layers, with shadows; shaders and pipelines made in " + world.scene.MadeIn.ToString("0") + " ms)");
		}

		public IWorldScene Scene => scene;

		/// <summary>Whether the frame being made has a cab layer (Metal draws it whenever it draws the world layer).</summary>
		private bool cabStarted;

		public bool DrawsCab => cabStarted;

		public long LastJoinTicks { get; private set; }

		public void Timed(long gather, long draw)
		{
		}

		public void LoadAllTextures() => Textures.LoadAll();

		public void PreloadTrain(TrainBase train) => Textures.PreloadTrain(train);

		private IntPtr DepthBuffer()
		{
			IntPtr desc = Mtl.Id(Mtl.Class("MTLTextureDescriptor"), S.selTexture2D, Mtl.PixelDepth32Float, (ulong)width, (ulong)height, (byte)0);
			Mtl.Void(desc, S.selSetUsage, Mtl.UsageRenderTarget);
			Mtl.Void(desc, S.selSetStorageMode, Mtl.StoragePrivate);
			return Mtl.Id(device.Device, S.selNewTexture, desc);
		}

		/// <summary>The images at the viewport's size, made again when it changes.</summary>
		private void EnsureTarget(int newWidth, int newHeight)
		{
			if (sets[0] != null && width == newWidth && height == newHeight)
			{
				return;
			}

			ReleaseTargets();
			width = (uint)Math.Max(1, newWidth);
			height = (uint)Math.Max(1, newHeight);
			for (int i = 0; i < sets.Length; i++)
			{
				ImageSet set = new ImageSet();
				sets[i] = set;
				set.Color = device.CreateSharedImage(width, height, false);
				set.Depth = device.CreateSharedImage(width, height, true);
				set.Cab = device.CreateSharedImage(width, height, false);
				set.DepthBuffer = DepthBuffer();
				set.CabDepthBuffer = DepthBuffer();
			}
		}

		private static IntPtr ColorAttachment(IntPtr pass, ulong index, IntPtr texture, Mtl.ClearColor clear)
		{
			IntPtr attachment = Mtl.Id(Mtl.Id(pass, S.selColorAttachments), S.selObjectAtIndex, index);
			Mtl.Void(attachment, S.selSetTexture, texture);
			Mtl.Void(attachment, S.selSetLoadAction, Mtl.LoadClear);
			Mtl.Void(attachment, S.selSetStoreAction, Mtl.StoreStore);
			Mtl.Void(attachment, S.selSetClearColor, clear);
			return attachment;
		}

		private static void DepthAttachment(IntPtr pass, IntPtr texture)
		{
			IntPtr depth = Mtl.Id(pass, S.selDepthAttachment);
			Mtl.Void(depth, S.selSetTexture, texture);
			Mtl.Void(depth, S.selSetLoadAction, Mtl.LoadClear);
			Mtl.Void(depth, S.selSetStoreAction, Mtl.StoreDontCare);
			Mtl.Void(depth, S.selSetClearDepth, 1.0);
		}

		/// <summary>The frame's records are complete: Metal draws them into the next set of images (once OpenGL is done with it).</summary>
		public void Start(int x, int y, int viewportWidth, int viewportHeight)
		{
			if (failed)
			{
				return;
			}

			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				EnsureTarget(viewportWidth, viewportHeight);
				// The faces resolved (meshes, textures made and written: Metal draws nothing now), what the frames before let
				// go of freed.
				scene.Prepare();
				frameNumber++;
				Textures.FrameStarted(frameNumber);
				Meshes.FrameStarted(frameNumber);
				Textures.Send();
				cabStarted = scene.HasCab;
				scene.BuildRecords();
				scene.Upload();

				// OpenGL's use of the last set so far fenced; this set's (two frames ago) waited for.
				ImageSet last = sets[current];
				if (last.Fence != IntPtr.Zero)
				{
					GL.DeleteSync(last.Fence);
				}

				last.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
				current = (current + 1) % sets.Length;
				ImageSet set = sets[current];
				if (set.Fence != IntPtr.Zero)
				{
					GL.ClientWaitSync(set.Fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 1000000000L);
					GL.DeleteSync(set.Fence);
					set.Fence = IntPtr.Zero;
				}

				IntPtr commands = Mtl.Id(device.Queue, S.selCommandBuffer);
				scene.EncodeShadows(commands);

				// The world layer: its colour cleared to nothing (coverage 0), its depth as a colour to the far plane.
				IntPtr world = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), S.selRenderPassDescriptor);
				ColorAttachment(world, 0, set.Color.Texture, tint ? new Mtl.ClearColor { G = 0.12, A = 0.12 } : new Mtl.ClearColor());
				ColorAttachment(world, 1, set.Depth.Texture, new Mtl.ClearColor { R = 1, G = 1, B = 1, A = 1 });
				DepthAttachment(world, set.DepthBuffer);
				IntPtr encoder = Mtl.Id(commands, S.selRenderEncoder, world);
				scene.Draw(encoder, WorldScene.WorldLayerIndex, width, height);
				Mtl.Void(encoder, S.selEndEncoding);

				// The cab layer, in its own image (laid over after OpenGL's particles, as upstream draws it), its depth its own.
				IntPtr cab = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), S.selRenderPassDescriptor);
				ColorAttachment(cab, 0, set.Cab.Texture, new Mtl.ClearColor());
				DepthAttachment(cab, set.CabDepthBuffer);
				encoder = Mtl.Id(commands, S.selRenderEncoder, cab);
				scene.Draw(encoder, WorldScene.CabLayer, width, height);
				Mtl.Void(encoder, S.selEndEncoding);

				Mtl.Void(commands, S.selCommit);
				pending = Mtl.Retain(commands);
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}
		}

		/// <summary>Waits for Metal's frame, and lays the world over OpenGL's.</summary>
		public void Finish()
		{
			if (failed || pending == IntPtr.Zero)
			{
				return;
			}

			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			string error = MetalDevice.Wait(pending);
			Mtl.Release(pending);
			pending = IntPtr.Zero;
			LastJoinTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
			if (error != null)
			{
				Fail(new InvalidOperationException("a Metal frame failed: " + error));
				return;
			}

			composite.World(sets[current].Color.GLTexture, sets[current].Depth.GLTexture);
		}

		public void CompositeCab()
		{
			if (!failed && DrawsCab && sets[current] != null)
			{
				composite.Cab(sets[current].Cab.GLTexture, sets[current].Depth.GLTexture);
			}
		}

		private void Fail(Exception ex)
		{
			failed = true;
			Log("OBNS Metal world layer stopped, OpenGL draws it from now: " + ex);
		}

		private void ReleaseTargets()
		{
			if (pending != IntPtr.Zero)
			{
				MetalDevice.Wait(pending);
				Mtl.Release(pending);
				pending = IntPtr.Zero;
			}

			// OpenGL done with every set before they go.
			GL.Finish();
			for (int i = 0; i < sets.Length; i++)
			{
				sets[i]?.Dispose();
				sets[i] = null;
			}
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => "Metal world layer (" + instance.device.Description + "), OpenGL for the rest";

		/// <summary>The layer's state for the frame statistics.</summary>
		internal static string Summary
		{
			get
			{
				if (!Enabled)
				{
					return "off";
				}

				MetalScene scene = instance.scene;
				return "on (Metal: " + scene.LastDraws + " draws (" + scene.LastCabDraws + " of them the cab's), " + scene.LastStateChanges + " pipeline and texture changes, " + scene.LastShadowCasters + " shadow casters a cascade; waited " +
				       (instance.LastJoinTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0") + " ms for the last frame; " + instance.Textures.Describe() + "; " + instance.Meshes.Describe() +
				       (scene.SkippedFaces > 0 ? "; " + scene.SkippedFaces + " faces without a copy" : "") + ")";
			}
		}

		public void Dispose()
		{
			ReleaseTargets();
			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			device.Dispose();
		}
	}
}
