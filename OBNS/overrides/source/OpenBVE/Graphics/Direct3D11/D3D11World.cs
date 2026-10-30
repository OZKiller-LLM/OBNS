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
	internal sealed class D3D11World : IWorldLayer, IDisposable
	{
		private static D3D11World instance;

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

		private static void Log(string text) => Program.FileSystem.AppendToLogFile(text);

		/// <summary>
		/// Makes the layer when the options choose Direct3D 11 (or OBNS_D3D11=1; 0 never), in a 64-bit Windows program,
		/// where Direct3D 11 can share images with OpenGL; logs which, and why. Where it cannot, Vulkan is tried.
		/// </summary>
		internal static void Initialize()
		{
			string forced = Environment.GetEnvironmentVariable("OBNS_D3D11");
			if (instance != null || forced == "0" || forced != "1" && ObnsOptions.Renderer != ObnsOptions.RendererKind.Direct3D11)
			{
				return;
			}

			if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
			{
				Log("OBNS Direct3D 11 world layer not used: it needs a 64-bit Windows program");
				return;
			}

			D3D11Device device = D3D11Device.Create(out string reason);
			string test = device?.SelfTest();
			if (device == null || test != null)
			{
				device?.Dispose();
				Log("OBNS Direct3D 11 world layer not used: " + (reason ?? test) + "; OpenGL draws it");
				return;
			}

			D3D11World world = new D3D11World(device);
			try
			{
				world.composite = new WorldComposite();
				world.Meshes = new D3D11Meshes(device);
				world.Textures = new D3D11Textures(device);
				world.scene = new D3D11Scene(device, world.Meshes, world.Textures);
				WorldHooks.Install("Direct3D 11", "OBNS_D3D11", world.Meshes, world.Textures);
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Direct3D 11 world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Direct3D 11 world layer on: " + device.Description + " (the world and cab layers, with shadows)");
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
				scene.DrawShadows();
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

			foreach (IntPtr com in new[] { colorView, depthView, cabView, depthStencilView, cabDepthStencilView, colorTexture, depthTexture, cabTexture, depthBuffer, cabDepthBuffer })
			{
				Dx.Release(com);
			}

			colorView = depthView = cabView = depthStencilView = cabDepthStencilView = colorTexture = depthTexture = cabTexture = depthBuffer = cabDepthBuffer = IntPtr.Zero;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => "Direct3D 11 world layer (" + instance.device.Description + "), OpenGL 4 for the rest";

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
			composite?.Dispose();
			composite = null;
			scene?.Dispose();
			Meshes?.Dispose();
			Textures?.Dispose();
			device.Dispose();
		}
	}
}
