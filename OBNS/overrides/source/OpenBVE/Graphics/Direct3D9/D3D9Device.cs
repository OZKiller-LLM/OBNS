// OBNS: not an upstream file. The Direct3D 9.0c world layer's device (OBNS/DIRECT3D.md): Direct3D 9Ex on the GPU OpenGL
// draws on (by LUID), made with a hidden window of its own (it presents nothing), and OpenGL's interop opened on it
// (WGL_NV_DX_interop): textures made with share handles, registered with OpenGL, locked by OpenGL while it reads them.

using System;
using System.Runtime.InteropServices;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Direct3D9
{
	internal sealed unsafe class D3D9Device : IDisposable
	{
		internal IntPtr Direct3D { get; private set; }
		internal IntPtr Device { get; private set; }
		internal uint Adapter { get; private set; }
		internal string Description { get; private set; }

		/// <summary>OpenGL's handle for the device (wglDXOpenDeviceNV).</summary>
		internal IntPtr Interop { get; private set; }

		/// <summary>The depth buffers' format: 32-bit float as the other layers' where the GPU has it (else 24-bit).</summary>
		internal uint DepthFormat { get; private set; }

		/// <summary>Whether R32F targets can be blended into (else the depth target is left out of blended draws).</summary>
		internal bool FloatBlending { get; private set; }

		private IntPtr window, flushQuery;

		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DXOpenDevice(IntPtr dxDevice);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXCloseDevice(IntPtr device);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXSetResourceShareHandle(IntPtr dxObject, IntPtr shareHandle);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DXRegisterObject(IntPtr device, IntPtr dxObject, uint name, uint type, uint access);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXUnregisterObject(IntPtr device, IntPtr glObject);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXLockObjects(IntPtr device, int count, IntPtr* objects);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXUnlockObjects(IntPtr device, int count, IntPtr* objects);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetUnsignedBytev(uint target, byte* data);

		private DXOpenDevice dxOpenDevice;
		private DXCloseDevice dxCloseDevice;
		private DXSetResourceShareHandle dxSetResourceShareHandle;
		private DXRegisterObject dxRegisterObject;
		private DXUnregisterObject dxUnregisterObject;
		private DXLockObjects dxLockObjects;
		private DXUnlockObjects dxUnlockObjects;

		internal const uint AccessReadOnly = 0x0000;

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

		[DllImport("user32.dll")]
		private static extern bool DestroyWindow(IntPtr window);

		private D3D9Device()
		{
		}

		/// <summary>The device on OpenGL's GPU, with OpenGL's interop opened on it; null, with the reason, if it cannot be.</summary>
		internal static D3D9Device Create(out string reason)
		{
			D3D9Device device = new D3D9Device();
			try
			{
				reason = device.Initialize();
			}
			catch (DllNotFoundException)
			{
				reason = "no Direct3D 9 (d3d9.dll)";
			}
			catch (EntryPointNotFoundException)
			{
				reason = "no Direct3D 9Ex (Windows Vista or later)";
			}
			catch (Exception ex)
			{
				reason = ex.Message;
			}

			if (reason != null)
			{
				device.Dispose();
				return null;
			}

			return device;
		}

		private static T Function<T>(string name) where T : class
		{
			IntPtr address = ((IGraphicsContextInternal)GraphicsContext.CurrentContext).GetAddress(name);
			return address == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
		}

		private string Initialize()
		{
			if (Environment.OSVersion.Platform != PlatformID.Win32NT)
			{
				return "Direct3D is Windows' only";
			}

			D9.CheckLayouts();
			dxOpenDevice = Function<DXOpenDevice>("wglDXOpenDeviceNV");
			dxCloseDevice = Function<DXCloseDevice>("wglDXCloseDeviceNV");
			dxSetResourceShareHandle = Function<DXSetResourceShareHandle>("wglDXSetResourceShareHandleNV");
			dxRegisterObject = Function<DXRegisterObject>("wglDXRegisterObjectNV");
			dxUnregisterObject = Function<DXUnregisterObject>("wglDXUnregisterObjectNV");
			dxLockObjects = Function<DXLockObjects>("wglDXLockObjectsNV");
			dxUnlockObjects = Function<DXUnlockObjects>("wglDXUnlockObjectsNV");
			if (dxOpenDevice == null || dxCloseDevice == null || dxSetResourceShareHandle == null || dxRegisterObject == null || dxUnregisterObject == null || dxLockObjects == null || dxUnlockObjects == null)
			{
				return "OpenGL has no WGL_NV_DX_interop";
			}

			// OpenGL's adapter, by LUID (GL_EXT_memory_object), else the first.
			ulong luid = 0;
			GetUnsignedBytev getUnsignedBytev = Function<GetUnsignedBytev>("glGetUnsignedBytevEXT");
			if (getUnsignedBytev != null)
			{
				const uint deviceLuid = 0x9599; // GL_DEVICE_LUID_EXT
				getUnsignedBytev(deviceLuid, (byte*)&luid);
				while (GL.GetError() != ErrorCode.NoError)
				{
				}
			}

			Direct3D = D9.Create();
			uint count = D9.AdapterCount(Direct3D);
			Adapter = uint.MaxValue;
			for (uint i = 0; i < count; i++)
			{
				if (luid == 0 ? i == 0 : D9.AdapterLuid(Direct3D, i) == luid)
				{
					Adapter = i;
					break;
				}
			}

			if (Adapter == uint.MaxValue)
			{
				return "no Direct3D 9 adapter is the GPU OpenGL is on";
			}

			Description = D9.AdapterName(Direct3D, Adapter);
			// What the layer needs: records read by the vertex shader from a float texture, and R32F targets (the depth).
			if (!D9.CheckDeviceFormat(Direct3D, Adapter, D9.UsageQueryVertexTexture, D9.ResourceTexture, D9.FormatA32B32G32R32F))
			{
				return Description + " cannot read float textures in vertex shaders";
			}

			if (!D9.CheckDeviceFormat(Direct3D, Adapter, D9.UsageRenderTarget, D9.ResourceTexture, D9.FormatR32F))
			{
				return Description + " cannot draw into R32F textures";
			}

			FloatBlending = D9.CheckDeviceFormat(Direct3D, Adapter, D9.UsageRenderTarget | D9.UsageQueryBlending, D9.ResourceTexture, D9.FormatR32F);
			window = CreateWindowExW(0, "STATIC", "OBNS Direct3D 9", 0, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			D9.PresentParameters parameters = new D9.PresentParameters
			{
				BackBufferWidth = 16,
				BackBufferHeight = 16,
				BackBufferFormat = D9.FormatX8R8G8B8,
				BackBufferCount = 1,
				SwapEffect = 1, // D3DSWAPEFFECT_DISCARD
				DeviceWindow = window,
				Windowed = 1
			};
			int hr = D9.CreateDeviceEx(Direct3D, Adapter, window, D9.CreateHardwareVertexProcessing | D9.CreateFpuPreserve, &parameters, out IntPtr device);
			if (hr < 0)
			{
				return "CreateDeviceEx failed (0x" + hr.ToString("X8") + ") on " + Description;
			}

			Device = device;
			// The depth buffers as precise as the GPU makes them (the other layers' are 32-bit float): the first that can be made.
			foreach (uint format in new[] { D9.FormatD32FLockable, D9.FormatD32, D9.FormatD24X8 })
			{
				IntPtr test = D9.TryCreateDepthStencilSurface(Device, 16, 16, format);
				if (test != IntPtr.Zero)
				{
					D9.Release(test);
					DepthFormat = format;
					break;
				}
			}

			if (DepthFormat == 0)
			{
				return "no depth buffer format can be made on " + Description;
			}

			Description += ", Direct3D 9Ex, " + (DepthFormat == D9.FormatD32FLockable ? "32-bit float" : DepthFormat == D9.FormatD32 ? "32-bit" : "24-bit") + " depth";
			flushQuery = D9.CreateQuery(Device, D9.QueryEvent);
			Interop = dxOpenDevice(Device);
			if (Interop == IntPtr.Zero)
			{
				return "OpenGL could not open the Direct3D 9 device (wglDXOpenDeviceNV, error " + Marshal.GetLastWin32Error() + ")";
			}

			return null;
		}

		/// <summary>Sends what has been queued to the GPU.</summary>
		internal void Flush() => D9.Flush(flushQuery);

		/// <summary>
		/// A texture Direct3D draws into and OpenGL reads: made shared, its share handle given to the interop, registered as
		/// an OpenGL texture; with its top surface (to be released) and OpenGL's handle for it.
		/// </summary>
		internal IntPtr CreateSharedTarget(uint width, uint height, uint format, int glName, out IntPtr surface, out IntPtr handle)
		{
			IntPtr share = IntPtr.Zero;
			IntPtr texture = D9.CreateTexture(Device, width, height, 1, D9.UsageRenderTarget, format, D9.PoolDefault, &share);
			if (!dxSetResourceShareHandle(texture, share))
			{
				D9.Release(texture);
				throw new InvalidOperationException("wglDXSetResourceShareHandleNV failed (error " + Marshal.GetLastWin32Error() + ")");
			}

			handle = dxRegisterObject(Interop, texture, (uint)glName, (uint)TextureTarget.Texture2D, AccessReadOnly);
			if (handle == IntPtr.Zero)
			{
				D9.Release(texture);
				throw new InvalidOperationException("wglDXRegisterObjectNV failed (error " + Marshal.GetLastWin32Error() + ")");
			}

			surface = D9.SurfaceLevel(texture, 0);
			return texture;
		}

		internal void Unregister(IntPtr handle)
		{
			if (handle != IntPtr.Zero && Interop != IntPtr.Zero)
			{
				dxUnregisterObject(Interop, handle);
			}
		}

		/// <summary>OpenGL takes the objects (Direct3D's work on them before this is done first); Direct3D must not use them until Unlock.</summary>
		internal void Lock(IntPtr[] handles)
		{
			fixed (IntPtr* objects = handles)
			{
				if (!dxLockObjects(Interop, handles.Length, objects))
				{
					throw new InvalidOperationException("wglDXLockObjectsNV failed (error " + Marshal.GetLastWin32Error() + ")");
				}
			}
		}

		/// <summary>OpenGL lets go of the objects.</summary>
		internal void Unlock(IntPtr[] handles)
		{
			fixed (IntPtr* objects = handles)
			{
				if (!dxUnlockObjects(Interop, handles.Length, objects))
				{
					throw new InvalidOperationException("wglDXUnlockObjectsNV failed (error " + Marshal.GetLastWin32Error() + ")");
				}
			}
		}

		/// <summary>
		/// Checks the whole path once: Direct3D clears a shared texture (and an R32F one) OpenGL has registered, OpenGL locks
		/// them and reads them back. Null if the values arrived, else what went wrong.
		/// </summary>
		internal string SelfTest()
		{
			const uint size = 16;
			IntPtr color = IntPtr.Zero, depth = IntPtr.Zero, colorSurface = IntPtr.Zero, depthSurface = IntPtr.Zero, colorHandle = IntPtr.Zero, depthHandle = IntPtr.Zero;
			int glColor = 0, glDepth = 0;
			try
			{
				glColor = GL.GenTexture();
				glDepth = GL.GenTexture();
				color = CreateSharedTarget(size, size, D9.FormatA8R8G8B8, glColor, out colorSurface, out colorHandle);
				depth = CreateSharedTarget(size, size, D9.FormatR32F, glDepth, out depthSurface, out depthHandle);
				D9.SetRenderTarget(Device, 0, colorSurface);
				D9.SetViewport(Device, 0, 0, size, size);
				D9.Clear(Device, D9.ClearTarget, 0xFF4080BF, 1.0f);
				D9.SetRenderTarget(Device, 0, depthSurface);
				D9.Clear(Device, D9.ClearTarget, 0xFFFFFFFF, 1.0f);
				Flush();
				IntPtr[] handles = { colorHandle, depthHandle };
				Lock(handles);
				byte[] pixels = new byte[size * size * 4];
				float[] depths = new float[size * size];
				GL.BindTexture(TextureTarget.Texture2D, glColor);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.BindTexture(TextureTarget.Texture2D, glDepth);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Red, PixelType.Float, depths);
				GL.BindTexture(TextureTarget.Texture2D, 0);
				Unlock(handles);
				ErrorCode error = GL.GetError();
				bool right = Math.Abs(pixels[0] - 64) <= 1 && Math.Abs(pixels[1] - 128) <= 1 && Math.Abs(pixels[2] - 191) <= 1 && pixels[3] == 255 && Math.Abs(depths[0] - 1.0f) < 1e-6f;
				return right && error == ErrorCode.NoError ? null : "Direct3D 9's clears read back as " + pixels[0] + ", " + pixels[1] + ", " + pixels[2] + ", " + pixels[3] + " and " + depths[0] + " (OpenGL error " + error + ")";
			}
			catch (Exception ex)
			{
				return ex.Message;
			}
			finally
			{
				// (The device keeps its last target until it is given another: the first target cannot be none.)
				Unregister(colorHandle);
				Unregister(depthHandle);
				if (glColor != 0) GL.DeleteTexture(glColor);
				if (glDepth != 0) GL.DeleteTexture(glDepth);
				D9.Release(colorSurface);
				D9.Release(depthSurface);
				D9.Release(color);
				D9.Release(depth);
			}
		}

		public void Dispose()
		{
			if (Interop != IntPtr.Zero)
			{
				dxCloseDevice(Interop);
				Interop = IntPtr.Zero;
			}

			D9.Release(flushQuery);
			D9.Release(Device);
			D9.Release(Direct3D);
			flushQuery = Device = Direct3D = IntPtr.Zero;
			if (window != IntPtr.Zero)
			{
				DestroyWindow(window);
				window = IntPtr.Zero;
			}
		}
	}
}
