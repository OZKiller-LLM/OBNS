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

// OBNS: not an upstream file. The world layer drawn by Direct3D 9.0c (OBNS/DIRECT3D.md): Direct3D 9Ex draws the world and
// cab layers into textures OpenGL has registered (WGL_NV_DX_interop), OpenGL lays them over its frame (WorldComposite).
// The records and meshes as the other layers'; the slowest of them (a draw's record set per draw; shader model 3).

using System;
using OpenTK.Graphics.OpenGL;
using TrainManager.Trains;

namespace OpenBve.Graphics.Direct3D9
{
	internal sealed class D3D9World : IWorldLayer, IDisposable
	{
		private static D3D9World instance;

		internal static bool Enabled => instance != null && !instance.failed;

		internal static D3D9World Instance => instance;

		private readonly D3D9Device device;
		private WorldComposite composite;
		private D3D9Scene scene;
		private bool failed;

		/// <summary>The copies of upstream's meshes and of the world's textures.</summary>
		internal D3D9Meshes Meshes { get; private set; }
		internal D3D9Textures Textures { get; private set; }

		/// <summary>Frames drawn, from 1 (the textures and meshes free what is let go of by it).</summary>
		private long frameNumber;

		// The frame's images: the world's colour, its depth as a single-channel colour (what the composite reads), the cab
		// layer's colour, all registered with OpenGL; Direct3D's own depth buffers; the device's own target (bound when the
		// layer is not drawing, so that none of the shared textures is).
		private uint width, height;
		private IntPtr colorTexture, depthTexture, cabTexture, colorSurface, depthSurface, cabSurface, depthBuffer, cabDepthBuffer, ownTarget;
		private int glColor, glDepth, glCab;
		private IntPtr[] handles;
		private bool locked;

		/// <summary>A tint for testing the exchange (OBNS_D3D9_TINT=1): the cleared image is a faint green, not clear.</summary>
		private static readonly bool tint = Environment.GetEnvironmentVariable("OBNS_D3D9_TINT") == "1";

		private D3D9World(D3D9Device device)
		{
			this.device = device;
		}

		private static void Log(string text) => WorldLayer.Log(text);

		/// <summary>
		/// Makes the layer when the options choose Direct3D 9 (or OBNS_D3D9=1; 0 never), in a 64-bit Windows program, where
		/// Direct3D 9Ex can share images with OpenGL; logs which, and why. Where it cannot, Vulkan is tried.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_D3D9");
			if (instance != null || forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Direct3D9)
			{
				return;
			}

			if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
			{
				Log("OBNS Direct3D 9 world layer not used: it needs a 64-bit Windows program");
				return;
			}

			D3D9Device device = D3D9Device.Create(out string reason);
			string test = device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				Log("OBNS Direct3D 9 world layer not used: " + (reason ?? test) + "; Vulkan is tried");
				return;
			}

			D3D9World world = new D3D9World(device);
			try
			{
				world.composite = new WorldComposite();
				world.Meshes = new D3D9Meshes(device);
				world.Textures = new D3D9Textures(device);
				world.scene = new D3D9Scene(device, world.Meshes, world.Textures);
				world.ownTarget = D9.GetRenderTarget(device.Device, 0);
				WorldHooks.Install("Direct3D 9", "OBNS_D3D9", world.Meshes, world.Textures);
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Direct3D 9 world layer not used: " + ex.Message + "; Vulkan is tried");
				return;
			}

			instance = world;
			Log("OBNS Direct3D 9 world layer on: " + device.Description + " (the world and cab layers, with shadows" + (device.FloatBlending ? "" : "; blended faces write no depth for the composite") + ")");
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
			glColor = GL.GenTexture();
			glDepth = GL.GenTexture();
			glCab = GL.GenTexture();
			IntPtr colorHandle, depthHandle, cabHandle;
			colorTexture = device.CreateSharedTarget(width, height, D9.FormatA8R8G8B8, glColor, out colorSurface, out colorHandle);
			depthTexture = device.CreateSharedTarget(width, height, D9.FormatR32F, glDepth, out depthSurface, out depthHandle);
			cabTexture = device.CreateSharedTarget(width, height, D9.FormatA8R8G8B8, glCab, out cabSurface, out cabHandle);
			handles = new[] { colorHandle, depthHandle, cabHandle };
			depthBuffer = D9.CreateDepthStencilSurface(device.Device, width, height, device.DepthFormat);
			cabDepthBuffer = D9.CreateDepthStencilSurface(device.Device, width, height, device.DepthFormat);
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
		public void Start(int x, int y, int viewportWidth, int viewportHeight)
		{
			if (failed)
			{
				return;
			}

			try
			{
				EnsureTarget(viewportWidth, viewportHeight);
				// The faces resolved (meshes, textures made), what the frames before let go of freed, the textures' uploads sent.
				scene.Prepare();
				frameNumber++;
				Textures.FrameStarted(frameNumber);
				Meshes.FrameStarted(frameNumber);
				Textures.Send();
				cabStarted = scene.HasCab;
				scene.BuildRecords();
				scene.Upload();
				if (locked)
				{
					device.Unlock(handles);
					locked = false;
				}

				IntPtr d = device.Device;
				D9.BeginScene(d);
				scene.SetFixedStates();
				scene.DrawShadows();

				// The world layer: its depth as a colour cleared to the far plane, its colour to nothing (coverage 0).
				D9.SetDepthStencilSurface(d, depthBuffer);
				D9.SetRenderTarget(d, 0, depthSurface);
				D9.Clear(d, D9.ClearTarget, 0xFFFFFFFF, 1.0f);
				D9.SetRenderTarget(d, 0, colorSurface);
				D9.Clear(d, D9.ClearTarget | D9.ClearZBuffer, tint ? 0x1F001F00u : 0u, 1.0f);
				scene.Draw(WorldScene.WorldLayerIndex, width, height, depthSurface);

				// The cab layer, in its own image (laid over after OpenGL's particles, as upstream draws it), its depth its own.
				D9.SetRenderTarget(d, 0, cabSurface);
				D9.SetDepthStencilSurface(d, cabDepthBuffer);
				D9.Clear(d, D9.ClearTarget | D9.ClearZBuffer, 0u, 1.0f);
				scene.Draw(WorldScene.CabLayer, width, height, IntPtr.Zero);
				D9.EndScene(d);

				// None of the shared textures left bound while OpenGL has them.
				D9.SetRenderTarget(d, 0, ownTarget);
				D9.SetDepthStencilSurface(d, IntPtr.Zero);
				device.Flush();
			}
			catch (Exception ex)
			{
				Fail(ex);
			}
		}

		/// <summary>OpenGL takes the images back and lays the world over its frame.</summary>
		public void Finish()
		{
			if (failed || handles == null)
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
			failed = true;
			Log("OBNS Direct3D 9 world layer stopped, OpenGL draws it from now: " + ex);
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
			}

			foreach (int texture in new[] { glColor, glDepth, glCab })
			{
				if (texture != 0)
				{
					GL.DeleteTexture(texture);
				}
			}

			glColor = glDepth = glCab = 0;
			foreach (IntPtr com in new[] { colorSurface, depthSurface, cabSurface, colorTexture, depthTexture, cabTexture, depthBuffer, cabDepthBuffer })
			{
				D9.Release(com);
			}

			colorSurface = depthSurface = cabSurface = colorTexture = depthTexture = cabTexture = depthBuffer = cabDepthBuffer = IntPtr.Zero;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => "Direct3D 9 world layer (" + instance.device.Description + "), OpenGL 4 for the rest";

		/// <summary>The layer's state for the frame statistics.</summary>
		internal static string Summary
		{
			get
			{
				if (!Enabled)
				{
					return "off";
				}

				D3D9Scene scene = instance.scene;
				return "on (Direct3D 9: " + scene.LastDraws + " draws (" + scene.LastCabDraws + " of them the cab's), " + scene.LastStateChanges + " pipeline and texture changes, " + scene.LastShadowCasters + " shadow casters a cascade; " + instance.Textures.Describe() + "; " + instance.Meshes.Describe() +
				       (scene.SkippedFaces > 0 ? "; " + scene.SkippedFaces + " faces without a copy" : "") + ")";
			}
		}

		public void Dispose()
		{
			if (device.Device != IntPtr.Zero && ownTarget != IntPtr.Zero)
			{
				D9.SetRenderTarget(device.Device, 0, ownTarget);
			}

			ReleaseTarget();
			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			D9.Release(ownTarget);
			ownTarget = IntPtr.Zero;
			device.Dispose();
		}
	}
}
