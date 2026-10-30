// OBNS: not an upstream file. Direct3D 12 called through its COM interfaces' tables (OBNS/DIRECT3D.md), for the Direct3D
// 12 world layer; no wrapper library. Each interface's methods are at fixed places in its table (from the Windows SDK's
// d3d12.h, base interfaces first). A method returning a structure has one place, the structure written through a pointer
// passed after the interface (as Microsoft's C++ compiler calls it).

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Direct3D12
{
	internal static unsafe class D12
	{
		[DllImport("d3d12.dll")]
		private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, out IntPtr device);

		[DllImport("d3d12.dll")]
		private static extern int D3D12SerializeRootSignature(RootSignatureDesc* desc, int version, out IntPtr blob, out IntPtr errors);

		internal static readonly Guid IDevice = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");
		internal static readonly Guid ICommandQueue = new Guid("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
		internal static readonly Guid ICommandAllocator = new Guid("6102dee4-af59-4b09-b999-b44d73f09b24");
		internal static readonly Guid IGraphicsCommandList = new Guid("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
		internal static readonly Guid IFence = new Guid("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
		internal static readonly Guid IResource = new Guid("696442be-a72e-4059-bc79-5b5c98040fad");
		internal static readonly Guid IDescriptorHeap = new Guid("8efb471d-616c-4f49-90f7-127bb763fa51");
		internal static readonly Guid IRootSignature = new Guid("c54a6b66-72df-4ee8-8be5-a946a1429214");
		internal static readonly Guid IPipelineState = new Guid("765a30f3-f624-4c6f-a828-ace948622445");

		internal const int FeatureLevel11_0 = 0xb000;

		// D3D12_COMMAND_LIST_TYPE, D3D12_HEAP_TYPE, D3D12_HEAP_FLAGS, D3D12_RESOURCE_DIMENSION, D3D12_RESOURCE_FLAGS
		internal const int CommandListDirect = 0;
		internal const int HeapDefault = 1, HeapUpload = 2;
		internal const int HeapFlagNone = 0, HeapFlagShared = 1;
		internal const int DimensionBuffer = 1, DimensionTexture2D = 3;
		internal const int LayoutUnknown = 0, LayoutRowMajor = 1;
		internal const int ResourceAllowRenderTarget = 1, ResourceAllowDepthStencil = 2;

		// D3D12_RESOURCE_STATES
		internal const int StateCommon = 0, StateVertexAndConstantBuffer = 0x1, StateIndexBuffer = 0x2, StateRenderTarget = 0x4, StateDepthWrite = 0x10,
			StateNonPixelShaderResource = 0x40, StatePixelShaderResource = 0x80, StateCopyDest = 0x400, StateGenericRead = 0xAC3;

		internal const uint AllSubresources = 0xFFFFFFFF;

		// D3D12_DESCRIPTOR_HEAP_TYPE and flags
		internal const int DescriptorHeapResources = 0, DescriptorHeapSamplers = 1, DescriptorHeapRenderTargets = 2, DescriptorHeapDepthStencils = 3;
		internal const int DescriptorHeapShaderVisible = 1;

		// D3D12_SRV_DIMENSION, D3D12_DSV_DIMENSION, the default component mapping
		internal const int SrvTexture2D = 4, SrvTexture2DArray = 5, DsvTexture2DArray = 4;
		internal const uint DefaultComponentMapping = 0x1688;

		// D3D12_ROOT_PARAMETER_TYPE, D3D12_DESCRIPTOR_RANGE_TYPE, D3D12_SHADER_VISIBILITY, D3D12_ROOT_SIGNATURE_FLAGS
		internal const int RootTable = 0, RootConstants = 1, RootShaderResource = 3;
		internal const int RangeShaderResources = 0, RangeSamplers = 3;
		internal const int VisibleAll = 0, VisibleVertex = 1, VisiblePixel = 5;
		internal const int RootAllowInputLayout = 1;

		// D3D12_LOGIC_OP, D3D12_PRIMITIVE_TOPOLOGY_TYPE, D3D12_CLEAR_FLAGS, D3D12_TEXTURE_COPY_TYPE
		internal const int LogicNoop = 4, TopologyTypeTriangle = 3, ClearDepth = 1;
		internal const int CopySubresource = 0, CopyFootprint = 1;

		/// <summary>D3D12_TEXTURE_DATA_PITCH_ALIGNMENT and D3D12_TEXTURE_DATA_PLACEMENT_ALIGNMENT.</summary>
		internal const uint PitchAlignment = 256, PlacementAlignment = 512;

		internal const uint GenericAll = 0x10000000;

		internal static void Check(int hr, string call)
		{
			if (hr < 0)
			{
				throw new InvalidOperationException(call + " failed (0x" + hr.ToString("X8") + ")");
			}
		}

		private static void** Table(IntPtr com) => *(void***)com;

		internal static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Table(com)[2])(com);

		// --- structures (x64 layouts; their sizes checked against d3d12.h's by CheckLayouts) ---

		[StructLayout(LayoutKind.Sequential)]
		internal struct CommandQueueDesc
		{
			public int Type, Priority, Flags;
			public uint NodeMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct HeapProperties
		{
			public int Type, CpuPageProperty, MemoryPoolPreference;
			public uint CreationNodeMask, VisibleNodeMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ResourceDesc
		{
			public int Dimension;
			public ulong Alignment, Width;
			public uint Height;
			public ushort DepthOrArraySize, MipLevels;
			public uint Format;
			public uint SampleCount, SampleQuality;
			public int Layout, Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ResourceBarrier
		{
			public int Type, Flags;
			public IntPtr Resource;
			public uint Subresource;
			public int StateBefore, StateAfter;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorHeapDesc
		{
			public int Type;
			public uint NumDescriptors;
			public int Flags;
			public uint NodeMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ShaderResourceViewDesc
		{
			public uint Format;
			public int ViewDimension;
			public uint Shader4ComponentMapping, Pad;
			// The union (8-byte aligned, for a buffer's), as Texture2DArray (Texture2D: the first four).
			public uint MostDetailedMip, MipLevels, FirstArraySlice, ArraySize, PlaneSlice;
			public float ResourceMinLodClamp;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DepthStencilViewDesc
		{
			public uint Format;
			public int ViewDimension, Flags;
			public uint MipSlice, FirstArraySlice, ArraySize;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SamplerDesc
		{
			public uint Filter, AddressU, AddressV, AddressW;
			public float MipLodBias;
			public uint MaxAnisotropy, ComparisonFunc;
			public float BorderR, BorderG, BorderB, BorderA;
			public float MinLod, MaxLod;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct StaticSamplerDesc
		{
			public uint Filter, AddressU, AddressV, AddressW;
			public float MipLodBias;
			public uint MaxAnisotropy, ComparisonFunc, BorderColor;
			public float MinLod, MaxLod;
			public uint ShaderRegister, RegisterSpace;
			public int ShaderVisibility;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorRange
		{
			public int RangeType;
			public uint NumDescriptors, BaseShaderRegister, RegisterSpace, OffsetInDescriptorsFromTableStart;
		}

		/// <summary>D3D12_ROOT_PARAMETER: a table (count, ranges), constants (register, space, count) or a view (register, space).</summary>
		[StructLayout(LayoutKind.Explicit, Size = 32)]
		internal struct RootParameter
		{
			[FieldOffset(0)] public int ParameterType;
			[FieldOffset(8)] public uint NumDescriptorRanges;
			[FieldOffset(16)] public DescriptorRange* DescriptorRanges;
			[FieldOffset(8)] public uint ShaderRegister;
			[FieldOffset(12)] public uint RegisterSpace;
			[FieldOffset(16)] public uint Num32BitValues;
			[FieldOffset(24)] public int ShaderVisibility;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RootSignatureDesc
		{
			public uint NumParameters;
			public RootParameter* Parameters;
			public uint NumStaticSamplers;
			public StaticSamplerDesc* StaticSamplers;
			public int Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ShaderBytecode
		{
			public void* Code;
			public UIntPtr Length;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct RenderTargetBlendDesc
		{
			public int BlendEnable, LogicOpEnable;
			public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
			public int LogicOp;
			public byte RenderTargetWriteMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct StencilOpDesc
		{
			public uint StencilFailOp, StencilDepthFailOp, StencilPassOp, StencilFunc;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct GraphicsPipelineStateDesc
		{
			public IntPtr RootSignature;
			public ShaderBytecode VS, PS, DS, HS, GS;
			// D3D12_STREAM_OUTPUT_DESC
			public IntPtr SODeclaration;
			public uint SONumEntries;
			public IntPtr SOBufferStrides;
			public uint SONumStrides, SORasterizedStream;
			// D3D12_BLEND_DESC
			public int AlphaToCoverageEnable, IndependentBlendEnable;
			public RenderTargetBlendDesc Target0, Target1, Target2, Target3, Target4, Target5, Target6, Target7;
			public uint SampleMask;
			// D3D12_RASTERIZER_DESC
			public uint FillMode, CullMode;
			public int FrontCounterClockwise, DepthBias;
			public float DepthBiasClamp, SlopeScaledDepthBias;
			public int DepthClipEnable, MultisampleEnable, AntialiasedLineEnable;
			public uint ForcedSampleCount;
			public int ConservativeRaster;
			// D3D12_DEPTH_STENCIL_DESC
			public int DepthEnable;
			public uint DepthWriteMask, DepthFunc;
			public int StencilEnable;
			public byte StencilReadMask, StencilWriteMask;
			public StencilOpDesc FrontFace, BackFace;
			// D3D12_INPUT_LAYOUT_DESC
			public Direct3D11.Dx.InputElementDesc* InputElements;
			public uint NumInputElements, InputLayoutPad;
			public int IBStripCutValue, PrimitiveTopologyType;
			public uint NumRenderTargets;
			public fixed uint RTVFormats[8];
			public uint DSVFormat;
			public uint SampleCount, SampleQuality;
			public uint NodeMask;
			public IntPtr CachedBlob;
			public UIntPtr CachedBlobSize;
			public int Flags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct VertexBufferView
		{
			public ulong BufferLocation;
			public uint SizeInBytes, StrideInBytes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct IndexBufferView
		{
			public ulong BufferLocation;
			public uint SizeInBytes, Format;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Rect
		{
			public int Left, Top, Right, Bottom;
		}

		/// <summary>D3D12_TEXTURE_COPY_LOCATION: a subresource, or a placed footprint in a buffer.</summary>
		[StructLayout(LayoutKind.Explicit, Size = 48)]
		internal struct TextureCopyLocation
		{
			[FieldOffset(0)] public IntPtr Resource;
			[FieldOffset(8)] public int Type;
			[FieldOffset(16)] public uint SubresourceIndex;
			[FieldOffset(16)] public ulong Offset;
			[FieldOffset(24)] public uint Format;
			[FieldOffset(28)] public uint Width;
			[FieldOffset(32)] public uint Height;
			[FieldOffset(36)] public uint Depth;
			[FieldOffset(40)] public uint RowPitch;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ResourceAllocationInfo
		{
			public ulong SizeInBytes, Alignment;
		}

		/// <summary>The structures' sizes as d3d12.h has them on x64: a mistake in one would be a wrong call, not an error.</summary>
		internal static void CheckLayouts()
		{
			Expect(sizeof(ResourceDesc), 56, "D3D12_RESOURCE_DESC");
			Expect(sizeof(HeapProperties), 20, "D3D12_HEAP_PROPERTIES");
			Expect(sizeof(VertexBufferView), 16, "D3D12_VERTEX_BUFFER_VIEW");
			Expect((int)Marshal.OffsetOf(typeof(ShaderResourceViewDesc), nameof(ShaderResourceViewDesc.MostDetailedMip)), 16, "D3D12_SHADER_RESOURCE_VIEW_DESC.Texture2D");
			Expect(sizeof(ResourceBarrier), 32, "D3D12_RESOURCE_BARRIER");
			Expect(sizeof(ShaderResourceViewDesc), 40, "D3D12_SHADER_RESOURCE_VIEW_DESC");
			Expect(sizeof(DepthStencilViewDesc), 24, "D3D12_DEPTH_STENCIL_VIEW_DESC");
			Expect(sizeof(SamplerDesc), 52, "D3D12_SAMPLER_DESC");
			Expect(sizeof(StaticSamplerDesc), 52, "D3D12_STATIC_SAMPLER_DESC");
			Expect(sizeof(RootParameter), 32, "D3D12_ROOT_PARAMETER");
			Expect(sizeof(RootSignatureDesc), 40, "D3D12_ROOT_SIGNATURE_DESC");
			Expect(sizeof(RenderTargetBlendDesc), 40, "D3D12_RENDER_TARGET_BLEND_DESC");
			Expect(sizeof(GraphicsPipelineStateDesc), 656, "D3D12_GRAPHICS_PIPELINE_STATE_DESC");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.SampleMask)), 448, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.SampleMask");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.InputElements)), 552, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.InputLayout");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.NumRenderTargets)), 576, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.NumRenderTargets");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.CachedBlob)), 632, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.CachedPSO");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.FillMode)), 452, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.RasterizerState");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.DepthEnable)), 496, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.DepthStencilState");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.IBStripCutValue)), 568, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.IBStripCutValue");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.DSVFormat)), 612, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.DSVFormat");
			Expect((int)Marshal.OffsetOf(typeof(GraphicsPipelineStateDesc), nameof(GraphicsPipelineStateDesc.Flags)), 648, "D3D12_GRAPHICS_PIPELINE_STATE_DESC.Flags");
			Expect(sizeof(TextureCopyLocation), 48, "D3D12_TEXTURE_COPY_LOCATION");
			Expect(sizeof(Direct3D11.Dx.InputElementDesc), 32, "D3D12_INPUT_ELEMENT_DESC");
		}

		private static void Expect(int size, int expected, string name)
		{
			if (size != expected)
			{
				throw new InvalidOperationException(name + " is " + size + " bytes here, not " + expected);
			}
		}

		// --- making the device and the root signature ---

		internal static int CreateDevice(IntPtr adapter, out IntPtr device)
		{
			Guid id = IDevice;
			return D3D12CreateDevice(adapter, FeatureLevel11_0, ref id, out device);
		}

		/// <summary>A root signature's serialized form (D3D12SerializeRootSignature, version 1.0).</summary>
		internal static byte[] SerializeRootSignature(RootSignatureDesc desc)
		{
			int hr = D3D12SerializeRootSignature(&desc, 1, out IntPtr blob, out IntPtr errors);
			string messages = errors == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(Direct3D11.Dx.BlobPointer(errors), (int)Direct3D11.Dx.BlobSize(errors));
			Release(errors);
			if (hr < 0)
			{
				Release(blob);
				throw new InvalidOperationException("D3D12SerializeRootSignature failed (0x" + hr.ToString("X8") + "): " + messages);
			}

			byte[] bytes = new byte[(int)Direct3D11.Dx.BlobSize(blob)];
			Marshal.Copy(Direct3D11.Dx.BlobPointer(blob), bytes, 0, bytes.Length);
			Release(blob);
			return bytes;
		}

		// --- ID3D12Device ---

		private static IntPtr Make(int hr, string call, IntPtr made)
		{
			Check(hr, call);
			return made;
		}

		/// <summary>ID3D12Device::CreateCommandQueue (8): a direct queue.</summary>
		internal static IntPtr CreateCommandQueue(IntPtr device)
		{
			CommandQueueDesc desc = new CommandQueueDesc { Type = CommandListDirect };
			Guid id = ICommandQueue;
			IntPtr queue;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, CommandQueueDesc*, Guid*, IntPtr*, int>)Table(device)[8])(device, &desc, &id, &queue);
			return Make(hr, "ID3D12Device::CreateCommandQueue", queue);
		}

		/// <summary>ID3D12Device::CreateCommandAllocator (9).</summary>
		internal static IntPtr CreateCommandAllocator(IntPtr device)
		{
			Guid id = ICommandAllocator;
			IntPtr allocator;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, IntPtr*, int>)Table(device)[9])(device, CommandListDirect, &id, &allocator);
			return Make(hr, "ID3D12Device::CreateCommandAllocator", allocator);
		}

		/// <summary>ID3D12Device::CreateGraphicsPipelineState (10).</summary>
		internal static IntPtr CreateGraphicsPipelineState(IntPtr device, GraphicsPipelineStateDesc* desc)
		{
			Guid id = IPipelineState;
			IntPtr state;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, GraphicsPipelineStateDesc*, Guid*, IntPtr*, int>)Table(device)[10])(device, desc, &id, &state);
			return Make(hr, "ID3D12Device::CreateGraphicsPipelineState", state);
		}

		/// <summary>ID3D12Device::CreateCommandList (12): a direct list, open (recording).</summary>
		internal static IntPtr CreateCommandList(IntPtr device, IntPtr allocator)
		{
			Guid id = IGraphicsCommandList;
			IntPtr list;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, IntPtr, Guid*, IntPtr*, int>)Table(device)[12])(device, 0, CommandListDirect, allocator, IntPtr.Zero, &id, &list);
			return Make(hr, "ID3D12Device::CreateCommandList", list);
		}

		/// <summary>ID3D12Device::CreateDescriptorHeap (14).</summary>
		internal static IntPtr CreateDescriptorHeap(IntPtr device, int type, uint count, bool shaderVisible)
		{
			DescriptorHeapDesc desc = new DescriptorHeapDesc { Type = type, NumDescriptors = count, Flags = shaderVisible ? DescriptorHeapShaderVisible : 0 };
			Guid id = IDescriptorHeap;
			IntPtr heap;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, DescriptorHeapDesc*, Guid*, IntPtr*, int>)Table(device)[14])(device, &desc, &id, &heap);
			return Make(hr, "ID3D12Device::CreateDescriptorHeap", heap);
		}

		/// <summary>ID3D12Device::GetDescriptorHandleIncrementSize (15).</summary>
		internal static uint DescriptorSize(IntPtr device, int type) => ((delegate* unmanaged[Stdcall]<IntPtr, int, uint>)Table(device)[15])(device, type);

		/// <summary>ID3D12Device::CreateRootSignature (16).</summary>
		internal static IntPtr CreateRootSignature(IntPtr device, byte[] serialized)
		{
			Guid id = IRootSignature;
			IntPtr signature;
			int hr;
			fixed (byte* data = serialized)
			{
				hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, UIntPtr, Guid*, IntPtr*, int>)Table(device)[16])(device, 0, data, (UIntPtr)serialized.Length, &id, &signature);
			}

			return Make(hr, "ID3D12Device::CreateRootSignature", signature);
		}

		/// <summary>ID3D12Device::CreateShaderResourceView (18).</summary>
		internal static void CreateShaderResourceView(IntPtr device, IntPtr resource, ShaderResourceViewDesc* desc, UIntPtr handle)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ShaderResourceViewDesc*, UIntPtr, void>)Table(device)[18])(device, resource, desc, handle);
		}

		/// <summary>ID3D12Device::CreateRenderTargetView (20), with the resource's own description.</summary>
		internal static void CreateRenderTargetView(IntPtr device, IntPtr resource, UIntPtr handle)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, UIntPtr, void>)Table(device)[20])(device, resource, null, handle);
		}

		/// <summary>ID3D12Device::CreateDepthStencilView (21); null for the resource's own description.</summary>
		internal static void CreateDepthStencilView(IntPtr device, IntPtr resource, DepthStencilViewDesc* desc, UIntPtr handle)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, DepthStencilViewDesc*, UIntPtr, void>)Table(device)[21])(device, resource, desc, handle);
		}

		/// <summary>ID3D12Device::CreateSampler (22).</summary>
		internal static void CreateSampler(IntPtr device, SamplerDesc desc, UIntPtr handle)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, SamplerDesc*, UIntPtr, void>)Table(device)[22])(device, &desc, handle);
		}

		/// <summary>ID3D12Device::GetResourceAllocationInfo (25): one resource's size in memory.</summary>
		internal static ResourceAllocationInfo GetResourceAllocationInfo(IntPtr device, ResourceDesc desc)
		{
			ResourceAllocationInfo info;
			((delegate* unmanaged[Stdcall]<IntPtr, ResourceAllocationInfo*, uint, uint, ResourceDesc*, ResourceAllocationInfo*>)Table(device)[25])(device, &info, 0, 1, &desc);
			return info;
		}

		/// <summary>ID3D12Device::CreateCommittedResource (27), with no clear value.</summary>
		internal static IntPtr CreateCommittedResource(IntPtr device, int heapType, int heapFlags, ResourceDesc desc, int initialState)
		{
			HeapProperties heap = new HeapProperties { Type = heapType, CreationNodeMask = 1, VisibleNodeMask = 1 };
			Guid id = IResource;
			IntPtr resource;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, HeapProperties*, int, ResourceDesc*, int, void*, Guid*, IntPtr*, int>)Table(device)[27])(device, &heap, heapFlags, &desc, initialState, null, &id, &resource);
			return Make(hr, "ID3D12Device::CreateCommittedResource", resource);
		}

		/// <summary>ID3D12Device::CreateSharedHandle (31): an NT handle for another API (closed by the caller).</summary>
		internal static IntPtr CreateSharedHandle(IntPtr device, IntPtr deviceChild)
		{
			IntPtr handle;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, char*, IntPtr*, int>)Table(device)[31])(device, deviceChild, null, GenericAll, null, &handle);
			return Make(hr, "ID3D12Device::CreateSharedHandle", handle);
		}

		/// <summary>ID3D12Device::CreateFence (36).</summary>
		internal static IntPtr CreateFence(IntPtr device, bool shared)
		{
			Guid id = IFence;
			IntPtr fence;
			int hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong, int, Guid*, IntPtr*, int>)Table(device)[36])(device, 0, shared ? 1 : 0, &id, &fence);
			return Make(hr, "ID3D12Device::CreateFence", fence);
		}

		/// <summary>ID3D12Device::GetDeviceRemovedReason (37).</summary>
		internal static int GetDeviceRemovedReason(IntPtr device) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(device)[37])(device);

		// --- ID3D12CommandQueue ---

		/// <summary>ID3D12CommandQueue::ExecuteCommandLists (10): one list.</summary>
		internal static void ExecuteCommandList(IntPtr queue, IntPtr list)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, void>)Table(queue)[10])(queue, 1, &list);
		}

		/// <summary>ID3D12CommandQueue::Signal (14).</summary>
		internal static void Signal(IntPtr queue, IntPtr fence, ulong value)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)Table(queue)[14])(queue, fence, value), "ID3D12CommandQueue::Signal");
		}

		/// <summary>ID3D12CommandQueue::Wait (15): the queue waits (not the CPU).</summary>
		internal static void Wait(IntPtr queue, IntPtr fence, ulong value)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)Table(queue)[15])(queue, fence, value), "ID3D12CommandQueue::Wait");
		}

		// --- ID3D12Fence, ID3D12CommandAllocator, ID3D12Resource, ID3D12DescriptorHeap ---

		/// <summary>ID3D12Fence::GetCompletedValue (8).</summary>
		internal static ulong CompletedValue(IntPtr fence) => ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)Table(fence)[8])(fence);

		/// <summary>ID3D12Fence::SetEventOnCompletion (9).</summary>
		internal static void SetEventOnCompletion(IntPtr fence, ulong value, IntPtr handle)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)Table(fence)[9])(fence, value, handle), "ID3D12Fence::SetEventOnCompletion");
		}

		/// <summary>ID3D12CommandAllocator::Reset (8).</summary>
		internal static void ResetAllocator(IntPtr allocator)
		{
			Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(allocator)[8])(allocator), "ID3D12CommandAllocator::Reset");
		}

		/// <summary>ID3D12Resource::Map (8): the whole resource, nothing read.</summary>
		internal static byte* Map(IntPtr resource)
		{
			ulong* range = stackalloc ulong[2];
			range[0] = range[1] = 0;
			void* data;
			Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, ulong*, void**, int>)Table(resource)[8])(resource, 0, range, &data), "ID3D12Resource::Map");
			return (byte*)data;
		}

		/// <summary>ID3D12Resource::GetGPUVirtualAddress (11).</summary>
		internal static ulong GpuAddress(IntPtr resource) => ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)Table(resource)[11])(resource);

		/// <summary>ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart (9).</summary>
		internal static UIntPtr CpuStart(IntPtr heap)
		{
			UIntPtr handle;
			((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr*, UIntPtr*>)Table(heap)[9])(heap, &handle);
			return handle;
		}

		/// <summary>ID3D12DescriptorHeap::GetGPUDescriptorHandleForHeapStart (10).</summary>
		internal static ulong GpuStart(IntPtr heap)
		{
			ulong handle;
			((delegate* unmanaged[Stdcall]<IntPtr, ulong*, ulong*>)Table(heap)[10])(heap, &handle);
			return handle;
		}

		// --- ID3D12GraphicsCommandList ---

		/// <summary>ID3D12GraphicsCommandList::Close (9).</summary>
		internal static void Close(IntPtr list) => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Table(list)[9])(list), "ID3D12GraphicsCommandList::Close");

		/// <summary>ID3D12GraphicsCommandList::Reset (10).</summary>
		internal static void Reset(IntPtr list, IntPtr allocator) => Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Table(list)[10])(list, allocator, IntPtr.Zero), "ID3D12GraphicsCommandList::Reset");

		/// <summary>ID3D12GraphicsCommandList::DrawIndexedInstanced (13).</summary>
		internal static void DrawIndexedInstanced(IntPtr list, uint indexCount, uint instanceCount, uint startIndex, int baseVertex, uint startInstance)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, int, uint, void>)Table(list)[13])(list, indexCount, instanceCount, startIndex, baseVertex, startInstance);
		}

		/// <summary>ID3D12GraphicsCommandList::CopyBufferRegion (15).</summary>
		internal static void CopyBufferRegion(IntPtr list, IntPtr destination, ulong destinationOffset, IntPtr source, ulong sourceOffset, ulong bytes)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, IntPtr, ulong, ulong, void>)Table(list)[15])(list, destination, destinationOffset, source, sourceOffset, bytes);
		}

		/// <summary>ID3D12GraphicsCommandList::CopyTextureRegion (16): a whole subresource.</summary>
		internal static void CopyTextureRegion(IntPtr list, TextureCopyLocation destination, TextureCopyLocation source)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, TextureCopyLocation*, uint, uint, uint, TextureCopyLocation*, void*, void>)Table(list)[16])(list, &destination, 0, 0, 0, &source, null);
		}

		/// <summary>ID3D12GraphicsCommandList::IASetPrimitiveTopology (20).</summary>
		internal static void IASetPrimitiveTopology(IntPtr list, uint topology) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)Table(list)[20])(list, topology);

		/// <summary>ID3D12GraphicsCommandList::RSSetViewports (21) and RSSetScissorRects (22): the whole target.</summary>
		internal static void SetViewport(IntPtr list, uint width, uint height)
		{
			Direct3D11.Dx.Viewport viewport = new Direct3D11.Dx.Viewport { Width = width, Height = height, MaxDepth = 1.0f };
			Rect scissor = new Rect { Right = (int)width, Bottom = (int)height };
			((delegate* unmanaged[Stdcall]<IntPtr, uint, Direct3D11.Dx.Viewport*, void>)Table(list)[21])(list, 1, &viewport);
			((delegate* unmanaged[Stdcall]<IntPtr, uint, Rect*, void>)Table(list)[22])(list, 1, &scissor);
		}

		/// <summary>ID3D12GraphicsCommandList::SetPipelineState (25).</summary>
		internal static void SetPipelineState(IntPtr list, IntPtr state) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Table(list)[25])(list, state);

		/// <summary>ID3D12GraphicsCommandList::ResourceBarrier (26).</summary>
		internal static void ResourceBarriers(IntPtr list, ResourceBarrier* barriers, int count)
		{
			if (count > 0)
			{
				((delegate* unmanaged[Stdcall]<IntPtr, uint, ResourceBarrier*, void>)Table(list)[26])(list, (uint)count, barriers);
			}
		}

		/// <summary>A transition of all of a resource's subresources.</summary>
		internal static ResourceBarrier Transition(IntPtr resource, int before, int after)
		{
			return new ResourceBarrier { Resource = resource, Subresource = AllSubresources, StateBefore = before, StateAfter = after };
		}

		/// <summary>ID3D12GraphicsCommandList::SetDescriptorHeaps (28): the resources' and the samplers'.</summary>
		internal static void SetDescriptorHeaps(IntPtr list, IntPtr resources, IntPtr samplers)
		{
			IntPtr* heaps = stackalloc IntPtr[2];
			heaps[0] = resources;
			heaps[1] = samplers;
			((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, void>)Table(list)[28])(list, 2, heaps);
		}

		/// <summary>ID3D12GraphicsCommandList::SetGraphicsRootSignature (30).</summary>
		internal static void SetGraphicsRootSignature(IntPtr list, IntPtr signature) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)Table(list)[30])(list, signature);

		/// <summary>ID3D12GraphicsCommandList::SetGraphicsRootDescriptorTable (32).</summary>
		internal static void SetGraphicsRootDescriptorTable(IntPtr list, uint parameter, ulong gpuHandle) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, ulong, void>)Table(list)[32])(list, parameter, gpuHandle);

		/// <summary>ID3D12GraphicsCommandList::SetGraphicsRoot32BitConstants (36).</summary>
		internal static void SetGraphicsRoot32BitConstants(IntPtr list, uint parameter, uint count, void* data) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void*, uint, void>)Table(list)[36])(list, parameter, count, data, 0);

		/// <summary>ID3D12GraphicsCommandList::SetGraphicsRootShaderResourceView (40).</summary>
		internal static void SetGraphicsRootShaderResourceView(IntPtr list, uint parameter, ulong address) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, ulong, void>)Table(list)[40])(list, parameter, address);

		/// <summary>ID3D12GraphicsCommandList::IASetIndexBuffer (43).</summary>
		internal static void IASetIndexBuffer(IntPtr list, IndexBufferView view) => ((delegate* unmanaged[Stdcall]<IntPtr, IndexBufferView*, void>)Table(list)[43])(list, &view);

		/// <summary>ID3D12GraphicsCommandList::IASetVertexBuffers (44): one slot.</summary>
		internal static void IASetVertexBuffer(IntPtr list, uint slot, VertexBufferView view) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, VertexBufferView*, void>)Table(list)[44])(list, slot, 1, &view);

		/// <summary>ID3D12GraphicsCommandList::OMSetRenderTargets (46).</summary>
		internal static void OMSetRenderTargets(IntPtr list, uint count, UIntPtr* targets, UIntPtr* depth)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr*, int, UIntPtr*, void>)Table(list)[46])(list, count, targets, 0, depth);
		}

		/// <summary>ID3D12GraphicsCommandList::ClearDepthStencilView (47): depth only, the whole view.</summary>
		internal static void ClearDepthView(IntPtr list, UIntPtr view, float depth)
		{
			((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr, int, float, byte, uint, Rect*, void>)Table(list)[47])(list, view, ClearDepth, depth, 0, 0, null);
		}

		/// <summary>ID3D12GraphicsCommandList::ClearRenderTargetView (48): the whole view.</summary>
		internal static void ClearRenderTargetView(IntPtr list, UIntPtr view, float r, float g, float b, float a)
		{
			float* color = stackalloc float[4];
			color[0] = r;
			color[1] = g;
			color[2] = b;
			color[3] = a;
			((delegate* unmanaged[Stdcall]<IntPtr, UIntPtr, float*, uint, Rect*, void>)Table(list)[48])(list, view, color, 0, null);
		}
	}
}
