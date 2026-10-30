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
	 * uniform ring; set 1 the texture; set 2, for faces reading shadow maps, one frame's cascades
	 * (VulkanGL.Shadows); a push constant the clip-space transform (Y flip and pre-rotation). The GL state that Vulkan bakes into a pipeline - the program, the primitive,
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


		/*
		 * The commands recorded for every draw, called through the device's own function pointers
		 * (vkGetDeviceProcAddr) rather than Silk.NET's dispatch: at seven thousand draws a frame its
		 * lookup per call cost more than the commands themselves.
		 */
		private delegate* unmanaged<CommandBuffer, uint, uint, uint, int, uint, void> cmdDrawIndexed;
		private delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, void> cmdDraw;
		private delegate* unmanaged<CommandBuffer, PipelineBindPoint, Pipeline, void> cmdBindPipeline;
		private delegate* unmanaged<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void> cmdBindDescriptorSets;
		private delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, void> cmdBindVertexBuffers;
		private delegate* unmanaged<CommandBuffer, Buffer, ulong, IndexType, void> cmdBindIndexBuffer;

		private void* DeviceProc(string name)
		{
			void* proc = (void*)vk.GetDeviceProcAddr(device, name).Handle;
			if (proc == null)
			{
				throw new InvalidOperationException(name + " is missing");
			}

			return proc;
		}

		// The Vulkan 1.3 path's dynamic state commands, and what was last set in the frame's command buffer.
		private delegate* unmanaged<CommandBuffer, CullModeFlags, void> cmdSetCullMode;
		private delegate* unmanaged<CommandBuffer, PrimitiveTopology, void> cmdSetPrimitiveTopology;
		private delegate* unmanaged<CommandBuffer, uint, void> cmdSetDepthTestEnable;
		private delegate* unmanaged<CommandBuffer, uint, void> cmdSetDepthWriteEnable;
		private delegate* unmanaged<CommandBuffer, CompareOp, void> cmdSetDepthCompareOp;
		private delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, ulong*, ulong*, void> cmdBindVertexBuffers2;

		private void LoadCommands()
		{
			LoadRecordCommands();
			if (context.Vulkan13)
			{
				cmdSetCullMode = (delegate* unmanaged<CommandBuffer, CullModeFlags, void>)DeviceProc("vkCmdSetCullMode");
				cmdSetPrimitiveTopology = (delegate* unmanaged<CommandBuffer, PrimitiveTopology, void>)DeviceProc("vkCmdSetPrimitiveTopology");
				cmdSetDepthTestEnable = (delegate* unmanaged<CommandBuffer, uint, void>)DeviceProc("vkCmdSetDepthTestEnable");
				cmdSetDepthWriteEnable = (delegate* unmanaged<CommandBuffer, uint, void>)DeviceProc("vkCmdSetDepthWriteEnable");
				cmdSetDepthCompareOp = (delegate* unmanaged<CommandBuffer, CompareOp, void>)DeviceProc("vkCmdSetDepthCompareOp");
				cmdBindVertexBuffers2 = (delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, ulong*, ulong*, void>)DeviceProc("vkCmdBindVertexBuffers2");
			}

			cmdDrawIndexed = (delegate* unmanaged<CommandBuffer, uint, uint, uint, int, uint, void>)DeviceProc("vkCmdDrawIndexed");
			cmdDraw = (delegate* unmanaged<CommandBuffer, uint, uint, uint, uint, void>)DeviceProc("vkCmdDraw");
			cmdBindPipeline = (delegate* unmanaged<CommandBuffer, PipelineBindPoint, Pipeline, void>)DeviceProc("vkCmdBindPipeline");
			cmdBindDescriptorSets = (delegate* unmanaged<CommandBuffer, PipelineBindPoint, PipelineLayout, uint, uint, DescriptorSet*, uint, uint*, void>)DeviceProc("vkCmdBindDescriptorSets");
			cmdBindVertexBuffers = (delegate* unmanaged<CommandBuffer, uint, uint, Buffer*, ulong*, void>)DeviceProc("vkCmdBindVertexBuffers");
			cmdBindIndexBuffer = (delegate* unmanaged<CommandBuffer, Buffer, ulong, IndexType, void>)DeviceProc("vkCmdBindIndexBuffer");
		}

		private void CreateLayouts()
		{
			LoadCommands();
			DescriptorSetLayoutBinding* uniformBindings = stackalloc DescriptorSetLayoutBinding[2];
			uniformBindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
			uniformBindings[1] = new DescriptorSetLayoutBinding(1, DescriptorType.UniformBufferDynamic, 1, ShaderStageFlags.VertexBit);
			DescriptorSetLayoutCreateInfo uniformInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = uniformBindings };
			Check(vk.CreateDescriptorSetLayout(device, in uniformInfo, null, out uniformLayout), "vkCreateDescriptorSetLayout");

			DescriptorSetLayoutBinding textureBinding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
			DescriptorSetLayoutCreateInfo textureInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &textureBinding };
			Check(vk.CreateDescriptorSetLayout(device, in textureInfo, null, out textureLayout), "vkCreateDescriptorSetLayout");

			CreateShadowLayout();
			DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3] { uniformLayout, textureLayout, shadowLayout };
			PushConstantRange push = new PushConstantRange(ShaderStageFlags.VertexBit, 0, 16);
			PipelineLayoutCreateInfo layoutInfo = new PipelineLayoutCreateInfo
			{
				SType = StructureType.PipelineLayoutCreateInfo,
				SetLayoutCount = 3,
				PSetLayouts = layouts,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Check(vk.CreatePipelineLayout(device, in layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");

			CreatePipelineCache();

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
			CreateShadowShaders();
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

		/// <summary>A frame was begun: its slot's ring and secondary command buffers start over, and it has no draws yet.</summary>
		private void FrameBegun()
		{
			ring = rings[context.FrameSlot];
			ring.Segment = 0;
			ring.Position = 0;
			StartSegment();
			ResetChunkPools(context.FrameSlot);
			recordCount = 0;
			ShadowsFrameBegun();
			passClear = new Vector4(clearRed, clearGreen, clearBlue, clearAlpha);
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

		private BufferHeap.Block lastUniformBlock;
		private DescriptorSet lastUniformSet;

		/// <summary>The uniform descriptor set over a heap block's buffer.</summary>
		private DescriptorSet UniformSet(BufferHeap.Block block)
		{
			if (block == lastUniformBlock)
			{
				return lastUniformSet;
			}

			if (uniformSets.TryGetValue(block, out DescriptorSet set))
			{
				lastUniformBlock = block;
				lastUniformSet = set;
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
				layout.Set(i, format, format == Format.Undefined || Dynamic13 ? 0 : attribute.Stride);
				array.Reads[i] = format != Format.Undefined;
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

		/*
		 * The Vulkan 1.3 path: culling, depth test, depth writes, the depth comparison, the
		 * primitive (within its class: triangles or lines) and the vertex strides are dynamic
		 * state, set with commands as they change rather than baked into the pipeline. The key then
		 * leaves them out, so there are fewer pipelines and fewer pipeline changes.
		 */
		private bool Dynamic13 => context.Vulkan13 && UseDynamicState;

		/// <summary>
		/// Whether the Vulkan 1.3 path also takes culling, depth state, the primitive and vertex
		/// strides as dynamic state (otherwise only dynamic rendering). Off: on the S24 Ultra's
		/// Adreno 750 the per-draw state commands made TML's exterior slower (29 fps against 39).
		/// </summary>
		public static bool UseDynamicState;

		private ulong PipelineKey(ProgramKind kind, int topology, int layoutId)
		{
			if (Dynamic13)
			{
				ulong dynamicKey = (ulong)kind;
				dynamicKey |= (ulong)TopologyClass(topology) << 2;
				dynamicKey |= (blend ? 1UL : 0UL) << 13;
				dynamicKey |= (ulong)(blend ? BlendIndex(blendSource) : 0) << 14;
				dynamicKey |= (ulong)(blend ? BlendIndex(blendDestination) : 0) << 18;
				dynamicKey |= (ulong)layoutId << 22;
				return dynamicKey;
			}

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

		/// <summary>The topology a pipeline of this primitive's class is made with (the draw sets the real one).</summary>
		private static int TopologyClass(int topology)
		{
			PrimitiveTopology value = (PrimitiveTopology)topology;
			return value == PrimitiveTopology.LineList || value == PrimitiveTopology.LineStrip ? (int)PrimitiveTopology.LineList : (int)PrimitiveTopology.TriangleList;
		}

		private ulong lastPipelineKey = ulong.MaxValue;
		private Pipeline lastPipeline;

		private Pipeline GetPipeline(ulong key)
		{
			if (key == lastPipelineKey)
			{
				return lastPipeline;
			}

			if (!pipelines.TryGetValue(key, out Pipeline pipeline))
			{
				pipeline = CreatePipeline(key);
				pipelines[key] = pipeline;
				PipelineMade();
			}

			lastPipelineKey = key;
			lastPipeline = pipeline;
			return pipeline;
		}

		private Pipeline CreatePipeline(ulong key)
		{
			Pipeline pipeline;

			ProgramKind kind = (ProgramKind)(key & 3);
			PrimitiveTopology topology = (PrimitiveTopology)(key >> 2 & 15);
			int cull = (int)(key >> 6 & 3);
			bool test = (key >> 8 & 1) != 0, write = (key >> 9 & 1) != 0;
			CompareOp compare = (CompareOp)(key >> 10 & 7);
			bool blending = (key >> 13 & 1) != 0;
			BlendFactor source = VulkanBlendFactors[key >> 14 & 15], destination = VulkanBlendFactors[key >> 18 & 15];
			int layoutId = (int)(key >> 22 & 0xFFFFFFF);
			bool depthPass = (key & DepthPassFlag) != 0, shadowed = (key & ShadowedFlag) != 0, depthOnly = (key & DepthOnlyFlag) != 0;

			IntPtr main = Silk.NET.Core.Native.SilkMarshal.StringToPtr("main");
			try
			{
				PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
				stages[0] = new PipelineShaderStageCreateInfo
				{
					SType = StructureType.PipelineShaderStageCreateInfo,
					Stage = ShaderStageFlags.VertexBit,
					Module = depthPass ? shadowDepthVertex : shadowed ? defaultShadowVertex : kind == ProgramKind.Default ? defaultVertex : kind == ProgramKind.Text ? textVertex : rectangleVertex,
					PName = (byte*)main
				};
				stages[1] = new PipelineShaderStageCreateInfo
				{
					SType = StructureType.PipelineShaderStageCreateInfo,
					Stage = ShaderStageFlags.FragmentBit,
					Module = depthPass ? shadowDepthFragment : shadowed ? defaultShadowFragment : kind == ProgramKind.Default ? defaultFragment : rectangleFragment,
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
					// A shadow map is not flipped (shadow_depth.vert), so there the same faces wind the other way.
					FrontFace = depthPass ? FrontFace.Clockwise : FrontFace.CounterClockwise,
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
				PipelineColorBlendStateCreateInfo colorBlend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = depthPass ? 0u : 1u, PAttachments = &attachment };
				Silk.NET.Vulkan.DynamicState* dynamics = stackalloc Silk.NET.Vulkan.DynamicState[9];
				uint dynamicCount = 0;
				dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.Viewport;
				dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.Scissor;
				if (Dynamic13)
				{
					dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.CullMode;
					dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.PrimitiveTopology;
					dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.DepthTestEnable;
					dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.DepthWriteEnable;
					dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.DepthCompareOp;
					if (kind == ProgramKind.Default)
					{
						dynamics[dynamicCount++] = Silk.NET.Vulkan.DynamicState.VertexInputBindingStride;
					}
				}

				PipelineDynamicStateCreateInfo dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = dynamicCount, PDynamicStates = dynamics };
				// Dynamic rendering (1.3): the attachments' formats instead of a render pass.
				Format colorFormat = context.ColorFormat;
				PipelineRenderingCreateInfo rendering = new PipelineRenderingCreateInfo
				{
					SType = StructureType.PipelineRenderingCreateInfo,
					ColorAttachmentCount = 1,
					PColorAttachmentFormats = &colorFormat,
					DepthAttachmentFormat = context.DepthFormat
				};
				GraphicsPipelineCreateInfo info = new GraphicsPipelineCreateInfo
				{
					SType = StructureType.GraphicsPipelineCreateInfo,
					PNext = context.Vulkan13 && !depthPass ? &rendering : null,
					StageCount = depthOnly ? 1u : 2u,
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
					RenderPass = depthPass ? ShadowRenderPass() : context.Vulkan13 ? default : context.RenderPass,
					Subpass = 0
				};
				Check(vk.CreateGraphicsPipelines(device, pipelineCache, 1, in info, null, out pipeline), "vkCreateGraphicsPipelines");
			}
			finally
			{
				Silk.NET.Core.Native.SilkMarshal.Free(main);
			}

			return pipeline;
		}

		// --- draws ---

		public void glDrawArrays(int mode, int first, int count)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			DrawArrays(mode, first, count);
			drawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}

		public void glDrawElements(int mode, int count, int type, IntPtr indices)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			DrawElements(mode, count, type, indices);
			drawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}

		/// <summary>Stopwatch ticks per stage of a draw this frame: texture, pipeline, uniforms, descriptor sets, viewport and vertex buffers, the draw.</summary>
		private readonly long[] sectionTicks = new long[6];

		/// <summary>The last finished frame's stage times, for the log.</summary>
		public string LastFrameSections { get; private set; } = string.Empty;

		/// <summary>Whether to time each stage of a draw for the log (a few clock reads a draw: off unless asked for).</summary>
		public static bool ProfileStages;

		private void Section(int index, ref long mark)
		{
			if (!ProfileStages)
			{
				return;
			}

			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			sectionTicks[index] += now - mark;
			mark = now;
		}

		private void SectionsDone()
		{
			string[] names = { "texture", "pipeline", "uniforms", "sets", "vertex", "draw" };
			System.Text.StringBuilder text = new System.Text.StringBuilder();
			for (int i = 0; i < sectionTicks.Length; i++)
			{
				text.Append(i == 0 ? string.Empty : ", ").Append(names[i]).Append(' ').Append((sectionTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0"));
				sectionTicks[i] = 0;
			}

			LastFrameSections = text.ToString();
		}

		/// <summary>Time spent recording draws this frame (Stopwatch ticks), for the log.</summary>
		private long drawTicks;

		private void DrawArrays(int mode, int first, int count)
		{
			if (count <= 0 || !PrepareDraw(mode))
			{
				return;
			}

			if (mode == GL_LINE_LOOP)
			{
				uint[] loop = new uint[count + 1];
				for (int i = 0; i < count; i++)
				{
					loop[i] = (uint)(first + i);
				}

				loop[count] = (uint)first;
				DrawLoop(loop);
				return;
			}

			AddDraw((uint)count, (uint)Math.Max(0, first), null, default, false);
		}

		/// <summary>
		/// A line loop (the wireframe view's polygon outlines): Vulkan has none, so its indices, closed
		/// back to the first, go in a scratch index buffer freed once the frame has run, drawn as a strip.
		/// </summary>
		private void DrawLoop(uint[] indices)
		{
			BufferHeap.Allocation memory = bufferHeap.Allocate((ulong)indices.Length * 4, 4);
			fixed (uint* source = indices)
			{
				System.Buffer.MemoryCopy(source, memory.Pointer, indices.Length * 4, indices.Length * 4);
			}

			GLBuffer scratch = new GLBuffer { Memory = memory, Size = indices.Length * 4 };
			AddDraw((uint)indices.Length, 0, scratch, IndexType.Uint32, false);
			context.Defer(() => bufferHeap.Free(memory));
		}

		private void DrawElements(int mode, int count, int type, IntPtr indices)
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

			if (mode == GL_LINE_LOOP)
			{
				if (offset + (long)count * indexBytes > elements.Size)
				{
					return;
				}

				uint[] loop = new uint[count + 1];
				byte* source = elements.Memory.Pointer + offset;
				for (int i = 0; i < count; i++)
				{
					loop[i] = indexBytes == 2 ? ((ushort*)source)[i] : ((uint*)source)[i];
				}

				loop[count] = loop[0];
				DrawLoop(loop);
				return;
			}

			AddDraw((uint)count, (uint)(offset / indexBytes), elements, indexType, false);
		}

		/// <summary>
		/// Records everything a draw needs but the draw itself. False when it cannot be drawn. A
		/// native face draw (VulkanGL.Faces) passes its vertex array, texture and uniform block;
		/// a GL draw uses what GL has bound and the program's own block.
		/// </summary>
		private bool PrepareDraw(int mode, GLVertexArray array = null, GLTexture texture = null, byte[] faceBlock = null)
		{
			GLProgram program = currentProgram;
			if (program == null || program.Kind == ProgramKind.Unsupported)
			{
				return false;
			}

			if (mode == GL_POINTS)
			{
				ReportOnce("points are not supported by the Vulkan renderer");
				return false;
			}

			array ??= CurrentVertexArray;
			bool usesVertices = program.Kind == ProgramKind.Default || program.Kind == ProgramKind.ShadowDepth;
			if (usesVertices)
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

			if (!EnsureFrame())
			{
				return false;
			}

			// Into a cascade: its own records (NextRecord). Not a framebuffer this renderer can draw into: nothing.
			if (boundFramebuffer != null && ShadowPass() == null)
			{
				return false;
			}

			ulong serial = context.RecordingSerial;
			pending = default;

			// The texture first: realizing it records uploads, outside the frame's drawing.
			long mark = ProfileStages ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
			if (!shadowDepthOnly)
			{
				pending.TextureSet = TextureSet(faceBlock != null ? texture : units[0]);
			}

			Section(0, ref mark);

			int topology = Topology(mode);
			ulong pipelineKey = PipelineKey(usesVertices ? ProgramKind.Default : program.Kind, topology, usesVertices ? VertexLayoutId(array) : 0);
			if (program.Kind == ProgramKind.ShadowDepth)
			{
				pipelineKey = pipelineKey & ~BlendBits | DepthPassFlag | (shadowDepthOnly ? DepthOnlyFlag : 0UL);
			}
			else if (program.Kind == ProgramKind.Default && ShadowsActive())
			{
				pipelineKey |= ShadowedFlag;
				pending.ShadowSet = ShadowSet();
			}

			pending.Pipeline = GetPipeline(pipelineKey);
			if (Dynamic13)
			{
				pending.Cull = !cullFace ? (int)CullModeFlags.None : cullMode == GL_FRONT ? (int)CullModeFlags.FrontBit : cullMode == GL_BACK ? (int)CullModeFlags.BackBit : (int)CullModeFlags.FrontAndBack;
				pending.Topology = topology;
				pending.DepthTest = depthTest ? 1 : 0;
				pending.DepthWrite = depthTest && depthMask ? 1 : 0;
				pending.DepthCompare = depthTest ? Math.Max(0, Math.Min(7, depthFunc - 0x0200)) : (int)CompareOp.Always;
			}

			Section(1, ref mark);
			// Uniforms: the program's block, and for the default program the animation matrices,
			// both in the ring's current segment (one descriptor set covers one buffer).
			ReserveRing(AnimationBlockSize + DrawBlockRange);
			BufferHeap.Block block = RingBlock;
			uint animationOffset = ring.ZeroOffset;
			if (usesVertices)
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

				if (program.NormalDirty && faceBlock == null && program.Kind == ProgramKind.Default)
				{
					UpdateNormalMatrix(program);
				}
			}

			uint drawOffset;
			if (faceBlock == shadowFaceBlock && !shadowFaceBlockDirty && shadowFaceBlockSerial == serial && shadowFaceBlockRing == block)
			{
				// The faces of one object into one cascade share their block.
				drawOffset = shadowFaceBlockOffset;
			}
			else if (faceBlock != null)
			{
				drawOffset = RingWrite(faceBlock, faceBlock.Length);
				ring.Position = Math.Max(ring.Position, drawOffset - ring.Segments[ring.Segment].Offset + DrawBlockRange);
				if (faceBlock == shadowFaceBlock)
				{
					shadowFaceBlockDirty = false;
					shadowFaceBlockSerial = serial;
					shadowFaceBlockRing = block;
					shadowFaceBlockOffset = drawOffset;
				}
			}
			else
			{
				if (program.Dirty || program.RingSerial != serial || program.RingBlock != block)
				{
					program.RingOffset = RingWrite(program.Data, program.Data.Length);
					ring.Position = Math.Max(ring.Position, program.RingOffset - ring.Segments[ring.Segment].Offset + DrawBlockRange);
					program.RingSerial = serial;
					program.RingBlock = block;
					program.Dirty = false;
				}

				drawOffset = program.RingOffset;
			}

			Section(2, ref mark);
			pending.UniformSet = UniformSet(block);
			pending.Offset0 = drawOffset;
			pending.Offset1 = animationOffset;
			if (viewportDirty)
			{
				// A shadow map isn't the swapchain's: neither flipped nor turned.
				currentViewport = boundFramebuffer != null
					? new Rect2D(new Offset2D(viewportX, viewportY), new Extent2D((uint)Math.Max(1, viewportWidth), (uint)Math.Max(1, viewportHeight)))
					: context.ToImageRect(viewportX, viewportY, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
				viewportDirty = false;
			}

			pending.Viewport = currentViewport;
			Section(3, ref mark);
			if (usesVertices)
			{
				pending.VertexBufferCount = DefaultLocations;
				for (int i = 0; i < DefaultLocations; i++)
				{
					if (array.Reads[i])
					{
						GLBuffer source = array.Attributes[i].Buffer;
						source.LastUse = serial;
						pending.Buffers[i] = source.Memory.Buffer.Handle;
						pending.Offsets[i] = source.Memory.Offset + (ulong)array.Attributes[i].Offset;
						pending.Strides[i] = (ulong)array.Attributes[i].Stride;
					}
					else
					{
						pending.Buffers[i] = defaultAttributes.Buffer.Handle;
						pending.Offsets[i] = defaultAttributes.Offset + (i == 4 ? 16UL : 0UL);
						pending.Strides[i] = 0;
					}
				}
			}

			Section(4, ref mark);
			return true;
		}

		private void DisposeDraw()
		{
			DisposeRecords();
			DisposeShadows();
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
