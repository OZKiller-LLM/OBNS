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

// OBNS: not an upstream file. Direct3D 11 and DXGI called through their COM interfaces' tables (OBNS/DIRECT3D.md),
// for the Direct3D 11 world layer; no wrapper library. Each interface's methods are at fixed places in its table
// (from the Windows SDK's d3d11.idl and dxgi.idl, base interfaces first).

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Direct3D11
{
	internal static unsafe partial class Dx
	{
		[DllImport("dxgi.dll")]
		private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

		[DllImport("d3d11.dll")]
		private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, int* featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

		private static readonly Guid IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

		internal const int DriverTypeUnknown = 0;
		internal const uint CreateDeviceBgraSupport = 0x20;
		internal const uint SdkVersion = 7;
		internal const int FeatureLevel11_0 = 0xb000, FeatureLevel11_1 = 0xb100;

		// DXGI_FORMAT
		internal const uint FormatR8G8B8A8Unorm = 28, FormatR32Float = 41, FormatD32Float = 40, FormatR32Uint = 42, FormatR32G32B32Float = 6, FormatR32G32Float = 16, FormatR32G32B32Sint = 8, FormatR8G8B8A8UnormSrgb = 29;

		// D3D11_BIND_FLAG, D3D11_USAGE, D3D11_RESOURCE_MISC_FLAG
		internal const uint BindVertexBuffer = 0x1, BindIndexBuffer = 0x2, BindConstantBuffer = 0x4, BindShaderResource = 0x8, BindRenderTarget = 0x20, BindDepthStencil = 0x40;
		internal const uint UsageDefault = 0, UsageDynamic = 2, UsageStaging = 3;
		internal const uint CpuAccessWrite = 0x10000, CpuAccessRead = 0x20000;
		internal const uint MiscGenerateMips = 0x1, MiscShared = 0x2, MiscBufferStructured = 0x40;

		internal static void Check(int hr, string call)
		{
			if (hr < 0)
			{
				throw new InvalidOperationException(call + " failed (0x" + hr.ToString("X8") + ")");
			}
		}

		private static void** Table(IntPtr com) => *(void***)com;

		internal static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(com)[2])(com);

		// --- DXGI ---

		[StructLayout(LayoutKind.Sequential)]
		internal struct AdapterDesc1
		{
			public fixed char Description[128];
			public uint VendorId, DeviceId, SubSysId, Revision;
			public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
			public uint LuidLow;
			public int LuidHigh;
			public uint Flags;
		}

		/// <summary>The adapters (IDXGIAdapter1, each to be released) with their descriptions.</summary>
		internal static IntPtr Factory()
		{
			Guid id = IDXGIFactory1;
			Check(CreateDXGIFactory1(ref id, out IntPtr factory), "CreateDXGIFactory1");
			return factory;
		}

		/// <summary>IDXGIFactory1::EnumAdapters1 (12): false past the last.</summary>
		internal static bool EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
		{
			IntPtr found;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Table(factory)[12])(factory, index, &found);
			adapter = hr < 0 ? IntPtr.Zero : found;
			return hr >= 0;
		}

		/// <summary>IDXGIAdapter1::GetDesc1 (10).</summary>
		internal static AdapterDesc1 GetDesc1(IntPtr adapter)
		{
			AdapterDesc1 desc;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)Table(adapter)[10])(adapter, &desc), "IDXGIAdapter1::GetDesc1");
			return desc;
		}

		internal static string Name(AdapterDesc1 desc) => new string(desc.Description);

		// --- the device ---

		internal static int CreateDevice(IntPtr adapter, out IntPtr device, out IntPtr context, out int featureLevel)
		{
			int* levels = stackalloc int[2];
			levels[0] = FeatureLevel11_1;
			levels[1] = FeatureLevel11_0;
			int hr = D3D11CreateDevice(adapter, DriverTypeUnknown, IntPtr.Zero, CreateDeviceBgraSupport, levels, 2, SdkVersion, out device, out featureLevel, out context);
			if (hr < 0)
			{
				// An 11.0 runtime refuses 11.1 in the list.
				hr = D3D11CreateDevice(adapter, DriverTypeUnknown, IntPtr.Zero, CreateDeviceBgraSupport, levels + 1, 1, SdkVersion, out device, out featureLevel, out context);
			}

			return hr;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Texture2DDesc
		{
			public uint Width, Height, MipLevels, ArraySize, Format;
			public uint SampleCount, SampleQuality;
			public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct BufferDesc
		{
			public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SubresourceData
		{
			public void* SysMem;
			public uint SysMemPitch, SysMemSlicePitch;
		}

		/// <summary>ID3D11Device::CreateBuffer (3).</summary>
		internal static IntPtr CreateBuffer(IntPtr device, BufferDesc desc, void* initial)
		{
			SubresourceData data = new SubresourceData { SysMem = initial };
			IntPtr buffer;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, BufferDesc*, SubresourceData*, IntPtr*, int>)Table(device)[3])(device, &desc, initial == null ? null : &data, &buffer), "ID3D11Device::CreateBuffer");
			return buffer;
		}

		/// <summary>ID3D11Device::CreateTexture2D (5), empty.</summary>
		internal static IntPtr CreateTexture2D(IntPtr device, Texture2DDesc desc)
		{
			IntPtr texture;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, SubresourceData*, IntPtr*, int>)Table(device)[5])(device, &desc, null, &texture), "ID3D11Device::CreateTexture2D");
			return texture;
		}

		/// <summary>ID3D11Device::CreateShaderResourceView (7), with the resource's own description.</summary>
		internal static IntPtr CreateShaderResourceView(IntPtr device, IntPtr resource)
		{
			IntPtr view;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Table(device)[7])(device, resource, null, &view), "ID3D11Device::CreateShaderResourceView");
			return view;
		}

		/// <summary>ID3D11Device::CreateRenderTargetView (9), with the resource's own description.</summary>
		internal static IntPtr CreateRenderTargetView(IntPtr device, IntPtr resource)
		{
			IntPtr view;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Table(device)[9])(device, resource, null, &view), "ID3D11Device::CreateRenderTargetView");
			return view;
		}

		/// <summary>ID3D11Device::CreateDepthStencilView (10), with the resource's own description.</summary>
		internal static IntPtr CreateDepthStencilView(IntPtr device, IntPtr resource)
		{
			IntPtr view;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, IntPtr*, int>)Table(device)[10])(device, resource, null, &view), "ID3D11Device::CreateDepthStencilView");
			return view;
		}

		// --- the immediate context ---

		/// <summary>ID3D11DeviceContext::OMSetRenderTargets (33).</summary>
		internal static void OMSetRenderTargets(IntPtr context, uint count, IntPtr* views, IntPtr depth)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)Table(context)[33])(context, count, views, depth);
		}

		/// <summary>ID3D11DeviceContext::ClearRenderTargetView (50).</summary>
		internal static void ClearRenderTargetView(IntPtr context, IntPtr view, float r, float g, float b, float a)
		{
			float* color = stackalloc float[4];
			color[0] = r;
			color[1] = g;
			color[2] = b;
			color[3] = a;
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)Table(context)[50])(context, view, color);
		}

		/// <summary>ID3D11DeviceContext::ClearDepthStencilView (53): depth only.</summary>
		internal static void ClearDepth(IntPtr context, IntPtr view, float depth)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, float, byte, void>)Table(context)[53])(context, view, 1, depth, 0);
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Box
		{
			public uint Left, Top, Front, Right, Bottom, Back;
		}

		/// <summary>ID3D11DeviceContext::UpdateSubresource (48): a buffer's bytes [offset, offset + bytes).</summary>
		internal static void UpdateBuffer(IntPtr context, IntPtr buffer, ulong offset, void* data, ulong bytes)
		{
			Box box = new Box { Left = (uint)offset, Right = (uint)(offset + bytes), Bottom = 1, Back = 1 };
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Box*, void*, uint, uint, void>)Table(context)[48])(context, buffer, 0, &box, data, 0, 0);
		}

		/// <summary>ID3D11DeviceContext::UpdateSubresource (48): a whole subresource (a texture's level) from rows of so many bytes.</summary>
		internal static void UpdateSubresource(IntPtr context, IntPtr resource, uint subresource, void* data, uint rowPitch)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Box*, void*, uint, uint, void>)Table(context)[48])(context, resource, subresource, null, data, rowPitch, 0);
		}

		/// <summary>ID3D11DeviceContext::GenerateMips (54): every level below the first, from it.</summary>
		internal static void GenerateMips(IntPtr context, IntPtr view)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Table(context)[54])(context, view);
		}

		/// <summary>ID3D11DeviceContext::Flush (111).</summary>
		internal static void Flush(IntPtr context)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, void>)Table(context)[111])(context);
		}
	}
}
