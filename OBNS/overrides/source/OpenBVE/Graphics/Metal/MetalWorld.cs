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

// OBNS: not an upstream file. The world layer drawn by Metal on macOS (OBNS/METAL.md): Metal draws the world and cab layers
// into IOSurfaces OpenGL binds as rectangle textures, OpenGL lays them over its frame (WorldComposite). Two sets of images,
// a frame each in turn: OpenGL's fence after its composite says when a set may be drawn into again; OpenGL waits for
// Metal's frame (its command buffer completed) before laying it over.

using System;
using OpenTK.Graphics.OpenGL;
using TrainManager.Trains;

namespace OpenBve.Graphics.Metal
{
	internal sealed unsafe class MetalWorld : IWorldLayer, IWholeFrameLayer, IDisposable
	{
		private static MetalWorld instance;

		/// <summary>Whether Metal draws the whole game with OpenGL out of it (OBNS_METAL_FULL=1, OBNS/RENDERERS.md).</summary>
		internal static bool Full { get; set; }  // chosen by WholeGame.Probe

		/// <summary>Before the window is made: whether a device can be had for it. If not, OpenGL draws, as usual.</summary>
		internal static void ProbeFull()
		{
			if (!Full)
			{
				return;
			}

			MetalDevice device = MetalDevice.Create(out string reason);
			if (device != null)
			{
				device.Dispose();
				reason = GLStub.PrepareOnMac();
			}

			if (reason != null)
			{
				Full = false;
				Program.FileSystem.AppendToLogFile("OBNS Metal can't draw the whole game: " + reason);
			}
		}

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
				selSetStorageMode = Mtl.Sel("setStorageMode:"), selNewTexture = Mtl.Sel("newTextureWithDescriptor:"),
				selLayer = Mtl.Sel("layer"), selSetLayer = Mtl.Sel("setLayer:"), selSetWantsLayer = Mtl.Sel("setWantsLayer:"), selContentView = Mtl.Sel("contentView"),
				selSetDevice = Mtl.Sel("setDevice:"), selSetPixelFormat = Mtl.Sel("setPixelFormat:"), selSetDrawableSize = Mtl.Sel("setDrawableSize:"),
				selSetDisplaySync = Mtl.Sel("setDisplaySyncEnabled:"), selNextDrawable = Mtl.Sel("nextDrawable"), selTexture = Mtl.Sel("texture"),
				selPresentDrawable = Mtl.Sel("presentDrawable:"), selBlitEncoder = Mtl.Sel("blitCommandEncoder"), selCopyTexture = Mtl.Sel("copyFromTexture:toTexture:"),
				selGetBytes = Mtl.Sel("getBytes:bytesPerRow:fromRegion:mipmapLevel:");
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

		private static void Log(string text) => WorldLayer.Log(text);

		/// <summary>
		/// Makes the layer on macOS when the options choose Metal (or OBNS_METAL=1; 0 never), where Metal can share images
		/// with OpenGL; logs which, and why. Where it cannot, OpenGL draws the world.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_METAL");
			if (instance != null || !Full && (forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Metal))
			{
				return;
			}

			if (Program.CurrentHost.Platform != OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				Log("OBNS Metal world layer not used: Metal is macOS's only");
				return;
			}

			MetalDevice device = MetalDevice.Create(out string reason);
			string test = Full ? null : device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				if (Full)
				{
					throw new InvalidOperationException("Metal can't draw the game, and the window has no OpenGL to fall back on: " + (reason ?? test));
				}

				Log("OBNS Metal world layer not used: " + (reason ?? test) + "; OpenGL draws it");
				return;
			}

			MetalWorld world = new MetalWorld(device);
			try
			{
				if (!Full)
				{
					world.composite = new WorldComposite(true);
				}

				world.Meshes = new MetalMeshes(device);
				world.Textures = new MetalTextures(device);
				world.scene = new MetalScene(device, world.Meshes, world.Textures);
				WorldHooks.Install("Metal", "OBNS_METAL", world.Meshes, world.Textures);
				if (Full)
				{
					world.frameDrawing = new MetalFrameDrawing(device, world.scene, world.Textures, Program.Renderer.Frame);
					Program.Renderer.Frame = world.frameDrawing;
					world.MakeLayer();
				}
			}
			catch (Exception ex)
			{
				world.Dispose();
				if (Full)
				{
					throw new InvalidOperationException("Metal can't draw the game, and the window has no OpenGL to fall back on: " + ex.Message, ex);
				}

				Log("OBNS Metal world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Metal world layer on: " + device.Description + (device.Managed ? ", managed memory" : "") + " (the world and cab layers, with shadows; shaders and pipelines made in " + world.scene.MadeIn.ToString("0") + " ms)");
			if (Full)
			{
				Log("OBNS Metal draws the whole game: " + device.Description + "; " + NoGLContext.Describe(IntPtr.Zero));
			}
		}

		// --- the whole game drawn by Metal (OBNS/RENDERERS.md): one command buffer a frame, from the start of the frame until it is shown ---

		[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
		private delegate void GetVersion(byte* version);

		[System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
		private delegate int GetWindowInfo(IntPtr window, byte* info);

		private MetalFrameDrawing frameDrawing;
		private IntPtr metalLayer, commands, colorTexture, depthBuffer, cabDepthBuffer, lastFrame;
		private uint layerWidth, layerHeight;
		private bool layerSync, layerSyncSet, worldDrawn, lastFrameValid;
		private float motionBlurAlpha;
		private string snapshotFile;

		public WholeFrameDrawing FrameDrawing => frameDrawing;

		public void RequestSnapshot(string file)
		{
			snapshotFile = file;
		}

		public void SetMotionBlur(double frameRate, double speed)
		{
			motionBlurAlpha = WholeGame.MotionBlurAlpha(Interface.CurrentOptions.MotionBlur, frameRate, speed);
		}

		private static bool MotionBlurOn => Interface.CurrentOptions.MotionBlur != LibRender2.MotionBlurs.MotionBlurMode.None;

		/// <summary>The game window's NSWindow: OpenTK's window on Apple silicon is SDL2's, which says what it is made of.</summary>
		private static IntPtr NativeWindow()
		{
			IntPtr window = Program.Renderer.GameWindow.WindowInfo.Handle;
			IntPtr getVersion = Mtl.LoadedFunction("SDL_GetVersion"), getInfo = Mtl.LoadedFunction("SDL_GetWindowWMInfo");
			if (getVersion == IntPtr.Zero || getInfo == IntPtr.Zero)
			{
				throw new InvalidOperationException("the window isn't SDL2's: use preferNativeBackend = false");
			}

			// SDL_SysWMinfo: its version (3 bytes), its subsystem at 4, and the union at 8, whose first member on Cocoa is the NSWindow.
			byte* info = stackalloc byte[256];
			for (int i = 0; i < 256; i++)
			{
				info[i] = 0;
			}

			((GetVersion)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(getVersion, typeof(GetVersion)))(info);
			const int subsystemCocoa = 4;
			if (((GetWindowInfo)System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(getInfo, typeof(GetWindowInfo)))(window, info) == 0 || *(int*)(info + 4) != subsystemCocoa)
			{
				throw new InvalidOperationException("SDL2 can't say what the window is made of");
			}

			return *(IntPtr*)(info + 8);
		}

		/// <summary>A CAMetalLayer in place of what the window's view draws.</summary>
		private void MakeLayer()
		{
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				IntPtr view = Mtl.Id(NativeWindow(), S.selContentView);
				metalLayer = Mtl.Retain(Mtl.Id(Mtl.Class("CAMetalLayer"), S.selLayer));
				Mtl.Void(metalLayer, S.selSetDevice, device.Device);
				Mtl.Void(metalLayer, S.selSetPixelFormat, Mtl.PixelBGRA8Unorm);
				Mtl.Void(view, S.selSetLayer, metalLayer);
				Mtl.Void(view, S.selSetWantsLayer, (byte)1);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}
		}

		private IntPtr ColorImage()
		{
			IntPtr desc = Mtl.Id(Mtl.Class("MTLTextureDescriptor"), S.selTexture2D, Mtl.PixelBGRA8Unorm, (ulong)width, (ulong)height, (byte)0);
			Mtl.Void(desc, S.selSetUsage, Mtl.UsageRenderTarget | Mtl.UsageShaderRead);
			Mtl.Void(desc, S.selSetStorageMode, Mtl.StorageShared);
			return Mtl.Id(device.Device, S.selNewTexture, desc);
		}

		/// <summary>The images for the whole frame: its colour, which is read for showing it (and in memory the CPU sees, for snapshots), and the two depths.</summary>
		private void EnsureWholeTarget(int newWidth, int newHeight)
		{
			if (colorTexture != IntPtr.Zero && width == newWidth && height == newHeight)
			{
				return;
			}

			ReleaseWholeTarget();
			width = (uint)Math.Max(1, newWidth);
			height = (uint)Math.Max(1, newHeight);
			colorTexture = ColorImage();
			if (MotionBlurOn)
			{
				lastFrame = ColorImage();
				lastFrameValid = false;
			}

			depthBuffer = DepthBuffer();
			cabDepthBuffer = DepthBuffer();
		}

		private void ReleaseWholeTarget()
		{
			foreach (IntPtr image in new[] { colorTexture, lastFrame, depthBuffer, cabDepthBuffer })
			{
				Mtl.Release(image);
			}

			colorTexture = lastFrame = depthBuffer = cabDepthBuffer = IntPtr.Zero;
		}

		/// <summary>The frame's command buffer begun: what the frames before let go of freed.</summary>
		private void OpenFrame()
		{
			if (commands != IntPtr.Zero)
			{
				// A frame not shown: its drawing is finished with all the same.
				SendFrame(IntPtr.Zero);
			}

			frameNumber++;
			Textures.FrameStarted(frameNumber);
			Meshes.FrameStarted(frameNumber);
			commands = Mtl.Retain(Mtl.Id(device.Queue, S.selCommandBuffer));
		}

		/// <summary>The frame sent, and waited for: Metal draws nothing between frames, when textures and records are written.</summary>
		private void SendFrame(IntPtr drawable)
		{
			if (drawable != IntPtr.Zero)
			{
				Mtl.Void(commands, S.selPresentDrawable, drawable);
			}

			Mtl.Void(commands, S.selCommit);
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			string error = MetalDevice.Wait(commands);
			LastJoinTicks = System.Diagnostics.Stopwatch.GetTimestamp() - start;
			Mtl.Release(commands);
			commands = IntPtr.Zero;
			if (error != null)
			{
				throw new InvalidOperationException("a Metal frame failed: " + error);
			}
		}

		private static Mtl.ClearColor ClearValue()
		{
			const double inv255 = 1.0 / 255.0;
			return new Mtl.ClearColor { R = Interface.CurrentOptions.ClearColor.R * inv255, G = Interface.CurrentOptions.ClearColor.G * inv255, B = Interface.CurrentOptions.ClearColor.B * inv255, A = 1.0 };
		}

		/// <summary>A pass on the frame's image: its colour loaded or cleared, with a depth buffer or none.</summary>
		private IntPtr BeginPass(ulong colorLoad, IntPtr depth, ulong depthLoad)
		{
			IntPtr pass = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), S.selRenderPassDescriptor);
			IntPtr attachment = ColorAttachment(pass, 0, colorTexture, ClearValue());
			Mtl.Void(attachment, S.selSetLoadAction, colorLoad);
			if (depth != IntPtr.Zero)
			{
				DepthAttachment(pass, depth);
				IntPtr depthAttachment = Mtl.Id(pass, S.selDepthAttachment);
				Mtl.Void(depthAttachment, S.selSetLoadAction, depthLoad);
				Mtl.Void(depthAttachment, S.selSetStoreAction, Mtl.StoreStore);
			}

			return Mtl.Id(commands, S.selRenderEncoder, pass);
		}

		/// <summary>The background, world, motion blur, particles and cab, in the frame's one image, as upstream draws them into its framebuffer.</summary>
		private void DrawWhole()
		{
			IntPtr encoder = BeginPass(Mtl.LoadClear, depthBuffer, Mtl.LoadClear);
			scene.Draw(encoder, WorldScene.SkyLayer, width, height);
			scene.Draw(encoder, WorldScene.WorldLayerIndex, width, height);
			Mtl.Void(encoder, S.selEndEncoding);
			if (MotionBlurOn)
			{
				if (lastFrameValid && motionBlurAlpha > 0.0f)
				{
					encoder = BeginPass(Mtl.LoadLoad, IntPtr.Zero, 0);
					frameDrawing.DrawImage(encoder, lastFrame, width, height, motionBlurAlpha, true, false);
					Mtl.Void(encoder, S.selEndEncoding);
				}

				IntPtr blit = Mtl.Id(commands, S.selBlitEncoder);
				Mtl.Void(blit, S.selCopyTexture, colorTexture, lastFrame);
				Mtl.Void(blit, S.selEndEncoding);
				lastFrameValid = true;
			}

			if (frameDrawing.HasParticles)
			{
				encoder = BeginPass(Mtl.LoadLoad, depthBuffer, Mtl.LoadLoad);
				frameDrawing.RecordParticles(encoder, width, height);
				Mtl.Void(encoder, S.selEndEncoding);
			}

			encoder = BeginPass(Mtl.LoadLoad, cabDepthBuffer, Mtl.LoadClear);
			scene.Draw(encoder, WorldScene.CabLayer, width, height);
			Mtl.Void(encoder, S.selEndEncoding);
			worldDrawn = true;
		}

		/// <summary>The frame's command buffer begun, and what is under the 2D part encoded into it.</summary>
		private void StartWhole()
		{
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				// OpenGL's viewport is a stub's.
				EnsureWholeTarget(Program.Renderer.Screen.Width, Program.Renderer.Screen.Height);
				scene.Prepare();
				OpenFrame();
				Textures.Send();
				cabStarted = scene.HasCab;
				scene.BuildRecords();
				scene.Upload();
				scene.EncodeShadows(commands);
				DrawWhole();
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

		public void Present()
		{
			int screenWidth = Program.Renderer.Screen.Width, screenHeight = Program.Renderer.Screen.Height;
			bool show = !Program.Renderer.Screen.Minimized && screenWidth > 0 && screenHeight > 0;
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				if (commands == IntPtr.Zero)
				{
					if (!show)
					{
						frameDrawing.Presented();
						return;
					}

					EnsureWholeTarget(screenWidth, screenHeight);
					OpenFrame();
				}

				IntPtr drawable = IntPtr.Zero;
				if (show)
				{
					Textures.Send();
					IntPtr encoder = BeginPass(worldDrawn ? Mtl.LoadLoad : Mtl.LoadClear, IntPtr.Zero, 0);
					frameDrawing.Record(encoder, width, height);
					Mtl.Void(encoder, S.selEndEncoding);
					System.Drawing.Size client = Program.Renderer.GameWindow.ClientSize;
					UpdateLayer((uint)Math.Max(1, client.Width), (uint)Math.Max(1, client.Height));
					drawable = Mtl.Id(metalLayer, S.selNextDrawable);
					if (drawable != IntPtr.Zero)
					{
						IntPtr pass = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), S.selRenderPassDescriptor);
						IntPtr attachment = ColorAttachment(pass, 0, Mtl.Id(drawable, S.selTexture), new Mtl.ClearColor());
						Mtl.Void(attachment, S.selSetLoadAction, Mtl.LoadDontCare);
						encoder = Mtl.Id(commands, S.selRenderEncoder, pass);
						// Row 0 of the frame's image is the bottom of the screen; a drawable's is the top.
						frameDrawing.DrawImage(encoder, colorTexture, layerWidth, layerHeight, 1.0f, false, true);
						Mtl.Void(encoder, S.selEndEncoding);
					}
				}

				SendFrame(drawable);
				if (show && snapshotFile != null)
				{
					SaveSnapshot(snapshotFile);
					snapshotFile = null;
				}
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}

			frameDrawing.Presented();
			worldDrawn = false;
		}

		private void UpdateLayer(uint newWidth, uint newHeight)
		{
			if (layerWidth != newWidth || layerHeight != newHeight)
			{
				Mtl.Void(metalLayer, S.selSetDrawableSize, new Mtl.Size { Width = newWidth, Height = newHeight });
				layerWidth = newWidth;
				layerHeight = newHeight;
			}

			bool sync = Interface.CurrentOptions.VerticalSynchronization;
			if (!layerSyncSet || layerSync != sync)
			{
				Mtl.Void(metalLayer, S.selSetDisplaySync, (byte)(sync ? 1 : 0));
				layerSync = sync;
				layerSyncSet = true;
			}
		}

		/// <summary>The frame's image saved as OpenGL's is.</summary>
		private void SaveSnapshot(string file)
		{
			int w = (int)width, h = (int)height;
			IntPtr pixels = System.Runtime.InteropServices.Marshal.AllocHGlobal(w * h * 4);
			try
			{
				Mtl.Void(colorTexture, S.selGetBytes, (void*)pixels, (ulong)w * 4, new Mtl.Region { Width = (ulong)w, Height = (ulong)h, Depth = 1 }, 0UL);
				WholeGame.SaveImage((byte*)pixels, w * 4, w, h, file, true);
			}
			finally
			{
				System.Runtime.InteropServices.Marshal.FreeHGlobal(pixels);
			}
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

			if (Full)
			{
				StartWhole();
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
			if (failed || pending == IntPtr.Zero || Full)
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
			if (!failed && DrawsCab && sets[current] != null && !Full)
			{
				composite.Cab(sets[current].Cab.GLTexture, sets[current].Depth.GLTexture);
			}
		}

		private void Fail(Exception ex)
		{
			if (Full)
			{
				throw new InvalidOperationException("Metal stopped drawing the game, and the window has no OpenGL to fall back on: " + ex.Message, ex);
			}

			failed = true;
			Log("OBNS Metal world layer stopped, OpenGL draws it from now: " + ex);
		}

		private void ReleaseTargets()
		{
			ReleaseWholeTarget();
			if (Full)
			{
				return;
			}

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
		internal static string RendererName => Full ? "Metal alone (" + instance.device.Description + ")" : "Metal world layer (" + instance.device.Description + "), OpenGL for the rest";

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
			frameDrawing?.Dispose();
			Mtl.Release(metalLayer);
			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			device.Dispose();
		}
	}
}
