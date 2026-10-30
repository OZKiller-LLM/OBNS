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

// OBNS: not an upstream file. The Direct3D 11 world layer's device (OBNS/DIRECT3D.md): Direct3D 11 on the adapter
// OpenGL draws on (matched by LUID), and OpenGL's view of its images through WGL_NV_DX_interop2.

using System;
using System.Runtime.InteropServices;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Direct3D11
{
	internal sealed unsafe class D3D11Device : IDisposable
	{
		internal IntPtr Device { get; private set; }
		internal IntPtr Context { get; private set; }

		/// <summary>The adapter's name and feature level, for the log.</summary>
		internal string Description { get; private set; }

		// WGL_NV_DX_interop2: OpenGL's handle on the device, and its functions.
		internal IntPtr Interop { get; private set; }
		private DXOpenDevice dxOpenDevice;
		private DXCloseDevice dxCloseDevice;
		private DXRegisterObject dxRegisterObject;
		private DXUnregisterObject dxUnregisterObject;
		private DXLockObjects dxLockObjects;
		private DXUnlockObjects dxUnlockObjects;

		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DXOpenDevice(IntPtr dxDevice);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXCloseDevice(IntPtr device);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr DXRegisterObject(IntPtr device, IntPtr dxObject, uint name, uint type, uint access);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXUnregisterObject(IntPtr device, IntPtr glObject);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXLockObjects(IntPtr device, int count, IntPtr* objects);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool DXUnlockObjects(IntPtr device, int count, IntPtr* objects);
		[UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetUnsignedBytev(uint target, byte* data);

		internal const uint AccessReadOnly = 0x0000, AccessReadWrite = 0x0001, AccessWriteDiscard = 0x0002;

		private D3D11Device()
		{
		}

		/// <summary>The device on OpenGL's adapter, with OpenGL's interop opened on it; null, with the reason, if it cannot be.</summary>
		internal static D3D11Device Create(out string reason)
		{
			D3D11Device device = new D3D11Device();
			try
			{
				reason = device.Initialize();
			}
			catch (DllNotFoundException)
			{
				reason = "no Direct3D 11 (d3d11.dll, dxgi.dll)";
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

			dxOpenDevice = Function<DXOpenDevice>("wglDXOpenDeviceNV");
			dxCloseDevice = Function<DXCloseDevice>("wglDXCloseDeviceNV");
			dxRegisterObject = Function<DXRegisterObject>("wglDXRegisterObjectNV");
			dxUnregisterObject = Function<DXUnregisterObject>("wglDXUnregisterObjectNV");
			dxLockObjects = Function<DXLockObjects>("wglDXLockObjectsNV");
			dxUnlockObjects = Function<DXUnlockObjects>("wglDXUnlockObjectsNV");
			if (dxOpenDevice == null || dxCloseDevice == null || dxRegisterObject == null || dxUnregisterObject == null || dxLockObjects == null || dxUnlockObjects == null)
			{
				return "OpenGL has no WGL_NV_DX_interop2";
			}

			// OpenGL's adapter, by LUID (GL_EXT_memory_object), else the first.
			byte[] luid = null;
			GetUnsignedBytev getUnsignedBytev = Function<GetUnsignedBytev>("glGetUnsignedBytevEXT");
			if (getUnsignedBytev != null)
			{
				luid = new byte[8];
				const uint deviceLuid = 0x9599; // GL_DEVICE_LUID_EXT
				fixed (byte* data = luid)
				{
					getUnsignedBytev(deviceLuid, data);
				}

				while (GL.GetError() != ErrorCode.NoError)
				{
				}
			}

			IntPtr factory = Dx.Factory();
			IntPtr chosen = IntPtr.Zero;
			string name = null;
			try
			{
				for (uint i = 0; Dx.EnumAdapters1(factory, i, out IntPtr adapter); i++)
				{
					Dx.AdapterDesc1 desc = Dx.GetDesc1(adapter);
					bool software = (desc.Flags & 2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
					bool same = luid == null ? i == 0 : BitConverter.ToUInt32(luid, 0) == desc.LuidLow && BitConverter.ToInt32(luid, 4) == desc.LuidHigh;
					if (chosen == IntPtr.Zero && same && !software)
					{
						chosen = adapter;
						name = Dx.Name(desc).TrimEnd('\0');
					}
					else
					{
						Dx.Release(adapter);
					}
				}
			}
			finally
			{
				Dx.Release(factory);
			}

			if (chosen == IntPtr.Zero)
			{
				return "no Direct3D adapter is the GPU OpenGL is on";
			}

			int hr = Dx.CreateDevice(chosen, out IntPtr device, out IntPtr context, out int level);
			Dx.Release(chosen);
			if (hr < 0)
			{
				return "D3D11CreateDevice failed (0x" + hr.ToString("X8") + ") on " + name;
			}

			Device = device;
			Context = context;
			Description = name + ", feature level " + (level >> 12) + "." + ((level >> 8) & 0xF);
			Interop = dxOpenDevice(Device);
			if (Interop == IntPtr.Zero)
			{
				return "OpenGL could not open the Direct3D 11 device (wglDXOpenDeviceNV, error " + Marshal.GetLastWin32Error() + ")";
			}

			return null;
		}

		/// <summary>OpenGL's name for a Direct3D object (a texture, as GL_TEXTURE_2D), for wglDXLockObjectsNV.</summary>
		internal IntPtr Register(IntPtr dxObject, int glName, uint access)
		{
			IntPtr handle = dxRegisterObject(Interop, dxObject, (uint)glName, (uint)TextureTarget.Texture2D, access);
			if (handle == IntPtr.Zero)
			{
				throw new InvalidOperationException("wglDXRegisterObjectNV failed (error " + Marshal.GetLastWin32Error() + ")");
			}

			return handle;
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

		/// <summary>OpenGL lets go of the objects (its work on them is done first, as Direct3D sees it).</summary>
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
		/// Checks the whole path once: Direct3D clears an image OpenGL has registered, OpenGL locks it and reads it back.
		/// Null if the colour arrived, else what went wrong.
		/// </summary>
		internal string SelfTest()
		{
			const uint size = 16;
			IntPtr texture = IntPtr.Zero, view = IntPtr.Zero, handle = IntPtr.Zero;
			int glTexture = 0;
			try
			{
				texture = Dx.CreateTexture2D(Device, new Dx.Texture2DDesc { Width = size, Height = size, MipLevels = 1, ArraySize = 1, Format = Dx.FormatR8G8B8A8Unorm, SampleCount = 1, Usage = Dx.UsageDefault, BindFlags = Dx.BindRenderTarget | Dx.BindShaderResource });
				view = Dx.CreateRenderTargetView(Device, texture);
				glTexture = GL.GenTexture();
				handle = Register(texture, glTexture, AccessReadOnly);
				Dx.ClearRenderTargetView(Context, view, 0.25f, 0.5f, 0.75f, 1.0f);
				Dx.Flush(Context);
				IntPtr[] handles = { handle };
				Lock(handles);
				byte[] pixels = new byte[size * size * 4];
				GL.BindTexture(TextureTarget.Texture2D, glTexture);
				GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.BindTexture(TextureTarget.Texture2D, 0);
				Unlock(handles);
				ErrorCode error = GL.GetError();
				bool right = Math.Abs(pixels[0] - 64) <= 1 && Math.Abs(pixels[1] - 128) <= 1 && Math.Abs(pixels[2] - 191) <= 1 && pixels[3] == 255;
				return right && error == ErrorCode.NoError ? null : "the colour Direct3D cleared to read back as " + pixels[0] + ", " + pixels[1] + ", " + pixels[2] + ", " + pixels[3] + " (OpenGL error " + error + ")";
			}
			catch (Exception ex)
			{
				return ex.Message;
			}
			finally
			{
				Unregister(handle);
				if (glTexture != 0) GL.DeleteTexture(glTexture);
				Dx.Release(view);
				Dx.Release(texture);
			}
		}

		public void Dispose()
		{
			if (Interop != IntPtr.Zero)
			{
				dxCloseDevice(Interop);
				Interop = IntPtr.Zero;
			}

			if (Context != IntPtr.Zero)
			{
				Dx.Release(Context);
				Context = IntPtr.Zero;
			}

			if (Device != IntPtr.Zero)
			{
				Dx.Release(Device);
				Device = IntPtr.Zero;
			}
		}
	}
}
