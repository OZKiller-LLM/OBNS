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
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace OpenBve.Vulkan
{
	/*
	 * Drawing. Every program shares one pipeline layout: set 0 holds the program's uniform block
	 * (binding 0) and the animation matrices (binding 1), both dynamic offsets into the frame's
	 * uniform ring; set 1 the texture; a push constant the clip-space transform (Y flip and
	 * pre-rotation). The GL state that Vulkan bakes into a pipeline - the program, the primitive,
	 * the vertex layout, culling, depth and blending - makes a key, and each key's pipeline is
	 * made once, the first time it is drawn with. Each vertex attribute has its own binding, so
	 * a layout is only formats and strides, and the buffers and offsets come at draw time; a
	 * disabled attribute reads GL's default value (0, 0, 0, 1) from a small buffer with stride 0.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int GL_POINTS = 0, GL_LINES = 1, GL_LINE_LOOP = 2, GL_LINE_STRIP = 3, GL_TRIANGLES = 4, GL_TRIANGLE_STRIP = 5, GL_TRIANGLE_FAN = 6;
		private const int DefaultLocations = 5;
		private const uint AnimationBlockSize = 128 * 64;
		private const uint DrawBlockRange = 512;
		private const ulong RingSegmentSize = 4UL << 20;

		private DescriptorSetLayout uniformLayout;
		private DescriptorSetLayout textureLayout;
		private PipelineLayout pipelineLayout;
		private PipelineCache pipelineCache;
		private DescriptorPool uniformPool;
		private readonly Dictionary<BufferHeap.Block, DescriptorSet> uniformSets = new Dictionary<BufferHeap.Block, DescriptorSet>();

		private ShaderModule defaultVertex, defaultFragment, rectangleVertex, textVertex, rectangleFragment;

		/// <summary>GL's default attribute values: float (0, 0, 0, 1) at 0, int (0, 0, 0, 1) at 16.</summary>
		private BufferHeap.Allocation defaultAttributes;

		private readonly Dictionary<ulong, Pipeline> pipelines = new Dictionary<ulong, Pipeline>();
		private readonly Dictionary<VertexLayout, int> vertexLayouts = new Dictionary<VertexLayout, int>();
		private readonly List<VertexLayout> vertexLayoutList = new List<VertexLayout>();

		/// <summary>The formats and strides of the default program's five attributes (Undefined: disabled).</summary>
		private struct VertexLayout : IEquatable<VertexLayout>
		{
			internal Format F0, F1, F2, F3, F4;
			internal int S0, S1, S2, S3, S4;

			internal Format Format(int i) => i == 0 ? F0 : i == 1 ? F1 : i == 2 ? F2 : i == 3 ? F3 : F4;

			internal int Stride(int i) => i == 0 ? S0 : i == 1 ? S1 : i == 2 ? S2 : i == 3 ? S3 : S4;

			internal void Set(int i, Format format, int stride)
			{
				switch (i)
				{
					case 0: F0 = format; S0 = stride; break;
					case 1: F1 = format; S1 = stride; break;
					case 2: F2 = format; S2 = stride; break;
					case 3: F3 = format; S3 = stride; break;
					default: F4 = format; S4 = stride; break;
				}
			}

			public bool Equals(VertexLayout other) => F0 == other.F0 && F1 == other.F1 && F2 == other.F2 && F3 == other.F3 && F4 == other.F4 &&
			                                          S0 == other.S0 && S1 == other.S1 && S2 == other.S2 && S3 == other.S3 && S4 == other.S4;

			public override bool Equals(object obj) => obj is VertexLayout other && Equals(other);

			public override int GetHashCode() => HashCode.Combine(HashCode.Combine(F0, F1, F2, F3, F4), HashCode.Combine(S0, S1, S2, S3, S4));
		}

		// The uniform ring: per frame slot, segments of host-visible memory written front to back.
		private sealed class Ring
		{
			internal readonly List<BufferHeap.Allocation> Segments = new List<BufferHeap.Allocation>();
			internal int Segment;
			internal ulong Position;
			/// <summary>Where the current segment's block of zeros is (the animation matrices of an object without any).</summary>
			internal uint ZeroOffset;
		}

		private readonly Ring[] rings = new Ring[VulkanContext.Slots];
		private Ring ring;
		private ulong uniformAlignment;

		// What is bound in the frame's command buffer, so as not to bind it again.
		private Pipeline boundPipeline;
		private DescriptorSet boundUniformSet;
		private uint boundOffset0 = uint.MaxValue, boundOffset1 = uint.MaxValue;
		private DescriptorSet boundTextureSet;
		private readonly Buffer[] boundVertexBuffers = new Buffer[DefaultLocations];
		private readonly ulong[] boundVertexOffsets = new ulong[DefaultLocations];
		private Buffer boundIndexBuffer;
		private ulong boundIndexOffset;
		private IndexType boundIndexType;

		private void CreateLayouts()
		{
			DescriptorSetLayoutBinding* uniformBindings = stackalloc DescriptorSetLayoutBinding[2];
			uniformBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
			uniformBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit);
			DescriptorSetLayoutCreateInfo uniformInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = uniformBindings };
			Check(vk.CreateDescriptorSetLayout(device, in uniformInfo, null, out uniformLayout), "vkCreateDescriptorSetLayout");

			DescriptorSetLayoutBinding textureBinding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
			DescriptorSetLayoutCreateInfo textureInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &textureBinding };
			Check(vk.CreateDescriptorSetLayout(device, in textureInfo, null, out textureLayout), "vkCreateDescriptorSetLayout");

			DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[2] { uniformLayout, textureLayout };
			PushConstantRange push = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 16);
			PipelineLayoutCreateInfo layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 2,
				PSetLayouts = layouts,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Check(vk.CreatePipelineLayout(device, in layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

			PipelineCacheCreateInfo cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
			Check(vk.CreatePipelineCache(device, in cacheInfo, null, out pipelineCache), "vkCreatePipelineCache");

			DescriptorPoolSize poolSize = new DescriptorPoolSize(DescriptorType.UniformBufferDynamic, 256);
			DescriptorPoolCreateInfo poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 128, PoolSizeCount = 1, PPoolSizes = &poolSize };
			Check(vk.CreateDescriptorPool(device, in poolInfo, null, out uniformPool), "vkCreateDescriptorPool");

			uniformAlignment = Math.Max(16, context.Limits.MinUniformBufferOffsetAlignment);
		}

		private void CreateShaderModules()
		{
			defaultVertex = LoadShader("default.vert");
			defaultFragment = LoadShader("default.frag");
			rectangleVertex = LoadShader("rectangle.vert");
			textVertex = LoadShader("text.vert");
			rectangleFragment = LoadShader("rectangle.frag");
		}

		private ShaderModule LoadShader(string name)
		{
			using Stream stream = typeof(VulkanGL).Assembly.GetManifestResourceStream("OpenBve.Vulkan.Shaders." + name + ".spv");
			if (stream == null)
			{
				throw new InvalidOperationException("the SPIR-V for " + name + " is missing");
			}

			byte[] code = new byte[stream.Length];
			stream.ReadExactly(code);
			fixed (byte* p = code)
			{
				ShaderModuleCreateInfo info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)p };
				Check(vk.CreateShaderModule(device, in info, null, out ShaderModule module), "vkCreateShaderModule");
				return module;
			}
		}

		private void CreateDefaults()
		{
			defaultAttributes = bufferHeap.Allocate(32, 16);
			float* floats = (float*)defaultAttributes.Pointer;
			floats[0] = floats[1] = floats[2] = 0.0f;
			floats[3] = 1.0f;
			int* ints = (int*)(defaultAttributes.Pointer + 16);
			ints[0] = ints[1] = ints[2] = 0;
			ints[3] = 1;
			for (int i = 0; i < rings.Length; i++)
			{
				rings[i] = new Ring();
			}

			CreateBlackTexture();
		}

		/// <summary>A frame was begun: its slot's ring starts over, and nothing is bound in its command buffer yet.</summary>
		private void FrameBegun()
		{
			ring = rings[context.FrameSlot];
			ring.Segment = 0;
			ring.Position = 0;
			StartSegment();
			boundPipeline = default;
			boundUniformSet = default;
			boundOffset0 = boundOffset1 = uint.MaxValue;
			boundTextureSet = default;
			Array.Clear(boundVertexBuffers);
			Array.Clear(boundVertexOffsets);
			boundIndexBuffer = default;
		}

		/// <summary>The render pass began: the clip transform and the viewport go into the command buffer.</summary>
		private void RenderPassBegun()
		{
			Vector4 clip = context.ClipTransform;
			vk.CmdPushConstants(context.DrawCommands, pipelineLayout, ShaderStageFlags.VertexBit, 0, 16, &clip);
			viewportDirty = true;
		}

		/// <summary>Begins the ring's current segment (allocating it if new) with a block of zeros at its start.</summary>
		private void StartSegment()
		{
			if (ring.Segment == ring.Segments.Count)
			{
				ring.Segments.Add(bufferHeap.Allocate(RingSegmentSize, uniformAlignment));
			}

			BufferHeap.Allocation segment = ring.Segments[ring.Segment];
			new Span<byte>(segment.Pointer, (int)AnimationBlockSize).Clear();
			ring.ZeroOffset = (uint)segment.Offset;
			ring.Position = AnimationBlockSize;
		}

		/// <summary>Makes sure the current segment has this many bytes left, moving to the next one if not.</summary>
		private void ReserveRing(ulong bytes)
		{
			if (ring.Position + bytes + 2 * uniformAlignment > RingSegmentSize)
			{
				ring.Segment++;
				StartSegment();
			}
		}

		private BufferHeap.Block RingBlock => ring.Segments[ring.Segment].Block;

		/// <summary>Copies data into the ring; returns its offset in the segment's buffer.</summary>
		private uint RingWrite(byte[] data, int length)
		{
			BufferHeap.Allocation segment = ring.Segments[ring.Segment];
			ulong position = (ring.Position + uniformAlignment - 1) / uniformAlignment * uniformAlignment;
			fixed (byte* source = data)
			{
				System.Buffer.MemoryCopy(source, segment.Pointer + position, length, length);
			}

			ring.Position = position + (ulong)length;
			return (uint)(segment.Offset + position);
		}

		/// <summary>The uniform descriptor set over a heap block's buffer.</summary>
		private DescriptorSet UniformSet(BufferHeap.Block block)
		{
			if (uniformSets.TryGetValue(block, out DescriptorSet set))
			{
				return set;
			}

			DescriptorSetLayout layout = uniformLayout;
			DescriptorSetAllocateInfo allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = uniformPool, DescriptorSetCount = 1, PSetLayouts = &layout };
			Check(vk.AllocateDescriptorSets(device, in allocate, out set), "vkAllocateDescriptorSets");
			DescriptorBufferInfo* infos = stackalloc DescriptorBufferInfo[2];
			infos[0] = new DescriptorBufferInfo(block.Buffer, 0, DrawBlockRange);
			infos[1] = new DescriptorBufferInfo(block.Buffer, 0, AnimationBlockSize);
			WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[2];
			for (uint i = 0; i < 2; i++)
			{
				writes[i] = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = set,
					DstBinding = i,
					DescriptorCount = 1,
					DescriptorType = DescriptorType.UniformBufferDynamic,
					PBufferInfo = &infos[i]
				};
			}

			vk.UpdateDescriptorSets(device, 2, writes, 0, null);
			uniformSets[block] = set;
			return set;
		}

		// --- pipelines ---

		private static int Topology(int mode)
		{
			switch (mode)
			{
				case GL_LINES:
					return (int)PrimitiveTopology.LineList;
				case GL_LINE_LOOP:
				case GL_LINE_STRIP:
					return (int)PrimitiveTopology.LineStrip;
				case GL_TRIANGLE_STRIP:
					return (int)PrimitiveTopology.TriangleStrip;
				case GL_TRIANGLE_FAN:
					return (int)PrimitiveTopology.TriangleFan;
				default:
					return (int)PrimitiveTopology.TriangleList;
			}
		}

		private static readonly int[] BlendFactors = { 0, 1, 0x0300, 0x0301, 0x0302, 0x0303, 0x0304, 0x0305, 0x0306, 0x0307, 0x0308 };

		private static readonly BlendFactor[] VulkanBlendFactors =
		{
			BlendFactor.Zero, BlendFactor.One, BlendFactor.SrcColor, BlendFactor.OneMinusSrcColor, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha,
			BlendFactor.DstAlpha, BlendFactor.OneMinusDstAlpha, BlendFactor.DstColor, BlendFactor.OneMinusDstColor, BlendFactor.SrcAlphaSaturate
		};

		private static int BlendIndex(int factor)
		{
			int index = Array.IndexOf(BlendFactors, factor);
			return index < 0 ? 1 : index;
		}

		/// <summary>The vertex layout number of the current vertex array for the default program.</summary>
		private int VertexLayoutId(GLVertexArray array)
		{
			if (array.LayoutId >= 0)
			{
				return array.LayoutId;
			}

			VertexLayout layout = default;
			for (int i = 0; i < DefaultLocations; i++)
			{
				Attribute attribute = array.Attributes[i];
				Format format = attribute.Enabled && attribute.Buffer != null ? AttributeFormat(attribute.Type, attribute.Size, attribute.Normalized, attribute.Integer) : Format.Undefined;
				layout.Set(i, format, format == Format.Undefined ? 0 : attribute.Stride);
			}

			if (!vertexLayouts.TryGetValue(layout, out int id))
			{
				id = vertexLayoutList.Count;
				vertexLayouts[layout] = id;
				vertexLayoutList.Add(layout);
			}

			array.LayoutId = id;
			return id;
		}

		private ulong PipelineKey(ProgramKind kind, int topology, int layoutId)
		{
			int cull = !cullFace ? 0 : cullMode == GL_FRONT ? 1 : cullMode == GL_BACK ? 2 : 3;
			int depthCompare = Math.Max(0, Math.Min(7, depthFunc - 0x0200));
			ulong key = (ulong)kind;
			key |= (ulong)topology << 2;
			key |= (ulong)cull << 6;
			key |= (depthTest ? 1UL : 0UL) << 8;
			key |= (depthTest && depthMask ? 1UL : 0UL) << 9;
			key |= (ulong)depthCompare << 10;
			key |= (blend ? 1UL : 0UL) << 13;
			key |= (ulong)(blend ? BlendIndex(blendSource) : 0) << 14;
			key |= (ulong)(blend ? BlendIndex(blendDestination) : 0) << 18;
			key |= (ulong)layoutId << 22;
			return key;
		}

		private Pipeline GetPipeline(ulong key)
		{
			if (pipelines.TryGetValue(key, out Pipeline pipeline))
			{
				return pipeline;
			}

			ProgramKind kind = (ProgramKind)(key & 3);
			PrimitiveTopology topology = (PrimitiveTopology)(key >> 2 & 15);
			int cull = (int)(key >> 6 & 3);
			bool test = (key >> 8 & 1) != 0, write = (key >> 9 & 1) != 0;
			CompareOp compare = (CompareOp)(key >> 10 & 7);
			bool blending = (key >> 13 & 1) != 0;
			BlendFactor source = VulkanBlendFactors[key >> 14 & 15], destination = VulkanBlendFactors[key >> 18 & 15];
			int layoutId = (int)(key >> 22);

			IntPtr main = Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
			try
			{
				PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
				stages[0] = new PipelineShaderStageCreateInfo
				{
					SType = StructureType.PipelineShaderStageCreateInfo,
					Stage = ShaderStageFlags.VertexBit,
					Module = kind == ProgramKind.Default ? defaultVertex : kind == ProgramKind.Text ? textVertex : rectangleVertex,
					PName = (byte*)main
				};
				stages[1] = new PipelineShaderStageCreateInfo
				{
					SType = StructureType.PipelineShaderStageCreateInfo,
					Stage = ShaderStageFlags.FragmentBit,
					Module = kind == ProgramKind.Default ? defaultFragment : rectangleFragment,
					PName = (byte*)main
				};

				VertexInputBindingDescription* bindings = stackalloc VertexInputBindingDescription[DefaultLocations];
				VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[DefaultLocations];
				uint count = 0;
				if (kind == ProgramKind.Default)
				{
					VertexLayout layout = vertexLayoutList[layoutId];
					for (int i = 0; i < DefaultLocations; i++)
					{
						Format format = layout.Format(i);
						uint stride = (uint)layout.Stride(i);
						if (format == Format.Undefined)
						{
							// GL's default value, from the defaults buffer at stride 0.
							format = i == 4 ? Format.R32G32B32A32Sint : Format.R32G32B32A32Sfloat;
							stride = 0;
						}

						bindings[i] = new VertexInputBindingDescription((uint)i, stride, VertexInputRate.Vertex);
						attributes[i] = new VertexInputAttributeDescription((uint)i, (uint)i, format, 0);
					}

					count = DefaultLocations;
				}

				PipelineVertexInputStateCreateInfo vertexInput = new PipelineVertexInputStateCreateInfo
				{
					SType = StructureType.PipelineVertexInputStateCreateInfo,
					VertexBindingDescriptionCount = count,
					PVertexBindingDescriptions = bindings,
					VertexAttributeDescriptionCount = count,
					PVertexAttributeDescriptions = attributes
				};
				PipelineInputAssemblyStateCreateInfo inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology };
				PipelineViewportStateCreateInfo viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
				PipelineRasterizationStateCreateInfo rasterization = new PipelineRasterizationStateCreateInfo
				{
					SType = StructureType.PipelineRasterizationStateCreateInfo,
					PolygonMode = PolygonMode.Fill,
					CullMode = cull == 0 ? CullModeFlags.None : cull == 1 ? CullModeFlags.FrontBit : cull == 2 ? CullModeFlags.BackBit : CullModeFlags.FrontAndBack,
					// GL's counter-clockwise front faces: the clip transform flips Y and turns, so on screen they keep their winding.
					FrontFace = FrontFace.CounterClockwise,
					LineWidth = 1.0f
				};
				PipelineMultisampleStateCreateInfo multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
				PipelineDepthStencilStateCreateInfo depth = new PipelineDepthStencilStateCreateInfo
				{
					SType = StructureType.PipelineDepthStencilStateCreateInfo,
					DepthTestEnable = test,
					DepthWriteEnable = write,
					DepthCompareOp = test ? compare : CompareOp.Always
				};
				PipelineColorBlendAttachmentState attachment = new PipelineColorBlendAttachmentState
				{
					BlendEnable = blending,
					SrcColorBlendFactor = source,
					DstColorBlendFactor = destination,
					ColorBlendOp = BlendOp.Add,
					SrcAlphaBlendFactor = source,
					DstAlphaBlendFactor = destination,
					AlphaBlendOp = BlendOp.Add,
					ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
				};
				PipelineColorBlendStateCreateInfo colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
				DynamicState* dynamics = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
				PipelineDynamicStateCreateInfo dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamics };
				GraphicsPipelineCreateInfo info = new GraphicsPipelineCreateInfo
				{
					SType = StructureType.GraphicsPipelineCreateInfo,
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
					RenderPass = context.RenderPass,
					Subpass = 0
				};
				Check(vk.CreateGraphicsPipelines(device, pipelineCache, 1, in info, null, out pipeline), "vkCreateGraphicsPipelines");
			}
			finally
			{
				Silk.NET.Core.Native.SilkMarshal.Free(main);
			}

			pipelines[key] = pipeline;
			return pipeline;
		}

		// --- draws ---

		public void glDrawArrays(int mode, int first, int count)
		{
			if (count <= 0 || !PrepareDraw(mode))
			{
				return;
			}

			vk.CmdDraw(context.DrawCommands, (uint)count, 1, (uint)Math.Max(0, first), 0);
			frameDraws++;
		}

		public void glDrawElements(int mode, int count, int type, IntPtr indices)
		{
			if (count <= 0)
			{
				return;
			}

			GLBuffer elements = CurrentVertexArray.Elements;
			if (elements == null || elements.Memory.IsNull)
			{
				return;
			}

			IndexType indexType;
			int indexBytes;
			if (type == GL_UNSIGNED_SHORT)
			{
				indexType = IndexType.Uint16;
				indexBytes = 2;
			}
			else if (type == GL_UNSIGNED_INT)
			{
				indexType = IndexType.Uint32;
				indexBytes = 4;
			}
			else
			{
				ReportOnce("byte indices are not supported by the Vulkan renderer");
				return;
			}

			long offset = (long)indices;
			if (offset % indexBytes != 0 || !PrepareDraw(mode))
			{
				return;
			}

			CommandBuffer commands = context.DrawCommands;
			elements.LastUse = context.RecordingSerial;
			Buffer buffer = elements.Memory.Buffer;
			ulong bufferOffset = elements.Memory.Offset;
			if (buffer.Handle != boundIndexBuffer.Handle || bufferOffset != boundIndexOffset || indexType != boundIndexType)
			{
				vk.CmdBindIndexBuffer(commands, buffer, bufferOffset, indexType);
				boundIndexBuffer = buffer;
				boundIndexOffset = bufferOffset;
				boundIndexType = indexType;
			}

			vk.CmdDrawIndexed(commands, (uint)count, 1, (uint)(offset / indexBytes), 0, 0);
			frameDraws++;
		}

		/// <summary>Records everything a draw needs but the draw itself. False when it cannot be drawn.</summary>
		private bool PrepareDraw(int mode)
		{
			GLProgram program = currentProgram;
			if (offscreenBound || program == null || program.Kind == ProgramKind.Unsupported)
			{
				return false;
			}

			if (mode == GL_POINTS)
			{
				ReportOnce("points are not supported by the Vulkan renderer");
				return false;
			}

			if (mode == GL_LINE_LOOP)
			{
				ReportOnce("line loops are drawn as line strips by the Vulkan renderer");
			}

			GLVertexArray array = CurrentVertexArray;
			if (program.Kind == ProgramKind.Default)
			{
				// Every enabled attribute needs its data.
				for (int i = 0; i < DefaultLocations; i++)
				{
					Attribute attribute = array.Attributes[i];
					if (attribute.Enabled && attribute.Buffer != null && attribute.Buffer.Memory.IsNull)
					{
						return false;
					}
				}
			}

			if (!EnsureRenderPass())
			{
				return false;
			}

			CommandBuffer commands = context.DrawCommands;
			ulong serial = context.RecordingSerial;

			// The texture first: realizing it records uploads, outside this command buffer.
			DescriptorSet textureSet = TextureSet(units[0]);

			Pipeline pipeline = GetPipeline(PipelineKey(program.Kind, Topology(mode), program.Kind == ProgramKind.Default ? VertexLayoutId(array) : 0));
			if (pipeline.Handle != boundPipeline.Handle)
			{
				vk.CmdBindPipeline(commands, PipelineBindPoint.Graphics, pipeline);
				boundPipeline = pipeline;
			}

			// Uniforms: the program's block, and for the default program the animation matrices,
			// both in the ring's current segment (one descriptor set covers one buffer).
			ReserveRing(AnimationBlockSize + DrawBlockRange);
			BufferHeap.Block block = RingBlock;
			uint animationOffset = ring.ZeroOffset;
			if (program.Kind == ProgramKind.Default)
			{
				GLBuffer matrices = uniformBindings[Math.Max(0, Math.Min(uniformBindings.Length - 1, program.BlockBinding))];
				if (matrices != null && matrices.Uniform && matrices.Shadow != null && matrices.Shadow.Length > 0)
				{
					if (matrices.RingSerial != serial || matrices.RingVersion != matrices.Version || matrices.RingBlock != block)
					{
						matrices.RingOffset = RingWrite(matrices.Shadow, (int)Math.Min((uint)matrices.Shadow.Length, AnimationBlockSize));
						// The descriptor reads the whole block: what follows the copy must still be in the segment.
						ring.Position = Math.Max(ring.Position, matrices.RingOffset - ring.Segments[ring.Segment].Offset + AnimationBlockSize);
						matrices.RingSerial = serial;
						matrices.RingVersion = matrices.Version;
						matrices.RingBlock = block;
					}

					animationOffset = matrices.RingOffset;
				}

				if (program.NormalDirty)
				{
					UpdateNormalMatrix(program);
				}
			}

			if (program.Dirty || program.RingSerial != serial || program.RingBlock != block)
			{
				program.RingOffset = RingWrite(program.Data, program.Data.Length);
				ring.Position = Math.Max(ring.Position, program.RingOffset - ring.Segments[ring.Segment].Offset + DrawBlockRange);
				program.RingSerial = serial;
				program.RingBlock = block;
				program.Dirty = false;
			}

			DescriptorSet uniformSet = UniformSet(block);
			if (uniformSet.Handle != boundUniformSet.Handle || program.RingOffset != boundOffset0 || animationOffset != boundOffset1)
			{
				uint* offsets = stackalloc uint[2] { program.RingOffset, animationOffset };
				vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, pipelineLayout, 0, 1, &uniformSet, 2, offsets);
				boundUniformSet = uniformSet;
				boundOffset0 = program.RingOffset;
				boundOffset1 = animationOffset;
			}

			if (textureSet.Handle != boundTextureSet.Handle)
			{
				vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Graphics, pipelineLayout, 1, 1, &textureSet, 0, null);
				boundTextureSet = textureSet;
			}

			if (viewportDirty)
			{
				Rect2D rect = context.ToImageRect(viewportX, viewportY, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
				Viewport viewport = new Viewport(rect.Offset.X, rect.Offset.Y, rect.Extent.Width, rect.Extent.Height, 0.0f, 1.0f);
				vk.CmdSetViewport(commands, 0, 1, &viewport);
				Rect2D scissor = new Rect2D(new Offset2D(0, 0), context.ImageExtent);
				vk.CmdSetScissor(commands, 0, 1, &scissor);
				viewportDirty = false;
			}

			if (program.Kind == ProgramKind.Default)
			{
				Buffer* buffers = stackalloc Buffer[DefaultLocations];
				ulong* offsets = stackalloc ulong[DefaultLocations];
				bool changed = false;
				for (int i = 0; i < DefaultLocations; i++)
				{
					Attribute attribute = array.Attributes[i];
					if (attribute.Enabled && attribute.Buffer != null && AttributeFormat(attribute.Type, attribute.Size, attribute.Normalized, attribute.Integer) != Format.Undefined)
					{
						attribute.Buffer.LastUse = serial;
						buffers[i] = attribute.Buffer.Memory.Buffer;
						offsets[i] = attribute.Buffer.Memory.Offset + (ulong)attribute.Offset;
					}
					else
					{
						buffers[i] = defaultAttributes.Buffer;
						offsets[i] = defaultAttributes.Offset + (i == 4 ? 16UL : 0UL);
					}

					changed |= buffers[i].Handle != boundVertexBuffers[i].Handle || offsets[i] != boundVertexOffsets[i];
				}

				if (changed)
				{
					vk.CmdBindVertexBuffers(commands, 0, DefaultLocations, buffers, offsets);
					for (int i = 0; i < DefaultLocations; i++)
					{
						boundVertexBuffers[i] = buffers[i];
						boundVertexOffsets[i] = offsets[i];
					}
				}
			}

			return true;
		}

		private void DisposeDraw()
		{
			foreach (Pipeline pipeline in pipelines.Values)
			{
				vk.DestroyPipeline(device, pipeline, null);
			}

			pipelines.Clear();
			foreach (ShaderModule module in new[] { defaultVertex, defaultFragment, rectangleVertex, textVertex, rectangleFragment })
			{
				vk.DestroyShaderModule(device, module, null);
			}

			vk.DestroyPipelineCache(device, pipelineCache, null);
			vk.DestroyDescriptorPool(device, uniformPool, null);
			vk.DestroyPipelineLayout(device, pipelineLayout, null);
			vk.DestroyDescriptorSetLayout(device, uniformLayout, null);
			vk.DestroyDescriptorSetLayout(device, textureLayout, null);
		}
	}
}
