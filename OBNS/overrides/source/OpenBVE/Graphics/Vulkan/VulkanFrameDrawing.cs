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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibRender2.Frame;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Vulkan
{
	/// <summary>
	/// Between <see cref="Begin"/> and <see cref="End"/>, keeps the frame's rectangles and glyphs for Vulkan to draw; the rest of
	/// the time everything goes to OpenGL, as does the state, which the 3D part of the frame and the loading screen use.
	/// </summary>
	internal sealed unsafe class VulkanFrameDrawing : IFrameDrawing, IDisposable
	{
		/// <summary>The blending of a quad, as upstream's blend functions (each is a pipeline).</summary>
		private enum Blend { None = 0, Normal = 1, GlyphMask = 2, GlyphAdd = 3 }

		/// <summary>What frame2d.vert and frame2d.frag are pushed for a quad.</summary>
		[StructLayout(LayoutKind.Sequential)]
		private struct QuadData
		{
			internal float X, Y, Width, Height, U, V, UWidth, VHeight, R, G, B, A, AlphaFunction, AlphaValue;
			internal int Texture, Sampler;
		}

		private struct Quad
		{
			internal QuadData Data;
			internal Blend Blend;
		}

		private readonly VulkanDevice device;
		private readonly VulkanScene scene;
		private readonly IFrameDrawing openGl;
		private readonly List<Quad> quads = new List<Quad>();
		private readonly ulong[] pipelines = new ulong[4];
		private readonly ulong vertexModule, fragmentModule, pipelineLayout;
		private bool active, blendEnabled = true;
		private float alphaFunction, alphaValue;
		private float scaleX, scaleY;

		internal VulkanFrameDrawing(VulkanDevice device, VulkanScene scene, IFrameDrawing openGl)
		{
			this.device = device;
			this.scene = scene;
			this.openGl = openGl;
			ulong setLayout = scene.SetLayout;
			Vk.PushConstantRange push = new Vk.PushConstantRange { StageFlags = Vk.ShaderStageVertex | Vk.ShaderStageFragment, Size = (uint)sizeof(QuadData) };
			Vk.PipelineLayoutCreateInfo layoutInfo = new Vk.PipelineLayoutCreateInfo
			{
				SType = Vk.StructureTypePipelineLayoutCreateInfo,
				SetLayoutCount = 1,
				PSetLayouts = &setLayout,
				PushConstantRangeCount = 1,
				PPushConstantRanges = &push
			};
			Vk.Check(Vk.vkCreatePipelineLayout(device.Device, &layoutInfo, null, out pipelineLayout), "vkCreatePipelineLayout");
			vertexModule = scene.ShaderModule("frame2d.vert.spv");
			fragmentModule = scene.ShaderModule("frame2d.frag.spv");
		}

		/// <summary>The frame's 2D part starts, if this frame's images are ready for it.</summary>
		internal void Begin()
		{
			quads.Clear();
			active = VulkanWorld.Enabled && VulkanWorld.Instance.FrameReady(out uint width, out uint height) && width == (uint)Program.Renderer.Screen.Width && height == (uint)Program.Renderer.Screen.Height;
			if (active)
			{
				scaleX = 2.0f / Program.Renderer.Screen.Width;
				scaleY = 2.0f / Program.Renderer.Screen.Height;
			}
		}

		/// <summary>The frame's 2D part is done: drawn, and the frame shown through OpenGL.</summary>
		internal void End()
		{
			if (!active)
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
			foreach (Quad quad in quads)
			{
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

		private ulong Pipeline(Blend blend)
		{
			if (pipelines[(int)blend] != 0)
			{
				return pipelines[(int)blend];
			}

			IntPtr main = Marshal.StringToHGlobalAnsi("main");
			try
			{
				Vk.PipelineShaderStageCreateInfo* stages = stackalloc Vk.PipelineShaderStageCreateInfo[2];
				stages[0] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageVertexBit, Module = vertexModule, PName = (byte*)main };
				stages[1] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureTypePipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFragmentBit, Module = fragmentModule, PName = (byte*)main };
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
				Vk.PipelineDepthStencilStateCreateInfo depth = new Vk.PipelineDepthStencilStateCreateInfo { SType = Vk.StructureTypePipelineDepthStencilStateCreateInfo, MaxDepthBounds = 1.0f };
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
				Vk.PipelineRenderingCreateInfo rendering = new Vk.PipelineRenderingCreateInfo { SType = Vk.StructureTypePipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &colorFormat };
				Vk.GraphicsPipelineCreateInfo info = new Vk.GraphicsPipelineCreateInfo
				{
					SType = Vk.StructureTypeGraphicsPipelineCreateInfo,
					PNext = &rendering,
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
				pipelines[(int)blend] = pipeline;
				return pipeline;
			}
			finally
			{
				Marshal.FreeHGlobal(main);
			}
		}

		private void Add(Blend blend, int texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, double u, double v, double uWidth, double vHeight)
		{
			quads.Add(new Quad
			{
				Blend = blend,
				Data = new QuadData
				{
					// Row 0 of the images is the bottom of the screen, as in OpenGL; points come from the top.
					X = (float)point.X * scaleX - 1.0f,
					Y = 1.0f - (float)point.Y * scaleY,
					Width = (float)size.X * scaleX,
					Height = -(float)size.Y * scaleY,
					U = (float)u,
					V = (float)v,
					UWidth = (float)uWidth,
					VHeight = (float)vHeight,
					R = color.R,
					G = color.G,
					B = color.B,
					A = color.A,
					AlphaFunction = alphaFunction,
					AlphaValue = alphaValue,
					Texture = texture,
					Sampler = (int)wrapMode
				}
			});
		}

		/// <summary>A texture's place in the layer's textures, white without one or if it can't be made; Later if not made yet.</summary>
		private int TextureIndex(Texture texture, OpenGlTextureWrapMode wrapMode)
		{
			int index = scene.Textures.Prepare(texture, wrapMode, out bool _);
			return index == -1 ? scene.Textures.WhiteIndex : index;
		}

		public void Initialize() => openGl.Initialize();

		public void ScreenChanged() => openGl.ScreenChanged();

		public void Clear() => openGl.Clear();

		public void SetBlend(bool enabled, BlendingFactor source, BlendingFactor destination)
		{
			blendEnabled = enabled;
			openGl.SetBlend(enabled, source, destination);
		}

		public void SetAlphaTest(bool enabled, AlphaFunction function, float value)
		{
			alphaFunction = enabled ? (float)(int)function : 0.0f;
			alphaValue = value;
			openGl.SetAlphaTest(enabled, function, value);
		}

		public void SetDepthWrite(bool enabled) => openGl.SetDepthWrite(enabled);

		public void Rectangle(Texture texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, Vector2 coordinates)
		{
			if (!active)
			{
				openGl.Rectangle(texture, wrapMode, point, size, color, coordinates);
				return;
			}

			int index = TextureIndex(texture, wrapMode);
			if (index != WorldTextures.Later)
			{
				Add(blendEnabled ? Blend.Normal : Blend.None, index, wrapMode, point, size, color, 0.0, 0.0, coordinates.X, coordinates.Y);
			}
		}

		public void BeginText()
		{
			if (!active)
			{
				openGl.BeginText();
			}
		}

		public void Glyph(Texture texture, Vector4 atlas, Vector2 point, Vector2 size, Color128 color)
		{
			if (!active)
			{
				openGl.Glyph(texture, atlas, point, size, color);
				return;
			}

			int index = TextureIndex(texture, OpenGlTextureWrapMode.ClampClamp);
			if (index == WorldTextures.Later)
			{
				return;
			}

			// Upstream's two passes: the background masked off, then the character added.
			Add(blendEnabled ? Blend.GlyphMask : Blend.None, index, OpenGlTextureWrapMode.ClampClamp, point, size, new Color128(color.A, color.A, color.A, 1.0f), atlas.X, atlas.Y, atlas.Z, atlas.W);
			Add(blendEnabled ? Blend.GlyphAdd : Blend.None, index, OpenGlTextureWrapMode.ClampClamp, point, size, color, atlas.X, atlas.Y, atlas.Z, atlas.W);
		}

		public void EndText()
		{
			if (!active)
			{
				openGl.EndText();
			}
		}

		public void Dispose()
		{
			foreach (ulong pipeline in pipelines)
			{
				if (pipeline != 0) Vk.vkDestroyPipeline(device.Device, pipeline, null);
			}

			if (pipelineLayout != 0) Vk.vkDestroyPipelineLayout(device.Device, pipelineLayout, null);
			if (vertexModule != 0) Vk.vkDestroyShaderModule(device.Device, vertexModule, null);
			if (fragmentModule != 0) Vk.vkDestroyShaderModule(device.Device, fragmentModule, null);
		}
	}
}
