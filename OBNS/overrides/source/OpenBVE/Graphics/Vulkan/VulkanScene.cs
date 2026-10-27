// OBNS: not an upstream file. The Vulkan world layer's frame (OBNS/VULKAN.md): upstream's per-face drawing
// (LibRender2's BaseRenderer.RenderFace) as records in storage buffers, drawn with indirect draws.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using LibRender2;
using LibRender2.Objects;
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
			public int IsLight, Pad0, Pad1, Pad2;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ObjectData
		{
			public fixed float ModelView[16];
			public fixed float Normal[12];
			public fixed float TextureMatrix[16];
			public int MatrixBase, Pad0, Pad1, Pad2;
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
			public int VertexBase, Draw;
		}

		/// <summary>OpenGL's alpha functions as upstream's shader numbers them; Never means none.</summary>
		internal const int AlphaNever = 512, AlphaLess = 513, AlphaEqual = 514, AlphaGreater = 516;

		internal enum Blend { None = 0, Normal = 1, Additive = 2 }

		private const int FramesInFlight = 2;
		private const uint FrameBinding = 0, ObjectBinding = 1, DrawBinding = 2, MatrixBinding = 3, SamplerBinding = 4, TextureBinding = 5;

		private readonly VulkanDevice device;
		internal readonly VulkanTextures Textures;
		internal readonly VulkanMeshes Meshes;
		private ulong setLayout, pipelineLayout, descriptorPool, vertexModule, fragmentModule;
		internal readonly ulong[] DescriptorSets = new ulong[FramesInFlight];
		private readonly Dictionary<int, ulong> pipelines = new Dictionary<int, ulong>();

		// Per frame in flight: the records and the indirect commands.
		private readonly HostBuffer[] frameBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] objectBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] drawBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] matrixBuffers = new HostBuffer[FramesInFlight];
		private readonly HostBuffer[] indirectBuffers = new HostBuffer[FramesInFlight];

		// The frame being gathered.
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
		private FrameData frameData;

		private BaseRenderer renderer;
		private ObjectState lastState;
		private Matrix4D lastModelMatrix;
		private int lastObject;

		// The pass being gathered (upstream's GL state between faces).
		private Blend passBlend;
		private int passAlphaFunction;
		private float passAlphaReference;
		private bool passDepthWrite, passOrdered;

		/// <summary>Draw records and indirect draws in the last frame, for the log.</summary>
		internal int LastDraws { get; private set; }
		internal int LastIndirectCalls { get; private set; }
		private int skippedFaces;

		internal VulkanScene(VulkanDevice device, Uploader uploader, BaseRenderer renderer)
		{
			this.device = device;
			CreateDescriptors();
			Textures = new VulkanTextures(device, uploader, DescriptorSets, TextureBinding, Interface.CurrentOptions.Interpolation, Interface.CurrentOptions.AnisotropicFilteringLevel);
			Meshes = new VulkanMeshes(device, uploader);
			WriteSamplers();
			vertexModule = ShaderModule("world.vert.spv");
			fragmentModule = ShaderModule("world.frag.spv");
		}

		// --- set-up ---

		private void CreateDescriptors()
		{
			const uint vertexAndFragment = Vk.ShaderStageVertex | Vk.ShaderStageFragment;
			Vk.DescriptorSetLayoutBinding* bindings = stackalloc Vk.DescriptorSetLayoutBinding[6];
			bindings[0] = new Vk.DescriptorSetLayoutBinding { Binding = FrameBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[1] = new Vk.DescriptorSetLayoutBinding { Binding = ObjectBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[2] = new Vk.DescriptorSetLayoutBinding { Binding = DrawBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[3] = new Vk.DescriptorSetLayoutBinding { Binding = MatrixBinding, DescriptorType = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 1, StageFlags = vertexAndFragment };
			bindings[4] = new Vk.DescriptorSetLayoutBinding { Binding = SamplerBinding, DescriptorType = Vk.DescriptorTypeSampler, DescriptorCount = 4, StageFlags = Vk.ShaderStageFragment };
			bindings[5] = new Vk.DescriptorSetLayoutBinding { Binding = TextureBinding, DescriptorType = Vk.DescriptorTypeSampledImage, DescriptorCount = VulkanTextures.Capacity, StageFlags = Vk.ShaderStageFragment };
			uint* flags = stackalloc uint[6];
			for (int i = 0; i < 5; i++)
			{
				flags[i] = 0;
			}

			// The textures: written as they are uploaded, while frames using other elements may be in flight.
			flags[5] = Vk.DescriptorBindingPartiallyBound | Vk.DescriptorBindingUpdateAfterBind | Vk.DescriptorBindingUpdateUnusedWhilePending | Vk.DescriptorBindingVariableDescriptorCount;
			Vk.DescriptorSetLayoutBindingFlagsCreateInfo flagsInfo = new Vk.DescriptorSetLayoutBindingFlagsCreateInfo
			{
				SType = Vk.StructureTypeDescriptorSetLayoutBindingFlagsCreateInfo,
				BindingCount = 6,
				PBindingFlags = flags
			};
			Vk.DescriptorSetLayoutCreateInfo layoutInfo = new Vk.DescriptorSetLayoutCreateInfo
			{
				SType = Vk.StructureTypeDescriptorSetLayoutCreateInfo,
				PNext = &flagsInfo,
				Flags = Vk.DescriptorSetLayoutCreateUpdateAfterBindPool,
				BindingCount = 6,
				PBindings = bindings
			};
			Vk.Check(Vk.vkCreateDescriptorSetLayout(device.Device, &layoutInfo, null, out setLayout), "vkCreateDescriptorSetLayout");

			Vk.PushConstantRange push = new Vk.PushConstantRange { StageFlags = vertexAndFragment, Size = 4 };
			ulong layout = setLayout;
			Vk.PipelineLayoutCreateInfo pipelineLayoutInfo = new Vk.PipelineLayoutCreateInfo
			{
				SType = Vk.StructureTypePipelineLayoutCreateInfo,
				SetLayoutCount = 1,
				PSetLayouts = &layout,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Vk.Check(Vk.vkCreatePipelineLayout(device.Device, &pipelineLayoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

			Vk.DescriptorPoolSize* sizes = stackalloc Vk.DescriptorPoolSize[3];
			sizes[0] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeStorageBuffer, DescriptorCount = 4 * FramesInFlight };
			sizes[1] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeSampler, DescriptorCount = 4 * FramesInFlight };
			sizes[2] = new Vk.DescriptorPoolSize { Type = Vk.DescriptorTypeSampledImage, DescriptorCount = VulkanTextures.Capacity * FramesInFlight };
			Vk.DescriptorPoolCreateInfo poolInfo = new Vk.DescriptorPoolCreateInfo
			{
				SType = Vk.StructureTypeDescriptorPoolCreateInfo,
				Flags = Vk.DescriptorPoolCreateUpdateAfterBind,
				MaxSets = FramesInFlight,
				PoolSizeCount = 3,
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
				PNext = &variableInfo,
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
			bool depthWrite = (key & 4) != 0, cull = (key & 8) != 0;
			IntPtr main = Marshal.StringToHGlobalAnsi("main");
			try
			{
				// Opaque drawing writes full coverage (specialization constant 0).
				uint opaque = blend == Blend.None ? 1u : 0u;
				Vk.SpecializationMapEntry entry = new Vk.SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = (UIntPtr)4 };
				Vk.SpecializationInfo specialization = new Vk.SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = (UIntPtr)4, PData = &opaque };
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
					DepthTestEnable = 1,
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

		// --- gathering the frame (on the main thread, as upstream draws it) ---

		/// <summary>Starts a frame's world layer with upstream's renderer state: camera, lighting and fog.</summary>
		internal void Begin(BaseRenderer baseRenderer)
		{
			renderer = baseRenderer;
			objectCount = drawCount = matrixCount = commandCount = 0;
			segmentStarts.Clear();
			segmentOrdered.Clear();
			lastState = null;
			skippedFaces = 0;

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
			frameData = data;
		}

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
		internal void Pass(Blend blend, int alphaFunction, float alphaReference, bool depthWrite, bool ordered)
		{
			passBlend = blend;
			passAlphaFunction = alphaFunction;
			passAlphaReference = alphaReference;
			passDepthWrite = depthWrite;
			passOrdered = ordered;
			segmentStarts.Add(commandCount);
			segmentOrdered.Add(ordered);
		}

		/// <summary>Changes the pass's alpha function (upstream's additive faces have none) without starting a new pass.</summary>
		internal void SetAlphaFunction(int alphaFunction, float alphaReference)
		{
			passAlphaFunction = alphaFunction;
			passAlphaReference = alphaReference;
		}

		private const float Inv255 = 1.0f / 255.0f;

		/// <summary>A face, as upstream's RenderFace would draw it in this pass.</summary>
		internal void Face(ObjectState state, MeshFace face)
		{
			Mesh mesh = state.Prototype.Mesh;
			if (mesh.Vertices.Length < 1)
			{
				return;
			}

			if (mesh.VAO == null)
			{
				VAOExtensions.CreateOrUpdateVAO(mesh, state.Prototype.Dynamic, renderer.DefaultShader.VertexLayout, renderer);
			}

			if (!((mesh.VAO as VertexArrayObject)?.ObnsVulkanMesh is VulkanMesh vulkanMesh) || !vulkanMesh.Find(face.IboStartIndex, out uint firstIndex, out uint indexCount))
			{
				skippedFaces++;
				return;
			}

			// The object: its matrices, once per object as upstream sends them.
			if (state != lastState || state.Prototype.Dynamic)
			{
				lastModelMatrix = state.ModelMatrix * renderer.Camera.TranslationMatrix;
				lastObject = AddObject(state, lastModelMatrix * renderer.CurrentViewMatrix);
				lastState = state;
			}

			MeshMaterial material = mesh.Materials[face.Material];
			bool cull = renderer.OptionBackFaceCulling && (face.Flags & FaceFlags.Face2Mask) == 0;
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;

			float distanceFactor = material.GlowAttenuationData != 0
				? (float)Glow.GetDistanceFactor(lastModelMatrix, mesh.Vertices, ref face, material.GlowAttenuationData)
				: 1.0f;
			float blendFactor = Inv255 * state.DaytimeNighttimeBlend + 1.0f - renderer.Lighting.OptionLightingResultingAmount;
			if (blendFactor > 1.0f)
			{
				blendFactor = 1.0f;
			}

			DrawData draw = new DrawData
			{
				ColorR = material.Color.R * Inv255, ColorG = material.Color.G * Inv255, ColorB = material.Color.B * Inv255, ColorA = material.Color.A * Inv255,
				Flags = (int)material.Flags,
				Object = lastObject,
				Sampler = (int)wrap & 3,
				Fog = renderer.Fog.Enabled ? 1 : 0,
				AlphaFunction = passAlphaFunction,
				AlphaReference = passAlphaReference,
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
			draw.Texture = material.DaytimeTexture != null && Program.CurrentHost.LoadTexture(ref material.DaytimeTexture, wrap)
				? Textures.IndexOf(material.DaytimeTexture.OpenGlTextures[(int)wrap])
				: VulkanTextures.White;

			Blend blend = passBlend;
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
			AddDraw(draw, blend, cull, firstIndex, indexCount, vulkanMesh, false);

			// Nighttime texture, blended over the daytime one.
			if (blendFactor != 0 && material.NighttimeTexture != null && material.NighttimeTexture != material.DaytimeTexture && Program.CurrentHost.LoadTexture(ref material.NighttimeTexture, wrap))
			{
				draw.Texture = Textures.IndexOf(material.NighttimeTexture.OpenGlTextures[(int)wrap]);
				draw.AlphaFunction = AlphaGreater;
				draw.AlphaReference = 0.0f;
				draw.Opacity = Inv255 * material.Color.A * distanceFactor * blendFactor;
				AddDraw(draw, additive ? Blend.Additive : Blend.Normal, cull, firstIndex, indexCount, vulkanMesh, true);
			}
		}

		private int AddObject(ObjectState state, Matrix4D modelView)
		{
			if (objectCount == objects.Length)
			{
				Array.Resize(ref objects, objects.Length * 2);
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

			if (state.Matricies != null && state.Matricies.Length > 0)
			{
				data.MatrixBase = matrixCount;
				int needed = (matrixCount + state.Matricies.Length) * 16;
				if (needed > matrices.Length)
				{
					Array.Resize(ref matrices, Math.Max(needed, matrices.Length * 2));
				}

				fixed (float* to = matrices)
				{
					for (int m = 0; m < state.Matricies.Length; m++)
					{
						Store(state.Matricies[m], to + (matrixCount + m) * 16);
					}
				}

				matrixCount += state.Matricies.Length;
			}

			objects[objectCount] = data;
			return objectCount++;
		}

		private void AddDraw(DrawData draw, Blend blend, bool cull, uint firstIndex, uint indexCount, VulkanMesh mesh, bool night)
		{
			if (drawCount == draws.Length)
			{
				Array.Resize(ref draws, draws.Length * 2);
			}

			if (commandCount == commands.Length)
			{
				Array.Resize(ref commands, commands.Length * 2);
			}

			// Grouped passes cull in the pipeline; ordered ones draw both sides and discard OpenGL's front faces.
			bool pipelineCull = cull && !passOrdered;
			draw.CullFront = cull && passOrdered ? 1 : 0;
			int pipelineKey = (int)blend | (passDepthWrite ? 4 : 0) | (pipelineCull ? 8 : 0);
			// Grouped by: nighttime draws after daytime ones (they blend over them), then pipeline, then vertex and index blocks.
			long key = ((night ? 1L : 0L) << 62) | ((long)pipelineKey << 32) | ((long)(mesh.VertexBlock & 0xFFFF) << 16) | (long)(mesh.IndexBlock & 0xFFFF);
			draws[drawCount] = draw;
			commands[commandCount++] = new DrawCommand { Key = key, FirstIndex = firstIndex, IndexCount = indexCount, VertexBase = mesh.VertexBase, Draw = drawCount };
			drawCount++;
		}

		// --- recording ---

		/// <summary>Whether there is anything to draw.</summary>
		internal bool Empty => commandCount == 0;

		/// <summary>Copies the frame's records into the slot's buffers and records its draws (inside the rendering).</summary>
		internal void Record(IntPtr commandBuffer, int slot, uint width, uint height)
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

			EnsureBuffers(slot);
			FrameData frame = frameData;
			Buffer.MemoryCopy(&frame, frameBuffers[slot].Data, (long)frameBuffers[slot].Size, sizeof(FrameData));
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

			Vk.Viewport viewport = new Vk.Viewport { Width = width, Height = height, MaxDepth = 1.0f };
			Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = width, Height = height } };
			Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
			Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
			ulong set = DescriptorSets[slot];
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
			int frameIndex = 0;
			Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, 4, &frameIndex);

			// Runs of the same pipeline and buffer blocks, each one indirect draw.
			int calls = 0;
			int boundPipeline = -1, boundVertexBlock = -1, boundIndexBlock = -1;
			int run = 0;
			while (run < commandCount)
			{
				long key = commands[run].Key & ~(1L << 62);
				int next = run + 1;
				while (next < commandCount && (commands[next].Key & ~(1L << 62)) == key)
				{
					next++;
				}

				int pipelineKey = (int)(key >> 32);
				int vertexBlock = (int)((key >> 16) & 0xFFFF), indexBlock = (int)(key & 0xFFFF);
				if (pipelineKey != boundPipeline)
				{
					Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, Pipeline(pipelineKey));
					boundPipeline = pipelineKey;
				}

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

			LastDraws = commandCount;
			LastIndirectCalls = calls;
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
			changed |= Ensure(ref frameBuffers[slot], (ulong)sizeof(FrameData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref objectBuffers[slot], (ulong)Math.Max(1, objectCount) * (ulong)sizeof(ObjectData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref drawBuffers[slot], (ulong)Math.Max(1, drawCount) * (ulong)sizeof(DrawData), Vk.BufferUsageStorageBuffer);
			changed |= Ensure(ref matrixBuffers[slot], (ulong)Math.Max(1, matrixCount) * 64UL, Vk.BufferUsageStorageBuffer);
			Ensure(ref indirectBuffers[slot], (ulong)Math.Max(1, commandCount) * (ulong)sizeof(Vk.DrawIndexedIndirectCommand), Vk.BufferUsageIndirectBuffer);
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
			if (pipelineLayout != 0) Vk.vkDestroyPipelineLayout(device.Device, pipelineLayout, null);
			if (setLayout != 0) Vk.vkDestroyDescriptorSetLayout(device.Device, setLayout, null);
		}
	}
}
