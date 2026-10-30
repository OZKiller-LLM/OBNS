// OBNS: not an upstream file. The Metal world layer's frame (OBNS/METAL.md): the scene's records (WorldScene) in shared
// buffers the shaders read by the draw's index (its base instance), each draw a drawIndexedPrimitives; the states are
// pipeline objects made at the start, as Direct3D 12's. The shaders are Shaders/world.metal, compiled at the start.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using OpenBveApi.Graphics;
using Blend = OpenBve.Graphics.WorldBlend;

namespace OpenBve.Graphics.Metal
{
	internal sealed unsafe class MetalScene : WorldScene
	{
		private static readonly IntPtr
			selNewLibrary = Mtl.Sel("newLibraryWithSource:options:error:"), selNewFunction = Mtl.Sel("newFunctionWithName:"),
			selVertexDescriptor = Mtl.Sel("vertexDescriptor"), selAttributes = Mtl.Sel("attributes"), selLayouts = Mtl.Sel("layouts"),
			selObjectAtIndex = Mtl.Sel("objectAtIndexedSubscript:"), selSetFormat = Mtl.Sel("setFormat:"), selSetOffset = Mtl.Sel("setOffset:"),
			selSetBufferIndex = Mtl.Sel("setBufferIndex:"), selSetStride = Mtl.Sel("setStride:"), selSetStepFunction = Mtl.Sel("setStepFunction:"),
			selSetVertexFunction = Mtl.Sel("setVertexFunction:"), selSetFragmentFunction = Mtl.Sel("setFragmentFunction:"),
			selSetVertexDescriptor = Mtl.Sel("setVertexDescriptor:"), selColorAttachments = Mtl.Sel("colorAttachments"),
			selSetPixelFormat = Mtl.Sel("setPixelFormat:"), selSetBlendingEnabled = Mtl.Sel("setBlendingEnabled:"),
			selSetSourceRgb = Mtl.Sel("setSourceRGBBlendFactor:"), selSetDestinationRgb = Mtl.Sel("setDestinationRGBBlendFactor:"),
			selSetRgbOperation = Mtl.Sel("setRgbBlendOperation:"), selSetSourceAlpha = Mtl.Sel("setSourceAlphaBlendFactor:"),
			selSetDestinationAlpha = Mtl.Sel("setDestinationAlphaBlendFactor:"), selSetAlphaOperation = Mtl.Sel("setAlphaBlendOperation:"),
			selSetWriteMask = Mtl.Sel("setWriteMask:"), selSetDepthAttachmentPixelFormat = Mtl.Sel("setDepthAttachmentPixelFormat:"),
			selNewPipeline = Mtl.Sel("newRenderPipelineStateWithDescriptor:error:"),
			selSetDepthCompare = Mtl.Sel("setDepthCompareFunction:"), selSetDepthWrite = Mtl.Sel("setDepthWriteEnabled:"),
			selNewDepthState = Mtl.Sel("newDepthStencilStateWithDescriptor:"),
			selSetMinFilter = Mtl.Sel("setMinFilter:"), selSetMagFilter = Mtl.Sel("setMagFilter:"), selSetMipFilter = Mtl.Sel("setMipFilter:"),
			selSetSAddress = Mtl.Sel("setSAddressMode:"), selSetTAddress = Mtl.Sel("setTAddressMode:"), selSetMaxAnisotropy = Mtl.Sel("setMaxAnisotropy:"),
			selSetLodMaxClamp = Mtl.Sel("setLodMaxClamp:"), selNewSampler = Mtl.Sel("newSamplerStateWithDescriptor:"),
			selTexture2D = Mtl.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), selSetTextureType = Mtl.Sel("setTextureType:"),
			selSetArrayLength = Mtl.Sel("setArrayLength:"), selSetUsage = Mtl.Sel("setUsage:"), selSetStorageMode = Mtl.Sel("setStorageMode:"),
			selNewTexture = Mtl.Sel("newTextureWithDescriptor:"), selNewBuffer = Mtl.Sel("newBufferWithLength:options:"), selContents = Mtl.Sel("contents"),
			selRenderPassDescriptor = Mtl.Sel("renderPassDescriptor"), selDepthAttachment = Mtl.Sel("depthAttachment"), selSetTexture = Mtl.Sel("setTexture:"),
			selSetSlice = Mtl.Sel("setSlice:"), selSetLoadAction = Mtl.Sel("setLoadAction:"), selSetStoreAction = Mtl.Sel("setStoreAction:"),
			selSetClearDepth = Mtl.Sel("setClearDepth:"), selRenderEncoder = Mtl.Sel("renderCommandEncoderWithDescriptor:"), selEndEncoding = Mtl.Sel("endEncoding"),
			selSetPipeline = Mtl.Sel("setRenderPipelineState:"), selSetDepthStencilState = Mtl.Sel("setDepthStencilState:"), selSetCullMode = Mtl.Sel("setCullMode:"),
			selSetViewport = Mtl.Sel("setViewport:"), selSetVertexBuffer = Mtl.Sel("setVertexBuffer:offset:atIndex:"), selSetFragmentBuffer = Mtl.Sel("setFragmentBuffer:offset:atIndex:"),
			selSetVertexBytes = Mtl.Sel("setVertexBytes:length:atIndex:"), selSetFragmentBytes = Mtl.Sel("setFragmentBytes:length:atIndex:"),
			selSetFragmentTexture = Mtl.Sel("setFragmentTexture:atIndex:"), selSetFragmentSampler = Mtl.Sel("setFragmentSamplerState:atIndex:"),
			selDrawIndexed = Mtl.Sel("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:baseVertex:baseInstance:");

		// The buffers' indices (world.metal): the vertices (stage in), the layer's constants, the records.
		private const ulong VertexBufferIndex = 0, LayerIndex = 1, FramesIndex = 2, ObjectsIndex = 3, DrawsIndex = 4, MatricesIndex = 5;

		private readonly MetalDevice device;
		private readonly MetalMeshes meshes;
		private readonly MetalTextures textures;

		protected override WorldTextures SceneTextures => textures;

		private IntPtr library, vertexDescriptor;
		/// <summary>The pipelines by key: the draw's pipeline key, and the layer (the cab's has its colour target only).</summary>
		private readonly Dictionary<int, IntPtr> pipelines = new Dictionary<int, IntPtr>();
		private IntPtr shadowPipeline, shadowDepthState;
		private readonly Dictionary<int, IntPtr> depthStates = new Dictionary<int, IntPtr>();
		private readonly IntPtr[] samplers = new IntPtr[4];

		/// <summary>The records' buffers (frames, objects, draws, matrices): shared memory, written while Metal draws nothing.</summary>
		private readonly IntPtr[] recordBuffers = new IntPtr[4];
		private readonly IntPtr[] recordContents = new IntPtr[4];
		private readonly long[] recordSizes = new long[4];

		private IntPtr shadowMap;
		private int shadowResolution, shadowLayers;

		/// <summary>Pipeline and texture changes in the last frame, for the log.</summary>
		internal int LastStateChanges { get; private set; }

		/// <summary>The time the pipelines took to make, for the log.</summary>
		internal double MadeIn { get; private set; }

		internal MetalScene(MetalDevice device, MetalMeshes meshes, MetalTextures textures)
		{
			this.device = device;
			this.meshes = meshes;
			this.textures = textures;
			// Pages of one texture: the draws are grouped by their texture (it is bound per draw).
			TexturePage = 1;
			long start = System.Diagnostics.Stopwatch.GetTimestamp();
			CreateLibrary();
			CreateVertexDescriptor();
			MakePipelines();
			CreateSamplers(Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			MadeIn = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			EnsureShadowMaps(1, 1);
		}

		private void CreateLibrary()
		{
			string source;
			using (Stream stream = typeof(MetalScene).Assembly.GetManifestResourceStream("OBNS.Metal.world.metal"))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the Metal shaders are missing from the program");
				}

				using (StreamReader reader = new StreamReader(stream))
				{
					source = reader.ReadToEnd();
				}
			}

			IntPtr text = Mtl.String(source);
			IntPtr error = IntPtr.Zero;
			library = Mtl.Id(device.Device, selNewLibrary, text, IntPtr.Zero, &error);
			Mtl.Release(text);
			if (library == IntPtr.Zero)
			{
				throw new InvalidOperationException("compiling world.metal failed: " + Mtl.ErrorText(error));
			}
		}

		private IntPtr Function(string name)
		{
			IntPtr text = Mtl.String(name);
			IntPtr function = Mtl.Id(library, selNewFunction, text);
			Mtl.Release(text);
			if (function == IntPtr.Zero)
			{
				throw new InvalidOperationException("world.metal has no function " + name);
			}

			return function;
		}

		/// <summary>LibRender2's vertex, as upstream's VAOs have it, from buffer 0.</summary>
		private void CreateVertexDescriptor()
		{
			vertexDescriptor = Mtl.Retain(Mtl.Id(Mtl.Class("MTLVertexDescriptor"), selVertexDescriptor));
			IntPtr attributes = Mtl.Id(vertexDescriptor, selAttributes);
			string[] fields = { nameof(LibRenderVertex.Position), nameof(LibRenderVertex.Normal), nameof(LibRenderVertex.UV), nameof(LibRenderVertex.Color), nameof(LibRenderVertex.MatrixChain) };
			ulong[] formats = { Mtl.VertexFloat3, Mtl.VertexFloat3, Mtl.VertexFloat2, Mtl.VertexFloat4, Mtl.VertexInt3 };
			for (int i = 0; i < fields.Length; i++)
			{
				IntPtr attribute = Mtl.Id(attributes, selObjectAtIndex, (ulong)i);
				Mtl.Void(attribute, selSetFormat, formats[i]);
				Mtl.Void(attribute, selSetOffset, (ulong)(int)Marshal.OffsetOf(typeof(LibRenderVertex), fields[i]));
				Mtl.Void(attribute, selSetBufferIndex, VertexBufferIndex);
			}

			IntPtr layout = Mtl.Id(Mtl.Id(vertexDescriptor, selLayouts), selObjectAtIndex, VertexBufferIndex);
			Mtl.Void(layout, selSetStride, (ulong)WorldMeshes.VertexSize);
			Mtl.Void(layout, selSetStepFunction, Mtl.StepPerVertex);
		}

		private IntPtr MakePipeline(IntPtr descriptor)
		{
			IntPtr error = IntPtr.Zero;
			IntPtr pipeline = Mtl.Id(device.Device, selNewPipeline, descriptor, &error);
			if (pipeline == IntPtr.Zero)
			{
				throw new InvalidOperationException("a Metal pipeline could not be made: " + Mtl.ErrorText(error));
			}

			return pipeline;
		}

		/// <summary>
		/// Every pipeline: by key, blending (2 bits), depth writes (4); and layer, the world's with its depth as a colour (written
		/// only with depth writes), the cab's colour only. Culling and the depth test are the encoder's (MetalScene.Draw).
		/// </summary>
		private void MakePipelines()
		{
			IntPtr vertex = Function("VS");
			IntPtr[] fragments = { Function("PSWorld"), Function("PSWorldOpaque"), Function("PSCab"), Function("PSCabOpaque") };
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				for (int layer = 0; layer < 2; layer++)
				{
					for (int key = 0; key < 8; key++)
					{
						Blend blend = (Blend)(key & 3);
						if ((int)blend == 3)
						{
							continue;
						}

						bool depthWrite = (key & 4) != 0;
						IntPtr desc = Mtl.New("MTLRenderPipelineDescriptor");
						Mtl.Void(desc, selSetVertexFunction, vertex);
						Mtl.Void(desc, selSetFragmentFunction, fragments[layer * 2 + (blend == Blend.None ? 1 : 0)]);
						Mtl.Void(desc, selSetVertexDescriptor, vertexDescriptor);
						IntPtr attachments = Mtl.Id(desc, selColorAttachments);
						IntPtr color = Mtl.Id(attachments, selObjectAtIndex, 0UL);
						Mtl.Void(color, selSetPixelFormat, Mtl.PixelBGRA8Unorm);
						// Colours blend as upstream's, alpha accumulating coverage for the composite.
						if (blend != Blend.None)
						{
							Mtl.Void(color, selSetBlendingEnabled, (byte)1);
							Mtl.Void(color, selSetRgbOperation, Mtl.BlendAdd);
							Mtl.Void(color, selSetAlphaOperation, Mtl.BlendAdd);
							Mtl.Void(color, selSetSourceRgb, Mtl.BlendSourceAlpha);
							Mtl.Void(color, selSetDestinationRgb, blend == Blend.Normal ? Mtl.BlendOneMinusSourceAlpha : Mtl.BlendOne);
							Mtl.Void(color, selSetSourceAlpha, blend == Blend.Normal ? Mtl.BlendOne : Mtl.BlendZero);
							Mtl.Void(color, selSetDestinationAlpha, blend == Blend.Normal ? Mtl.BlendOneMinusSourceAlpha : Mtl.BlendOne);
						}

						if (layer == WorldLayerIndex)
						{
							IntPtr depth = Mtl.Id(attachments, selObjectAtIndex, 1UL);
							Mtl.Void(depth, selSetPixelFormat, Mtl.PixelR32Float);
							Mtl.Void(depth, selSetWriteMask, depthWrite ? Mtl.WriteRed : Mtl.WriteNone);
						}

						Mtl.Void(desc, selSetDepthAttachmentPixelFormat, Mtl.PixelDepth32Float);
						pipelines[key | (layer << 3)] = MakePipeline(desc);
						Mtl.Release(desc);
					}
				}

				IntPtr shadowVertex = Function("ShadowVS"), shadowFragment = Function("ShadowPS");
				IntPtr shadowDesc = Mtl.New("MTLRenderPipelineDescriptor");
				Mtl.Void(shadowDesc, selSetVertexFunction, shadowVertex);
				Mtl.Void(shadowDesc, selSetFragmentFunction, shadowFragment);
				Mtl.Void(shadowDesc, selSetVertexDescriptor, vertexDescriptor);
				Mtl.Void(shadowDesc, selSetDepthAttachmentPixelFormat, Mtl.PixelDepth32Float);
				shadowPipeline = MakePipeline(shadowDesc);
				Mtl.Release(shadowDesc);
				Mtl.Release(shadowVertex);
				Mtl.Release(shadowFragment);
				shadowDepthState = DepthState(true, true, Mtl.CompareLess);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
				Mtl.Release(vertex);
				foreach (IntPtr fragment in fragments)
				{
					Mtl.Release(fragment);
				}
			}
		}

		/// <summary>A depth state: the test (less or equal, as upstream; the shadow pass's less) and writes.</summary>
		private IntPtr DepthState(bool depthTest, bool depthWrite, ulong compare = Mtl.CompareLessEqual)
		{
			IntPtr desc = Mtl.New("MTLDepthStencilDescriptor");
			// Without a depth test, depth writes are off as well (Metal, like OpenGL, writes none then).
			Mtl.Void(desc, selSetDepthCompare, depthTest ? compare : Mtl.CompareAlways);
			Mtl.Void(desc, selSetDepthWrite, (byte)(depthTest && depthWrite ? 1 : 0));
			IntPtr state = Mtl.Id(device.Device, selNewDepthState, desc);
			Mtl.Release(desc);
			return state;
		}

		private IntPtr DepthState(int key)
		{
			int which = key & 20; // depth writes (4), no depth test (16)
			if (!depthStates.TryGetValue(which, out IntPtr state))
			{
				state = DepthState((key & 16) == 0, (key & 4) != 0);
				depthStates[which] = state;
			}

			return state;
		}

		/// <summary>The samplers, filtering as upstream's options say (as the other layers').</summary>
		private void CreateSamplers(InterpolationMode interpolation, int anisotropy)
		{
			ulong min = Mtl.FilterLinear, mag = Mtl.FilterLinear, mip = Mtl.MipLinear, maxAnisotropy = 1;
			switch (interpolation)
			{
				case InterpolationMode.NearestNeighbor:
					min = mag = Mtl.FilterNearest;
					mip = Mtl.MipNone;
					break;
				case InterpolationMode.Bilinear:
					mip = Mtl.MipNone;
					break;
				case InterpolationMode.NearestNeighborMipmapped:
					min = mag = Mtl.FilterNearest;
					mip = Mtl.MipNearest;
					break;
				case InterpolationMode.BilinearMipmapped:
					// As upstream: GL_NEAREST_MIPMAP_LINEAR.
					min = Mtl.FilterNearest;
					break;
				case InterpolationMode.AnisotropicFiltering:
					if (anisotropy > 0)
					{
						maxAnisotropy = (ulong)Math.Min(16, anisotropy);
					}

					break;
			}

			for (int wrap = 0; wrap < 4; wrap++)
			{
				IntPtr desc = Mtl.New("MTLSamplerDescriptor");
				Mtl.Void(desc, selSetMinFilter, min);
				Mtl.Void(desc, selSetMagFilter, mag);
				Mtl.Void(desc, selSetMipFilter, mip);
				Mtl.Void(desc, selSetMaxAnisotropy, maxAnisotropy);
				// OpenGlTextureWrapMode: RepeatClamp (2) repeats S, ClampRepeat (1) repeats T.
				Mtl.Void(desc, selSetSAddress, (wrap & 2) != 0 ? Mtl.AddressRepeat : Mtl.AddressClampToEdge);
				Mtl.Void(desc, selSetTAddress, (wrap & 1) != 0 ? Mtl.AddressRepeat : Mtl.AddressClampToEdge);
				Mtl.Void(desc, selSetLodMaxClamp, mip == Mtl.MipNone ? 0.0f : float.MaxValue);
				samplers[wrap] = Mtl.Id(device.Device, selNewSampler, desc);
				Mtl.Release(desc);
			}
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

			// (Metal draws nothing between frames: the old maps may go now.)
			Mtl.Release(shadowMap);
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				IntPtr desc = Mtl.Id(Mtl.Class("MTLTextureDescriptor"), selTexture2D, Mtl.PixelDepth32Float, (ulong)resolution, (ulong)resolution, (byte)0);
				Mtl.Void(desc, selSetTextureType, Mtl.TextureType2DArray);
				Mtl.Void(desc, selSetArrayLength, (ulong)cascades);
				Mtl.Void(desc, selSetUsage, Mtl.UsageRenderTarget | Mtl.UsageShaderRead);
				Mtl.Void(desc, selSetStorageMode, Mtl.StoragePrivate);
				shadowMap = Mtl.Id(device.Device, selNewTexture, desc);
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}

			shadowResolution = resolution;
			shadowLayers = cascades;
		}

		// --- the frame ---

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
		}

		private void Write(int which, void* data, int count, int stride)
		{
			long bytes = (long)Math.Max(1, count) * stride;
			if (recordSizes[which] < bytes)
			{
				Mtl.Release(recordBuffers[which]);
				long size = Math.Max(bytes + bytes / 2, 64L << 10);
				recordBuffers[which] = Mtl.Id(device.Device, selNewBuffer, (ulong)size, Mtl.ResourceShared);
				recordContents[which] = Mtl.Id(recordBuffers[which], selContents);
				recordSizes[which] = size;
			}

			if (count > 0)
			{
				Buffer.MemoryCopy(data, (void*)recordContents[which], recordSizes[which], (long)count * stride);
			}
		}

		/// <summary>What every draw of a pass shares: the layer's constants, the records, the viewport.</summary>
		private void Bind(IntPtr encoder, int layer, int cascade, uint width, uint height)
		{
			int* constants = stackalloc int[4];
			constants[0] = layer;
			constants[1] = cascade;
			constants[2] = constants[3] = 0;
			Mtl.Void(encoder, selSetVertexBytes, constants, 16UL, LayerIndex);
			Mtl.Void(encoder, selSetFragmentBytes, constants, 16UL, LayerIndex);
			for (int i = 0; i < 4; i++)
			{
				Mtl.Void(encoder, selSetVertexBuffer, recordBuffers[i], 0UL, FramesIndex + (ulong)i);
			}

			Mtl.Void(encoder, selSetFragmentBuffer, recordBuffers[0], 0UL, FramesIndex);
			Mtl.Void(encoder, selSetFragmentBuffer, recordBuffers[2], 0UL, DrawsIndex);
			Mtl.Void(encoder, selSetViewport, new Mtl.Viewport { Width = width, Height = height, Far = 1.0 });
		}

		/// <summary>
		/// The shadow pass (after Upload, before the world layer): each cascade's layer cleared and the casters drawn into it
		/// in the cascade's light space, as upstream's Shadows.RenderPass; with shadows off, the maps are only cleared.
		/// </summary>
		internal void EncodeShadows(IntPtr commandBuffer)
		{
			for (int cascade = 0; cascade < shadowLayers; cascade++)
			{
				IntPtr pass = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), selRenderPassDescriptor);
				IntPtr depth = Mtl.Id(pass, selDepthAttachment);
				Mtl.Void(depth, selSetTexture, shadowMap);
				Mtl.Void(depth, selSetSlice, (ulong)cascade);
				Mtl.Void(depth, selSetLoadAction, Mtl.LoadClear);
				Mtl.Void(depth, selSetStoreAction, Mtl.StoreStore);
				Mtl.Void(depth, selSetClearDepth, 1.0);
				IntPtr encoder = Mtl.Id(commandBuffer, selRenderEncoder, pass);
				if (cascade < shadowCascades && shadowLength[cascade] > 0)
				{
					Bind(encoder, WorldLayerIndex, cascade, (uint)shadowResolution, (uint)shadowResolution);
					Mtl.Void(encoder, selSetPipeline, shadowPipeline);
					Mtl.Void(encoder, selSetDepthStencilState, shadowDepthState);
					int boundCull = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1;
					for (int i = shadowFirst[cascade], end = shadowFirst[cascade] + shadowLength[cascade]; i < end; i++)
					{
						DrawCommand command = shadowCommands[i];
						int cull = (int)(command.Key >> 48) == 1 ? 1 : 0;
						if (cull != boundCull)
						{
							Mtl.Void(encoder, selSetCullMode, cull == 1 ? Mtl.CullFront : Mtl.CullNone);
							boundCull = cull;
						}

						BindFace(encoder, command, ref boundTexture, ref boundSampler, ref boundVertexBlock);
						DrawNow(encoder, command);
					}
				}

				Mtl.Void(encoder, selEndEncoding);
			}
		}

		/// <summary>A draw's texture, sampler and vertex buffer, where they change; true if the texture did.</summary>
		private bool BindFace(IntPtr encoder, DrawCommand command, ref int boundTexture, ref int boundSampler, ref int boundVertexBlock)
		{
			int texture = draws[command.Draw].Texture, sampler = draws[command.Draw].Sampler & 3, vertexBlock = (int)((command.Key >> 16) & 0xFFFF);
			bool changed = false;
			if (texture != boundTexture)
			{
				Mtl.Void(encoder, selSetFragmentTexture, textures.Texture(texture), 0UL);
				boundTexture = texture;
				changed = true;
			}

			if (sampler != boundSampler)
			{
				Mtl.Void(encoder, selSetFragmentSampler, samplers[sampler], 0UL);
				boundSampler = sampler;
			}

			if (vertexBlock != boundVertexBlock)
			{
				Mtl.Void(encoder, selSetVertexBuffer, meshes.Vertices.Buffers[vertexBlock], 0UL, VertexBufferIndex);
				boundVertexBlock = vertexBlock;
			}

			return changed;
		}

		/// <summary>A draw: its indices from its index block, its record its base instance.</summary>
		private void DrawNow(IntPtr encoder, DrawCommand command)
		{
			IntPtr indices = meshes.Indices.Buffers[(int)(command.Key & 0xFFFF)];
			Mtl.Void(encoder, selDrawIndexed, Mtl.PrimitiveTriangle, command.IndexCount, Mtl.IndexUInt32, indices, (ulong)command.FirstIndex * 4, 1UL, command.VertexBase, (ulong)command.Draw);
		}

		/// <summary>Draws a layer's draws with an encoder of its pass (the world's with its depth as a colour, the cab's not).</summary>
		internal void Draw(IntPtr encoder, int layer, uint width, uint height)
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

			Bind(encoder, layer, 0, width, height);
			Mtl.Void(encoder, selSetFragmentTexture, shadowMap, 1UL);
			int boundPipeline = -1, boundTexture = -1, boundSampler = -1, boundVertexBlock = -1, changes = 0;
			for (int i = start; i < end; i++)
			{
				DrawCommand command = commands[i];
				int pipelineKey = (int)((command.Key >> 48) & 0x3FFF);
				if (pipelineKey != boundPipeline)
				{
					// Upstream culls OpenGL's front faces; drawn upside down, those are clockwise, Metal's front faces by default.
					Mtl.Void(encoder, selSetPipeline, pipelines[(pipelineKey & 7) | (layer << 3)]);
					Mtl.Void(encoder, selSetDepthStencilState, DepthState(pipelineKey));
					Mtl.Void(encoder, selSetCullMode, (pipelineKey & 8) != 0 ? Mtl.CullFront : Mtl.CullNone);
					boundPipeline = pipelineKey;
					changes++;
				}

				if (BindFace(encoder, command, ref boundTexture, ref boundSampler, ref boundVertexBlock))
				{
					changes++;
				}

				DrawNow(encoder, command);
			}

			LastStateChanges += changes;
		}

		/// <summary>Shadow casters drawn in the last frame (per cascade), for the log.</summary>

		public override void Dispose()
		{
			foreach (IntPtr pipeline in pipelines.Values)
			{
				Mtl.Release(pipeline);
			}

			pipelines.Clear();
			foreach (IntPtr state in depthStates.Values)
			{
				Mtl.Release(state);
			}

			depthStates.Clear();
			foreach (IntPtr obj in samplers)
			{
				Mtl.Release(obj);
			}

			foreach (IntPtr obj in recordBuffers)
			{
				Mtl.Release(obj);
			}

			foreach (IntPtr obj in new[] { shadowPipeline, shadowDepthState, shadowMap, vertexDescriptor, library })
			{
				Mtl.Release(obj);
			}

			shadowPipeline = shadowDepthState = shadowMap = vertexDescriptor = library = IntPtr.Zero;
		}
	}
}
