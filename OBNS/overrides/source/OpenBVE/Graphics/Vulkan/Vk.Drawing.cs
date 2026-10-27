// OBNS: not an upstream file. The Vulkan API the world renderer draws with (OBNS/VULKAN.md): buffers, copies,
// samplers, descriptors, pipelines and draws, bound by hand as in Vk.cs.

using System;
using System.Runtime.InteropServices;

namespace OpenBve.Graphics.Vulkan
{
	internal static unsafe partial class Vk
	{
		// --- constants ---

		internal const uint StructureTypeMappedMemoryRange = 6;
		internal const uint StructureTypeBufferCreateInfo = 12;
		internal const uint StructureTypeShaderModuleCreateInfo = 16;
		internal const uint StructureTypePipelineShaderStageCreateInfo = 18;
		internal const uint StructureTypePipelineVertexInputStateCreateInfo = 19;
		internal const uint StructureTypePipelineInputAssemblyStateCreateInfo = 20;
		internal const uint StructureTypePipelineViewportStateCreateInfo = 22;
		internal const uint StructureTypePipelineRasterizationStateCreateInfo = 23;
		internal const uint StructureTypePipelineMultisampleStateCreateInfo = 24;
		internal const uint StructureTypePipelineDepthStencilStateCreateInfo = 25;
		internal const uint StructureTypePipelineColorBlendStateCreateInfo = 26;
		internal const uint StructureTypePipelineDynamicStateCreateInfo = 27;
		internal const uint StructureTypeGraphicsPipelineCreateInfo = 28;
		internal const uint StructureTypePipelineLayoutCreateInfo = 30;
		internal const uint StructureTypeSamplerCreateInfo = 31;
		internal const uint StructureTypeDescriptorSetLayoutCreateInfo = 32;
		internal const uint StructureTypeDescriptorPoolCreateInfo = 33;
		internal const uint StructureTypeDescriptorSetAllocateInfo = 34;
		internal const uint StructureTypeWriteDescriptorSet = 35;
		internal const uint StructureTypeBufferMemoryBarrier = 44;
		internal const uint StructureTypeMemoryBarrier = 46;
		internal const uint StructureTypeDescriptorSetLayoutBindingFlagsCreateInfo = 1000161000;
		internal const uint StructureTypeDescriptorSetVariableDescriptorCountAllocateInfo = 1000161003;
		internal const uint StructureTypePipelineRenderingCreateInfo = 1000044002;
		internal const uint StructureTypeExternalMemoryBufferCreateInfo = 1000072000;
		internal const uint FormatR8Unorm = 9;
		internal const uint ComponentSwizzleIdentity = 0, ComponentSwizzleOne = 2, ComponentSwizzleR = 3;

		// Buffer usage.
		internal const uint BufferUsageTransferSrc = 0x1, BufferUsageTransferDst = 0x2, BufferUsageUniformBuffer = 0x10, BufferUsageStorageBuffer = 0x20,
			BufferUsageIndexBuffer = 0x40, BufferUsageVertexBuffer = 0x80, BufferUsageIndirectBuffer = 0x100;

		// Pipeline stages and access, for uploads and the frame's buffers.
		internal const uint PipelineStageDrawIndirect = 0x2, PipelineStageVertexInput = 0x4, PipelineStageVertexShader = 0x8, PipelineStageFragmentShader = 0x80,
			PipelineStageTransfer = 0x1000, PipelineStageHost = 0x4000;
		internal const uint AccessIndirectCommandRead = 0x1, AccessIndexRead = 0x2, AccessVertexAttributeRead = 0x4, AccessUniformRead = 0x8,
			AccessTransferRead = 0x800, AccessTransferWrite = 0x1000, AccessHostWrite = 0x4000;
		internal const uint ImageLayoutTransferSrcOptimal = 6;

		// Descriptors.
		internal const uint DescriptorTypeSampler = 0, DescriptorTypeSampledImage = 2, DescriptorTypeUniformBuffer = 6, DescriptorTypeStorageBuffer = 7;
		internal const uint ShaderStageVertex = 0x1, ShaderStageFragment = 0x10;
		internal const uint DescriptorBindingUpdateAfterBind = 0x1, DescriptorBindingUpdateUnusedWhilePending = 0x2, DescriptorBindingPartiallyBound = 0x4,
			DescriptorBindingVariableDescriptorCount = 0x8;
		internal const uint DescriptorSetLayoutCreateUpdateAfterBindPool = 0x2;
		internal const uint DescriptorPoolCreateFreeDescriptorSet = 0x1, DescriptorPoolCreateUpdateAfterBind = 0x2;

		// Samplers.
		internal const uint FilterNearest = 0, FilterLinear = 1;
		internal const uint SamplerMipmapModeNearest = 0, SamplerMipmapModeLinear = 1;
		internal const uint SamplerAddressModeRepeat = 0, SamplerAddressModeClampToEdge = 2;

		// Pipelines.
		internal const uint PipelineBindPointGraphics = 0;
		internal const uint PrimitiveTopologyTriangleList = 3;
		internal const uint PolygonModeFill = 0;
		internal const uint CullModeNone = 0, CullModeFront = 1, CullModeBack = 2;
		internal const uint FrontFaceCounterClockwise = 0, FrontFaceClockwise = 1;
		internal const uint CompareOpLessOrEqual = 3, CompareOpAlways = 7;
		internal const uint BlendFactorZero = 0, BlendFactorOne = 1, BlendFactorSrcAlpha = 6, BlendFactorOneMinusSrcAlpha = 7;
		internal const uint BlendOpAdd = 0;
		internal const uint ColorComponentAll = 0xF;
		internal const uint DynamicStateViewport = 0, DynamicStateScissor = 1;
		internal const uint VertexInputRateVertex = 0;
		internal const uint FormatR32G32Sfloat = 103, FormatR32G32B32Sfloat = 106, FormatR32G32B32A32Sfloat = 109, FormatR32G32B32Sint = 105;
		internal const uint IndexTypeUint32 = 1;
		internal const uint SharingModeExclusive = 0;
		internal const uint ShaderStageVertexBit = 0x1, ShaderStageFragmentBit = 0x10;

		// --- structures ---

		[StructLayout(LayoutKind.Sequential)]
		internal struct BufferCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public ulong Size;
			public uint Usage;
			public uint SharingMode;
			public uint QueueFamilyIndexCount;
			public uint* PQueueFamilyIndices;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ExternalMemoryBufferCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint HandleTypes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MappedMemoryRange
		{
			public uint SType;
			public void* PNext;
			public ulong Memory;
			public ulong Offset;
			public ulong Size;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct BufferCopy
		{
			public ulong SrcOffset;
			public ulong DstOffset;
			public ulong Size;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageSubresourceLayers
		{
			public uint AspectMask;
			public uint MipLevel;
			public uint BaseArrayLayer;
			public uint LayerCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Offset3D
		{
			public int X, Y, Z;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct BufferImageCopy
		{
			public ulong BufferOffset;
			public uint BufferRowLength;
			public uint BufferImageHeight;
			public ImageSubresourceLayers ImageSubresource;
			public Offset3D ImageOffset;
			public Extent3D ImageExtent;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ImageBlit
		{
			public ImageSubresourceLayers SrcSubresource;
			public Offset3D SrcOffset0, SrcOffset1;
			public ImageSubresourceLayers DstSubresource;
			public Offset3D DstOffset0, DstOffset1;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct BufferMemoryBarrier
		{
			public uint SType;
			public void* PNext;
			public uint SrcAccessMask;
			public uint DstAccessMask;
			public uint SrcQueueFamilyIndex;
			public uint DstQueueFamilyIndex;
			public ulong Buffer;
			public ulong Offset;
			public ulong Size;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct MemoryBarrier
		{
			public uint SType;
			public void* PNext;
			public uint SrcAccessMask;
			public uint DstAccessMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SamplerCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint MagFilter;
			public uint MinFilter;
			public uint MipmapMode;
			public uint AddressModeU;
			public uint AddressModeV;
			public uint AddressModeW;
			public float MipLodBias;
			public uint AnisotropyEnable;
			public float MaxAnisotropy;
			public uint CompareEnable;
			public uint CompareOp;
			public float MinLod;
			public float MaxLod;
			public uint BorderColor;
			public uint UnnormalizedCoordinates;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorSetLayoutBinding
		{
			public uint Binding;
			public uint DescriptorType;
			public uint DescriptorCount;
			public uint StageFlags;
			public ulong* PImmutableSamplers;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorSetLayoutCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint BindingCount;
			public DescriptorSetLayoutBinding* PBindings;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorSetLayoutBindingFlagsCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint BindingCount;
			public uint* PBindingFlags;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorPoolSize
		{
			public uint Type;
			public uint DescriptorCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorPoolCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint MaxSets;
			public uint PoolSizeCount;
			public DescriptorPoolSize* PPoolSizes;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorSetAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public ulong DescriptorPool;
			public uint DescriptorSetCount;
			public ulong* PSetLayouts;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorSetVariableDescriptorCountAllocateInfo
		{
			public uint SType;
			public void* PNext;
			public uint DescriptorSetCount;
			public uint* PDescriptorCounts;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorImageInfo
		{
			public ulong Sampler;
			public ulong ImageView;
			public uint ImageLayout;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DescriptorBufferInfo
		{
			public ulong Buffer;
			public ulong Offset;
			public ulong Range;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct WriteDescriptorSet
		{
			public uint SType;
			public void* PNext;
			public ulong DstSet;
			public uint DstBinding;
			public uint DstArrayElement;
			public uint DescriptorCount;
			public uint DescriptorType;
			public DescriptorImageInfo* PImageInfo;
			public DescriptorBufferInfo* PBufferInfo;
			public ulong* PTexelBufferView;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PushConstantRange
		{
			public uint StageFlags;
			public uint Offset;
			public uint Size;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineLayoutCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint SetLayoutCount;
			public ulong* PSetLayouts;
			public uint PushConstantRangeCount;
			public PushConstantRange* PPushConstantRanges;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct ShaderModuleCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public UIntPtr CodeSize;
			public uint* PCode;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SpecializationMapEntry
		{
			public uint ConstantID;
			public uint Offset;
			public UIntPtr Size;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct SpecializationInfo
		{
			public uint MapEntryCount;
			public SpecializationMapEntry* PMapEntries;
			public UIntPtr DataSize;
			public void* PData;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineShaderStageCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint Stage;
			public ulong Module;
			public byte* PName;
			public SpecializationInfo* PSpecializationInfo;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct VertexInputBindingDescription
		{
			public uint Binding;
			public uint Stride;
			public uint InputRate;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct VertexInputAttributeDescription
		{
			public uint Location;
			public uint Binding;
			public uint Format;
			public uint Offset;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineVertexInputStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint VertexBindingDescriptionCount;
			public VertexInputBindingDescription* PVertexBindingDescriptions;
			public uint VertexAttributeDescriptionCount;
			public VertexInputAttributeDescription* PVertexAttributeDescriptions;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineInputAssemblyStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint Topology;
			public uint PrimitiveRestartEnable;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineViewportStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint ViewportCount;
			public void* PViewports;
			public uint ScissorCount;
			public void* PScissors;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineRasterizationStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint DepthClampEnable;
			public uint RasterizerDiscardEnable;
			public uint PolygonMode;
			public uint CullMode;
			public uint FrontFace;
			public uint DepthBiasEnable;
			public float DepthBiasConstantFactor;
			public float DepthBiasClamp;
			public float DepthBiasSlopeFactor;
			public float LineWidth;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineMultisampleStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint RasterizationSamples;
			public uint SampleShadingEnable;
			public float MinSampleShading;
			public uint* PSampleMask;
			public uint AlphaToCoverageEnable;
			public uint AlphaToOneEnable;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct StencilOpState
		{
			public uint FailOp, PassOp, DepthFailOp, CompareOp, CompareMask, WriteMask, Reference;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineDepthStencilStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint DepthTestEnable;
			public uint DepthWriteEnable;
			public uint DepthCompareOp;
			public uint DepthBoundsTestEnable;
			public uint StencilTestEnable;
			public StencilOpState Front;
			public StencilOpState Back;
			public float MinDepthBounds;
			public float MaxDepthBounds;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineColorBlendAttachmentState
		{
			public uint BlendEnable;
			public uint SrcColorBlendFactor;
			public uint DstColorBlendFactor;
			public uint ColorBlendOp;
			public uint SrcAlphaBlendFactor;
			public uint DstAlphaBlendFactor;
			public uint AlphaBlendOp;
			public uint ColorWriteMask;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineColorBlendStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint LogicOpEnable;
			public uint LogicOp;
			public uint AttachmentCount;
			public PipelineColorBlendAttachmentState* PAttachments;
			public fixed float BlendConstants[4];
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineDynamicStateCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint DynamicStateCount;
			public uint* PDynamicStates;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct PipelineRenderingCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint ViewMask;
			public uint ColorAttachmentCount;
			public uint* PColorAttachmentFormats;
			public uint DepthAttachmentFormat;
			public uint StencilAttachmentFormat;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct GraphicsPipelineCreateInfo
		{
			public uint SType;
			public void* PNext;
			public uint Flags;
			public uint StageCount;
			public PipelineShaderStageCreateInfo* PStages;
			public PipelineVertexInputStateCreateInfo* PVertexInputState;
			public PipelineInputAssemblyStateCreateInfo* PInputAssemblyState;
			public void* PTessellationState;
			public PipelineViewportStateCreateInfo* PViewportState;
			public PipelineRasterizationStateCreateInfo* PRasterizationState;
			public PipelineMultisampleStateCreateInfo* PMultisampleState;
			public PipelineDepthStencilStateCreateInfo* PDepthStencilState;
			public PipelineColorBlendStateCreateInfo* PColorBlendState;
			public PipelineDynamicStateCreateInfo* PDynamicState;
			public ulong Layout;
			public ulong RenderPass;
			public uint Subpass;
			public ulong BasePipelineHandle;
			public int BasePipelineIndex;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct Viewport
		{
			public float X, Y, Width, Height, MinDepth, MaxDepth;
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DrawIndexedIndirectCommand
		{
			public uint IndexCount;
			public uint InstanceCount;
			public uint FirstIndex;
			public int VertexOffset;
			public uint FirstInstance;
		}

		// --- functions ---

		[DllImport(Library)] internal static extern int vkCreateBuffer(IntPtr device, BufferCreateInfo* createInfo, void* allocator, out ulong buffer);
		[DllImport(Library)] internal static extern void vkDestroyBuffer(IntPtr device, ulong buffer, void* allocator);
		[DllImport(Library)] internal static extern void vkGetBufferMemoryRequirements(IntPtr device, ulong buffer, out MemoryRequirements requirements);
		[DllImport(Library)] internal static extern int vkBindBufferMemory(IntPtr device, ulong buffer, ulong memory, ulong offset);
		[DllImport(Library)] internal static extern int vkMapMemory(IntPtr device, ulong memory, ulong offset, ulong size, uint flags, out IntPtr data);
		[DllImport(Library)] internal static extern void vkUnmapMemory(IntPtr device, ulong memory);
		[DllImport(Library)] internal static extern int vkFlushMappedMemoryRanges(IntPtr device, uint count, MappedMemoryRange* ranges);

		[DllImport(Library)] internal static extern int vkCreateSampler(IntPtr device, SamplerCreateInfo* createInfo, void* allocator, out ulong sampler);
		[DllImport(Library)] internal static extern void vkDestroySampler(IntPtr device, ulong sampler, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateDescriptorSetLayout(IntPtr device, DescriptorSetLayoutCreateInfo* createInfo, void* allocator, out ulong layout);
		[DllImport(Library)] internal static extern void vkDestroyDescriptorSetLayout(IntPtr device, ulong layout, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateDescriptorPool(IntPtr device, DescriptorPoolCreateInfo* createInfo, void* allocator, out ulong pool);
		[DllImport(Library)] internal static extern void vkDestroyDescriptorPool(IntPtr device, ulong pool, void* allocator);
		[DllImport(Library)] internal static extern int vkAllocateDescriptorSets(IntPtr device, DescriptorSetAllocateInfo* allocateInfo, ulong* sets);
		[DllImport(Library)] internal static extern void vkUpdateDescriptorSets(IntPtr device, uint writeCount, WriteDescriptorSet* writes, uint copyCount, void* copies);
		[DllImport(Library)] internal static extern int vkCreatePipelineLayout(IntPtr device, PipelineLayoutCreateInfo* createInfo, void* allocator, out ulong layout);
		[DllImport(Library)] internal static extern void vkDestroyPipelineLayout(IntPtr device, ulong layout, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateShaderModule(IntPtr device, ShaderModuleCreateInfo* createInfo, void* allocator, out ulong module);
		[DllImport(Library)] internal static extern void vkDestroyShaderModule(IntPtr device, ulong module, void* allocator);
		[DllImport(Library)] internal static extern int vkCreateGraphicsPipelines(IntPtr device, ulong cache, uint count, GraphicsPipelineCreateInfo* createInfos, void* allocator, ulong* pipelines);
		[DllImport(Library)] internal static extern void vkDestroyPipeline(IntPtr device, ulong pipeline, void* allocator);

		[DllImport(Library)] internal static extern void vkCmdCopyBuffer(IntPtr commandBuffer, ulong src, ulong dst, uint regionCount, BufferCopy* regions);
		[DllImport(Library)] internal static extern void vkCmdCopyBufferToImage(IntPtr commandBuffer, ulong src, ulong dst, uint dstLayout, uint regionCount, BufferImageCopy* regions);
		[DllImport(Library)] internal static extern void vkCmdBlitImage(IntPtr commandBuffer, ulong src, uint srcLayout, ulong dst, uint dstLayout, uint regionCount, ImageBlit* regions, uint filter);
		[DllImport(Library)] internal static extern void vkCmdPipelineBarrier(IntPtr commandBuffer, uint srcStageMask, uint dstStageMask, uint dependencyFlags,
			uint memoryBarrierCount, MemoryBarrier* memoryBarriers, uint bufferMemoryBarrierCount, BufferMemoryBarrier* bufferMemoryBarriers, uint imageMemoryBarrierCount, ImageMemoryBarrier* imageMemoryBarriers);
		[DllImport(Library)] internal static extern void vkCmdBindPipeline(IntPtr commandBuffer, uint bindPoint, ulong pipeline);
		[DllImport(Library)] internal static extern void vkCmdBindDescriptorSets(IntPtr commandBuffer, uint bindPoint, ulong layout, uint firstSet, uint setCount, ulong* sets, uint dynamicOffsetCount, uint* dynamicOffsets);
		[DllImport(Library)] internal static extern void vkCmdBindVertexBuffers(IntPtr commandBuffer, uint firstBinding, uint bindingCount, ulong* buffers, ulong* offsets);
		[DllImport(Library)] internal static extern void vkCmdBindIndexBuffer(IntPtr commandBuffer, ulong buffer, ulong offset, uint indexType);
		[DllImport(Library)] internal static extern void vkCmdSetViewport(IntPtr commandBuffer, uint firstViewport, uint viewportCount, Viewport* viewports);
		[DllImport(Library)] internal static extern void vkCmdSetScissor(IntPtr commandBuffer, uint firstScissor, uint scissorCount, Rect2D* scissors);
		[DllImport(Library)] internal static extern void vkCmdPushConstants(IntPtr commandBuffer, ulong layout, uint stageFlags, uint offset, uint size, void* values);
		[DllImport(Library)] internal static extern void vkCmdDrawIndexedIndirect(IntPtr commandBuffer, ulong buffer, ulong offset, uint drawCount, uint stride);
	}
}
