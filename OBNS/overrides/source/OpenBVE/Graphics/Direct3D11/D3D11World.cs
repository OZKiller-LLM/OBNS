// OBNS: not an upstream file. The world layer drawn by Direct3D 11 (OBNS/DIRECT3D.md): Direct3D draws the world and cab
// layers into images OpenGL has registered (WGL_NV_DX_interop2), OpenGL lays them over its frame (WorldComposite).
// Step 2: the images and the composite; the scene's records are not drawn yet (the layer is chosen by OBNS_D3D11=1 only).

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
		private readonly D3D11Scene scene = new D3D11Scene();
		private bool failed;

		// The frame's images: the world's colour, its depth as a single-channel colour (what the composite reads), the cab
		// layer's colour; Direct3D's own depth buffer; OpenGL's names for the first three, and its interop handles.
		private uint width, height;
		private IntPtr colorTexture, depthTexture, cabTexture, depthBuffer;
		private IntPtr colorView, depthView, cabView, depthStencilView;
		private int glColor, glDepth, glCab;
		private IntPtr[] handles;
		private bool locked;

		private static readonly bool tint = Environment.GetEnvironmentVariable("OBNS_D3D11_TINT") != "0";

		private D3D11World(D3D11Device device)
		{
			this.device = device;
		}

		private static void Log(string text) => Program.FileSystem.AppendToLogFile(text);

		/// <summary>Makes the layer with OBNS_D3D11=1 where Direct3D 11 can share images with OpenGL; logs which, and why.</summary>
		internal static void Initialize()
		{
			if (instance != null || Environment.GetEnvironmentVariable("OBNS_D3D11") != "1")
			{
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
			}
			catch (Exception ex)
			{
				world.Dispose();
				Log("OBNS Direct3D 11 world layer not used: " + ex.Message + "; OpenGL draws it");
				return;
			}

			instance = world;
			Log("OBNS Direct3D 11 world layer on: " + device.Description + " (step 2: its images laid over OpenGL's frame; the world is not drawn yet)");
		}

		public IWorldScene Scene => scene;

		public bool DrawsCab => false;

		public long LastJoinTicks => 0;

		public void Timed(long gather, long draw)
		{
		}

		public void LoadAllTextures()
		{
		}

		public void PreloadTrain(TrainBase train)
		{
		}

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
			colorView = Dx.CreateRenderTargetView(device.Device, colorTexture);
			cabView = Dx.CreateRenderTargetView(device.Device, cabTexture);
			depthView = Dx.CreateRenderTargetView(device.Device, depthTexture);
			depthStencilView = Dx.CreateDepthStencilView(device.Device, depthBuffer);

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
				if (locked)
				{
					device.Unlock(handles);
					locked = false;
				}

				// Cleared to nothing (coverage 0) and the far plane; OBNS_D3D11_TINT (on by default at this step) clears to a
				// faint green, to show the images reach OpenGL's frame.
				if (tint)
				{
					Dx.ClearRenderTargetView(device.Context, colorView, 0.0f, 0.12f, 0.0f, 0.12f);
				}
				else
				{
					Dx.ClearRenderTargetView(device.Context, colorView, 0, 0, 0, 0);
				}

				Dx.ClearRenderTargetView(device.Context, depthView, 1, 1, 1, 1);
				Dx.ClearRenderTargetView(device.Context, cabView, 0, 0, 0, 0);
				Dx.ClearDepth(device.Context, depthStencilView, 1.0f);
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

			foreach (IntPtr com in new[] { colorView, depthView, cabView, depthStencilView, colorTexture, depthTexture, cabTexture, depthBuffer })
			{
				Dx.Release(com);
			}

			colorView = depthView = cabView = depthStencilView = colorTexture = depthTexture = cabTexture = depthBuffer = IntPtr.Zero;
		}

		/// <summary>The renderer as it is drawing, for the debug overlay.</summary>
		internal static string RendererName => "Direct3D 11 world layer (" + instance.device.Description + "), OpenGL 4 for the rest";

		/// <summary>The layer's state for the frame statistics.</summary>
		internal static string Summary => Enabled ? "on (Direct3D 11, step 2: images only)" : "off";

		public void Dispose()
		{
			ReleaseTarget();
			composite?.Dispose();
			composite = null;
			device.Dispose();
		}
	}

	/// <summary>The Direct3D 11 layer's records (step 2: kept nowhere; the world is not drawn yet).</summary>
	internal sealed class D3D11Scene : IWorldScene
	{
		public void Begin(BaseRenderer renderer)
		{
		}

		public void BeginCab(Matrix4D projection, Matrix4D view, bool lit, Vector3 lightPosition)
		{
		}

		public void Pass(WorldBlend blend, int alphaFunction, float alphaReference, bool depthWrite, bool ordered, bool depthTest = true)
		{
		}

		public void SetAlphaFunction(int alphaFunction, float alphaReference)
		{
		}

		public void Face(ObjectState state, MeshFace face)
		{
		}

		public void EnsureShadowMaps(int resolution, int cascades)
		{
		}

		public void SetShadows(CascadedShadowCaster caster, float strength, bool smooth, float filterRadius, float userBias, float normalBiasTexels)
		{
		}
	}
}
