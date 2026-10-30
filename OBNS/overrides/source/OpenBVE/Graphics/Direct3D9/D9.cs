// OBNS: not an upstream file. Direct3D 9Ex called through its COM interfaces' tables (OBNS/DIRECT3D.md), for the Direct3D
// 9.0c world layer; no wrapper library. Each interface's methods are at fixed places in its table (from the Windows SDK's
// d3d9.h, which lists every interface's methods with its bases'); the constants and structures' sizes are d3d9.h's,
// checked with Microsoft's compiler.

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Direct3D9
{
	internal static unsafe class D9
	{
		[DllImport("d3d9.dll")]
		private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D);

		internal const uint SdkVersion = 32;
		internal const int DeviceHal = 1;

		// D3DFORMAT
		internal const uint FormatA8R8G8B8 = 21, FormatX8R8G8B8 = 22, FormatR32F = 114, FormatA32B32G32R32F = 116, FormatD32 = 71, FormatD24X8 = 77, FormatD32FLockable = 82, FormatIndex32 = 102;

		// D3DUSAGE, D3DPOOL, D3DRESOURCETYPE, D3DCREATE, D3DLOCK
		internal const uint UsageRenderTarget = 1, UsageDepthStencil = 2, UsageWriteOnly = 8, UsageDynamic = 0x200;
		internal const uint UsageQueryVertexTexture = 0x100000, UsageQueryBlending = 0x80000;
		internal const uint PoolDefault = 0, PoolSystemMemory = 2;
		internal const uint ResourceSurface = 1, ResourceTexture = 3;
		internal const uint CreateHardwareVertexProcessing = 0x40, CreateFpuPreserve = 0x2, CreateMultithreaded = 0x4;
		internal const uint LockDiscard = 0x2000, LockNoOverwrite = 0x1000;

		// D3DRENDERSTATETYPE
		internal const uint StateZEnable = 7, StateZWriteEnable = 14, StateAlphaTestEnable = 15, StateSrcBlend = 19, StateDestBlend = 20, StateCullMode = 22, StateZFunc = 23,
			StateAlphaBlendEnable = 27, StateStencilEnable = 52, StateLighting = 137, StateColorWriteEnable = 168, StateBlendOp = 171, StateScissorTestEnable = 174,
			StateColorWriteEnable1 = 190, StateSeparateAlphaBlendEnable = 206, StateSrcBlendAlpha = 207, StateDestBlendAlpha = 208, StateBlendOpAlpha = 209;

		// D3DBLEND, D3DBLENDOP, D3DCMPFUNC, D3DCULL
		internal const uint BlendZero = 1, BlendOne = 2, BlendSrcAlpha = 5, BlendInvSrcAlpha = 6, BlendOpAdd = 1;
		internal const uint CompareLess = 2, CompareLessEqual = 4, CompareAlways = 8;
		internal const uint CullNone = 1, CullClockwise = 2;

		// D3DSAMPLERSTATETYPE, D3DTEXTUREFILTERTYPE, D3DTEXTUREADDRESS; the first vertex texture sampler
		internal const uint SamplerAddressU = 1, SamplerAddressV = 2, SamplerMagFilter = 5, SamplerMinFilter = 6, SamplerMipFilter = 7, SamplerMaxAnisotropy = 10;
		internal const uint FilterNone = 0, FilterPoint = 1, FilterLinear = 2, FilterAnisotropic = 3;
		internal const uint AddressWrap = 1, AddressClamp = 3;
		internal const uint VertexSampler0 = 257;

		// D3DDECLTYPE, D3DDECLUSAGE
		internal const byte DeclFloat2 = 1, DeclFloat3 = 2, DeclFloat4 = 3, DeclUByte4 = 5, DeclUnused = 17;
		internal const byte UsagePosition = 0, UsageBlendIndices = 2, UsageNormal = 3, UsageTexCoord = 5, UsageColor = 10;

		// D3DCLEAR, D3DPRIMITIVETYPE, D3DQUERYTYPE, D3DISSUE, D3DGETDATA
		internal const uint ClearTarget = 1, ClearZBuffer = 2;
		internal const uint TriangleList = 4;
		internal const uint QueryEvent = 8, IssueEnd = 1, GetDataFlush = 1;

		internal static void Check(int hr, string call)
		{
			if (hr < 0)
			{
				throw new InvalidOperationException(call + " failed (0x" + hr.ToString("X8") + ")");
			}
		}

		private static void** Table(IntPtr com) => *(void***)com;

		internal static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(com)[2])(com);

		// --- structures (x64 layouts, sizes as d3d9.h's) ---

		[StructLayout(LayoutKind.Sequential)]
		internal struct PresentParameters
		{
			public uint BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount;
			public uint MultiSampleType, MultiSampleQuality, SwapEffect;
			public IntPtr DeviceWindow;
			public int Windowed, EnableAutoDepthStencil;
			public uint AutoDepthStencilFormat, Flags, FullScreenRefreshRate, PresentationInterval;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct VertexElement
		{
			public ushort Stream, Offset;
			public byte Type, Method, Usage, UsageIndex;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct LockedRect
		{
			public int Pitch;
			public byte* Bits;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Viewport
		{
			public uint X, Y, Width, Height;
			public float MinZ, MaxZ;
		}

		internal static void CheckLayouts()
		{
			if (sizeof(PresentParameters) != 64 || sizeof(VertexElement) != 8 || sizeof(LockedRect) != 16 || sizeof(Viewport) != 24 || Marshal.OffsetOf(typeof(PresentParameters), nameof(PresentParameters.Windowed)) != (IntPtr)40)
			{
				throw new InvalidOperationException("Direct3D 9's structures are laid out differently here");
			}
		}

		// --- IDirect3D9Ex ---

		internal static IntPtr Create()
		{
			Check(Direct3DCreate9Ex(SdkVersion, out IntPtr direct3D), "Direct3DCreate9Ex");
			return direct3D;
		}

		/// <summary>IDirect3D9Ex::GetAdapterCount (4).</summary>
		internal static uint AdapterCount(IntPtr d3d) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(d3d)[4])(d3d);

		/// <summary>IDirect3D9Ex::GetAdapterIdentifier (5): the adapter's description.</summary>
		internal static string AdapterName(IntPtr d3d, uint adapter)
		{
			// D3DADAPTER_IDENTIFIER9: Driver[512], Description[512], ...
			byte* identifier = stackalloc byte[1200];
			if (((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, byte*, int>)Table(d3d)[5])(d3d, adapter, 0, identifier) < 0)
			{
				return "adapter " + adapter;
			}

			return Marshal.PtrToStringAnsi((IntPtr)(identifier + 512));
		}

		/// <summary>IDirect3D9Ex::CheckDeviceFormat (10): whether a format can be used so.</summary>
		internal static bool CheckDeviceFormat(IntPtr d3d, uint adapter, uint usage, uint resourceType, uint format)
		{
			return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, uint, uint, uint, uint, int>)Table(d3d)[10])(d3d, adapter, DeviceHal, FormatX8R8G8B8, usage, resourceType, format) == 0;
		}

		/// <summary>IDirect3D9Ex::CreateDeviceEx (20).</summary>
		internal static int CreateDeviceEx(IntPtr d3d, uint adapter, IntPtr window, uint flags, PresentParameters* parameters, out IntPtr device)
		{
			IntPtr made;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, PresentParameters*, void*, IntPtr*, int>)Table(d3d)[20])(d3d, adapter, DeviceHal, window, flags, parameters, null, &made);
			device = hr < 0 ? IntPtr.Zero : made;
			return hr;
		}

		/// <summary>IDirect3D9Ex::GetAdapterLUID (21).</summary>
		internal static ulong AdapterLuid(IntPtr d3d, uint adapter)
		{
			ulong luid = 0;
			((delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, int>)Table(d3d)[21])(d3d, adapter, &luid);
			return luid;
		}

		// --- IDirect3DDevice9Ex: resources ---

		private static IntPtr Made(int hr, string call, IntPtr made)
		{
			Check(hr, call);
			return made;
		}

		/// <summary>IDirect3DDevice9Ex::CreateTexture (23); with a share handle given back when shared is not null.</summary>
		internal static IntPtr CreateTexture(IntPtr device, uint width, uint height, uint levels, uint usage, uint format, uint pool, IntPtr* shared = null)
		{
			IntPtr texture;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, uint, IntPtr*, IntPtr*, int>)Table(device)[23])(device, width, height, levels, usage, format, pool, &texture, shared);
			return Made(hr, "IDirect3DDevice9Ex::CreateTexture", texture);
		}

		/// <summary>IDirect3DDevice9Ex::CreateVertexBuffer (26).</summary>
		internal static IntPtr CreateVertexBuffer(IntPtr device, uint bytes, uint usage)
		{
			IntPtr buffer;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr*, int>)Table(device)[26])(device, bytes, usage, 0, PoolDefault, &buffer, null);
			return Made(hr, "IDirect3DDevice9Ex::CreateVertexBuffer", buffer);
		}

		/// <summary>IDirect3DDevice9Ex::CreateIndexBuffer (27): 32-bit indices.</summary>
		internal static IntPtr CreateIndexBuffer(IntPtr device, uint bytes, uint usage)
		{
			IntPtr buffer;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, IntPtr*, IntPtr*, int>)Table(device)[27])(device, bytes, usage, FormatIndex32, PoolDefault, &buffer, null);
			return Made(hr, "IDirect3DDevice9Ex::CreateIndexBuffer", buffer);
		}

		/// <summary>IDirect3DDevice9Ex::CreateDepthStencilSurface (29).</summary>
		internal static IntPtr CreateDepthStencilSurface(IntPtr device, uint width, uint height, uint format)
		{
			IntPtr surface;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int, IntPtr*, IntPtr*, int>)Table(device)[29])(device, width, height, format, 0, 0, 1, &surface, null);
			return Made(hr, "IDirect3DDevice9Ex::CreateDepthStencilSurface", surface);
		}

		/// <summary>IDirect3DDevice9Ex::CreateDepthStencilSurface (29), tried: none if the format cannot be made.</summary>
		internal static IntPtr TryCreateDepthStencilSurface(IntPtr device, uint width, uint height, uint format)
		{
			IntPtr surface;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, int, IntPtr*, IntPtr*, int>)Table(device)[29])(device, width, height, format, 0, 0, 1, &surface, null);
			return hr < 0 ? IntPtr.Zero : surface;
		}

		/// <summary>IDirect3DDevice9Ex::UpdateTexture (31): a system-memory texture's levels copied into a default one.</summary>
		internal static void UpdateTexture(IntPtr device, IntPtr source, IntPtr destination)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Table(device)[31])(device, source, destination), "IDirect3DDevice9Ex::UpdateTexture");
		}

		/// <summary>IDirect3DDevice9Ex::CreateVertexDeclaration (86).</summary>
		internal static IntPtr CreateVertexDeclaration(IntPtr device, VertexElement* elements)
		{
			IntPtr declaration;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, VertexElement*, IntPtr*, int>)Table(device)[86])(device, elements, &declaration);
			return Made(hr, "IDirect3DDevice9Ex::CreateVertexDeclaration", declaration);
		}

		/// <summary>IDirect3DDevice9Ex::CreateVertexShader (91).</summary>
		internal static IntPtr CreateVertexShader(IntPtr device, byte[] code)
		{
			IntPtr shader;
			int hr;
			fixed (byte* data = code)
			{
				hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)Table(device)[91])(device, data, &shader);
			}

			return Made(hr, "IDirect3DDevice9Ex::CreateVertexShader", shader);
		}

		/// <summary>IDirect3DDevice9Ex::CreatePixelShader (106).</summary>
		internal static IntPtr CreatePixelShader(IntPtr device, byte[] code)
		{
			IntPtr shader;
			int hr;
			fixed (byte* data = code)
			{
				hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr*, int>)Table(device)[106])(device, data, &shader);
			}

			return Made(hr, "IDirect3DDevice9Ex::CreatePixelShader", shader);
		}

		/// <summary>IDirect3DDevice9Ex::CreateQuery (118).</summary>
		internal static IntPtr CreateQuery(IntPtr device, uint type)
		{
			IntPtr query;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Table(device)[118])(device, type, &query);
			return Made(hr, "IDirect3DDevice9Ex::CreateQuery", query);
		}

		// --- IDirect3DDevice9Ex: drawing ---

		/// <summary>IDirect3DDevice9Ex::SetRenderTarget (37); a surface, or none.</summary>
		internal static void SetRenderTarget(IntPtr device, uint index, IntPtr surface)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Table(device)[37])(device, index, surface), "IDirect3DDevice9Ex::SetRenderTarget");
		}

		/// <summary>IDirect3DDevice9Ex::GetRenderTarget (38): a target (to be released).</summary>
		internal static IntPtr GetRenderTarget(IntPtr device, uint index)
		{
			IntPtr surface;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Table(device)[38])(device, index, &surface);
			return Made(hr, "IDirect3DDevice9Ex::GetRenderTarget", surface);
		}

		/// <summary>IDirect3DDevice9Ex::SetDepthStencilSurface (39).</summary>
		internal static void SetDepthStencilSurface(IntPtr device, IntPtr surface) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table(device)[39])(device, surface);

		/// <summary>IDirect3DDevice9Ex::BeginScene (41).</summary>
		internal static void BeginScene(IntPtr device) => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(device)[41])(device), "IDirect3DDevice9Ex::BeginScene");

		/// <summary>IDirect3DDevice9Ex::EndScene (42).</summary>
		internal static void EndScene(IntPtr device) => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(device)[42])(device), "IDirect3DDevice9Ex::EndScene");

		/// <summary>IDirect3DDevice9Ex::Clear (43): the whole viewport.</summary>
		internal static void Clear(IntPtr device, uint flags, uint color, float z)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, uint, uint, float, uint, int>)Table(device)[43])(device, 0, null, flags, color, z, 0);
		}

		/// <summary>IDirect3DDevice9Ex::SetViewport (47).</summary>
		internal static void SetViewport(IntPtr device, uint x, uint y, uint width, uint height)
		{
			Viewport viewport = new Viewport { X = x, Y = y, Width = width, Height = height, MaxZ = 1.0f };
			((delegate* unmanaged[Stdcall]<IntPtr, Viewport*, int>)Table(device)[47])(device, &viewport);
		}

		/// <summary>IDirect3DDevice9Ex::SetRenderState (57).</summary>
		internal static void SetRenderState(IntPtr device, uint state, uint value) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)Table(device)[57])(device, state, value);

		/// <summary>IDirect3DDevice9Ex::SetTexture (65).</summary>
		internal static void SetTexture(IntPtr device, uint sampler, IntPtr texture) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Table(device)[65])(device, sampler, texture);

		/// <summary>IDirect3DDevice9Ex::SetSamplerState (69).</summary>
		internal static void SetSamplerState(IntPtr device, uint sampler, uint type, uint value) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int>)Table(device)[69])(device, sampler, type, value);

		/// <summary>IDirect3DDevice9Ex::DrawIndexedPrimitive (82): triangles.</summary>
		internal static void DrawIndexedTriangles(IntPtr device, int baseVertex, uint vertexCount, uint startIndex, uint triangles)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, int, uint, uint, uint, uint, int>)Table(device)[82])(device, TriangleList, baseVertex, 0, vertexCount, startIndex, triangles);
		}

		/// <summary>IDirect3DDevice9Ex::SetVertexDeclaration (87).</summary>
		internal static void SetVertexDeclaration(IntPtr device, IntPtr declaration) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table(device)[87])(device, declaration);

		/// <summary>IDirect3DDevice9Ex::SetVertexShader (92).</summary>
		internal static void SetVertexShader(IntPtr device, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table(device)[92])(device, shader);

		/// <summary>IDirect3DDevice9Ex::SetVertexShaderConstantF (94): float4 registers from start.</summary>
		internal static void SetVertexConstants(IntPtr device, uint start, float* data, uint registers) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, float*, uint, int>)Table(device)[94])(device, start, data, registers);

		/// <summary>IDirect3DDevice9Ex::SetStreamSource (100).</summary>
		internal static void SetStreamSource(IntPtr device, uint stream, IntPtr buffer, uint stride) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint, int>)Table(device)[100])(device, stream, buffer, 0, stride);

		/// <summary>IDirect3DDevice9Ex::SetIndices (104).</summary>
		internal static void SetIndices(IntPtr device, IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table(device)[104])(device, buffer);

		/// <summary>IDirect3DDevice9Ex::SetPixelShader (107).</summary>
		internal static void SetPixelShader(IntPtr device, IntPtr shader) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Table(device)[107])(device, shader);

		/// <summary>IDirect3DDevice9Ex::SetPixelShaderConstantF (109): float4 registers from start.</summary>
		internal static void SetPixelConstants(IntPtr device, uint start, float* data, uint registers) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, float*, uint, int>)Table(device)[109])(device, start, data, registers);

		// --- textures, surfaces, buffers, queries ---

		/// <summary>IDirect3DTexture9::GetSurfaceLevel (18): a level's surface (to be released).</summary>
		internal static IntPtr SurfaceLevel(IntPtr texture, uint level)
		{
			IntPtr surface;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Table(texture)[18])(texture, level, &surface);
			return Made(hr, "IDirect3DTexture9::GetSurfaceLevel", surface);
		}

		/// <summary>IDirect3DTexture9::LockRect (19): a whole level.</summary>
		internal static LockedRect LockRect(IntPtr texture, uint level, uint flags)
		{
			LockedRect locked;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, LockedRect*, void*, uint, int>)Table(texture)[19])(texture, level, &locked, null, flags), "IDirect3DTexture9::LockRect");
			return locked;
		}

		/// <summary>IDirect3DTexture9::UnlockRect (20).</summary>
		internal static void UnlockRect(IntPtr texture, uint level) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table(texture)[20])(texture, level);

		/// <summary>IDirect3DVertexBuffer9::Lock and IDirect3DIndexBuffer9::Lock (11): bytes [offset, offset + size).</summary>
		internal static byte* LockBuffer(IntPtr buffer, uint offset, uint size, uint flags)
		{
			void* data;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void**, uint, int>)Table(buffer)[11])(buffer, offset, size, &data, flags), "IDirect3DVertexBuffer9::Lock");
			return (byte*)data;
		}

		/// <summary>IDirect3DVertexBuffer9::Unlock and IDirect3DIndexBuffer9::Unlock (12).</summary>
		internal static void UnlockBuffer(IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(buffer)[12])(buffer);

		/// <summary>IDirect3DQuery9::Issue (6) at the end of what is queued, and GetData (7) with flushing: sends the commands.</summary>
		internal static void Flush(IntPtr query)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Table(query)[6])(query, IssueEnd);
			((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, uint, int>)Table(query)[7])(query, null, 0, GetDataFlush);
		}
	}
}
