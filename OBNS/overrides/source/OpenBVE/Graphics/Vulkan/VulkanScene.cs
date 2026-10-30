// OBNS: not an upstream file. The Vulkan world layer's frame (OBNS/VULKAN.md): the scene's records (WorldScene) in
// storage buffers, drawn with indirect draws.

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
using Blend = OpenBve.Graphics.WorldBlend;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanScene : WorldScene
	{
		private const int FramesInFlight = 2;
		private const uint FrameBinding = 0, ObjectBinding = 1, DrawBinding = 2, MatrixBinding = 3, SamplerBinding = 4, ShadowBinding = 5, TextureBinding = 6;

		private readonly VulkanDevice device;
		internal readonly VulkanTextures Textures;
		internal readonly VulkanMeshes Meshes;

		protected override WorldTextures SceneTextures => Textures;
		private ulong setLayout, pipelineLayout, descriptorPool, vertexModule, fragmentModule, shadowVertexModule, shadowFragmentModule;
		internal readonly VulkanShadows Shadows;
		private readonly ulong[] shadowPipelines = new ulong[2];
		internal readonly ulong[] DescriptorSets = new ulong[FramesInFlight];

		/// <summary>
		/// The pages of textures (set 1), where the device has no descriptor indexing (else null, the textures one bindless
		/// array in set 0): draws are grouped by their texture's page too, and each page bound for its draws.
		/// </summary>
		private readonly VulkanTexturePages pages;
		private readonly Dictionary<int, ulong> pipelines = new Dictionary<int, ulong>();

		// Per frame in flight: the records and the indirect commands.
		private readonly HostBuffer[] frameBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] objectBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] drawBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] matrixBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] indirectBuffers = new HostBuffer[FramesInFlight];

		/// <summary>Indirect draws in the last frame, for the log.</summary>
		internal int LastIndirectCalls { get; private set; }

		internal VulkanScene(VulkanDevice device, Uploader uploader, BaseRenderer renderer)
		{
			this.device = device;
			if (!device.Bindless)
			{
				pages = new VulkanTexturePages(device, device.TexturePageSize, FramesInFlight);
				TexturePage = device.TexturePageSize;
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
		public override void EnsureShadowMaps(int resolution, int cascades)
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
				uint pageSize = (uint)TexturePage;
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
				constants[1] = (uint)TexturePage;
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

		// --- recording ---

		/// <summary>Copies the frame's records into the slot's buffers (before its renderings).</summary>
		internal void Upload(int slot)
		{
			Order();
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
			int run = layer == WorldLayerIndex ? 0 : cabCommandStart, end = layer == WorldLayerIndex ? cabCommandStart : commandCount;
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
					push[0] = WorldLayerIndex;
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

		public override void Dispose()
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
