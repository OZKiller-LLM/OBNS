// OBNS: not an upstream file. The Vulkan world layer's frame (OBNS/VULKAN.md): upstream's per-face drawing
// (LibRender2's BaseRenderer.RenderFace) as records in storage buffers, drawn with indirect draws.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using LibRender2.Objects;
using LibRender2.ShadowMapping;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanScene : IDisposable
	{
		// --- the shaders' records (std430; see Shaders/world.vert) ---

		[StructLayout(LayoutKind.Sequential)]
		private struct FrameData
		{
			public fixed float Projection[16];
			public fixed float Light[24]; // position, ambient, diffuse, specular, model, fog colour (vec4 each)
			public float FogStart, FogEnd, FogDensity;
			public int FogIsLinear;
			public int IsLight, ShadowEnabled, ShadowCascades, ShadowSmooth;
			public fixed float InverseView[16];
			public fixed float LightSpace[64];
			public fixed float ShadowSplit[4];
			public fixed float ShadowBias[4];
			public fixed float ShadowNormalBias[4];
			public fixed float ShadowTexelSize[4];
			public float ShadowStrength, ShadowFilterRadius, Pad0, Pad1;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ObjectData
		{
			public fixed float ModelView[16];
			public fixed float Normal[12];
			public fixed float TextureMatrix[16];
			public int MatrixBase, Pad0, MatrixCount, Pad2;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct DrawData
		{
			public float ColorR, ColorG, ColorB, ColorA;
			public float SpecularR, SpecularG, SpecularB, SpecularA;
			public float EmissionR, EmissionG, EmissionB, Shininess;
			public int Flags;
			public float Brightness;
			public float Opacity;
			public int Texture;
			public int Sampler;
			public int AlphaFunction;
			public float AlphaReference;
			public int Object;
			public int Fog;
			public int CullFront;
			public int Pad0, Pad1;
		}

		/// <summary>A draw to record: its pipeline and buffer block (the order key), its indices, and its record.</summary>
		private struct DrawCommand
		{
			public long Key;
			public uint FirstIndex, IndexCount;
			public int VertexBase, Draw, Pass;
			/// <summary>Whether the face casts a shadow: 0 no, 1 culled as upstream's shadow pass culls it, 2 both sides.</summary>
			public int Shadow;
		}

		/// <summary>OpenGL's alpha functions as upstream's shader numbers them; Never means none.</summary>
		internal const int AlphaNever = 512, AlphaLess = 513, AlphaEqual = 514, AlphaGreater = 516;

		internal enum Blend { None = 0, Normal = 1, Additive = 2 }

		private const int FramesInFlight = 2;
		private const uint FrameBinding = 0, ObjectBinding = 1, DrawBinding = 2, MatrixBinding = 3, SamplerBinding = 4, ShadowBinding = 5, TextureBinding = 6;

		private readonly VulkanDevice device;
		internal readonly VulkanTextures Textures;
		internal readonly VulkanMeshes Meshes;
		private ulong setLayout, pipelineLayout, descriptorPool, vertexModule, fragmentModule, shadowVertexModule, shadowFragmentModule;
		internal readonly VulkanShadows Shadows;
		private readonly ulong[] shadowPipelines = new ulong[2];
		internal readonly ulong[] DescriptorSets = new ulong[FramesInFlight];

		/// <summary>
		/// The pages of textures (set 1), where the device has no descriptor indexing (else null, the textures one bindless
		/// array in set 0): draws are grouped by their texture's page too, and each page bound for its draws.
		/// </summary>
		private readonly VulkanTexturePages pages;
		private readonly int texturePage;
		private readonly Dictionary<int, ulong> pipelines = new Dictionary<int, ulong>();

		// Per frame in flight: the records and the indirect commands.
		private readonly HostBuffer[] frameBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] objectBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] drawBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] matrixBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] indirectBuffers = new HostBuffer[FramesInFlight];

		// The frame's records, merged from the chunks they were made in.
		private ObjectData[] objects = new ObjectData[1024];
		private int objectCount;
		private DrawData[] draws = new DrawData[4096];
		private int drawCount;
		private float[] matrices = new float[16 * 256];
		private int matrixCount;
		private DrawCommand[] commands = new DrawCommand[4096];
		private int commandCount;
		/// <summary>Where each pass's commands start, and whether they must be drawn in order.</summary>
		private readonly List<int> segmentStarts = new List<int>();
		private readonly List<bool> segmentOrdered = new List<bool>();
		/// <summary>The layers' frame records: the world's (0) and the cab's (1), chosen by the push constant.</summary>
		private readonly FrameData[] frameData = new FrameData[2];
		private BaseRenderer renderer;

		/// <summary>A face to draw, as upstream's passes give it, with the alpha test in force for it.</summary>
		private struct Item
		{
			public ObjectState State;
			public MeshFace Face;
			public int Pass, AlphaFunction;
			public float AlphaReference;
			// Resolved by Prepare (main thread): the mesh, the face's indices, its textures' places (-1: none).
			public VulkanMesh Mesh;
			public uint FirstIndex, IndexCount;
			public int DayTexture, NightTexture;
		}

		// Upstream's renderer state for the frame, taken by Begin: the frame's records are made from these (on other
		// threads, while the main thread goes on) rather than from the renderer.
		private Matrix4D cameraTranslation, viewMatrix, cabViewMatrix;
		private bool backFaceCulling, fogEnabled;
		private float lightingAmount;

		/// <summary>The layers: upstream's world layer, and its overlay (cab) layer, drawn into an image of its own.</summary>
		internal const int WorldLayer = 0, CabLayer = 1;

		/// <summary>A pass's state: its layer, blending, depth test and writes, and whether its faces are drawn in the order given.</summary>
		private struct PassInfo
		{
			public int Layer;
			public Blend Blend;
			public bool DepthTest, DepthWrite, Ordered;
		}

		private int currentLayer;

		private Item[] items = new Item[8192];
		private int itemCount;
		private readonly List<PassInfo> passes = new List<PassInfo>();
		private int currentAlphaFunction;
		private float currentAlphaReference;

		/// <summary>The records one chunk of the faces makes, on whichever thread builds it.</summary>
		private sealed class Chunk
		{
			public ObjectData[] Objects = new ObjectData[256];
			public int ObjectCount;
			public DrawData[] Draws = new DrawData[512];
			public int DrawCount;
			public DrawCommand[] Commands = new DrawCommand[512];
			public int CommandCount;
			public float[] Matrices = new float[16 * 16];
			public int MatrixCount;
			public int Skipped;
			public long ObjectTicks;
		}

		private readonly List<Chunk> chunks = new List<Chunk>();
		private int chunkCount;
		private readonly Action<int, int> buildChunks;

		/// <summary>Draw records and indirect draws in the last frame, for the log.</summary>
		internal int LastDraws { get; private set; }
		internal int LastIndirectCalls { get; private set; }
		internal int LastCabDraws { get; private set; }
		private int skippedFaces;

		internal VulkanScene(VulkanDevice device, Uploader uploader, BaseRenderer renderer)
		{
			this.device = device;
			buildChunks = BuildChunks;
			if (!device.Bindless)
			{
				pages = new VulkanTexturePages(device, device.TexturePageSize, FramesInFlight);
				texturePage = device.TexturePageSize;
			}

			CreateDescriptors();
			Textures = new VulkanTextures(device, uploader, DescriptorSets, TextureBinding, pages, Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			Meshes = new VulkanMeshes(device, uploader);
			WriteSamplers();
			Shadows = new VulkanShadows(device);
			WriteShadowMaps();
			vertexModule = ShaderModule("world.vert.spv");
			fragmentModule = ShaderModule(pages == null ? "world.frag.spv" : "world.paged.frag.spv");
			shadowVertexModule = ShaderModule("shadow.vert.spv");
			shadowFragmentModule = ShaderModule(pages == null ? "shadow.frag.spv" : "shadow.paged.frag.spv");
		}

		// --- set-up ---

		private void CreateDescriptors()
		{
			const uint vertexAndFragment = Vk.ShaderStageVertex | Vk.ShaderStageFragment;
			Vk.DescriptorSetLayoutBinding* bindings = stackalloc Vk.DescriptorSetLayoutBinding[7];
			bindings[0] = new Vk.DescriptorSetLayoutBinding { Binding = FrameBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[1] = new Vk.DescriptorSetLayoutBinding { Binding = ObjectBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[2] = new Vk.DescriptorSetLayoutBinding { Binding = DrawBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[3] = new Vk.DescriptorSetLayoutBinding { Binding = MatrixBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[4] = new Vk.DescriptorSetLayoutBinding { Binding = SamplerBinding, DescriptorType = Vk.DescriptorTypeSampler, DescriptorCount = 4, StageFlags = Vk.ShaderStageFragment };
			bindings[5] = new Vk.DescriptorSetLayoutBinding { Binding = ShadowBinding, DescriptorType = Vk.DescriptorTypeCombinedImageSampler, DescriptorCount = 1, StageFlags = Vk.ShaderStageFragment };
			bindings[6] = new Vk.DescriptorSetLayoutBinding { Binding = TextureBinding, DescriptorType = Vk.DescriptorTypeSampledImage, DescriptorCount = VulkanTextures.Capacity, StageFlags = Vk.ShaderStageFragment };
			// With pages, the textures are not in this set (and nothing in it is written while in use).
			uint bindingCount = pages == null ? 7u : 6u;
			uint* flags = stackalloc uint[7];
			for (int i = 0; i < 6; i++)
			{
				flags[i] = 0;
			}

			// The textures: written as they are uploaded, while frames using other elements may be in flight.
			flags[6] = Vk.DescriptorBindingPartiallyBound | Vk.DescriptorBindingUpdateAfterBind | Vk.DescriptorBindingUpdateUnusedWhilePending | Vk.DescriptorBindingVariableDescriptorCount;
			Vk.DescriptorSetLayoutBindingFlagsCreateInfo flagsInfo = new Vk.DescriptorSetLayoutBindingFlagsCreateInfo
			{
				SType = Vk.StructureTypeDescriptorSetLayoutBindingFlagsCreateInfo,
				BindingCount = 7,
				PBindingFlags = flags
			};
			Vk.DescriptorSetLayoutCreateInfo layoutInfo = new Vk.DescriptorSetLayoutCreateInfo
			{
				SType = Vk.StructureTypeDescriptorSetLayoutCreateInfo,
				PNext = pages == null ? &flagsInfo : null,
				Flags = pages == null ? Vk.DescriptorSetLayoutCreateUpdateAfterBindPool : 0,
				BindingCount = bindingCount,
				PBindings = bindings
			};
			Vk.Check(Vk.vkCreateDescriptorSetLayout(device.Device, &layoutInfo, null, out setLayout), "vkCreateDescriptorSetLayout");

			// The frame's index, and the cascade the shadow pass draws.
			Vk.PushConstantRange push = new Vk.PushConstantRange { StageFlags = vertexAndFragment, Size = 8 };
			ulong* layoutsUsed = stackalloc ulong[2];
			layoutsUsed[0] = setLayout;
			layoutsUsed[1] = pages?.Layout ?? 0;
			Vk.PipelineLayoutCreateInfo pipelineLayoutInfo = new Vk.PipelineLayoutCreateInfo
			{
				SType = Vk.StructureTypePipelineLayoutCreateInfo,
				SetLayoutCount = pages == null ? 1u : 2u,
				PSetLayouts = layoutsUsed,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Vk.Check(Vk.vkCreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

			Vk.DescriptorPoolSize* sizes = stackalloc Vk.DescriptorPoolSize[4];
			sizes[0] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 4 * FramesInFlight };
			sizes[1] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeSampler, DescriptorCount = 4 * FramesInFlight };
			sizes[2] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeCombinedImageSampler, DescriptorCount = FramesInFlight };
			sizes[3] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeSampledImage, DescriptorCount = VulkanTextures.Capacity * FramesInFlight };
			Vk.DescriptorPoolCreateInfo poolInfo = new Vk.DescriptorPoolCreateInfo
			{
				SType = Vk.StructureTypeDescriptorPoolCreateInfo,
				Flags = pages == null ? Vk.DescriptorPoolCreateUpdateAfterBind : 0,
				MaxSets = FramesInFlight,
				PoolSizeCount = pages == null ? 4u : 3u,
				PPoolSizes = sizes
			};
			Vk.Check(Vk.vkCreateDescriptorPool(device.Device, &poolInfo, null, out descriptorPool), "vkCreateDescriptorPool");

			ulong* layouts = stackalloc ulong[FramesInFlight];
			uint* counts = stackalloc uint[FramesInFlight];
			for (int i = 0; i < FramesInFlight; i++)
			{
				layouts[i] = setLayout;
				counts[i] = VulkanTextures.Capacity;
			}

			Vk.DescriptorSetVariableDescriptorCountAllocateInfo variableInfo = new Vk.DescriptorSetVariableDescriptorCountAllocateInfo
			{
				SType = Vk.StructureTypeDescriptorSetVariableDescriptorCountAllocateInfo,
				DescriptorSetCount = FramesInFlight,
				PDescriptorCounts = counts
			};
			Vk.DescriptorSetAllocateInfo allocateInfo = new Vk.DescriptorSetAllocateInfo
			{
				SType = Vk.StructureTypeDescriptorSetAllocateInfo,
				PNext = pages == null ? &variableInfo : null,
				DescriptorPool = descriptorPool,
				DescriptorSetCount = FramesInFlight,
				PSetLayouts = layouts
			};
			fixed (ulong* sets = DescriptorSets)
			{
				Vk.Check(Vk.vkAllocateDescriptorSets(device.Device, &allocateInfo, sets), "vkAllocateDescriptorSets");
			}
		}

		private void WriteSamplers()
		{
			Vk.DescriptorImageInfo* samplers = stackalloc Vk.DescriptorImageInfo[4];
			for (int i = 0; i < 4; i++)
			{
				samplers[i] = new Vk.DescriptorImageInfo { Sampler = Textures.Samplers[i] };
			}

			foreach (ulong set in DescriptorSets)
			{
				Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = set,
					DstBinding = SamplerBinding,
					DescriptorCount = 4,
					DescriptorType = Vk.DescriptorTypeSampler,
					PImageInfo = samplers
				};
				Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
			}
		}

		/// <summary>Points both frames' descriptor sets at the shadow maps (after they are made, with the GPU idle).</summary>
		private void WriteShadowMaps()
		{
			Vk.DescriptorImageInfo image = new Vk.DescriptorImageInfo { Sampler = Shadows.Sampler, ImageView = Shadows.ArrayView, ImageLayout = Vk.ImageLayoutShaderReadOnlyOptimal };
			foreach (ulong set in DescriptorSets)
			{
				Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = set,
					DstBinding = ShadowBinding,
					DescriptorCount = 1,
					DescriptorType = Vk.DescriptorTypeCombinedImageSampler,
					PImageInfo = &image
				};
				Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
			}
		}

		/// <summary>The shadow maps for a resolution and cascade count, made again (with the GPU idle) when they change.</summary>
		internal void EnsureShadowMaps(int resolution, int cascades)
		{
			if (!Shadows.Differs(resolution, cascades))
			{
				return;
			}

			Vk.vkDeviceWaitIdle(device.Device);
			Shadows.Create(resolution, cascades);
			WriteShadowMaps();
		}

		/// <summary>The shadow pass's pipeline: depth only, culled as upstream's shadow pass culls (or not).</summary>
		private ulong ShadowPipeline(bool cull)
		{
			ulong pipeline = shadowPipelines[cull ? 1 : 0];
			if (pipeline != 0)
			{
				return pipeline;
			}

			IntPtr main = Marshal.StringToHGlobalAnsi("main");
			try
			{
				Vk.PipelineShaderStageCreateInfo* stages = stackalloc Vk.PipelineShaderStageCreateInfo[2];
				stages[0] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageVertexBit, Module = shadowVertexModule, PName = (byte*)main };
				// With pages, their size (constant 1).
				uint pageSize = (uint)texturePage;
				Vk.SpecializationMapEntry pageEntry = new Vk.SpecializationMapEntry { ConstantID = 1, Offset = 0, Size = (UIntPtr)4 };
				Vk.SpecializationInfo pageSpecialization = new Vk.SpecializationInfo { MapEntryCount = 1, PMapEntries = &pageEntry, DataSize = (UIntPtr)4, PData = &pageSize };
				stages[1] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFragmentBit, Module = shadowFragmentModule, PName = (byte*)main, PSpecializationInfo = pages == null ? null : &pageSpecialization };
				Vk.VertexInputBindingDescription binding = new Vk.VertexInputBindingDescription { Binding = 0, Stride = (uint)VulkanMeshes.VertexSize, InputRate = Vk.VertexInputRateVertex };
				Vk.VertexInputAttributeDescription* attributes = stackalloc Vk.VertexInputAttributeDescription[3];
				attributes[0] = Attribute(0, Vk.FormatR32G32B32Sfloat, nameof(LibRenderVertex.Position));
				attributes[1] = Attribute(2, Vk.FormatR32G32Sfloat, nameof(LibRenderVertex.UV));
				attributes[2] = Attribute(4, Vk.FormatR32G32B32Sint, nameof(LibRenderVertex.MatrixChain));
				Vk.PipelineVertexInputStateCreateInfo vertexInput = new Vk.PipelineVertexInputStateCreateInfo
				{
					SType = Vk.StructureTypePipelineVertexInputStateCreateInfo,
					VertexBindingDescriptionCount = 1,
					PVertexBindingDescriptions = &binding,
					VertexAttributeDescriptionCount = 3,
					PVertexAttributeDescriptions = attributes
				};
				Vk.PipelineInputAssemblyStateCreateInfo inputAssembly = new Vk.PipelineInputAssemblyStateCreateInfo { SType = Vk.StructureTypePipelineInputAssemblyStateCreateInfo, Topology = Vk.PrimitiveTopologyTriangleList };
				Vk.PipelineViewportStateCreateInfo viewport = new Vk.PipelineViewportStateCreateInfo { SType = Vk.StructureTypePipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
				Vk.PipelineRasterizationStateCreateInfo rasterization = new Vk.PipelineRasterizationStateCreateInfo
				{
					SType = Vk.StructureTypePipelineRasterizationStateCreateInfo,
					PolygonMode = Vk.PolygonModeFill,
					CullMode = cull ? Vk.CullModeFront : Vk.CullModeNone,
					FrontFace = Vk.FrontFaceClockwise,
					LineWidth = 1.0f
				};
				Vk.PipelineMultisampleStateCreateInfo multisample = new Vk.PipelineMultisampleStateCreateInfo { SType = Vk.StructureTypePipelineMultisampleStateCreateInfo, RasterizationSamples = 1 };
				Vk.PipelineDepthStencilStateCreateInfo depth = new Vk.PipelineDepthStencilStateCreateInfo
				{
					SType = Vk.StructureTypePipelineDepthStencilStateCreateInfo,
					DepthTestEnable = 1,
					DepthWriteEnable = 1,
					DepthCompareOp = Vk.CompareOpLess,
					MaxDepthBounds = 1.0f
				};
				Vk.PipelineColorBlendStateCreateInfo colorBlend = new Vk.PipelineColorBlendStateCreateInfo { SType = Vk.StructureTypePipelineColorBlendStateCreateInfo };
				uint* dynamicStates = stackalloc uint[2];
				dynamicStates[0] = Vk.DynamicStateViewport;
				dynamicStates[1] = Vk.DynamicStateScissor;
				Vk.PipelineDynamicStateCreateInfo dynamic = new Vk.PipelineDynamicStateCreateInfo { SType = Vk.StructureTypePipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
				Vk.PipelineRenderingCreateInfo rendering = new Vk.PipelineRenderingCreateInfo
				{
					SType = Vk.StructureTypePipelineRenderingCreateInfo,
					DepthAttachmentFormat = VulkanShadows.Format
				};
				Vk.GraphicsPipelineCreateInfo info = new Vk.GraphicsPipelineCreateInfo
				{
					SType = Vk.StructureTypeGraphicsPipelineCreateInfo,
					PNext = &rendering,
					StageCount = 2,
					PStages = stages,
					PVertexInputState = &vertexInput,
					PInputAssemblyState = &inputAssembly,
					PViewportState = &viewport,
					PRasterizationState = &rasterization,
					PMultisampleState = &multisample,
					PDepthStencilState = &depth,
					PColorBlendState = &colorBlend,
					PDynamicState = &dynamic,
					Layout = pipelineLayout,
					BasePipelineIndex = -1
				};
				Vk.Check(Vk.vkCreateGraphicsPipelines(device.Device, 0, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines (shadows)");
			}
			finally
			{
				Marshal.FreeHGlobal(main);
			}

			shadowPipelines[cull ? 1 : 0] = pipeline;
			return pipeline;
		}

		private ulong ShaderModule(string name)
		{
			byte[] code;
			using (Stream stream = typeof(VulkanScene).Assembly.GetManifestResourceStream("OBNS.Vulkan." + name))
			{
				if (stream == null)
				{
					throw new InvalidOperationException("the shader " + name + " is not in the program");
				}

				code = new byte[stream.Length];
				int read = 0;
				while (read < code.Length)
				{
					read += stream.Read(code, read, code.Length - read);
				}
			}

			fixed (byte* data = code)
			{
				Vk.ShaderModuleCreateInfo info = new Vk.ShaderModuleCreateInfo { SType = Vk.StructureTypeShaderModuleCreateInfo, CodeSize = (UIntPtr)code.Length, PCode = (uint*)data };
				Vk.Check(Vk.vkCreateShaderModule(device.Device, &info, null, out ulong module), "vkCreateShaderModule (" + name + ")");
				return module;
			}
		}

		/// <summary>The pipeline for a blend mode, depth writes and culling (made the first time it is needed).</summary>
		private ulong Pipeline(int key)
		{
			if (pipelines.TryGetValue(key, out ulong pipeline))
			{
				return pipeline;
			}

			Blend blend = (Blend)(key & 3);
			bool depthWrite = (key & 4) != 0, cull = (key & 8) != 0, depthTest = (key & 16) == 0;
			IntPtr main = Marshal.StringToHGlobalAnsi("main");
			try
			{
				// Opaque drawing writes full coverage (specialization constant 0).
				// With pages, their size too (constant 1).
				uint* constants = stackalloc uint[2];
				constants[0] = blend == Blend.None ? 1u : 0u;
				constants[1] = (uint)texturePage;
				Vk.SpecializationMapEntry* entries = stackalloc Vk.SpecializationMapEntry[2];
				entries[0] = new Vk.SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = (UIntPtr)4 };
				entries[1] = new Vk.SpecializationMapEntry { ConstantID = 1, Offset = 4, Size = (UIntPtr)4 };
				Vk.SpecializationInfo specialization = new Vk.SpecializationInfo { MapEntryCount = pages == null ? 1u : 2u, PMapEntries = entries, DataSize = (UIntPtr)8, PData = constants };
				Vk.PipelineShaderStageCreateInfo* stages = stackalloc Vk.PipelineShaderStageCreateInfo[2];
				stages[0] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageVertexBit, Module = vertexModule, PName = (byte*)main };
				stages[1] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFragmentBit, Module = fragmentModule, PName = (byte*)main, PSpecializationInfo = &specialization };

				// LibRender2's vertex, as upstream's VAOs have it.
				Vk.VertexInputBindingDescription binding = new Vk.VertexInputBindingDescription { Binding = 0, Stride = (uint)VulkanMeshes.VertexSize, InputRate = Vk.VertexInputRateVertex };
				Vk.VertexInputAttributeDescription* attributes = stackalloc Vk.VertexInputAttributeDescription[5];
				attributes[0] = Attribute(0, Vk.FormatR32G32B32Sfloat, nameof(LibRenderVertex.Position));
				attributes[1] = Attribute(1, Vk.FormatR32G32B32Sfloat, nameof(LibRenderVertex.Normal));
				attributes[2] = Attribute(2, Vk.FormatR32G32Sfloat, nameof(LibRenderVertex.UV));
				attributes[3] = Attribute(3, Vk.FormatR32G32B32A32Sfloat, nameof(LibRenderVertex.Color));
				attributes[4] = Attribute(4, Vk.FormatR32G32B32Sint, nameof(LibRenderVertex.MatrixChain));
				Vk.PipelineVertexInputStateCreateInfo vertexInput = new Vk.PipelineVertexInputStateCreateInfo
				{
					SType = Vk.StructureTypePipelineVertexInputStateCreateInfo,
					VertexBindingDescriptionCount = 1,
					PVertexBindingDescriptions = &binding,
					VertexAttributeDescriptionCount = 5,
					PVertexAttributeDescriptions = attributes
				};
				Vk.PipelineInputAssemblyStateCreateInfo inputAssembly = new Vk.PipelineInputAssemblyStateCreateInfo { SType = Vk.StructureTypePipelineInputAssemblyStateCreateInfo, Topology = Vk.PrimitiveTopologyTriangleList };
				Vk.PipelineViewportStateCreateInfo viewport = new Vk.PipelineViewportStateCreateInfo { SType = Vk.StructureTypePipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
				// Upstream culls OpenGL's front faces (counter-clockwise); drawn without a Y flip, Vulkan sees those as clockwise.
				Vk.PipelineRasterizationStateCreateInfo rasterization = new Vk.PipelineRasterizationStateCreateInfo
				{
					SType = Vk.StructureTypePipelineRasterizationStateCreateInfo,
					PolygonMode = Vk.PolygonModeFill,
					CullMode = cull ? Vk.CullModeFront : Vk.CullModeNone,
					FrontFace = Vk.FrontFaceClockwise,
					LineWidth = 1.0f
				};
				Vk.PipelineMultisampleStateCreateInfo multisample = new Vk.PipelineMultisampleStateCreateInfo { SType = Vk.StructureTypePipelineMultisampleStateCreateInfo, RasterizationSamples = 1 };
				Vk.PipelineDepthStencilStateCreateInfo depth = new Vk.PipelineDepthStencilStateCreateInfo
				{
					SType = Vk.StructureTypePipelineDepthStencilStateCreateInfo,
					DepthTestEnable = depthTest ? 1u : 0u,
					DepthWriteEnable = depthWrite ? 1u : 0u,
					DepthCompareOp = Vk.CompareOpLessOrEqual,
					MaxDepthBounds = 1.0f
				};
				// Colour blends as upstream; alpha accumulates coverage for the composite.
				Vk.PipelineColorBlendAttachmentState attachment = new Vk.PipelineColorBlendAttachmentState { ColorWriteMask = Vk.ColorComponentAll };
				if (blend == Blend.Normal)
				{
					attachment.BlendEnable = 1;
					attachment.SrcColorBlendFactor = Vk.BlendFactorSrcAlpha;
					attachment.DstColorBlendFactor = Vk.BlendFactorOneMinusSrcAlpha;
					attachment.SrcAlphaBlendFactor = Vk.BlendFactorOne;
					attachment.DstAlphaBlendFactor = Vk.BlendFactorOneMinusSrcAlpha;
				}
				else if (blend == Blend.Additive)
				{
					attachment.BlendEnable = 1;
					attachment.SrcColorBlendFactor = Vk.BlendFactorSrcAlpha;
					attachment.DstColorBlendFactor = Vk.BlendFactorOne;
					attachment.SrcAlphaBlendFactor = Vk.BlendFactorZero;
					attachment.DstAlphaBlendFactor = Vk.BlendFactorOne;
				}

				Vk.PipelineColorBlendStateCreateInfo colorBlend = new Vk.PipelineColorBlendStateCreateInfo { SType = Vk.StructureTypePipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
				uint* dynamicStates = stackalloc uint[2];
				dynamicStates[0] = Vk.DynamicStateViewport;
				dynamicStates[1] = Vk.DynamicStateScissor;
				Vk.PipelineDynamicStateCreateInfo dynamic = new Vk.PipelineDynamicStateCreateInfo { SType = Vk.StructureTypePipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
				uint colorFormat = SharedTarget.ColorFormat;
				Vk.PipelineRenderingCreateInfo rendering = new Vk.PipelineRenderingCreateInfo
				{
					SType = Vk.StructureTypePipelineRenderingCreateInfo,
					ColorAttachmentCount = 1,
					PColorAttachmentFormats = &colorFormat,
					DepthAttachmentFormat = SharedTarget.DepthFormat
				};
				Vk.GraphicsPipelineCreateInfo info = new Vk.GraphicsPipelineCreateInfo
				{
					SType = Vk.StructureTypeGraphicsPipelineCreateInfo,
					PNext = &rendering,
					StageCount = 2,
					PStages = stages,
					PVertexInputState = &vertexInput,
					PInputAssemblyState = &inputAssembly,
					PViewportState = &viewport,
					PRasterizationState = &rasterization,
					PMultisampleState = &multisample,
					PDepthStencilState = &depth,
					PColorBlendState = &colorBlend,
					PDynamicState = &dynamic,
					Layout = pipelineLayout,
					BasePipelineIndex = -1
				};
				Vk.Check(Vk.vkCreateGraphicsPipelines(device.Device, 0, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
			}
			finally
			{
				Marshal.FreeHGlobal(main);
			}

			pipelines[key] = pipeline;
			return pipeline;
		}

		private static Vk.VertexInputAttributeDescription Attribute(uint location, uint format, string field)
		{
			return new Vk.VertexInputAttributeDescription { Location = location, Binding = 0, Format = format, Offset = (uint)Marshal.OffsetOf(typeof(LibRenderVertex), field) };
		}

		// --- gathering the frame: upstream's passes give their faces (main thread), then Build makes the records ---

		/// <summary>Starts a frame's world layer with upstream's renderer state: camera, lighting and fog.</summary>
		internal void Begin(BaseRenderer baseRenderer)
		{
			renderer = baseRenderer;
			itemCount = 0;
			passes.Clear();
			currentLayer = WorldLayer;
			cabItemStart = -1;
			cameraTranslation = renderer.Camera.TranslationMatrix;
			viewMatrix = renderer.CurrentViewMatrix;
			backFaceCulling = renderer.OptionBackFaceCulling;
			fogEnabled = renderer.Fog.Enabled;
			lightingAmount = renderer.Lighting.OptionLightingResultingAmount;

			FrameData data = new FrameData();
			Store(renderer.CurrentProjectionMatrix, data.Projection);
			Vector3 light = renderer.TransformedLightPosition;
			light.Normalize();
			data.Light[0] = (float)light.X;
			data.Light[1] = (float)light.Y;
			data.Light[2] = (float)light.Z;
			StoreColor(renderer.Lighting.OptionAmbientColor, data.Light, 4);
			StoreColor(renderer.Lighting.OptionDiffuseColor, data.Light, 8);
			StoreColor(renderer.Lighting.OptionSpecularColor, data.Light, 12);
			Vector4 model = renderer.Lighting.LightModel;
			data.Light[16] = (float)model.X;
			data.Light[17] = (float)model.Y;
			data.Light[18] = (float)model.Z;
			data.Light[19] = (float)model.W;
			StoreColor(renderer.Fog.Color, data.Light, 20);
			data.FogStart = renderer.Fog.Start;
			data.FogEnd = renderer.Fog.End;
			data.FogDensity = renderer.Fog.Density;
			data.FogIsLinear = renderer.Fog.IsLinear ? 1 : 0;
			data.IsLight = renderer.OptionLighting ? 1 : 0;
			frameData[WorldLayer] = data;
		}

		/// <summary>Where the cab layer's faces start, or -1 if it has none this frame.</summary>
		private int cabItemStart;

		/// <summary>Whether the frame has a cab layer to draw (and lay over).</summary>
		internal bool HasCab => cabItemStart >= 0 && cabItemStart < itemCount;

		/// <summary>
		/// Starts the frame's overlay (cab) layer, as upstream's renderer draws it after the world: its own projection
		/// (the cab's near plane, 50 m far), the view, no fog, and for a 3D cab the light-grey light upstream sets for it.
		/// The passes given from now are the cab's.
		/// </summary>
		internal void BeginCab(Matrix4D projection, Matrix4D view, bool lit, Vector3 lightPosition)
		{
			currentLayer = CabLayer;
			cabItemStart = itemCount;
			cabViewMatrix = view;
			FrameData data = frameData[WorldLayer];
			Store(projection, data.Projection);
			if (lit)
			{
				Vector3 light = lightPosition;
				light.Normalize();
				data.Light[0] = (float)light.X;
				data.Light[1] = (float)light.Y;
				data.Light[2] = (float)light.Z;
				StoreColor(Color24.LightGrey, data.Light, 4);
				StoreColor(Color24.LightGrey, data.Light, 8);
			}

			data.IsLight = lit ? 1 : 0;
			// Upstream draws the cab without shadows.
			data.ShadowEnabled = 0;
			frameData[CabLayer] = data;
		}

		/// <summary>
		/// The world layer's shadows this frame, as upstream binds them to its shader (LibRender2's Shadows.Bind): the
		/// cascades' light-space matrices and splits, the biases, strength and filter; null for none.
		/// </summary>
		internal void SetShadows(CascadedShadowCaster caster, float strength, bool smooth, float filterRadius, float userBias, float normalBiasTexels)
		{
			FrameData data = frameData[WorldLayer];
			shadowCascades = 0;
			if (caster == null)
			{
				data.ShadowEnabled = 0;
				frameData[WorldLayer] = data;
				return;
			}

			shadowCascades = caster.CascadeCount;
			data.ShadowEnabled = 1;
			data.ShadowCascades = caster.CascadeCount;
			data.ShadowSmooth = smooth ? 1 : 0;
			data.ShadowStrength = strength;
			data.ShadowFilterRadius = filterRadius;
			Store(Matrix4D.Invert(viewMatrix), data.InverseView);
			for (int i = 0; i < 4; i++)
			{
				if (i < caster.CascadeCount)
				{
					Store(caster.LightSpaceMatrices[i], data.LightSpace + 16 * i);
					data.ShadowSplit[i] = caster.SplitDistances[i];
					data.ShadowBias[i] = caster.CascadeBiases[i] + userBias;
					data.ShadowNormalBias[i] = normalBiasTexels;
					data.ShadowTexelSize[i] = caster.TexelWorldSizes[i];
				}
				else
				{
					data.ShadowSplit[i] = 0.0f;
					data.ShadowTexelSize[i] = caster.CascadeCount > 0 ? caster.TexelWorldSizes[caster.CascadeCount - 1] : 0.05f;
				}
			}

			frameData[WorldLayer] = data;
		}

		/// <summary>How many cascades the shadow pass draws this frame (0: none).</summary>
		private int shadowCascades;

		private static void StoreColor(Color24 color, float* to, int at)
		{
			to[at] = color.R / 255.0f;
			to[at + 1] = color.G / 255.0f;
			to[at + 2] = color.B / 255.0f;
			to[at + 3] = 1.0f;
		}

		/// <summary>Upstream's Matrix4D as its shader has it: the rows, in order (OpenGL reads them as columns).</summary>
		private static void Store(Matrix4D m, float* to)
		{
			to[0] = (float)m.Row0.X; to[1] = (float)m.Row0.Y; to[2] = (float)m.Row0.Z; to[3] = (float)m.Row0.W;
			to[4] = (float)m.Row1.X; to[5] = (float)m.Row1.Y; to[6] = (float)m.Row1.Z; to[7] = (float)m.Row1.W;
			to[8] = (float)m.Row2.X; to[9] = (float)m.Row2.Y; to[10] = (float)m.Row2.Z; to[11] = (float)m.Row2.W;
			to[12] = (float)m.Row3.X; to[13] = (float)m.Row3.Y; to[14] = (float)m.Row3.Z; to[15] = (float)m.Row3.W;
		}

		/// <summary>
		/// Starts a pass with upstream's state for it: blending, the alpha function, depth writes, and whether its faces
		/// must be drawn in the order given (blended faces), or may be grouped (the rest).
		/// </summary>
		internal void Pass(Blend blend, int alphaFunction, float alphaReference, bool depthWrite, bool ordered, bool depthTest = true)
		{
			passes.Add(new PassInfo { Layer = currentLayer, Blend = blend, DepthTest = depthTest, DepthWrite = depthWrite, Ordered = ordered });
			currentAlphaFunction = alphaFunction;
			currentAlphaReference = alphaReference;
		}

		/// <summary>Changes the pass's alpha function (upstream's additive faces have none) without starting a new pass.</summary>
		internal void SetAlphaFunction(int alphaFunction, float alphaReference)
		{
			currentAlphaFunction = alphaFunction;
			currentAlphaReference = alphaReference;
		}

		/// <summary>A face for the pass, drawn as upstream's RenderFace would draw it.</summary>
		internal void Face(ObjectState state, MeshFace face)
		{
			if (itemCount == items.Length)
			{
				Array.Resize(ref items, items.Length * 2);
			}

			items[itemCount++] = new Item { State = state, Face = face, Pass = passes.Count - 1, AlphaFunction = currentAlphaFunction, AlphaReference = currentAlphaReference };
		}

		private const float Inv255 = 1.0f / 255.0f;


		/// <summary>
		/// The faces given, resolved on the main thread: what needs OpenGL or changes shared state (missing VAOs, textures
		/// not loaded yet or animated, objects' lazily made model matrices), and each face's mesh, indices and textures'
		/// places, so that making the records (BuildRecords, on other threads) reads nothing the main thread changes.
		/// </summary>
		internal void Prepare()
		{
			Textures.StartBudget();
			ObjectState last = null;
			for (int i = 0; i < itemCount; i++)
			{
				ref Item item = ref items[i];
				item.Mesh = null;
				ObjectState state = item.State;
				Mesh mesh = state.Prototype.Mesh;
				if (mesh.Vertices.Length < 1)
				{
					continue;
				}

				if (mesh.VAO == null)
				{
					VAOExtensions.CreateOrUpdateVAO(mesh, state.Prototype.Dynamic, renderer.DefaultShader.VertexLayout, renderer);
				}

				if (state != last)
				{
					// ObjectState works its model matrix out when first asked, so it is asked here, not on the workers.
					Matrix4D model = state.ModelMatrix;
					last = state;
				}

				if (!((mesh.VAO as VertexArrayObject)?.ObnsVulkanMesh is VulkanMesh vulkanMesh) || !vulkanMesh.Find(item.Face.IboStartIndex, out int slot, out item.FirstIndex, out item.IndexCount))
				{
					continue;
				}

				ref MeshMaterial material = ref mesh.Materials[item.Face.Material];
				OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
				item.DayTexture = TextureIndex(material.DaytimeTexture, wrap, vulkanMesh.DaySlots, slot);
				item.NightTexture = -1;
				if (material.NighttimeTexture != null && material.NighttimeTexture != material.DaytimeTexture && BlendFactor(state) != 0)
				{
					item.NightTexture = TextureIndex(material.NighttimeTexture, wrap, vulkanMesh.NightSlots, slot);
				}

				item.Mesh = vulkanMesh;
			}
		}

		/// <summary>
		/// Makes the frame's records from the prepared faces, in chunks on every core, then merges the chunks in order
		/// (blended faces keep upstream's order). Reads only the faces and the frame's state taken by Begin.
		/// </summary>
		internal void BuildRecords()
		{
			chunkCount = itemCount == 0 ? 0 : Math.Min(Math.Max(1, itemCount / 128), FrameWorkers.Shared.Threads * 4);
			while (chunks.Count < chunkCount)
			{
				chunks.Add(new Chunk());
			}

			FrameWorkers.Shared.Run(chunkCount, buildChunks);
			Merge();
		}

		private float BlendFactor(ObjectState state)
		{
			float blendFactor = Inv255 * state.DaytimeNighttimeBlend + 1.0f - lightingAmount;
			return blendFactor > 1.0f ? 1.0f : blendFactor;
		}

		private void BuildChunks(int first, int last)
		{
			for (int c = first; c < last; c++)
			{
				BuildChunk(chunks[c], (int)((long)itemCount * c / chunkCount), (int)((long)itemCount * (c + 1) / chunkCount));
			}
		}

		/// <summary>A chunk's records (on a worker): read-only use of upstream's state, writes only to the chunk.</summary>
		private void BuildChunk(Chunk chunk, int start, int end)
		{
			chunk.ObjectCount = chunk.DrawCount = chunk.CommandCount = chunk.MatrixCount = chunk.Skipped = 0;
			chunk.ObjectTicks = 0;
			ObjectState lastState = null;
			Matrix4D lastModelMatrix = Matrix4D.Identity;
			int lastObject = 0, lastLayer = -1;
			for (int i = start; i < end; i++)
			{
				int layer = passes[items[i].Pass].Layer;
				if (layer != lastLayer)
				{
					// An object's record holds its model-view, which is the layer's.
					lastState = null;
					lastLayer = layer;
				}

				BuildFace(chunk, ref items[i], ref lastState, ref lastModelMatrix, ref lastObject);
			}
		}

		/// <summary>A face's records, as upstream's RenderFace would draw it in its pass.</summary>
		private void BuildFace(Chunk chunk, ref Item item, ref ObjectState lastState, ref Matrix4D lastModelMatrix, ref int lastObject)
		{
			VulkanMesh vulkanMesh = item.Mesh;
			if (vulkanMesh == null)
			{
				if (item.State.Prototype.Mesh.Vertices.Length > 0)
				{
					chunk.Skipped++;
				}

				return;
			}

			ObjectState state = item.State;
			Mesh mesh = state.Prototype.Mesh;
			uint firstIndex = item.FirstIndex, indexCount = item.IndexCount;
			PassInfo pass = passes[item.Pass];

			// The object: its matrices, once per object as upstream sends them.
			if (state != lastState || state.Prototype.Dynamic)
			{
				long objectStart = System.Diagnostics.Stopwatch.GetTimestamp();
				lastModelMatrix = state.ModelMatrix * cameraTranslation;
				lastObject = AddObject(chunk, state, lastModelMatrix * (pass.Layer == CabLayer ? cabViewMatrix : viewMatrix));
				lastState = state;
				chunk.ObjectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - objectStart;
			}

			ref MeshMaterial material = ref mesh.Materials[item.Face.Material];
			bool cull = backFaceCulling && (item.Face.Flags & FaceFlags.Face2Mask) == 0;
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;

			float distanceFactor = material.GlowAttenuationData != 0
				? (float)Glow.GetDistanceFactor(lastModelMatrix, mesh.Vertices, ref item.Face, material.GlowAttenuationData)
				: 1.0f;
			float blendFactor = BlendFactor(state);

			DrawData draw = new DrawData
			{
				ColorR = material.Color.R * Inv255, ColorG = material.Color.G * Inv255, ColorB = material.Color.B * Inv255, ColorA = material.Color.A * Inv255,
				Flags = (int)material.Flags,
				Object = lastObject,
				Sampler = (int)wrap & 3,
				Fog = fogEnabled && pass.Layer == WorldLayer ? 1 : 0,
				AlphaFunction = item.AlphaFunction,
				AlphaReference = item.AlphaReference,
				Shininess = 1.0f
			};
			Color24 specular = (material.Flags & MaterialFlags.Specular) != 0 ? material.SpecularColor : new Color24(material.Color.R, material.Color.G, material.Color.B);
			draw.SpecularR = specular.R * Inv255;
			draw.SpecularG = specular.G * Inv255;
			draw.SpecularB = specular.B * Inv255;
			draw.SpecularA = (material.Flags & MaterialFlags.Specular) != 0 ? 1.0f : draw.ColorA;
			draw.EmissionR = material.EmissiveColor.R * Inv255;
			draw.EmissionG = material.EmissiveColor.G * Inv255;
			draw.EmissionB = material.EmissiveColor.B * Inv255;

			// Daytime texture, else white.
			draw.Texture = item.DayTexture >= 0 ? item.DayTexture : VulkanTextures.White;

			Blend blend = pass.Blend;
			bool additive = material.BlendMode == MeshMaterialBlendMode.Additive;
			float factor;
			if (additive)
			{
				factor = 1.0f;
				blend = Blend.Additive;
				draw.Fog = 0;
			}
			else if (material.NighttimeTexture == null || material.NighttimeTexture == material.DaytimeTexture)
			{
				factor = 1.0f - 0.7f * blendFactor;
			}
			else
			{
				factor = 1.0f;
			}

			draw.Brightness = factor;
			float alphaFactor = distanceFactor;
			if (material.NighttimeTexture != null && (material.Flags & MaterialFlags.CrossFadeTexture) != 0)
			{
				alphaFactor *= 1.0f - blendFactor;
			}

			draw.Opacity = Inv255 * material.Color.A * alphaFactor;
			// A shadow caster as upstream's shadow pass has it: a world face, not additive, its material and object casting.
			int shadow = 0;
			if (pass.Layer == WorldLayer && !additive && (material.Flags & MaterialFlags.NoShadow) == 0 && !state.DisableShadowCasting)
			{
				shadow = backFaceCulling && (item.Face.Flags & FaceFlags.Face2Mask) == 0 ? 1 : 2;
			}

			AddDraw(chunk, draw, pass, item.Pass, blend, cull, firstIndex, indexCount, vulkanMesh, false, shadow);

			// Nighttime texture, blended over the daytime one.
			if (blendFactor != 0 && item.NightTexture >= 0)
			{
				draw.Texture = item.NightTexture;
				draw.AlphaFunction = AlphaGreater;
				draw.AlphaReference = 0.0f;
				draw.Opacity = Inv255 * material.Color.A * distanceFactor * blendFactor;
				AddDraw(chunk, draw, pass, item.Pass, additive ? Blend.Additive : Blend.Normal, cull, firstIndex, indexCount, vulkanMesh, true, 0);
			}
		}

		/// <summary>
		/// A texture's place in the bindless array (made the first time it is needed; main thread), or -1 if it cannot be
		/// made. The place is kept per face, for as long as it is the same texture and nothing has moved; an animated
		/// texture is asked for every frame (which advances it).
		/// </summary>
		private int TextureIndex(Texture texture, OpenGlTextureWrapMode wrap, TextureSlot[] slots, int slot)
		{
			if (texture == null)
			{
				return -1;
			}

			TextureSlot cached = slots[slot];
			if (cached != null && ReferenceEquals(cached.Texture, texture) && cached.Generation == Textures.Generation)
			{
				return cached.Index;
			}

			int index = Textures.Prepare(texture, wrap, out bool animated);
			if (index == VulkanTextures.Later)
			{
				// Not made yet (the frame's budget is spent): white this frame, asked for again next frame.
				return -1;
			}

			slots[slot] = animated ? null : new TextureSlot(texture, index, Textures.Generation);
			return index;
		}

		private int AddObject(Chunk chunk, ObjectState state, Matrix4D modelView)
		{
			if (chunk.ObjectCount == chunk.Objects.Length)
			{
				Array.Resize(ref chunk.Objects, chunk.Objects.Length * 2);
			}

			ObjectData data = new ObjectData();
			Store(modelView, data.ModelView);
			Store(state.TextureTranslation, data.TextureMatrix);

			// The normal matrix: transpose(inverse(mat3(modelView as the shader has it))), which is the inverse of
			// the upper 3x3 of upstream's (row-major) matrix; stored as the shader's columns.
			double a = modelView.Row0.X, b = modelView.Row0.Y, c = modelView.Row0.Z;
			double d = modelView.Row1.X, e = modelView.Row1.Y, f = modelView.Row1.Z;
			double g = modelView.Row2.X, h = modelView.Row2.Y, i = modelView.Row2.Z;
			double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
			double s = Math.Abs(determinant) > 1e-30 ? 1.0 / determinant : 0.0;
			// inverse[row][column]
			double i00 = (e * i - f * h) * s, i01 = (c * h - b * i) * s, i02 = (b * f - c * e) * s;
			double i10 = (f * g - d * i) * s, i11 = (a * i - c * g) * s, i12 = (c * d - a * f) * s;
			double i20 = (d * h - e * g) * s, i21 = (b * g - a * h) * s, i22 = (a * e - b * d) * s;
			data.Normal[0] = (float)i00; data.Normal[1] = (float)i10; data.Normal[2] = (float)i20;
			data.Normal[4] = (float)i01; data.Normal[5] = (float)i11; data.Normal[6] = (float)i21;
			data.Normal[8] = (float)i02; data.Normal[9] = (float)i12; data.Normal[10] = (float)i22;

			Matrix4D[] animation = state.Matricies;
			if (animation != null && animation.Length > 0)
			{
				// Pad0 marks an object with animation matrices: their base is moved when the chunks are merged.
				data.MatrixBase = chunk.MatrixCount;
				data.Pad0 = 1;
				data.MatrixCount = animation.Length;
				int needed = (chunk.MatrixCount + animation.Length) * 16;
				if (needed > chunk.Matrices.Length)
				{
					Array.Resize(ref chunk.Matrices, Math.Max(needed, chunk.Matrices.Length * 2));
				}

				fixed (float* to = chunk.Matrices)
				{
					for (int m = 0; m < animation.Length; m++)
					{
						Store(animation[m], to + (chunk.MatrixCount + m) * 16);
					}
				}

				chunk.MatrixCount += animation.Length;
			}

			chunk.Objects[chunk.ObjectCount] = data;
			return chunk.ObjectCount++;
		}

		private void AddDraw(Chunk chunk, DrawData draw, PassInfo pass, int passIndex, Blend blend, bool cull, uint firstIndex, uint indexCount, VulkanMesh mesh, bool night, int shadow)
		{
			if (chunk.DrawCount == chunk.Draws.Length)
			{
				Array.Resize(ref chunk.Draws, chunk.Draws.Length * 2);
			}

			if (chunk.CommandCount == chunk.Commands.Length)
			{
				Array.Resize(ref chunk.Commands, chunk.Commands.Length * 2);
			}

			// Grouped passes cull in the pipeline; ordered ones draw both sides and discard OpenGL's front faces.
			bool pipelineCull = cull && !pass.Ordered;
			draw.CullFront = cull && pass.Ordered ? 1 : 0;
			int pipelineKey = (int)blend | (pass.DepthWrite ? 4 : 0) | (pipelineCull ? 8 : 0) | (pass.DepthTest ? 0 : 16);
			// Grouped by: nighttime draws after daytime ones (they blend over them), then pipeline, then (with pages) the
			// texture's page, then vertex and index blocks.
			long page = texturePage == 0 ? 0 : draw.Texture / texturePage;
			long key = ((night ? 1L : 0L) << 62) | ((long)pipelineKey << 48) | ((page & 0xFFFF) << 32) | ((long)(mesh.VertexBlock & 0xFFFF) << 16) | (long)(mesh.IndexBlock & 0xFFFF);
			chunk.Draws[chunk.DrawCount] = draw;
			chunk.Commands[chunk.CommandCount++] = new DrawCommand { Key = key, FirstIndex = firstIndex, IndexCount = indexCount, VertexBase = mesh.VertexBase, Draw = chunk.DrawCount, Pass = passIndex, Shadow = shadow };
			chunk.DrawCount++;
		}

		/// <summary>The chunks' records joined in order, their indices moved to the joined arrays, and the passes' ranges found.</summary>
		private void Merge()
		{
			objectCount = drawCount = matrixCount = commandCount = 0;
			skippedFaces = 0;
			for (int c = 0; c < chunkCount; c++)
			{
				Chunk chunk = chunks[c];
				int objectBase = objectCount, drawBase = drawCount, matrixBase = matrixCount;
				Grow(ref objects, objectCount + chunk.ObjectCount);
				Grow(ref draws, drawCount + chunk.DrawCount);
				Grow(ref commands, commandCount + chunk.CommandCount);
				Grow(ref matrices, (matrixCount + chunk.MatrixCount) * 16);
				for (int k = 0; k < chunk.ObjectCount; k++)
				{
					ObjectData data = chunk.Objects[k];
					if (data.Pad0 != 0)
					{
						data.MatrixBase += matrixBase;
					}

					objects[objectCount++] = data;
				}

				Array.Copy(chunk.Matrices, 0, matrices, matrixCount * 16, chunk.MatrixCount * 16);
				matrixCount += chunk.MatrixCount;
				for (int k = 0; k < chunk.DrawCount; k++)
				{
					DrawData draw = chunk.Draws[k];
					draw.Object += objectBase;
					draws[drawCount++] = draw;
				}

				for (int k = 0; k < chunk.CommandCount; k++)
				{
					DrawCommand command = chunk.Commands[k];
					command.Draw += drawBase;
					commands[commandCount++] = command;
				}

				skippedFaces += chunk.Skipped;
				ObjectTicks += chunk.ObjectTicks;
			}

			segmentStarts.Clear();
			segmentOrdered.Clear();
			cabCommandStart = commandCount;
			int lastPass = -1;
			for (int i = 0; i < commandCount; i++)
			{
				if (commands[i].Pass != lastPass)
				{
					lastPass = commands[i].Pass;
					segmentStarts.Add(i);
					segmentOrdered.Add(passes[lastPass].Ordered);
					if (passes[lastPass].Layer == CabLayer && cabCommandStart == commandCount)
					{
						cabCommandStart = i;
					}
				}
			}
		}

		/// <summary>Where the cab layer's commands start among the merged ones (commandCount if none).</summary>
		private int cabCommandStart;

		private static void Grow<T>(ref T[] array, int needed)
		{
			if (needed > array.Length)
			{
				Array.Resize(ref array, Math.Max(needed, array.Length * 2));
			}
		}

		// --- recording ---

		/// <summary>Whether a layer has anything to draw.</summary>
		internal bool Empty(int layer) => layer == WorldLayer ? cabCommandStart == 0 : cabCommandStart == commandCount;

		/// <summary>Copies the frame's records into the slot's buffers (before its renderings).</summary>
		internal void Upload(int slot)
		{
			// Grouped passes sorted by key; ordered ones kept as they are.
			for (int s = 0; s < segmentStarts.Count; s++)
			{
				int start = segmentStarts[s], end = s + 1 < segmentStarts.Count ? segmentStarts[s + 1] : commandCount;
				if (!segmentOrdered[s] && end - start > 1)
				{
					Array.Sort(commands, start, end - start, KeyComparer.Instance);
				}
			}

			// The shadow casters' draws, grouped by culling and buffer blocks, after the layers' commands.
			shadowCount = 0;
			if (shadowCascades > 0)
			{
				Grow(ref shadowCommands, cabCommandStart);
				for (int i = 0; i < cabCommandStart; i++)
				{
					if (commands[i].Shadow != 0)
					{
						DrawCommand command = commands[i];
						command.Key = ((long)command.Shadow << 48) | (command.Key & 0xFFFFFFFFFFFFL);
						shadowCommands[shadowCount++] = command;
					}
				}

				Array.Sort(shadowCommands, 0, shadowCount, KeyComparer.Instance);
			}

			EnsureBuffers(slot);
			fixed (FrameData* frames = frameData)
			{
				Buffer.MemoryCopy(frames, frameBuffers[slot].Data, (long)frameBuffers[slot].Size, (long)frameData.Length * sizeof(FrameData));
			}

			fixed (ObjectData* data = objects)
			{
				Buffer.MemoryCopy(data, objectBuffers[slot].Data, (long)objectBuffers[slot].Size, (long)objectCount * sizeof(ObjectData));
			}

			fixed (DrawData* data = draws)
			{
				Buffer.MemoryCopy(data, drawBuffers[slot].Data, (long)drawBuffers[slot].Size, (long)drawCount * sizeof(DrawData));
			}

			fixed (float* data = matrices)
			{
				Buffer.MemoryCopy(data, matrixBuffers[slot].Data, (long)matrixBuffers[slot].Size, (long)matrixCount * 64);
			}

			Vk.DrawIndexedIndirectCommand* indirect = (Vk.DrawIndexedIndirectCommand*)indirectBuffers[slot].Data;
			for (int i = 0; i < commandCount; i++)
			{
				indirect[i] = new Vk.DrawIndexedIndirectCommand
				{
					IndexCount = commands[i].IndexCount,
					InstanceCount = 1,
					FirstIndex = commands[i].FirstIndex,
					VertexOffset = commands[i].VertexBase,
					FirstInstance = (uint)commands[i].Draw
				};
			}

			for (int i = 0; i < shadowCount; i++)
			{
				indirect[commandCount + i] = new Vk.DrawIndexedIndirectCommand
				{
					IndexCount = shadowCommands[i].IndexCount,
					InstanceCount = 1,
					FirstIndex = shadowCommands[i].FirstIndex,
					VertexOffset = shadowCommands[i].VertexBase,
					FirstInstance = (uint)shadowCommands[i].Draw
				};
			}

			LastDraws = commandCount;
			LastCabDraws = commandCount - cabCommandStart;
			LastIndirectCalls = 0;
		}

		/// <summary>Records a layer's draws (inside its rendering), from the records Upload copied.</summary>
		internal void Draw(IntPtr commandBuffer, int slot, uint width, uint height, int layer)
		{
			Vk.Viewport viewport = new Vk.Viewport { Width = width, Height = height, MaxDepth = 1.0f };
			Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = width, Height = height } };
			Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
			Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
			ulong set = DescriptorSets[slot];
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
			int frameIndex = layer;
			Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, 4, &frameIndex);

			// Runs of the same pipeline and buffer blocks, each one indirect draw.
			int calls = 0;
			int boundPipeline = -1, boundPage = -1, boundVertexBlock = -1, boundIndexBlock = -1;
			int run = layer == WorldLayer ? 0 : cabCommandStart, end = layer == WorldLayer ? cabCommandStart : commandCount;
			while (run < end)
			{
				long key = commands[run].Key & ~(1L << 62);
				int next = run + 1;
				while (next < end && (commands[next].Key & ~(1L << 62)) == key)
				{
					next++;
				}

				int pipelineKey = (int)(key >> 48);
				int vertexBlock = (int)((key >> 16) & 0xFFFF), indexBlock = (int)(key & 0xFFFF);
				if (pipelineKey != boundPipeline)
				{
					Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, Pipeline(pipelineKey));
					boundPipeline = pipelineKey;
				}

				BindPage(commandBuffer, slot, (int)((key >> 32) & 0xFFFF), ref boundPage);

				if (vertexBlock != boundVertexBlock)
				{
					ulong buffer = Meshes.Vertices.Buffers[vertexBlock];
					ulong offset = 0;
					Vk.vkCmdBindVertexBuffers(commandBuffer, 0, 1, &buffer, &offset);
					boundVertexBlock = vertexBlock;
				}

				if (indexBlock != boundIndexBlock)
				{
					Vk.vkCmdBindIndexBuffer(commandBuffer, Meshes.Indices.Buffers[indexBlock], 0, Vk.IndexTypeUint32);
					boundIndexBlock = indexBlock;
				}

				Vk.vkCmdDrawIndexedIndirect(commandBuffer, indirectBuffers[slot].Buffer, (ulong)run * (ulong)sizeof(Vk.DrawIndexedIndirectCommand), (uint)(next - run), (uint)sizeof(Vk.DrawIndexedIndirectCommand));
				calls++;
				run = next;
			}

			LastIndirectCalls += calls;
		}

		/// <summary>With pages, binds a draw's page of textures (set 1) for the frame slot, if it is not bound already.</summary>
		private void BindPage(IntPtr commandBuffer, int slot, int page, ref int boundPage)
		{
			if (pages == null || page == boundPage)
			{
				return;
			}

			ulong set = pages.Set(page, slot);
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 1, 1, &set, 0, null);
			boundPage = page;
		}

		/// <summary>With pages, the slot's pages take the texture writes made since (its last frame done, before recording).</summary>
		internal void TakeTextureWrites(int slot) => pages?.Take(slot);

		private DrawCommand[] shadowCommands = new DrawCommand[1024];
		private int shadowCount;

		/// <summary>Shadow casters drawn in the last frame (per cascade), for the log.</summary>
		internal int LastShadowDraws => shadowCount;

		/// <summary>
		/// Records the shadow pass (before the world layer's rendering, after Upload): each cascade's layer cleared and the
		/// casters drawn into it in the cascade's light space, as upstream's Shadows.RenderPass; with shadows off, the
		/// maps are only cleared, so they are always ready for the world shader.
		/// </summary>
		internal void RecordShadows(IntPtr commandBuffer, int slot)
		{
			Shadows.BeginWriting(commandBuffer);
			uint size = (uint)Shadows.Resolution;
			ulong set = DescriptorSets[slot];
			for (int cascade = 0; cascade < Shadows.Cascades; cascade++)
			{
				Vk.RenderingAttachmentInfo depth = new Vk.RenderingAttachmentInfo
				{
					SType = Vk.StructureTypeRenderingAttachmentInfo,
					ImageView = Shadows.LayerViews[cascade],
					ImageLayout = Vk.ImageLayoutDepthStencilAttachmentOptimal,
					LoadOp = Vk.AttachmentLoadOpClear,
					StoreOp = Vk.AttachmentStoreOpStore,
					ClearValue = new Vk.ClearValue { Depth = 1.0f }
				};
				Vk.RenderingInfo renderingInfo = new Vk.RenderingInfo
				{
					SType = Vk.StructureTypeRenderingInfo,
					RenderArea = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = size, Height = size } },
					LayerCount = 1,
					PDepthAttachment = &depth
				};
				Vk.vkCmdBeginRendering(commandBuffer, &renderingInfo);
				if (cascade < shadowCascades && shadowCount > 0)
				{
					Vk.Viewport viewport = new Vk.Viewport { Width = size, Height = size, MaxDepth = 1.0f };
					Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = size, Height = size } };
					Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
					Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
					Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
					int* push = stackalloc int[2];
					push[0] = WorldLayer;
					push[1] = cascade;
					Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, 8, push);
					int boundCull = -1, boundPage = -1, boundVertexBlock = -1, boundIndexBlock = -1;
					int run = 0;
					while (run < shadowCount)
					{
						long key = shadowCommands[run].Key;
						int next = run + 1;
						while (next < shadowCount && shadowCommands[next].Key == key)
						{
							next++;
						}

						int cull = (int)(key >> 48) == 1 ? 1 : 0;
						int vertexBlock = (int)((key >> 16) & 0xFFFF), indexBlock = (int)(key & 0xFFFF);
						if (cull != boundCull)
						{
							Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, ShadowPipeline(cull == 1));
							boundCull = cull;
						}

						BindPage(commandBuffer, slot, (int)((key >> 32) & 0xFFFF), ref boundPage);

						if (vertexBlock != boundVertexBlock)
						{
							ulong buffer = Meshes.Vertices.Buffers[vertexBlock];
							ulong offset = 0;
							Vk.vkCmdBindVertexBuffers(commandBuffer, 0, 1, &buffer, &offset);
							boundVertexBlock = vertexBlock;
						}

						if (indexBlock != boundIndexBlock)
						{
							Vk.vkCmdBindIndexBuffer(commandBuffer, Meshes.Indices.Buffers[indexBlock], 0, Vk.IndexTypeUint32);
							boundIndexBlock = indexBlock;
						}

						Vk.vkCmdDrawIndexedIndirect(commandBuffer, indirectBuffers[slot].Buffer, (ulong)(commandCount + run) * (ulong)sizeof(Vk.DrawIndexedIndirectCommand), (uint)(next - run), (uint)sizeof(Vk.DrawIndexedIndirectCommand));
						run = next;
					}
				}

				Vk.vkCmdEndRendering(commandBuffer);
			}

			Shadows.EndWriting(commandBuffer);
		}

		private sealed class KeyComparer : IComparer<DrawCommand>
		{
			internal static readonly KeyComparer Instance = new KeyComparer();
			public int Compare(DrawCommand x, DrawCommand y) => x.Key.CompareTo(y.Key);
		}

		/// <summary>The slot's buffers, made larger (and pointed at again by its descriptor set) when the frame needs more.</summary>
		private void EnsureBuffers(int slot)
		{
			bool changed = false;
			changed |= Ensure(ref frameBuffers[slot], (ulong)frameData.Length * (ulong)sizeof(FrameData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref objectBuffers[slot], (ulong)Math.Max(1, objectCount) * (ulong)sizeof(ObjectData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref drawBuffers[slot], (ulong)Math.Max(1, drawCount) * (ulong)sizeof(DrawData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref matrixBuffers[slot], (ulong)Math.Max(1, matrixCount) * 64UL, Vk.BufferUsageStorageBuffer);
			Ensure(ref indirectBuffers[slot], (ulong)Math.Max(1, commandCount + shadowCount) * (ulong)sizeof(Vk.DrawIndexedIndirectCommand), Vk.BufferUsageIndirectBuffer);
			if (!changed)
			{
				return;
			}

			Vk.DescriptorBufferInfo* infos = stackalloc Vk.DescriptorBufferInfo[4];
			infos[0] = new Vk.DescriptorBufferInfo { Buffer = frameBuffers[slot].Buffer, Range = ~0UL };
			infos[1] = new Vk.DescriptorBufferInfo { Buffer = objectBuffers[slot].Buffer, Range = ~0UL };
			infos[2] = new Vk.DescriptorBufferInfo { Buffer = drawBuffers[slot].Buffer, Range = ~0UL };
			infos[3] = new Vk.DescriptorBufferInfo { Buffer = matrixBuffers[slot].Buffer, Range = ~0UL };
			Vk.WriteDescriptorSet* writes = stackalloc Vk.WriteDescriptorSet[4];
			for (uint b = 0; b < 4; b++)
			{
				writes[b] = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = DescriptorSets[slot],
					DstBinding = b,
					DescriptorCount = 1,
					DescriptorType = Vk.DescriptorTypeStorageBuffer,
					PBufferInfo = infos + b
				};
			}

			Vk.vkUpdateDescriptorSets(device.Device, 4, writes, 0, null);
		}

		/// <summary>Makes the buffer when it is missing or too small (with room to grow); true if it was made.</summary>
		private bool Ensure(ref HostBuffer buffer, ulong size, uint usage)
		{
			if (buffer != null && buffer.Size >= size)
			{
				return false;
			}

			// The slot's last frame is done (its fence was waited for), so the old buffer can go now.
			buffer?.Dispose();
			buffer = new HostBuffer(device, Math.Max(size + size / 2, 64UL << 10), usage);
			return true;
		}

		/// <summary>Time making object records (matrices, on all threads) since the statistics were last taken, for the log.</summary>
		internal long ObjectTicks;

		/// <summary>Object records in the last frame, for the log.</summary>
		internal int LastObjects => objectCount;

		/// <summary>Faces skipped this frame because their mesh had no Vulkan copy, for the log.</summary>
		internal int SkippedFaces => skippedFaces;

		public void Dispose()
		{
			Vk.vkDeviceWaitIdle(device.Device);
			foreach (ulong pipeline in pipelines.Values)
			{
				Vk.vkDestroyPipeline(device.Device, pipeline, null);
			}

			pipelines.Clear();
			for (int i = 0; i < FramesInFlight; i++)
			{
				frameBuffers[i]?.Dispose();
				objectBuffers[i]?.Dispose();
				drawBuffers[i]?.Dispose();
				matrixBuffers[i]?.Dispose();
				indirectBuffers[i]?.Dispose();
			}

			Meshes.Dispose();
			Textures.Dispose();
			if (vertexModule != 0) Vk.vkDestroyShaderModule(device.Device, vertexModule, null);
			if (fragmentModule != 0) Vk.vkDestroyShaderModule(device.Device, fragmentModule, null);
			if (descriptorPool != 0) Vk.vkDestroyDescriptorPool(device.Device, descriptorPool, null);
			foreach (ulong shadowPipeline in shadowPipelines)
			{
				if (shadowPipeline != 0) Vk.vkDestroyPipeline(device.Device, shadowPipeline, null);
			}

			if (shadowVertexModule != 0) Vk.vkDestroyShaderModule(device.Device, shadowVertexModule, null);
			if (shadowFragmentModule != 0) Vk.vkDestroyShaderModule(device.Device, shadowFragmentModule, null);
			Shadows?.Dispose();
			if (pipelineLayout != 0) Vk.vkDestroyPipelineLayout(device.Device, pipelineLayout, null);
			if (setLayout != 0) Vk.vkDestroyDescriptorSetLayout(device.Device, setLayout, null);
			pages?.Dispose();
		}
	}
}
