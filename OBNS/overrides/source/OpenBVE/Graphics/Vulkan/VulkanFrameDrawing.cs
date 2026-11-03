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

// OBNS: not an upstream file. The 2D part of a frame drawn by Vulkan, over the finished 3D frame (OBNS/RENDERERS.md). Text's
// first pass darkens what is under it, so this can't be a layer of its own laid over: it has to draw on the frame itself.

using System;
using System.Runtime.InteropServices;
using LibRender2.Frame;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>
	/// Between <see cref="Begin"/> and <see cref="End"/>, keeps the frame's rectangles and glyphs for Vulkan to draw; the rest of
	/// the time everything goes to OpenGL, as does the state, which the 3D part of the frame and the loading screen use.
	/// </summary>
	internal sealed unsafe class VulkanFrameDrawing : WholeFrameDrawing, IDisposable
	{
		private readonly VulkanDevice device;
		private readonly VulkanScene scene;
		private readonly ulong[] pipelines = new ulong[4];
		/// <summary>The pipelines for particles, in the world's rendering with its depth: opaque texels writing depth, then blended ones not.</summary>
		private readonly ulong[] particlePipelines = new ulong[2];
		private readonly ulong vertexModule, fragmentModule, pipelineLayout;

		/// <summary>On the core Vulkan 1.0 tier, render passes the pipelines are made for (any of the same attachments' formats will do): colour alone, and with depth.</summary>
		private readonly ulong colorPass, depthPass;

		/// <summary>The pages of textures, where the device has no descriptor indexing (else null): the quad's page is bound as set 1.</summary>
		private readonly VulkanTexturePages pages;

		protected override WorldTextures Textures => scene.Textures;

		internal VulkanFrameDrawing(VulkanDevice device, VulkanScene scene, IFrameDrawing openGl, ulong colorPass = 0, ulong depthPass = 0) : base(openGl)
		{
			this.device = device;
			this.scene = scene;
			this.colorPass = colorPass;
			this.depthPass = depthPass;
			pages = scene.Pages;
			ulong* setLayouts = stackalloc ulong[2];
			setLayouts[0] = scene.SetLayout;
			setLayouts[1] = pages?.Layout ?? 0;
			Vk.PushConstantRange push = new Vk.PushConstantRange { StageFlags = Vk.ShaderStageVertex | Vk.ShaderStageFragment, Size = (uint)sizeof(QuadData) };
			Vk.PipelineLayoutCreateInfo layoutInfo = new Vk.PipelineLayoutCreateInfo
			{
				SType = Vk.StructureTypePipelineLayoutCreateInfo,
				SetLayoutCount = pages == null ? 1u : 2u,
				PSetLayouts = setLayouts,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Vk.Check(Vk.vkCreatePipelineLayout(device.Device, &layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");
			// The shaders built for Vulkan 1.0 with pages (they run on the 1.2/1.3 path too, where it has pages).
			vertexModule = scene.ShaderModule(pages == null ? "frame2d.vert.spv" : "frame2d.vk10.vert.spv");
			fragmentModule = scene.ShaderModule(pages == null ? "frame2d.frag.spv" : "frame2d.vk10.frag.spv");
		}

		/// <summary>The frame's 2D part starts, if this frame's images are ready for it.</summary>
		internal void Begin()
		{
			if (VulkanDevice.Full)
			{
				return;
			}

			quads.Clear();
			active = VulkanWorld.Enabled && VulkanWorld.Instance.FrameReady(out uint width, out uint height) && width == (uint)Program.Renderer.Screen.Width && height == (uint)Program.Renderer.Screen.Height;
		}

		/// <summary>The frame's 2D part is done: drawn, and the frame shown through OpenGL.</summary>
		internal void End()
		{
			if (VulkanDevice.Full || !active)
			{
				return;
			}

			active = false;
			VulkanWorld.Instance.DrawFrame2D();
			framesDrawn++;
			if (framesDrawn == 1 || framesDrawn == 600)
			{
				Program.FileSystem.AppendToLogFile("OBNS Vulkan drew frame " + framesDrawn + "'s 2D part: " + quads.Count + " quads");
			}
		}

		private long framesDrawn;

		/// <summary>Records the last frame's image laid over the whole frame, with the blur's alpha, as upstream draws it (its rows run as the frame's).</summary>
		internal void RecordLastFrame(IntPtr commandBuffer, int slot, uint width, uint height, int texture, float alpha)
		{
			Vk.Viewport viewport = new Vk.Viewport { Width = width, Height = height, MaxDepth = 1.0f };
			Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = width, Height = height } };
			Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
			Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
			ulong set = scene.DescriptorSets[slot];
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
			Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, Pipeline(Blend.Normal));
			int boundPage = -1;
			BindPage(commandBuffer, slot, texture, ref boundPage);
			QuadData data = new QuadData { X = -1.0f, Y = -1.0f, Width = 2.0f, Height = 2.0f, UWidth = 1.0f, VHeight = 1.0f, R = 1.0f, G = 1.0f, B = 1.0f, A = alpha, Texture = texture };
			Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, (uint)sizeof(QuadData), &data);
			Vk.vkCmdDraw(commandBuffer, 6, 1, 0, 0);
		}

		/// <summary>Records the frame's particles, inside the world's rendering, tested against its depth.</summary>
		internal void RecordParticles(IntPtr commandBuffer, int slot)
		{
			if (particles.Count == 0)
			{
				return;
			}

			Vk.Viewport viewport = new Vk.Viewport { Width = Program.Renderer.Screen.Width, Height = Program.Renderer.Screen.Height, MaxDepth = 1.0f };
			Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = (uint)Program.Renderer.Screen.Width, Height = (uint)Program.Renderer.Screen.Height } };
			Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
			Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
			ulong set = scene.DescriptorSets[slot];
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
			Blend bound = (Blend)(-1);
			int boundPage = -1;
			foreach (Quad quad in particles)
			{
				BindPage(commandBuffer, slot, quad.Data.Texture, ref boundPage);
				if (quad.Blend != bound)
				{
					bound = quad.Blend;
					int which = bound == Blend.None ? 0 : 1;
					if (particlePipelines[which] == 0)
					{
						particlePipelines[which] = MakePipeline(bound, true, bound == Blend.None);
					}

					Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, particlePipelines[which]);
				}

				QuadData data = quad.Data;
				Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, (uint)sizeof(QuadData), &data);
				Vk.vkCmdDraw(commandBuffer, 6, 1, 0, 0);
			}
		}

		/// <summary>Records the frame's quads, inside its rendering.</summary>
		internal void Record(IntPtr commandBuffer, int slot, uint width, uint height)
		{
			Vk.Viewport viewport = new Vk.Viewport { Width = width, Height = height, MaxDepth = 1.0f };
			Vk.Rect2D scissor = new Vk.Rect2D { Extent = new Vk.Extent2D { Width = width, Height = height } };
			Vk.vkCmdSetViewport(commandBuffer, 0, 1, &viewport);
			Vk.vkCmdSetScissor(commandBuffer, 0, 1, &scissor);
			ulong set = scene.DescriptorSets[slot];
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 0, 1, &set, 0, null);
			Blend bound = (Blend)(-1);
			int boundPage = -1;
			foreach (Quad quad in quads)
			{
				BindPage(commandBuffer, slot, quad.Data.Texture, ref boundPage);
				if (quad.Blend != bound)
				{
					bound = quad.Blend;
					Vk.vkCmdBindPipeline(commandBuffer, Vk.PipelineBindPointGraphics, Pipeline(bound));
				}

				QuadData data = quad.Data;
				Vk.vkCmdPushConstants(commandBuffer, pipelineLayout, Vk.ShaderStageVertex | Vk.ShaderStageFragment, 0, (uint)sizeof(QuadData), &data);
				Vk.vkCmdDraw(commandBuffer, 6, 1, 0, 0);
			}
		}

		/// <summary>With pages, binds a texture's page (set 1) for the frame slot, if it is not bound already.</summary>
		private void BindPage(IntPtr commandBuffer, int slot, int texture, ref int boundPage)
		{
			int page = texture / Math.Max(1, pages?.PageSize ?? 1);
			if (pages == null || page == boundPage)
			{
				return;
			}

			ulong set = pages.Set(page, slot);
			Vk.vkCmdBindDescriptorSets(commandBuffer, Vk.PipelineBindPointGraphics, pipelineLayout, 1, 1, &set, 0, null);
			boundPage = page;
		}

		private ulong Pipeline(Blend blend)
		{
			if (pipelines[(int)blend] == 0)
			{
				pipelines[(int)blend] = MakePipeline(blend, false, false);
			}

			return pipelines[(int)blend];
		}

		private ulong MakePipeline(Blend blend, bool withDepth, bool depthWrite)
		{
			IntPtr main = Marshal.StringToHGlobalAnsi("main");
			try
			{
				Vk.PipelineShaderStageCreateInfo* stages = stackalloc Vk.PipelineShaderStageCreateInfo[2];
				stages[0] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageVertexBit, Module = vertexModule, PName = (byte*)main };
				// With pages, their size (constant 0).
				int pageSize = pages?.PageSize ?? 0;
				Vk.SpecializationMapEntry pageEntry = new Vk.SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = (UIntPtr)4 };
				Vk.SpecializationInfo pageSpecialization = new Vk.SpecializationInfo { MapEntryCount = 1, PMapEntries = &pageEntry, DataSize = (UIntPtr)4, PData = &pageSize };
				stages[1] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFragmentBit, Module = fragmentModule, PName = (byte*)main, PSpecializationInfo = pages == null ? null : &pageSpecialization };
				Vk.PipelineVertexInputStateCreateInfo vertexInput = new Vk.PipelineVertexInputStateCreateInfo { SType = Vk.StructureTypePipelineVertexInputStateCreateInfo };
				// Upstream draws a strip of six with culling on, which leaves each half of a rectangle covered once: as a list here.
				Vk.PipelineInputAssemblyStateCreateInfo inputAssembly = new Vk.PipelineInputAssemblyStateCreateInfo { SType = Vk.StructureTypePipelineInputAssemblyStateCreateInfo, Topology = Vk.PrimitiveTopologyTriangleList };
				Vk.PipelineViewportStateCreateInfo viewportState = new Vk.PipelineViewportStateCreateInfo { SType = Vk.StructureTypePipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
				Vk.PipelineRasterizationStateCreateInfo rasterization = new Vk.PipelineRasterizationStateCreateInfo
				{
					SType = Vk.StructureTypePipelineRasterizationStateCreateInfo,
					PolygonMode = Vk.PolygonModeFill,
					CullMode = Vk.CullModeNone,
					FrontFace = Vk.FrontFaceClockwise,
					LineWidth = 1.0f
				};
				Vk.PipelineMultisampleStateCreateInfo multisample = new Vk.PipelineMultisampleStateCreateInfo { SType = Vk.StructureTypePipelineMultisampleStateCreateInfo, RasterizationSamples = 1 };
				Vk.PipelineDepthStencilStateCreateInfo depth = new Vk.PipelineDepthStencilStateCreateInfo
				{
					SType = Vk.StructureTypePipelineDepthStencilStateCreateInfo,
					DepthTestEnable = withDepth ? 1u : 0u,
					DepthWriteEnable = depthWrite ? 1u : 0u,
					DepthCompareOp = Vk.CompareOpLessOrEqual,
					MaxDepthBounds = 1.0f
				};
				// OpenGL's blend functions apply to alpha alike.
				Vk.PipelineColorBlendAttachmentState attachment = new Vk.PipelineColorBlendAttachmentState { ColorWriteMask = Vk.ColorComponentAll };
				switch (blend)
				{
					case Blend.Normal:
						attachment.BlendEnable = 1;
						attachment.SrcColorBlendFactor = attachment.SrcAlphaBlendFactor = Vk.BlendFactorSrcAlpha;
						attachment.DstColorBlendFactor = attachment.DstAlphaBlendFactor = Vk.BlendFactorOneMinusSrcAlpha;
						break;
					case Blend.GlyphMask:
						attachment.BlendEnable = 1;
						attachment.SrcColorBlendFactor = attachment.SrcAlphaBlendFactor = Vk.BlendFactorZero;
						attachment.DstColorBlendFactor = attachment.DstAlphaBlendFactor = Vk.BlendFactorOneMinusSrcColor;
						break;
					case Blend.GlyphAdd:
						attachment.BlendEnable = 1;
						attachment.SrcColorBlendFactor = attachment.SrcAlphaBlendFactor = Vk.BlendFactorSrcAlpha;
						attachment.DstColorBlendFactor = attachment.DstAlphaBlendFactor = Vk.BlendFactorOne;
						break;
				}

				Vk.PipelineColorBlendStateCreateInfo colorBlend = new Vk.PipelineColorBlendStateCreateInfo { SType = Vk.StructureTypePipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
				uint* dynamicStates = stackalloc uint[2];
				dynamicStates[0] = Vk.DynamicStateViewport;
				dynamicStates[1] = Vk.DynamicStateScissor;
				Vk.PipelineDynamicStateCreateInfo dynamic = new Vk.PipelineDynamicStateCreateInfo { SType = Vk.StructureTypePipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
				uint colorFormat = SharedTarget.ColorFormat;
				Vk.PipelineRenderingCreateInfo rendering = new Vk.PipelineRenderingCreateInfo { SType = Vk.StructureTypePipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat, DepthAttachmentFormat = withDepth ? SharedTarget.DepthFormat : 0 };
				Vk.GraphicsPipelineCreateInfo info = new Vk.GraphicsPipelineCreateInfo
				{
					SType = Vk.StructureTypeGraphicsPipelineCreateInfo,
					// The core Vulkan 1.0 tier: a render pass in place of dynamic rendering.
					PNext = device.Core10 ? null : &rendering,
					RenderPass = device.Core10 ? withDepth ? depthPass : colorPass : 0,
					StageCount = 2,
					PStages = stages,
					PVertexInputState = &vertexInput,
					PInputAssemblyState = &inputAssembly,
					PViewportState = &viewportState,
					PRasterizationState = &rasterization,
					PMultisampleState = &multisample,
					PDepthStencilState = &depth,
					PColorBlendState = &colorBlend,
					PDynamicState = &dynamic,
					Layout = pipelineLayout,
					BasePipelineIndex = -1
				};
				ulong pipeline;
				Vk.Check(Vk.vkCreateGraphicsPipelines(device.Device, 0, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines (2D)");
				return pipeline;
			}
			finally
			{
				Marshal.FreeHGlobal(main);
			}
		}

		public void Dispose()
		{
			foreach (ulong pipeline in pipelines)
			{
				if (pipeline != 0) Vk.vkDestroyPipeline(device.Device, pipeline, null);
			}

			foreach (ulong pipeline in particlePipelines)
			{
				if (pipeline != 0) Vk.vkDestroyPipeline(device.Device, pipeline, null);
			}

			if (pipelineLayout != 0) Vk.vkDestroyPipelineLayout(device.Device, pipelineLayout, null);
			if (vertexModule != 0) Vk.vkDestroyShaderModule(device.Device, vertexModule, null);
			if (fragmentModule != 0) Vk.vkDestroyShaderModule(device.Device, fragmentModule, null);
		}
	}
}
