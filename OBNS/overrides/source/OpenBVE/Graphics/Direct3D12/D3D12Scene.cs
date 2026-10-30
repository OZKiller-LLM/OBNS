// OBNS: not an upstream file. The Direct3D 12 world layer's frame (OBNS/DIRECT3D.md): the scene's records (WorldScene) in
// upload memory read by the shaders as structured buffers (root views), each draw a DrawIndexedInstanced whose instance
// stream gives the shaders its record's index, as Direct3D 11's; the states are pipeline objects, made when first needed.
// The shaders are Direct3D 11's (Direct3D11/Shaders/world.hlsl), bound through a root signature.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Graphics;
using Blend = OpenBve.Graphics.WorldBlend;
using Dx = OpenBve.Graphics.Direct3D11.Dx;

namespace OpenBve.Graphics.Direct3D12
{
	internal sealed unsafe class D3D12Scene : WorldScene
	{
		/// <summary>Frames recorded while the GPU draws the one before: the records' buffers are a set per frame in flight.</summary>
		internal const int FramesInFlight = 2;

		// The root signature's parameters.
		private const uint RootLayer = 0, RootRecords = 1, RootTexture = 5, RootShadowMap = 6, RootSampler = 7;

		private readonly D3D12Device device;
		private readonly D3D12Meshes meshes;
		private readonly D3D12Textures textures;

		protected override WorldTextures SceneTextures => textures;

		private IntPtr rootSignature;
		private byte[] vertexCode, blendedCode, opaqueCode, shadowVertexCode, shadowPixelCode;
		/// <summary>The pipeline objects by key: the draw's pipeline key, and the layer (the cab's has one target).</summary>
		private readonly Dictionary<int, IntPtr> pipelines = new Dictionary<int, IntPtr>();
		private readonly IntPtr[] shadowPipelines = new IntPtr[2];

		/// <summary>The samplers by OpenGL wrap mode (ClampClamp, ClampRepeat, RepeatClamp, RepeatRepeat), in their shader-visible heap.</summary>
		internal readonly IntPtr SamplerHeap;
		private readonly ulong samplerStart;
		private readonly uint samplerSize;

		/// <summary>The records' buffers per frame in flight (frames, objects, draws, matrices): upload memory, mapped.</summary>
		private readonly IntPtr[,] recordBuffers = new IntPtr[FramesInFlight, 4];
		private readonly byte*[,] recordData = new byte*[FramesInFlight, 4];
		private readonly long[,] recordSizes = new long[FramesInFlight, 4];
		private readonly ulong[] recordAddresses = new ulong[4];

		// The shadow pass: the maps (one array, a layer per cascade; written as depth, read through a comparison sampler),
		// their depth views, and whether they are readable (else being written).
		private IntPtr shadowMap, shadowViews;
		private int shadowResolution, shadowLayers;
		private bool shadowReadable;
		private uint depthViewSize;

		/// <summary>The instance stream: 0, 1, 2, ... (a draw's StartInstanceLocation picks its record); replaced ones kept until done with.</summary>
		private IntPtr instanceBuffer;
		private ulong instanceAddress;
		private int instanceCount;
		private readonly List<KeyValuePair<IntPtr, long>> retired = new List<KeyValuePair<IntPtr, long>>();

		/// <summary>Pipeline and texture changes in the last frame, for the log.</summary>
		internal int LastStateChanges { get; private set; }

		internal D3D12Scene(D3D12Device device, D3D12Meshes meshes, D3D12Textures textures)
		{
			this.device = device;
			this.meshes = meshes;
			this.textures = textures;
			// Pages of one texture: the draws are grouped by their texture (its view is bound per draw).
			TexturePage = 1;
			CreateShaders();
			CreateRootSignature();
			SamplerHeap = D12.CreateDescriptorHeap(device.Device, D12.DescriptorHeapSamplers, 4, true);
			samplerStart = D12.GpuStart(SamplerHeap);
			samplerSize = D12.DescriptorSize(device.Device, D12.DescriptorHeapSamplers);
			CreateSamplers(Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			depthViewSize = D12.DescriptorSize(device.Device, D12.DescriptorHeapDepthStencils);
			EnsureShadowMaps(1, 1);
			MakePipelines();
		}

		/// <summary>Every pipeline object, made now (at the game's start) rather than in the frame that first needs it.</summary>
		private void MakePipelines()
		{
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			for (int layer = 0; layer < 2; layer++)
			{
				for (int key = 0; key < 32; key++)
				{
					// Blending is none, normal or additive (WorldBlend's 0 to 2).
					if ((key & 3) != 3)
					{
						Pipeline(key, layer);
					}
				}
			}

			ShadowPipeline(false);
			ShadowPipeline(true);
			MadeIn = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		}

		/// <summary>The time the pipeline objects took to make, for the log.</summary>
		internal double MadeIn { get; private set; }

		/// <summary>The pipeline objects made, for the log.</summary>
		internal int PipelineCount => pipelines.Count + 2;

		private void CreateShaders()
		{
			string source;
			using (Stream stream = typeof(D3D12Scene).Assembly.GetManifestResourceStream("OBNS.Direct3D11.world.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			vertexCode = Dx.Compile(source, "world.hlsl", "VS", "vs_5_0", "OPAQUE_COVERAGE", "0");
			blendedCode = Dx.Compile(source, "world.hlsl", "PS", "ps_5_0", "OPAQUE_COVERAGE", "0");
			opaqueCode = Dx.Compile(source, "world.hlsl", "PS", "ps_5_0", "OPAQUE_COVERAGE", "1");
			shadowVertexCode = Dx.Compile(source, "world.hlsl", "ShadowVS", "vs_5_0", "OPAQUE_COVERAGE", "0");
			shadowPixelCode = Dx.Compile(source, "world.hlsl", "ShadowPS", "ps_5_0", "OPAQUE_COVERAGE", "0");
		}

		/// <summary>
		/// The shaders' bindings: the layer and cascade (b0, constants), the records (t0-t3, root views), the face's texture
		/// (t4), the shadow maps (t5) and the face's sampler (s0) by tables, the shadow maps' comparison sampler (s1) fixed.
		/// </summary>
		private void CreateRootSignature()
		{
			D12.DescriptorRange* ranges = stackalloc D12.DescriptorRange[3];
			ranges[0] = new D12.DescriptorRange { RangeType = D12.RangeShaderResources, NumDescriptors = 1, BaseShaderRegister = 4 };
			ranges[1] = new D12.DescriptorRange { RangeType = D12.RangeShaderResources, NumDescriptors = 1, BaseShaderRegister = 5 };
			ranges[2] = new D12.DescriptorRange { RangeType = D12.RangeSamplers, NumDescriptors = 1, BaseShaderRegister = 0 };
			D12.RootParameter* parameters = stackalloc D12.RootParameter[8];
			parameters[0] = new D12.RootParameter { ParameterType = D12.RootConstants, ShaderRegister = 0, Num32BitValues = 4, ShaderVisibility = D12.VisibleAll };
			for (int i = 0; i < 4; i++)
			{
				parameters[1 + i] = new D12.RootParameter { ParameterType = D12.RootShaderResource, ShaderRegister = (uint)i, ShaderVisibility = D12.VisibleAll };
			}

			parameters[5] = new D12.RootParameter { ParameterType = D12.RootTable, NumDescriptorRanges = 1, DescriptorRanges = ranges, ShaderVisibility = D12.VisiblePixel };
			parameters[6] = new D12.RootParameter { ParameterType = D12.RootTable, NumDescriptorRanges = 1, DescriptorRanges = ranges + 1, ShaderVisibility = D12.VisiblePixel };
			parameters[7] = new D12.RootParameter { ParameterType = D12.RootTable, NumDescriptorRanges = 1, DescriptorRanges = ranges + 2, ShaderVisibility = D12.VisiblePixel };
			// Comparison with linear filtering: a sample is the 2x2 hardware PCF that upstream's sampler2DShadow gives.
			D12.StaticSamplerDesc shadowSampler = new D12.StaticSamplerDesc
			{
				Filter = Dx.FilterComparisonLinear,
				AddressU = Dx.AddressClamp,
				AddressV = Dx.AddressClamp,
				AddressW = Dx.AddressClamp,
				MaxAnisotropy = 1,
				ComparisonFunc = Dx.ComparisonLessEqual,
				MaxLod = float.MaxValue,
				ShaderRegister = 1,
				ShaderVisibility = D12.VisiblePixel
			};
			D12.RootSignatureDesc desc = new D12.RootSignatureDesc { NumParameters = 8, Parameters = parameters, NumStaticSamplers = 1, StaticSamplers = &shadowSampler, Flags = D12.RootAllowInputLayout };
			rootSignature = D12.CreateRootSignature(device.Device, D12.SerializeRootSignature(desc));
		}

		/// <summary>The samplers, filtering as upstream's options say (as the Vulkan and Direct3D 11 layers').</summary>
		private void CreateSamplers(InterpolationMode interpolation, int anisotropy)
		{
			uint filter = Dx.FilterLinear;
			float maxLod = float.MaxValue;
			switch (interpolation)
			{
				case InterpolationMode.NearestNeighbor:
					filter = Dx.FilterPoint;
					maxLod = 0.0f;
					break;
				case InterpolationMode.Bilinear:
					maxLod = 0.0f;
					break;
				case InterpolationMode.NearestNeighborMipmapped:
					filter = Dx.FilterPoint;
					break;
				case InterpolationMode.BilinearMipmapped:
					// As upstream: GL_NEAREST_MIPMAP_LINEAR.
					filter = Dx.FilterMinPointMagMipLinear;
					break;
			}

			bool anisotropic = interpolation == InterpolationMode.AnisotropicFiltering && anisotropy > 0;
			UIntPtr cpuStart = D12.CpuStart(SamplerHeap);
			for (int wrap = 0; wrap < 4; wrap++)
			{
				D12.SamplerDesc desc = new D12.SamplerDesc
				{
					Filter = anisotropic ? Dx.FilterAnisotropic : filter,
					// OpenGlTextureWrapMode: RepeatClamp (2) repeats S, ClampRepeat (1) repeats T.
					AddressU = (wrap & 2) != 0 ? Dx.AddressWrap : Dx.AddressClamp,
					AddressV = (wrap & 1) != 0 ? Dx.AddressWrap : Dx.AddressClamp,
					AddressW = Dx.AddressClamp,
					MaxAnisotropy = anisotropic ? (uint)Math.Min(16, anisotropy) : 1u,
					ComparisonFunc = Dx.ComparisonAlways,
					MaxLod = maxLod
				};
				D12.CreateSampler(device.Device, desc, (UIntPtr)((ulong)cpuStart + (ulong)wrap * samplerSize));
			}
		}

		// --- pipeline objects ---

		private static readonly string[] semantics = { "POSITION", "NORMAL", "TEXCOORD", "COLOR", "BLENDINDICES", "DRAW" };

		/// <summary>A pipeline object from a description with LibRender2's vertex and the instance stream as its input.</summary>
		private IntPtr MakePipeline(D12.GraphicsPipelineStateDesc desc)
		{
			IntPtr[] names = new IntPtr[semantics.Length];
			try
			{
				for (int i = 0; i < names.Length; i++)
				{
					names[i] = Marshal.StringToHGlobalAnsi(semantics[i]);
				}

				Dx.InputElementDesc* elements = stackalloc Dx.InputElementDesc[6];
				elements[0] = Element(names[0], Dx.FormatR32G32B32Float, nameof(LibRenderVertex.Position));
				elements[1] = Element(names[1], Dx.FormatR32G32B32Float, nameof(LibRenderVertex.Normal));
				elements[2] = Element(names[2], Dx.FormatR32G32Float, nameof(LibRenderVertex.UV));
				elements[3] = Element(names[3], Dx.FormatR32G32B32A32Float, nameof(LibRenderVertex.Color));
				elements[4] = Element(names[4], Dx.FormatR32G32B32Sint, nameof(LibRenderVertex.MatrixChain));
				elements[5] = new Dx.InputElementDesc { SemanticName = names[5], Format = Dx.FormatR32Uint, InputSlot = 1, InputSlotClass = Dx.InputPerInstance, InstanceDataStepRate = 1 };
				desc.RootSignature = rootSignature;
				desc.InputElements = elements;
				desc.NumInputElements = 6;
				desc.PrimitiveTopologyType = D12.TopologyTypeTriangle;
				desc.SampleMask = 0xFFFFFFFF;
				desc.SampleCount = 1;
				desc.FillMode = Dx.FillSolid;
				desc.DepthClipEnable = 1;
				desc.StencilReadMask = desc.StencilWriteMask = 0xFF;
				D12.StencilOpDesc keep = new D12.StencilOpDesc { StencilFailOp = Dx.StencilOpKeep, StencilDepthFailOp = Dx.StencilOpKeep, StencilPassOp = Dx.StencilOpKeep, StencilFunc = Dx.ComparisonAlways };
				desc.FrontFace = desc.BackFace = keep;
				desc.DSVFormat = Dx.FormatD32Float;
				return D12.CreateGraphicsPipelineState(device.Device, &desc);
			}
			finally
			{
				foreach (IntPtr name in names)
				{
					Marshal.FreeHGlobal(name);
				}
			}
		}

		private static Dx.InputElementDesc Element(IntPtr name, uint format, string field)
		{
			return new Dx.InputElementDesc { SemanticName = name, Format = format, AlignedByteOffset = (uint)Marshal.OffsetOf(typeof(LibRenderVertex), field), InputSlotClass = Dx.InputPerVertex };
		}

		private static D12.ShaderBytecode Code(byte[] code, byte* pinned) => new D12.ShaderBytecode { Code = pinned, Length = (UIntPtr)code.Length };

		private static D12.RenderTargetBlendDesc Opaque(byte writeMask) => new D12.RenderTargetBlendDesc { SrcBlend = Dx.BlendOne, DestBlend = Dx.BlendZero, BlendOp = Dx.BlendOpAdd, SrcBlendAlpha = Dx.BlendOne, DestBlendAlpha = Dx.BlendZero, BlendOpAlpha = Dx.BlendOpAdd, LogicOp = D12.LogicNoop, RenderTargetWriteMask = writeMask };

		/// <summary>
		/// The pipeline of a key: blending (2 bits), depth writes (4), culling (8), no depth test (16); the world layer's draws
		/// into its colour and its depth as a colour, the cab's into its colour only. Colours blend as upstream's, alpha
		/// accumulating coverage for the composite; the depth target is written only with depth writes.
		/// </summary>
		private IntPtr Pipeline(int key, int layer)
		{
			int full = key | (layer << 5);
			if (pipelines.TryGetValue(full, out IntPtr pipeline))
			{
				return pipeline;
			}

			Blend blend = (Blend)(key & 3);
			bool depthWrite = (key & 4) != 0, cull = (key & 8) != 0, depthTest = (key & 16) == 0;
			D12.GraphicsPipelineStateDesc desc = new D12.GraphicsPipelineStateDesc();
			D12.RenderTargetBlendDesc color = Opaque(0xF);
			if (blend == Blend.Normal)
			{
				color.BlendEnable = 1;
				color.SrcBlend = Dx.BlendSrcAlpha;
				color.DestBlend = Dx.BlendInvSrcAlpha;
				color.SrcBlendAlpha = Dx.BlendOne;
				color.DestBlendAlpha = Dx.BlendInvSrcAlpha;
			}
			else if (blend == Blend.Additive)
			{
				color.BlendEnable = 1;
				color.SrcBlend = Dx.BlendSrcAlpha;
				color.DestBlend = Dx.BlendOne;
				color.SrcBlendAlpha = Dx.BlendZero;
				color.DestBlendAlpha = Dx.BlendOne;
			}

			desc.IndependentBlendEnable = 1;
			desc.Target0 = color;
			desc.Target1 = desc.Target2 = desc.Target3 = desc.Target4 = desc.Target5 = desc.Target6 = desc.Target7 = Opaque((byte)(depthWrite ? 1 : 0));
			// Upstream culls OpenGL's front faces; drawn upside down, those are clockwise, Direct3D's front faces by default.
			desc.CullMode = cull ? Dx.CullFront : Dx.CullNone;
			// Without a depth test, depth writes are off as well (Direct3D, like OpenGL, writes none then).
			desc.DepthEnable = depthTest ? 1 : 0;
			desc.DepthWriteMask = depthWrite ? 1u : 0u;
			desc.DepthFunc = Dx.ComparisonLessEqual;
			desc.RTVFormats[0] = Dx.FormatR8G8B8A8Unorm;
			if (layer == WorldLayerIndex)
			{
				desc.NumRenderTargets = 2;
				desc.RTVFormats[1] = Dx.FormatR32Float;
			}
			else
			{
				desc.NumRenderTargets = 1;
			}

			byte[] pixelCode = blend == Blend.None ? opaqueCode : blendedCode;
			fixed (byte* vs = vertexCode, ps = pixelCode)
			{
				desc.VS = Code(vertexCode, vs);
				desc.PS = Code(pixelCode, ps);
				pipeline = MakePipeline(desc);
			}

			pipelines[full] = pipeline;
			return pipeline;
		}

		/// <summary>The shadow pass's pipeline: depth only (less), culled as upstream's shadow pass culls the face, or not.</summary>
		private IntPtr ShadowPipeline(bool cull)
		{
			int index = cull ? 1 : 0;
			if (shadowPipelines[index] != IntPtr.Zero)
			{
				return shadowPipelines[index];
			}

			D12.GraphicsPipelineStateDesc desc = new D12.GraphicsPipelineStateDesc();
			desc.Target0 = Opaque(0xF);
			desc.CullMode = cull ? Dx.CullFront : Dx.CullNone;
			desc.DepthEnable = 1;
			desc.DepthWriteMask = 1;
			desc.DepthFunc = Dx.ComparisonLess;
			desc.NumRenderTargets = 0;
			fixed (byte* vs = shadowVertexCode, ps = shadowPixelCode)
			{
				desc.VS = Code(shadowVertexCode, vs);
				desc.PS = Code(shadowPixelCode, ps);
				shadowPipelines[index] = MakePipeline(desc);
			}

			return shadowPipelines[index];
		}

		// --- shadow maps ---

		/// <summary>The maps at a resolution and cascade count (a 1x1 single layer when shadows are off), made again when they change.</summary>
		public override void EnsureShadowMaps(int resolution, int cascades)
		{
			resolution = Math.Max(1, resolution);
			cascades = Math.Max(1, Math.Min(4, cascades));
			if (shadowMap != IntPtr.Zero && resolution == shadowResolution && cascades == shadowLayers)
			{
				return;
			}

			// The frames in flight read the maps and their view: done with first (the options changed; rare).
			device.WaitIdle();
			ReleaseShadowMaps();
			D12.ResourceDesc desc = new D12.ResourceDesc
			{
				Dimension = D12.DimensionTexture2D,
				Width = (ulong)resolution,
				Height = (uint)resolution,
				DepthOrArraySize = (ushort)cascades,
				MipLevels = 1,
				Format = Dx.FormatR32Typeless,
				SampleCount = 1,
				Flags = D12.ResourceAllowDepthStencil
			};
			shadowMap = D12.CreateCommittedResource(device.Device, D12.HeapDefault, D12.HeapFlagNone, desc, D12.StateDepthWrite);
			shadowReadable = false;
			shadowViews = D12.CreateDescriptorHeap(device.Device, D12.DescriptorHeapDepthStencils, (uint)cascades, false);
			UIntPtr start = D12.CpuStart(shadowViews);
			for (int i = 0; i < cascades; i++)
			{
				D12.DepthStencilViewDesc view = new D12.DepthStencilViewDesc { Format = Dx.FormatD32Float, ViewDimension = D12.DsvTexture2DArray, FirstArraySlice = (uint)i, ArraySize = 1 };
				D12.CreateDepthStencilView(device.Device, shadowMap, &view, (UIntPtr)((ulong)start + (ulong)i * depthViewSize));
			}

			D12.ShaderResourceViewDesc read = new D12.ShaderResourceViewDesc { Format = Dx.FormatR32Float, ViewDimension = D12.SrvTexture2DArray, Shader4ComponentMapping = D12.DefaultComponentMapping, MipLevels = 1, ArraySize = (uint)cascades };
			D12.CreateShaderResourceView(device.Device, shadowMap, &read, textures.ExtraCpuView(0));
			shadowResolution = resolution;
			shadowLayers = cascades;
		}

		private void ReleaseShadowMaps()
		{
			D12.Release(shadowViews);
			D12.Release(shadowMap);
			shadowViews = shadowMap = IntPtr.Zero;
		}

		private UIntPtr ShadowView(int cascade) => (UIntPtr)((ulong)D12.CpuStart(shadowViews) + (ulong)cascade * depthViewSize);

		// --- the frame ---

		/// <summary>Writes the frame's records into the slot's buffers (its last frame done with them), made larger when needed.</summary>
		internal void Upload(int slot, long frameNumber)
		{
			Order();
			fixed (FrameData* data = frameData)
			{
				Write(slot, 0, data, frameData.Length, sizeof(FrameData));
			}

			fixed (ObjectData* data = objects)
			{
				Write(slot, 1, data, objectCount, sizeof(ObjectData));
			}

			fixed (DrawData* data = draws)
			{
				Write(slot, 2, data, drawCount, sizeof(DrawData));
			}

			fixed (float* data = matrices)
			{
				Write(slot, 3, data, matrixCount, 64);
			}

			for (int i = retired.Count - 1; i >= 0; i--)
			{
				if (frameNumber - retired[i].Value > FramesInFlight)
				{
					D12.Release(retired[i].Key);
					retired.RemoveAt(i);
				}
			}

			EnsureInstances(drawCount, frameNumber);
		}

		private void Write(int slot, int which, void* data, int count, int stride)
		{
			long bytes = (long)Math.Max(1, count) * stride;
			if (recordSizes[slot, which] < bytes)
			{
				// The slot's last frame is done: its buffer may go now.
				D12.Release(recordBuffers[slot, which]);
				long size = Math.Max(bytes + bytes / 2, 64L << 10);
				recordBuffers[slot, which] = UploadBuffer((ulong)size, out recordData[slot, which]);
				recordSizes[slot, which] = size;
			}

			if (count > 0)
			{
				Buffer.MemoryCopy(data, recordData[slot, which], recordSizes[slot, which], (long)count * stride);
			}

			recordAddresses[which] = D12.GpuAddress(recordBuffers[slot, which]);
		}

		private IntPtr UploadBuffer(ulong size, out byte* data)
		{
			D12.ResourceDesc desc = new D12.ResourceDesc { Dimension = D12.DimensionBuffer, Width = size, Height = 1, DepthOrArraySize = 1, MipLevels = 1, SampleCount = 1, Layout = D12.LayoutRowMajor };
			IntPtr buffer = D12.CreateCommittedResource(device.Device, D12.HeapUpload, D12.HeapFlagNone, desc, D12.StateGenericRead);
			data = D12.Map(buffer);
			return buffer;
		}

		/// <summary>The instance stream, at least so long (a replaced one kept until the frames in flight are done with it).</summary>
		private void EnsureInstances(int count, long frameNumber)
		{
			if (instanceCount >= count && instanceBuffer != IntPtr.Zero)
			{
				return;
			}

			if (instanceBuffer != IntPtr.Zero)
			{
				retired.Add(new KeyValuePair<IntPtr, long>(instanceBuffer, frameNumber));
			}

			instanceCount = Math.Max(count + count / 2, 16384);
			instanceBuffer = UploadBuffer((ulong)instanceCount * 4, out byte* data);
			uint* indices = (uint*)data;
			for (int i = 0; i < instanceCount; i++)
			{
				indices[i] = (uint)i;
			}

			instanceAddress = D12.GpuAddress(instanceBuffer);
		}

		/// <summary>The bindings every draw of the frame shares: heaps, root signature, records, instance stream, shadow maps.</summary>
		internal void Bind(IntPtr list)
		{
			D12.SetDescriptorHeaps(list, textures.Heap, SamplerHeap);
			D12.SetGraphicsRootSignature(list, rootSignature);
			for (uint i = 0; i < 4; i++)
			{
				D12.SetGraphicsRootShaderResourceView(list, RootRecords + i, recordAddresses[i]);
			}

			D12.SetGraphicsRootDescriptorTable(list, RootShadowMap, textures.ExtraView(0));
			D12.IASetPrimitiveTopology(list, Dx.TopologyTriangleList);
			D12.IASetVertexBuffer(list, 1, new D12.VertexBufferView { BufferLocation = instanceAddress, SizeInBytes = (uint)instanceCount * 4, StrideInBytes = 4 });
		}

		private void SetLayer(IntPtr list, int layer, int cascade)
		{
			int* constants = stackalloc int[4];
			constants[0] = layer;
			constants[1] = cascade;
			constants[2] = constants[3] = 0;
			D12.SetGraphicsRoot32BitConstants(list, RootLayer, 4, constants);
		}

		/// <summary>
		/// The shadow pass (after Bind, before the world layer): each cascade's layer cleared and the casters drawn into it
		/// in the cascade's light space, as upstream's Shadows.RenderPass; with shadows off, the maps are only cleared.
		/// The maps are left readable.
		/// </summary>
		internal void DrawShadows(IntPtr list)
		{
			D12.ResourceBarrier* barrier = stackalloc D12.ResourceBarrier[1];
			if (shadowReadable)
			{
				barrier[0] = D12.Transition(shadowMap, D12.StatePixelShaderResource, D12.StateDepthWrite);
				D12.ResourceBarriers(list, barrier, 1);
			}

			for (int cascade = 0; cascade < shadowLayers; cascade++)
			{
				D12.ClearDepthView(list, ShadowView(cascade), 1.0f);
			}

			if (shadowCascades > 0 && shadowCount > 0)
			{
				D12.SetViewport(list, (uint)shadowResolution, (uint)shadowResolution);
				for (int cascade = 0; cascade < Math.Min(shadowCascades, shadowLayers); cascade++)
				{
					UIntPtr view = ShadowView(cascade);
					D12.OMSetRenderTargets(list, 0, null, &view);
					SetLayer(list, WorldLayerIndex, cascade);
					int boundCull = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, boundIndexBlock = -1;
					for (int i = 0; i < shadowCount; i++)
					{
						DrawCommand command = shadowCommands[i];
						int cull = (int)(command.Key >> 48) == 1 ? 1 : 0;
						int texture = draws[command.Draw].Texture, sampler = draws[command.Draw].Sampler & 3;
						int vertexBlock = (int)((command.Key >> 16) & 0xFFFF), indexBlock = (int)(command.Key & 0xFFFF);
						if (cull != boundCull)
						{
							D12.SetPipelineState(list, ShadowPipeline(cull == 1));
							boundCull = cull;
						}

						BindFace(list, texture, sampler, vertexBlock, indexBlock, ref boundTexture, ref boundSampler, ref boundVertexBlock, ref boundIndexBlock);
						D12.DrawIndexedInstanced(list, command.IndexCount, 1, command.FirstIndex, command.VertexBase, (uint)command.Draw);
					}
				}
			}

			barrier[0] = D12.Transition(shadowMap, D12.StateDepthWrite, D12.StatePixelShaderResource);
			D12.ResourceBarriers(list, barrier, 1);
			shadowReadable = true;
		}

		/// <summary>A draw's texture, sampler and buffers, where they change; true if the texture did.</summary>
		private bool BindFace(IntPtr list, int texture, int sampler, int vertexBlock, int indexBlock, ref int boundTexture, ref int boundSampler, ref int boundVertexBlock, ref int boundIndexBlock)
		{
			bool changed = false;
			if (texture != boundTexture)
			{
				D12.SetGraphicsRootDescriptorTable(list, RootTexture, textures.View(texture));
				boundTexture = texture;
				changed = true;
			}

			if (sampler != boundSampler)
			{
				D12.SetGraphicsRootDescriptorTable(list, RootSampler, samplerStart + (ulong)sampler * samplerSize);
				boundSampler = sampler;
			}

			if (vertexBlock != boundVertexBlock)
			{
				D12.IASetVertexBuffer(list, 0, new D12.VertexBufferView { BufferLocation = meshes.Vertices.Addresses[vertexBlock], SizeInBytes = (uint)meshes.Vertices.Sizes[vertexBlock], StrideInBytes = (uint)WorldMeshes.VertexSize });
				boundVertexBlock = vertexBlock;
			}

			if (indexBlock != boundIndexBlock)
			{
				D12.IASetIndexBuffer(list, new D12.IndexBufferView { BufferLocation = meshes.Indices.Addresses[indexBlock], SizeInBytes = (uint)meshes.Indices.Sizes[indexBlock], Format = Dx.FormatR32Uint });
				boundIndexBlock = indexBlock;
			}

			return changed;
		}

		/// <summary>Draws a layer's draws into the targets set (Bind first).</summary>
		internal void Draw(IntPtr list, int layer, uint width, uint height)
		{
			int start = layer == WorldLayerIndex ? 0 : cabCommandStart, end = layer == WorldLayerIndex ? cabCommandStart : commandCount;
			if (layer == WorldLayerIndex)
			{
				LastStateChanges = 0;
			}

			if (start >= end)
			{
				return;
			}

			D12.SetViewport(list, width, height);
			SetLayer(list, layer, 0);
			int boundPipeline = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, boundIndexBlock = -1, changes = 0;
			for (int i = start; i < end; i++)
			{
				DrawCommand command = commands[i];
				long key = command.Key;
				int pipelineKey = (int)((key >> 48) & 0x3FFF);
				if (pipelineKey != boundPipeline)
				{
					D12.SetPipelineState(list, Pipeline(pipelineKey & 31, layer));
					boundPipeline = pipelineKey;
					changes++;
				}

				if (BindFace(list, draws[command.Draw].Texture, draws[command.Draw].Sampler & 3, (int)((key >> 16) & 0xFFFF), (int)(key & 0xFFFF), ref boundTexture, ref boundSampler, ref boundVertexBlock, ref boundIndexBlock))
				{
					changes++;
				}

				D12.DrawIndexedInstanced(list, command.IndexCount, 1, command.FirstIndex, command.VertexBase, (uint)command.Draw);
			}

			LastStateChanges += changes;
		}

		/// <summary>Shadow casters drawn in the last frame (per cascade), for the log.</summary>
		internal int LastShadowCasters => shadowCascades > 0 ? shadowCount : 0;

		public override void Dispose()
		{
			ReleaseShadowMaps();
			foreach (IntPtr pipeline in pipelines.Values)
			{
				D12.Release(pipeline);
			}

			pipelines.Clear();
			foreach (IntPtr pipeline in shadowPipelines)
			{
				D12.Release(pipeline);
			}

			for (int slot = 0; slot < FramesInFlight; slot++)
			{
				for (int which = 0; which < 4; which++)
				{
					D12.Release(recordBuffers[slot, which]);
					recordBuffers[slot, which] = IntPtr.Zero;
				}
			}

			foreach (KeyValuePair<IntPtr, long> buffer in retired)
			{
				D12.Release(buffer.Key);
			}

			retired.Clear();
			D12.Release(instanceBuffer);
			D12.Release(SamplerHeap);
			D12.Release(rootSignature);
			instanceBuffer = rootSignature = IntPtr.Zero;
		}
	}
}
