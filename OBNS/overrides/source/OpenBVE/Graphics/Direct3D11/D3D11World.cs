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

// OBNS: not an upstream file. The world layer drawn by Direct3D 11 (OBNS/DIRECT3D.md): Direct3D draws the world and cab
// layers into images OpenGL has registered (WGL_NV_DX_interop2), OpenGL lays them over its frame (WorldComposite).
// The world and cab layers with shadows; chosen in the options (OBNS_D3D11=1 or 0 overrides them).

using System;
using LibRender2;
using LibRender2.ShadowMapping;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenTK.Graphics.OpenGL;
using TrainManager.Trains;

namespace OpenBve.Graphics.Direct3D11
{
	internal sealed unsafe class D3D11World : IWorldLayer, IWholeFrameLayer, IDisposable
	{
		private static D3D11World instance;

		/// <summary>Whether Direct3D 11 draws the whole game with OpenGL out of it (OBNS_D3D11_FULL=1, OBNS/RENDERERS.md).</summary>
		internal static bool Full { get; private set; } = Environment.GetEnvironmentVariable("OBNS_D3D11_FULL") == "1" && GLStub.Available && !Vulkan.VulkanDevice.Full;

		/// <summary>Before the window is made: whether a device can be had for it. If not, OpenGL draws, as usual.</summary>
		internal static void ProbeFull()
		{
			if (!Full)
			{
				return;
			}

			D3D11Device device = D3D11Device.Create(out string reason);
			if (device == null)
			{
				Full = false;
				Program.FileSystem.AppendToLogFile("OBNS Direct3D 11 can't draw the whole game: " + reason);
				return;
			}

			device.Dispose();
		}

		internal static bool Enabled => instance != null && !instance.failed;

		internal static D3D11World Instance => instance;

		private readonly D3D11Device device;
		private WorldComposite composite;
		private D3D11Scene scene;
		private bool failed;

		/// <summary>The copies of upstream's meshes and of the world's textures.</summary>
		internal D3D11Meshes Meshes { get; private set; }
		internal D3D11Textures Textures { get; private set; }

		/// <summary>Frames drawn, from 1 (the textures and meshes free what is let go of by it).</summary>
		private long frameNumber;

		// The frame's images: the world's colour, its depth as a single-channel colour (what the composite reads), the cab
		// layer's colour; Direct3D's own depth buffer; OpenGL's names for the first three, and its interop handles.
		private uint width, height;
		private IntPtr colorTexture, depthTexture, cabTexture, depthBuffer, cabDepthBuffer;
		private IntPtr colorView, depthView, cabView, depthStencilView, cabDepthStencilView;
		private int glColor, glDepth, glCab;
		private IntPtr[] handles;
		private bool locked;

		/// <summary>A tint for testing the exchange (OBNS_D3D11_TINT=1): the cleared image is a faint green, not clear.</summary>
		private static readonly bool tint = Environment.GetEnvironmentVariable("OBNS_D3D11_TINT") == "1";

		private D3D11World(D3D11Device device)
		{
			this.device = device;
		}

		private static void Log(string text) => WorldLayer.Log(text);

		/// <summary>
		/// Makes the layer when the options choose Direct3D 11 or 12 (12 where it could not be used; or OBNS_D3D11=1; 0 never), in a 64-bit Windows program,
		/// where Direct3D 11 can share images with OpenGL; logs which, and why. Where it cannot, Vulkan is tried.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_D3D11");
			if (instance != null || !Full && (forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Direct3D11 && ObnsOptions.Renderer != ObnsOptions.RendererKind.Direct3D12))
			{
				return;
			}

			if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
			{
				Log("OBNS Direct3D 11 world layer not used: it needs a 64-bit Windows program");
				return;
			}

			D3D11Device device = D3D11Device.Create(out string reason);
			string test = Full ? null : device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				if (Full)
				{
					throw new InvalidOperationException("Direct3D 11 can't draw the game, and the window has no OpenGL to fall back on: " + (reason ?? test));
				}

				Log("OBNS Direct3D 11 world layer not used: " + (reason ?? test) + "; OpenGL draws it");
				return;
			}

			D3D11World world = new D3D11World(device);
			try
			{
				if (!Full)
				{
					world.composite = new WorldComposite();
				}

				world.Meshes = new D3D11Meshes(device);
				world.Textures = new D3D11Textures(device);
				world.scene = new D3D11Scene(device, world.Meshes, world.Textures);
				WorldHooks.Install("Direct3D 11", "OBNS_D3D11", world.Meshes, world.Textures);
				if (Full)
				{
					world.frameDrawing = new D3D11FrameDrawing(device, world.scene, world.Textures, Program.Renderer.Frame);
					Program.Renderer.Frame = world.frameDrawing;
					world.window = Program.Renderer.GameWindow.WindowInfo.Handle;
				}
			}
			catch (Exception ex)
			{
				world.Dispose();
				if (Full)
				{
					throw new InvalidOperationException("Direct3D 11 can't draw the game, and the window has no OpenGL to fall back on: " + ex.Message, ex);
				}

				Log("OBNS Direct3D 11 world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Direct3D 11 world layer on: " + device.Description + " (the world and cab layers, with shadows)");
			if (Full)
			{
				Log("OBNS Direct3D 11 draws the whole game: " + device.Description + "; " + NoGLContext.Describe(world.window));
			}
		}

		// --- the whole game drawn by Direct3D 11 (OBNS/RENDERERS.md) ---

		private D3D11FrameDrawing frameDrawing;
		private IntPtr window, swapChain, backBuffer, backView, colorResource, lastFrame, lastFrameResource, staging;
		private uint swapWidth, swapHeight, swapFlags;
		private bool worldDrawn, lastFrameValid;
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

		private void ClearColor()
		{
			const float inv255 = 1.0f / 255.0f;
			Dx.ClearRenderTargetView(device.Context, colorView, Interface.CurrentOptions.ClearColor.R * inv255, Interface.CurrentOptions.ClearColor.G * inv255, Interface.CurrentOptions.ClearColor.B * inv255, 1.0f);
		}

		/// <summary>The images for the whole frame: its colour, which the shader can read for showing it, and the two depths.</summary>
		private void EnsureWholeTarget(int newWidth, int newHeight)
		{
			if (colorTexture != IntPtr.Zero && width == newWidth && height == newHeight)
			{
				return;
			}

			ReleaseTarget();
			width = (uint)Math.Max(1, newWidth);
			height = (uint)Math.Max(1, newHeight);
			Dx.Texture2DDesc desc = new Dx.Texture2DDesc { Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Dx.FormatR8G8B8A8Unorm, SampleCount = 1, Usage = Dx.UsageDefault, BindFlags = Dx.BindRenderTarget | Dx.BindShaderResource };
			colorTexture = Dx.CreateTexture2D(device.Device, desc);
			colorView = Dx.CreateRenderTargetView(device.Device, colorTexture);
			colorResource = Dx.CreateShaderResourceView(device.Device, colorTexture);
			if (MotionBlurOn)
			{
				lastFrame = Dx.CreateTexture2D(device.Device, desc);
				lastFrameResource = Dx.CreateShaderResourceView(device.Device, lastFrame);
				lastFrameValid = false;
			}

			desc.Format = Dx.FormatD32Float;
			desc.BindFlags = Dx.BindDepthStencil;
			depthBuffer = Dx.CreateTexture2D(device.Device, desc);
			cabDepthBuffer = Dx.CreateTexture2D(device.Device, desc);
			depthStencilView = Dx.CreateDepthStencilView(device.Device, depthBuffer);
			cabDepthStencilView = Dx.CreateDepthStencilView(device.Device, cabDepthBuffer);
		}

		/// <summary>The background, world, motion blur, particles and cab, in the frame's one image, as upstream draws them into its framebuffer.</summary>
		private void DrawWhole()
		{
			IntPtr context = device.Context;
			ClearColor();
			Dx.ClearDepth(context, depthStencilView, 1.0f);
			IntPtr* targets = stackalloc IntPtr[1];
			targets[0] = colorView;
			Dx.OMSetRenderTargets(context, 1, targets, depthStencilView);
			scene.Draw(WorldScene.SkyLayer, width, height);
			scene.Draw(WorldScene.WorldLayerIndex, width, height);
			if (MotionBlurOn)
			{
				if (lastFrameValid && motionBlurAlpha > 0.0f)
				{
					frameDrawing.DrawImage(lastFrameResource, width, height, motionBlurAlpha, true, false);
				}

				targets[0] = IntPtr.Zero;
				Dx.OMSetRenderTargets(context, 0, targets, IntPtr.Zero);
				Dx.CopyResource(context, lastFrame, colorTexture);
				lastFrameValid = true;
				targets[0] = colorView;
				Dx.OMSetRenderTargets(context, 1, targets, depthStencilView);
			}

			frameDrawing.RecordParticles(width, height);
			Dx.ClearDepth(context, cabDepthStencilView, 1.0f);
			Dx.OMSetRenderTargets(context, 1, targets, cabDepthStencilView);
			scene.Draw(WorldScene.CabLayer, width, height);
			targets[0] = IntPtr.Zero;
			Dx.OMSetRenderTargets(context, 0, targets, IntPtr.Zero);
			worldDrawn = true;
		}

		public void Present()
		{
			int screenWidth = Program.Renderer.Screen.Width, screenHeight = Program.Renderer.Screen.Height;
			if (Program.Renderer.Screen.Minimized || screenWidth <= 0 || screenHeight <= 0)
			{
				frameDrawing.Presented();
				worldDrawn = false;
				return;
			}

			try
			{
				IntPtr context = device.Context;
				if (!worldDrawn)
				{
					EnsureWholeTarget(screenWidth, screenHeight);
					ClearColor();
				}

				Textures.Send();
				IntPtr* targets = stackalloc IntPtr[1];
				targets[0] = colorView;
				Dx.OMSetRenderTargets(context, 1, targets, IntPtr.Zero);
				frameDrawing.Record(width, height);
				targets[0] = IntPtr.Zero;
				Dx.OMSetRenderTargets(context, 0, targets, IntPtr.Zero);
				if (snapshotFile != null)
				{
					SaveSnapshot(snapshotFile);
					snapshotFile = null;
				}

				System.Drawing.Size client = Program.Renderer.GameWindow.ClientSize;
				EnsureSwapChain((uint)Math.Max(1, client.Width), (uint)Math.Max(1, client.Height));
				targets[0] = backView;
				Dx.OMSetRenderTargets(context, 1, targets, IntPtr.Zero);
				// Row 0 of the frame's image is the bottom of the screen; a swap chain's is the top.
				frameDrawing.DrawImage(colorResource, swapWidth, swapHeight, 1.0f, false, true);
				targets[0] = IntPtr.Zero;
				Dx.OMSetRenderTargets(context, 0, targets, IntPtr.Zero);
				bool sync = Interface.CurrentOptions.VerticalSynchronization;
				Dx.Check(Dx.Present(swapChain, sync ? 1u : 0u, !sync && swapFlags != 0 ? Dx.PresentAllowTearing : 0), "IDXGISwapChain::Present");
			}
			catch (Exception ex)
			{
				Fail(ex);
			}

			frameDrawing.Presented();
			worldDrawn = false;
		}

		private void EnsureSwapChain(uint newWidth, uint newHeight)
		{
			if (swapChain != IntPtr.Zero && swapWidth == newWidth && swapHeight == newHeight)
			{
				return;
			}

			Dx.Release(backView);
			Dx.Release(backBuffer);
			backView = backBuffer = IntPtr.Zero;
			if (swapChain == IntPtr.Zero)
			{
				IntPtr factory = Dx.Factory();
				try
				{
					const uint noAltEnter = 2, flipDiscard = 4;
					swapFlags = Dx.TearingAllowed(factory) ? Dx.SwapChainAllowTearing : 0;
					Dx.SwapChainDesc desc = new Dx.SwapChainDesc
					{
						Width = newWidth,
						Height = newHeight,
						Format = Dx.FormatR8G8B8A8Unorm,
						SampleCount = 1,
						BufferUsage = Dx.UsageRenderTargetOutput,
						// Without tearing, two would hold the game to the display's rate even with vertical sync off.
						BufferCount = swapFlags != 0 ? 2u : 3u,
						Flags = swapFlags,
						OutputWindow = window,
						Windowed = 1,
						SwapEffect = flipDiscard
					};
					int hr = Dx.CreateSwapChain(factory, device.Device, desc, out swapChain);
					if (hr < 0)
					{
						// Before Windows 10: the old way of swapping.
						swapFlags = 0;
						desc.Flags = 0;
						desc.BufferCount = 1;
						desc.SwapEffect = 0;
						Dx.Check(Dx.CreateSwapChain(factory, device.Device, desc, out swapChain), "IDXGIFactory::CreateSwapChain");
					}

					// Full screen is the window's business, as with OpenGL.
					Dx.MakeWindowAssociation(factory, window, noAltEnter);
				}
				finally
				{
					Dx.Release(factory);
				}
			}
			else
			{
				Dx.ResizeBuffers(swapChain, newWidth, newHeight, swapFlags);
			}

			swapWidth = newWidth;
			swapHeight = newHeight;
			backBuffer = Dx.GetBuffer(swapChain);
			backView = Dx.CreateRenderTargetView(device.Device, backBuffer);
		}

		/// <summary>The frame's image saved as OpenGL's is.</summary>
		private void SaveSnapshot(string file)
		{
			IntPtr context = device.Context;
			if (staging == IntPtr.Zero)
			{
				staging = Dx.CreateTexture2D(device.Device, new Dx.Texture2DDesc { Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Dx.FormatR8G8B8A8Unorm, SampleCount = 1, Usage = Dx.UsageStaging, CpuAccessFlags = Dx.CpuAccessRead });
			}

			Dx.CopyResource(context, staging, colorTexture);
			byte* pixels = (byte*)Dx.MapForReading(context, staging, out uint rowPitch);
			try
			{
				WholeGame.SaveImage(pixels, rowPitch, (int)width, (int)height, file);
			}
			finally
			{
				Dx.Unmap(context, staging);
			}
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

		/// <summary>The images at the viewport's size, made again when it changes.</summary>
		private void EnsureTarget(int newWidth, int newHeight)
		{
			if (colorTexture != IntPtr.Zero && width == newWidth && height == newHeight)
			{
				return;
			}

			ReleaseTarget();
			width = (uint)Math.Max(1, newWidth);
			height = (uint)Math.Max(1, newHeight);
			Dx.Texture2DDesc desc = new Dx.Texture2DDesc { Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Dx.FormatR8G8B8A8Unorm, SampleCount = 1, Usage = Dx.UsageDefault, BindFlags = Dx.BindRenderTarget | Dx.BindShaderResource };
			colorTexture = Dx.CreateTexture2D(device.Device, desc);
			cabTexture = Dx.CreateTexture2D(device.Device, desc);
			desc.Format = Dx.FormatR32Float;
			depthTexture = Dx.CreateTexture2D(device.Device, desc);
			desc.Format = Dx.FormatD32Float;
			desc.BindFlags = Dx.BindDepthStencil;
			depthBuffer = Dx.CreateTexture2D(device.Device, desc);
			cabDepthBuffer = Dx.CreateTexture2D(device.Device, desc);
			colorView = Dx.CreateRenderTargetView(device.Device, colorTexture);
			cabView = Dx.CreateRenderTargetView(device.Device, cabTexture);
			depthView = Dx.CreateRenderTargetView(device.Device, depthTexture);
			depthStencilView = Dx.CreateDepthStencilView(device.Device, depthBuffer);
			cabDepthStencilView = Dx.CreateDepthStencilView(device.Device, cabDepthBuffer);

			glColor = GL.GenTexture();
			glDepth = GL.GenTexture();
			glCab = GL.GenTexture();
			handles = new[]
			{
				device.Register(colorTexture, glColor, D3D11Device.AccessReadOnly),
				device.Register(depthTexture, glDepth, D3D11Device.AccessReadOnly),
				device.Register(cabTexture, glCab, D3D11Device.AccessReadOnly)
			};
			foreach (int name in new[] { glColor, glDepth, glCab })
			{
				GL.BindTexture(TextureTarget.Texture2D, name);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
				GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			}

			GL.BindTexture(TextureTarget.Texture2D, 0);
			locked = false;
		}

		/// <summary>The frame's records are complete: Direct3D draws them into the images (OpenGL lets go of them first).</summary>
		public unsafe void Start(int x, int y, int viewportWidth, int viewportHeight)
		{
			if (failed)
			{
				return;
			}

			try
			{
				if (Full)
				{
					// OpenGL's viewport is a stub's.
					EnsureWholeTarget(Program.Renderer.Screen.Width, Program.Renderer.Screen.Height);
				}
				else
				{
					EnsureTarget(viewportWidth, viewportHeight);
				}

				// The faces resolved (meshes, textures made), what the frames before let go of freed, the textures' uploads sent.
				scene.Prepare();
				frameNumber++;
				Textures.FrameStarted(frameNumber);
				Meshes.FrameStarted(frameNumber);
				Textures.Send();
				cabStarted = scene.HasCab;
				scene.BuildRecords();
				scene.Upload();
				scene.DrawShadows();
				if (Full)
				{
					DrawWhole();
					return;
				}

				if (locked)
				{
					device.Unlock(handles);
					locked = false;
				}

				// The world layer: cleared to nothing (coverage 0) and the far plane, its colour and its depth as a colour.
				IntPtr context = device.Context;
				if (tint)
				{
					Dx.ClearRenderTargetView(context, colorView, 0.0f, 0.12f, 0.0f, 0.12f);
				}
				else
				{
					Dx.ClearRenderTargetView(context, colorView, 0, 0, 0, 0);
				}

				Dx.ClearRenderTargetView(context, depthView, 1, 1, 1, 1);
				Dx.ClearDepth(context, depthStencilView, 1.0f);
				IntPtr* targets = stackalloc IntPtr[2];
				targets[0] = colorView;
				targets[1] = depthView;
				Dx.OMSetRenderTargets(context, 2, targets, depthStencilView);
				scene.Draw(WorldScene.WorldLayerIndex, width, height);

				// The cab layer, in its own image (laid over after OpenGL's particles, as upstream draws it), its depth its own.
				Dx.ClearRenderTargetView(context, cabView, 0, 0, 0, 0);
				Dx.ClearDepth(context, cabDepthStencilView, 1.0f);
				targets[0] = cabView;
				Dx.OMSetRenderTargets(context, 1, targets, cabDepthStencilView);
				scene.Draw(WorldScene.CabLayer, width, height);
				targets[0] = IntPtr.Zero;
				Dx.OMSetRenderTargets(context, 0, targets, IntPtr.Zero);
				Dx.Flush(device.Context);
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}

		/// <summary>OpenGL takes the images back and lays the world over its frame.</summary>
		public void Finish()
		{
			if (failed || handles == null || Full)
			{
				return;
			}

			try
			{
				if (!locked)
				{
					device.Lock(handles);
					locked = true;
				}

				composite.World(glColor, glDepth);
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}

		public void CompositeCab()
		{
			if (!failed && locked && DrawsCab)
			{
				composite.Cab(glCab, glDepth);
			}
		}

		private void Fail(Exception ex)
		{
			if (Full)
			{
				throw new InvalidOperationException("Direct3D 11 stopped drawing the game, and the window has no OpenGL to fall back on: " + ex.Message, ex);
			}

			failed = true;
			Log("OBNS Direct3D 11 world layer stopped, OpenGL draws it from now: " + ex);
		}

		private void ReleaseTarget()
		{
			if (handles != null)
			{
				if (locked)
				{
					device.Unlock(handles);
					locked = false;
				}

				foreach (IntPtr handle in handles)
				{
					device.Unregister(handle);
				}

				handles = null;
				GL.DeleteTexture(glColor);
				GL.DeleteTexture(glDepth);
				GL.DeleteTexture(glCab);
				glColor = glDepth = glCab = 0;
			}

			foreach (IntPtr com in new[] { colorResource, lastFrameResource, lastFrame, staging })
			{
				Dx.Release(com);
			}

			colorResource = lastFrameResource = lastFrame = staging = IntPtr.Zero;
			foreach (IntPtr com in new[] { colorView, depthView, cabView, depthStencilView, cabDepthStencilView, colorTexture, depthTexture, cabTexture, depthBuffer, cabDepthBuffer })
			{
				Dx.Release(com);
			}

			colorView = depthView = cabView = depthStencilView = cabDepthStencilView = colorTexture = depthTexture = cabTexture = depthBuffer = cabDepthBuffer = IntPtr.Zero;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => Full ? "Direct3D 11 alone (" + instance.device.Description + ")" : "Direct3D 11 world layer (" + instance.device.Description + "), OpenGL 4 for the rest";

		/// <summary>The layer's state for the frame statistics.</summary>
		internal static string Summary
		{
			get
			{
				if (!Enabled)
				{
					return "off";
				}

				D3D11Scene scene = instance.scene;
				return "on (Direct3D 11: " + scene.LastDraws + " draws (" + scene.LastCabDraws + " of them the cab's), " + scene.LastStateChanges + " pipeline and texture changes, " + scene.LastShadowCasters + " shadow casters a cascade; " + instance.Textures.Describe() + "; " + instance.Meshes.Describe() +
				       (scene.SkippedFaces > 0 ? "; " + scene.SkippedFaces + " faces without a copy" : "") + ")";
			}
		}

		public void Dispose()
		{
			ReleaseTarget();
			frameDrawing?.Dispose();
			Dx.Release(backView);
			Dx.Release(backBuffer);
			Dx.Release(swapChain);
			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			device.Dispose();
		}
	}
}
