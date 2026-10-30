// OBNS: not an upstream file. The Direct3D 11 world layer's frame (OBNS/DIRECT3D.md): the scene's records (WorldScene)
// in structured buffers, each draw a DrawIndexedInstanced whose instance stream gives the shaders its record's index.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Graphics;
using Blend = OpenBve.Graphics.WorldBlend;

namespace OpenBve.Graphics.Direct3D11
{
	internal sealed unsafe class D3D11Scene : WorldScene
	{
		private readonly D3D11Device device;
		private readonly D3D11Meshes meshes;
		private readonly D3D11Textures textures;

		protected override WorldTextures SceneTextures => textures;

		private IntPtr vertexShader, inputLayout;
		/// <summary>The pixel shaders: blended (coverage from alpha), and opaque (full coverage).</summary>
		private IntPtr blendedShader, opaqueShader;
		/// <summary>The layers' constant buffers, each holding its frame record's index.</summary>
		private readonly IntPtr[] layerBuffers = new IntPtr[2];
		/// <summary>The samplers by OpenGL wrap mode (ClampClamp, ClampRepeat, RepeatClamp, RepeatRepeat).</summary>
		private readonly IntPtr[] samplers = new IntPtr[4];
		private readonly Dictionary<int, IntPtr> blendStates = new Dictionary<int, IntPtr>(), depthStates = new Dictionary<int, IntPtr>(), rasterStates = new Dictionary<int, IntPtr>();

		/// <summary>The records' buffers and their views: frames, objects, draws, matrices.</summary>
		private readonly IntPtr[] recordBuffers = new IntPtr[4], recordViews = new IntPtr[4];
		private readonly long[] recordSizes = new long[4];

		/// <summary>The instance stream: 0, 1, 2, ... (a draw's StartInstanceLocation picks its record).</summary>
		private IntPtr instanceBuffer;
		private int instanceCount;

		/// <summary>Pipeline and texture changes in the last frame, for the log.</summary>
		internal int LastStateChanges { get; private set; }

		internal D3D11Scene(D3D11Device device, D3D11Meshes meshes, D3D11Textures textures)
		{
			this.device = device;
			this.meshes = meshes;
			this.textures = textures;
			// Pages of one texture: the draws are grouped by their texture (it is bound per draw).
			TexturePage = 1;
			CreateShaders();
			CreateSamplers(Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			for (int layer = 0; layer < 2; layer++)
			{
				int* data = stackalloc int[4];
				data[0] = layer;
				data[1] = data[2] = data[3] = 0;
				layerBuffers[layer] = Dx.CreateBuffer(device.Device, new Dx.BufferDesc { ByteWidth = 16, Usage = Dx.UsageDefault, BindFlags = Dx.BindConstantBuffer }, data);
			}
		}

		private void CreateShaders()
		{
			string source;
			using (Stream stream = typeof(D3D11Scene).Assembly.GetManifestResourceStream("OBNS.Direct3D11.world.hlsl"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Direct3D 11 shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			byte[] vertexCode = Dx.Compile(source, "world.hlsl", "VS", "vs_5_0", "OPAQUE_COVERAGE", "0");
			vertexShader = Dx.CreateVertexShader(device.Device, vertexCode);
			blendedShader = Dx.CreatePixelShader(device.Device, Dx.Compile(source, "world.hlsl", "PS", "ps_5_0", "OPAQUE_COVERAGE", "0"));
			opaqueShader = Dx.CreatePixelShader(device.Device, Dx.Compile(source, "world.hlsl", "PS", "ps_5_0", "OPAQUE_COVERAGE", "1"));

			// LibRender2's vertex, as upstream's VAOs have it, and the instance stream's index.
			string[] names = { "POSITION", "NORMAL", "TEXCOORD", "COLOR", "BLENDINDICES", "DRAW" };
			IntPtr[] nameTexts = new IntPtr[names.Length];
			try
			{
				for (int i = 0; i < names.Length; i++)
				{
					nameTexts[i] = Marshal.StringToHGlobalAnsi(names[i]);
				}

				Dx.InputElementDesc* elements = stackalloc Dx.InputElementDesc[6];
				elements[0] = Element(nameTexts[0], Dx.FormatR32G32B32Float, nameof(LibRenderVertex.Position));
				elements[1] = Element(nameTexts[1], Dx.FormatR32G32B32Float, nameof(LibRenderVertex.Normal));
				elements[2] = Element(nameTexts[2], Dx.FormatR32G32Float, nameof(LibRenderVertex.UV));
				elements[3] = Element(nameTexts[3], Dx.FormatR32G32B32A32Float, nameof(LibRenderVertex.Color));
				elements[4] = Element(nameTexts[4], Dx.FormatR32G32B32Sint, nameof(LibRenderVertex.MatrixChain));
				elements[5] = new Dx.InputElementDesc { SemanticName = nameTexts[5], Format = Dx.FormatR32Uint, InputSlot = 1, InputSlotClass = Dx.InputPerInstance, InstanceDataStepRate = 1 };
				inputLayout = Dx.CreateInputLayout(device.Device, elements, 6, vertexCode);
			}
			finally
			{
				foreach (IntPtr text in nameTexts)
				{
					Marshal.FreeHGlobal(text);
				}
			}
		}

		private static Dx.InputElementDesc Element(IntPtr name, uint format, string field)
		{
			return new Dx.InputElementDesc { SemanticName = name, Format = format, AlignedByteOffset = (uint)Marshal.OffsetOf(typeof(LibRenderVertex), field), InputSlotClass = Dx.InputPerVertex };
		}

		/// <summary>The samplers, filtering as upstream's options say (as the Vulkan layer's).</summary>
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
			for (int wrap = 0; wrap < 4; wrap++)
			{
				Dx.SamplerDesc desc = new Dx.SamplerDesc
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
				samplers[wrap] = Dx.CreateSamplerState(device.Device, desc);
			}
		}

		/// <summary>Shadows are drawn from step 5: none yet.</summary>
		public override void EnsureShadowMaps(int resolution, int cascades)
		{
		}

		/// <summary>Writes the frame's records into the buffers (made larger when the frame needs more).</summary>
		internal void Upload()
		{
			Order();
			fixed (FrameData* data = frameData)
			{
				Write(0, data, frameData.Length, sizeof(FrameData));
			}

			fixed (ObjectData* data = objects)
			{
				Write(1, data, objectCount, sizeof(ObjectData));
			}

			fixed (DrawData* data = draws)
			{
				Write(2, data, drawCount, sizeof(DrawData));
			}

			fixed (float* data = matrices)
			{
				Write(3, data, matrixCount, 64);
			}

			EnsureInstances(drawCount);
		}

		/// <summary>Writes records into a structured buffer, discarding what the last frame had (Direct3D renames it).</summary>
		private void Write(int which, void* data, int count, int stride)
		{
			long bytes = (long)Math.Max(1, count) * stride;
			if (recordSizes[which] < bytes)
			{
				Dx.Release(recordViews[which]);
				Dx.Release(recordBuffers[which]);
				long size = Math.Max(bytes + bytes / 2, 64L << 10) / stride * stride + stride;
				Dx.BufferDesc desc = new Dx.BufferDesc
				{
					ByteWidth = (uint)size,
					Usage = Dx.UsageDynamic,
					BindFlags = Dx.BindShaderResource,
					CpuAccessFlags = Dx.CpuAccessWrite,
					MiscFlags = Dx.MiscBufferStructured,
					StructureByteStride = (uint)stride
				};
				recordBuffers[which] = Dx.CreateBuffer(device.Device, desc, null);
				recordViews[which] = Dx.CreateShaderResourceView(device.Device, recordBuffers[which]);
				recordSizes[which] = size;
			}

			if (count > 0)
			{
				void* mapped = Dx.Map(device.Context, recordBuffers[which], Dx.MapWriteDiscard);
				Buffer.MemoryCopy(data, mapped, recordSizes[which], (long)count * stride);
				Dx.Unmap(device.Context, recordBuffers[which]);
			}
		}

		/// <summary>The instance stream, at least so long.</summary>
		private void EnsureInstances(int count)
		{
			if (instanceCount >= count && instanceBuffer != IntPtr.Zero)
			{
				return;
			}

			Dx.Release(instanceBuffer);
			instanceCount = Math.Max(count + count / 2, 16384);
			uint[] indices = new uint[instanceCount];
			for (int i = 0; i < instanceCount; i++)
			{
				indices[i] = (uint)i;
			}

			fixed (uint* data = indices)
			{
				instanceBuffer = Dx.CreateBuffer(device.Device, new Dx.BufferDesc { ByteWidth = (uint)(instanceCount * 4), Usage = Dx.UsageDefault, BindFlags = Dx.BindVertexBuffer }, data);
			}
		}

		/// <summary>Draws a layer's draws into the render targets bound (Upload first).</summary>
		internal void Draw(int layer, uint width, uint height)
		{
			int start = layer == WorldLayerIndex ? 0 : cabCommandStart, end = layer == WorldLayerIndex ? cabCommandStart : commandCount;
			if (start >= end)
			{
				return;
			}

			IntPtr context = device.Context;
			Dx.RSSetViewport(context, new Dx.Viewport { Width = width, Height = height, MaxDepth = 1.0f });
			Dx.IASetPrimitiveTopology(context, Dx.TopologyTriangleList);
			Dx.IASetInputLayout(context, inputLayout);
			Dx.IASetVertexBuffer(context, 1, instanceBuffer, 4, 0);
			Dx.VSSetShader(context, vertexShader);
			Dx.VSSetConstantBuffer(context, 0, layerBuffers[layer]);
			Dx.PSSetConstantBuffer(context, 0, layerBuffers[layer]);
			for (uint i = 0; i < 4; i++)
			{
				Dx.VSSetShaderResource(context, i, recordViews[i]);
			}

			Dx.PSSetShaderResource(context, 0, recordViews[0]);
			Dx.PSSetShaderResource(context, 2, recordViews[2]);

			int boundPipeline = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, boundIndexBlock = -1, changes = 0;
			for (int i = start; i < end; i++)
			{
				DrawCommand command = commands[i];
				long key = command.Key;
				int pipelineKey = (int)((key >> 48) & 0x3FFF);
				int texture = draws[command.Draw].Texture;
				int sampler = draws[command.Draw].Sampler;
				int vertexBlock = (int)((key >> 16) & 0xFFFF), indexBlock = (int)(key & 0xFFFF);
				if (pipelineKey != boundPipeline)
				{
					BindPipeline(pipelineKey);
					boundPipeline = pipelineKey;
					changes++;
				}

				if (texture != boundTexture)
				{
					Dx.PSSetShaderResource(context, 4, textures.View(texture));
					boundTexture = texture;
					changes++;
				}

				if (sampler != boundSampler)
				{
					Dx.PSSetSampler(context, 0, samplers[sampler & 3]);
					boundSampler = sampler;
				}

				if (vertexBlock != boundVertexBlock)
				{
					Dx.IASetVertexBuffer(context, 0, meshes.Vertices.Buffers[vertexBlock], (uint)WorldMeshes.VertexSize, 0);
					boundVertexBlock = vertexBlock;
				}

				if (indexBlock != boundIndexBlock)
				{
					Dx.IASetIndexBuffer(context, meshes.Indices.Buffers[indexBlock], Dx.FormatR32Uint, 0);
					boundIndexBlock = indexBlock;
				}

				Dx.DrawIndexedInstanced(context, command.IndexCount, 1, command.FirstIndex, command.VertexBase, (uint)command.Draw);
			}

			LastStateChanges = layer == WorldLayerIndex ? changes : LastStateChanges + changes;
		}

		/// <summary>The states and pixel shader of a pipeline key: blending (2 bits), depth writes (4), culling (8), no depth test (16).</summary>
		private void BindPipeline(int key)
		{
			Blend blend = (Blend)(key & 3);
			bool depthWrite = (key & 4) != 0, cull = (key & 8) != 0, depthTest = (key & 16) == 0;
			IntPtr context = device.Context;
			Dx.PSSetShader(context, blend == Blend.None ? opaqueShader : blendedShader);
			Dx.OMSetBlendState(context, BlendState(blend, depthWrite));
			Dx.OMSetDepthStencilState(context, DepthState(depthTest, depthWrite));
			Dx.RSSetState(context, RasterState(cull));
		}

		/// <summary>Colour blends as upstream, alpha accumulating coverage for the composite; the depth target written only with depth writes.</summary>
		private IntPtr BlendState(Blend blend, bool depthWrite)
		{
			int key = (int)blend | (depthWrite ? 4 : 0);
			if (blendStates.TryGetValue(key, out IntPtr state))
			{
				return state;
			}

			Dx.RenderTargetBlendDesc color = new Dx.RenderTargetBlendDesc { SrcBlend = Dx.BlendOne, DestBlend = Dx.BlendZero, BlendOp = Dx.BlendOpAdd, SrcBlendAlpha = Dx.BlendOne, DestBlendAlpha = Dx.BlendZero, BlendOpAlpha = Dx.BlendOpAdd, RenderTargetWriteMask = 0xF };
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

			Dx.RenderTargetBlendDesc depth = new Dx.RenderTargetBlendDesc { SrcBlend = Dx.BlendOne, DestBlend = Dx.BlendZero, BlendOp = Dx.BlendOpAdd, SrcBlendAlpha = Dx.BlendOne, DestBlendAlpha = Dx.BlendZero, BlendOpAlpha = Dx.BlendOpAdd, RenderTargetWriteMask = (byte)(depthWrite ? 1 : 0) };
			Dx.BlendDesc desc = new Dx.BlendDesc { IndependentBlendEnable = 1, Target0 = color, Target1 = depth, Target2 = depth, Target3 = depth, Target4 = depth, Target5 = depth, Target6 = depth, Target7 = depth };
			state = Dx.CreateBlendState(device.Device, desc);
			blendStates[key] = state;
			return state;
		}

		private IntPtr DepthState(bool depthTest, bool depthWrite)
		{
			int key = (depthTest ? 1 : 0) | (depthWrite ? 2 : 0);
			if (depthStates.TryGetValue(key, out IntPtr state))
			{
				return state;
			}

			Dx.StencilOpDesc keep = new Dx.StencilOpDesc { StencilFailOp = Dx.StencilOpKeep, StencilDepthFailOp = Dx.StencilOpKeep, StencilPassOp = Dx.StencilOpKeep, StencilFunc = Dx.ComparisonAlways };
			// Without a depth test, depth writes are off as well (Direct3D, like OpenGL, writes none then).
			Dx.DepthStencilDesc desc = new Dx.DepthStencilDesc { DepthEnable = depthTest ? 1 : 0, DepthWriteMask = depthWrite ? 1u : 0u, DepthFunc = Dx.ComparisonLessEqual, StencilReadMask = 0xFF, StencilWriteMask = 0xFF, FrontFace = keep, BackFace = keep };
			state = Dx.CreateDepthStencilState(device.Device, desc);
			depthStates[key] = state;
			return state;
		}

		/// <summary>Upstream culls OpenGL's front faces; drawn upside down, those are clockwise, Direct3D's front faces by default.</summary>
		private IntPtr RasterState(bool cull)
		{
			int key = cull ? 1 : 0;
			if (rasterStates.TryGetValue(key, out IntPtr state))
			{
				return state;
			}

			Dx.RasterizerDesc desc = new Dx.RasterizerDesc { FillMode = Dx.FillSolid, CullMode = cull ? Dx.CullFront : Dx.CullNone, DepthClipEnable = 1 };
			state = Dx.CreateRasterizerState(device.Device, desc);
			rasterStates[key] = state;
			return state;
		}

		public override void Dispose()
		{
			foreach (IntPtr com in new[] { vertexShader, inputLayout, blendedShader, opaqueShader, instanceBuffer })
			{
				Dx.Release(com);
			}

			foreach (IntPtr[] list in new[] { layerBuffers, samplers, recordViews, recordBuffers })
			{
				foreach (IntPtr com in list)
				{
					Dx.Release(com);
				}
			}

			foreach (Dictionary<int, IntPtr> states in new[] { blendStates, depthStates, rasterStates })
			{
				foreach (IntPtr com in states.Values)
				{
					Dx.Release(com);
				}

				states.Clear();
			}
		}
	}
}
