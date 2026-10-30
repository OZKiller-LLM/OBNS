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
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * Upstream's cascaded shadows on native Vulkan. LibRender2's Shadows draws each cascade into a
	 * framebuffer object with a depth texture, then has the default shader read those textures; the
	 * parts of GL that takes are here:
	 *
	 *   - a depth texture attached to a framebuffer is one depth image per frame slot, so a frame
	 *     being drawn into a map never waits for the frame before it to finish reading its own;
	 *   - draws made while such a framebuffer is bound become records of that cascade's own, and at
	 *     the swap each cascade's records run in a depth-only render pass before the frame's;
	 *   - what shadows add to the default shader is a shader pair and a descriptor set of their own
	 *     (one frame's matrices and maps), used only while shadows are on, so a frame without them
	 *     costs what it did.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int GL_DEPTH_COMPONENT = 0x1902;
		private const int MaxShadowPasses = 4;
		private const int ShadowChunksPerPass = 2;

		/// <summary>The texture unit upstream binds the first cascade's map to.</summary>
		private const int ShadowFirstUnit = 4;

		/// <summary>Where the shadows' uniform locations start: above the blocks' own (a location is the byte offset / 4) and the sampler's.</summary>
		private const int ShadowLocationBase = 4096;

		// Byte offsets in default_shadow.frag's ShadowBlock.
		private const int ShadowBlockSize = 480, InverseViewOffset = 0, ViewOffset = 64, ShadowCountOffset = 460, ShadowEnabledOffset = 464;

		private static readonly Dictionary<string, int> ShadowUniforms = new Dictionary<string, int>
		{
			{ "uCurrentViewMatrix", ViewOffset },
			{ "uLightSpaceMatrix0", 128 },
			{ "uLightSpaceMatrix1", 192 },
			{ "uLightSpaceMatrix2", 256 },
			{ "uLightSpaceMatrix3", 320 },
			{ "uShadowSplit0", 384 },
			{ "uShadowSplit1", 388 },
			{ "uShadowSplit2", 392 },
			{ "uShadowSplit3", 396 },
			{ "uShadowBias0", 400 },
			{ "uShadowBias1", 404 },
			{ "uShadowBias2", 408 },
			{ "uShadowBias3", 412 },
			{ "uShadowNormalBias0", 416 },
			{ "uShadowNormalBias1", 420 },
			{ "uShadowNormalBias2", 424 },
			{ "uShadowNormalBias3", 428 },
			{ "uShadowTexelWorldSize0", 432 },
			{ "uShadowTexelWorldSize1", 436 },
			{ "uShadowTexelWorldSize2", 440 },
			{ "uShadowTexelWorldSize3", 444 },
			{ "uShadowStrength", 448 },
			{ "uShadowFilterRadius", 452 },
			{ "uShadowSmooth", 456 },
			{ "uShadowCascadeCount", ShadowCountOffset },
			{ "uShadowEnabled", ShadowEnabledOffset }
		};

		// Byte offsets in shadow_depth.vert's DepthBlock.
		private const int DepthBlockSize = 208;

		private static readonly Dictionary<string, int> DepthUniforms = new Dictionary<string, int>
		{
			{ "uLightSpaceMatrix", 0 },
			{ "uModelMatrix", 64 },
			{ "uTextureMatrix", 128 },
			{ "uHasTexture", 192 },
			{ "uAlphaCutoff", 196 },
			{ "uMaterialAlpha", 200 },
			{ "uMaterialFlags", 204 }
		};

		// The pipeline key's flags: a cascade's depth pass, a default program face reading the maps, a depth pass with no fragment shader.
		private const ulong DepthPassFlag = 1UL << 60, ShadowedFlag = 1UL << 61, DepthOnlyFlag = 1UL << 62, BlendBits = 0x1FFUL << 13;

		/// <summary>A depth texture's image for one frame slot.</summary>
		private struct DepthImage
		{
			internal Image Image;
			internal ImageHeap.Allocation Memory;
			internal ImageView View;
		}

		private sealed class GLFramebuffer
		{
			internal GLTexture Depth;
			internal readonly Framebuffer[] Handles = new Framebuffer[VulkanContext.Slots];
			internal DrawRecord[] Records = new DrawRecord[1024];
			internal int RecordCount;
			internal bool InFrame;
		}

		private readonly Dictionary<int, GLFramebuffer> framebufferObjects = new Dictionary<int, GLFramebuffer>();
		private GLFramebuffer boundFramebuffer;

		/// <summary>The cascades drawn into this frame, in the order they were begun.</summary>
		private readonly List<GLFramebuffer> framePasses = new List<GLFramebuffer>();

		/// <summary>The frame's cascade being drawn into, or null: its draws go in its own records.</summary>
		private GLFramebuffer currentPass;

		private readonly byte[] shadowData = new byte[ShadowBlockSize];
		private bool shadowDataDirty = true;
		private readonly BufferHeap.Allocation[] shadowBlocks = new BufferHeap.Allocation[VulkanContext.Slots];
		private readonly DescriptorSet[] shadowSets = new DescriptorSet[VulkanContext.Slots];
		private readonly GLTexture[,] shadowSetMaps = new GLTexture[VulkanContext.Slots, MaxShadowPasses];
		private readonly bool[] shadowBlockCurrent = new bool[VulkanContext.Slots];

		private DescriptorSetLayout shadowLayout;
		private DescriptorPool shadowPool;
		private Sampler shadowSampler;
		private RenderPass shadowRenderPass;
		private Format shadowFormat;
		private ShaderModule shadowDepthVertex, shadowDepthFragment, defaultShadowVertex, defaultShadowFragment;

		/// <summary>The layout of set 2 (one frame's cascades): only the shadowed pipelines read it.</summary>
		private void CreateShadowLayout()
		{
			DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[1 + MaxShadowPasses];
			bindings[0] = new DescriptorSetLayoutBinding(0, DescriptorType.UniformBuffer, 1, ShaderStageFlags.FragmentBit);
			for (uint i = 0; i < MaxShadowPasses; i++)
			{
				bindings[1 + i] = new DescriptorSetLayoutBinding(1 + i, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
			}

			DescriptorSetLayoutCreateInfo info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1 + MaxShadowPasses, PBindings = bindings };
			Check(vk.CreateDescriptorSetLayout(device, in info, null, out shadowLayout), "vkCreateDescriptorSetLayout");
		}

		private void CreateShadowShaders()
		{
			shadowDepthVertex = LoadShader("shadow_depth.vert");
			shadowDepthFragment = LoadShader("shadow_depth.frag");
			defaultShadowVertex = LoadShader("default_shadow.vert");
			defaultShadowFragment = LoadShader("default_shadow.frag");
		}

		/// <summary>The depth format of the maps: 16 bits where the device can filter it (half the memory, and enough for cascades up to about a kilometre deep).</summary>
		private Format ShadowFormat()
		{
			if (shadowFormat != Format.Undefined)
			{
				return shadowFormat;
			}

			const FormatFeatureFlags needed = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;
			Format[] candidates = { Format.D16Unorm, Format.D32Sfloat, Format.X8D24UnormPack32 };
			foreach (Format candidate in candidates)
			{
				if ((Features(candidate) & (needed | FormatFeatureFlags.SampledImageFilterLinearBit)) == (needed | FormatFeatureFlags.SampledImageFilterLinearBit))
				{
					return shadowFormat = candidate;
				}
			}

			foreach (Format candidate in candidates)
			{
				if ((Features(candidate) & needed) == needed)
				{
					return shadowFormat = candidate;
				}
			}

			throw new InvalidOperationException("no depth format can be sampled");
		}

		/// <summary>The cascades' render pass: cleared on load, kept, and ready to be read at its end. The frame's fence has already seen the slot's last use of the image.</summary>
		private RenderPass ShadowRenderPass()
		{
			if (shadowRenderPass.Handle != 0)
			{
				return shadowRenderPass;
			}

			AttachmentDescription attachment = new AttachmentDescription
			{
				Format = ShadowFormat(),
				Samples = SampleCountFlags.Count1Bit,
				LoadOp = AttachmentLoadOp.Clear,
				StoreOp = AttachmentStoreOp.Store,
				StencilLoadOp = AttachmentLoadOp.DontCare,
				StencilStoreOp = AttachmentStoreOp.DontCare,
				InitialLayout = ImageLayout.Undefined,
				FinalLayout = ImageLayout.ShaderReadOnlyOptimal
			};
			AttachmentReference depth = new AttachmentReference(0, ImageLayout.DepthStencilAttachmentOptimal);
			SubpassDescription subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, PDepthStencilAttachment = &depth };
			SubpassDependency* dependencies = stackalloc SubpassDependency[2];
			dependencies[0] = new SubpassDependency
			{
				SrcSubpass = Vk.SubpassExternal,
				DstSubpass = 0,
				SrcStageMask = PipelineStageFlags.TopOfPipeBit,
				DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
				SrcAccessMask = 0,
				DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
			};
			dependencies[1] = new SubpassDependency
			{
				SrcSubpass = 0,
				DstSubpass = Vk.SubpassExternal,
				SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
				DstStageMask = PipelineStageFlags.FragmentShaderBit,
				SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
				DstAccessMask = AccessFlags.ShaderReadBit
			};
			RenderPassCreateInfo info = new RenderPassCreateInfo
			{
				SType = StructureType.RenderPassCreateInfo,
				AttachmentCount = 1,
				PAttachments = &attachment,
				SubpassCount = 1,
				PSubpasses = &subpass,
				DependencyCount = 2,
				PDependencies = dependencies
			};
			Check(vk.CreateRenderPass(device, in info, null, out shadowRenderPass), "vkCreateRenderPass");
			return shadowRenderPass;
		}

		// --- framebuffer objects: the shadow maps; anything else (picking, motion blur) is not drawn by this renderer ---

		public void glGenFramebuffers(int n, int[] framebuffers) => GenNames(n, framebuffers);

		public void glDeleteFramebuffers(int n, int[] framebuffers)
		{
			for (int i = 0; i < n && i < framebuffers.Length; i++)
			{
				if (!framebufferObjects.TryGetValue(framebuffers[i], out GLFramebuffer framebuffer))
				{
					continue;
				}

				framebufferObjects.Remove(framebuffers[i]);
				if (boundFramebuffer == framebuffer)
				{
					boundFramebuffer = null;
					currentPass = null;
				}

				ReleaseFramebuffer(framebuffer);
			}
		}

		private void ReleaseFramebuffer(GLFramebuffer framebuffer)
		{
			Framebuffer[] handles = (Framebuffer[])framebuffer.Handles.Clone();
			Array.Clear(framebuffer.Handles, 0, framebuffer.Handles.Length);
			context.Defer(() =>
			{
				foreach (Framebuffer handle in handles)
				{
					if (handle.Handle != 0)
					{
						vk.DestroyFramebuffer(device, handle, null);
					}
				}
			});
		}

		public void glBindFramebuffer(int target, int framebuffer)
		{
			if (framebuffer == 0)
			{
				boundFramebuffer = null;
			}
			else
			{
				if (!framebufferObjects.TryGetValue(framebuffer, out boundFramebuffer))
				{
					boundFramebuffer = new GLFramebuffer();
					framebufferObjects[framebuffer] = boundFramebuffer;
				}
			}

			currentPass = null;
			viewportDirty = true;
		}

		public void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
		{
			const int depthAttachment = 0x8D00;
			if (boundFramebuffer != null && attachment == depthAttachment)
			{
				if (texture != 0 && textures.TryGetValue(texture, out GLTexture depth) && depth.IsDepth)
				{
					boundFramebuffer.Depth = depth;
				}
				else
				{
					boundFramebuffer.Depth = null;
				}

				ReleaseFramebuffer(boundFramebuffer);
			}
		}

		public void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer)
		{
		}

		public int glCheckFramebufferStatus(int target) => GL_FRAMEBUFFER_COMPLETE;

		public void glGenRenderbuffers(int n, int[] renderbuffers) => GenNames(n, renderbuffers);

		public void glDeleteRenderbuffers(int n, int[] renderbuffers)
		{
		}

		public void glBindRenderbuffer(int target, int renderbuffer)
		{
		}

		public void glRenderbufferStorage(int target, int internalFormat, int width, int height)
		{
		}

		public void glDrawBuffers(int n, int[] buffers)
		{
		}

		public void glReadBuffer(int mode)
		{
		}

		public void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border)
		{
			ReportOnce("glCopyTexImage2D (motion blur) is not supported by the Vulkan renderer");
		}

		/// <summary>The bound framebuffer's cascade, begun for this frame if it wasn't; null if it isn't one this renderer can draw into.</summary>
		private GLFramebuffer ShadowPass()
		{
			GLFramebuffer framebuffer = boundFramebuffer;
			if (framebuffer.Depth == null)
			{
				ReportOnce("a framebuffer object without a depth texture is not supported by the Vulkan renderer: drawing into one is skipped");
				return null;
			}

			if (!framebuffer.InFrame)
			{
				if (framePasses.Count == MaxShadowPasses)
				{
					ReportOnce("more than four shadow cascades are not supported by the Vulkan renderer");
					return null;
				}

				// Its image now: the scene drawn after this reads it.
				ShadowFramebuffer(framebuffer, context.FrameSlot);
				framebuffer.InFrame = true;
				framebuffer.RecordCount = 0;
				framePasses.Add(framebuffer);
			}

			currentPass = framebuffer;
			return framebuffer;
		}

		/// <summary>A new frame has no cascades drawn yet.</summary>
		private void ShadowsFrameBegun()
		{
			foreach (GLFramebuffer framebuffer in framePasses)
			{
				framebuffer.InFrame = false;
			}

			framePasses.Clear();
			currentPass = null;
			shadowBlockCurrent[context.FrameSlot] = false;
		}

		// --- depth textures ---

		/// <summary>A depth texture has no image until a framebuffer draws into it: a texture that is only bound (upstream's stand-in for disabled shadows) takes no memory.</summary>
		private void RealizeDepth(GLTexture texture)
		{
			if (texture.DepthImages != null)
			{
				return;
			}

			texture.DepthImages = new DepthImage[VulkanContext.Slots];
			for (int slot = 0; slot < VulkanContext.Slots; slot++)
			{
				ImageCreateInfo info = new ImageCreateInfo
				{
					SType = StructureType.ImageCreateInfo,
					ImageType = ImageType.Type2D,
					Format = ShadowFormat(),
					Extent = new Extent3D((uint)texture.Width, (uint)texture.Height, 1),
					MipLevels = 1,
					ArrayLayers = 1,
					Samples = SampleCountFlags.Count1Bit,
					Tiling = ImageTiling.Optimal,
					Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
					SharingMode = SharingMode.Exclusive,
					InitialLayout = ImageLayout.Undefined
				};
				Check(vk.CreateImage(device, in info, null, out Image image), "vkCreateImage");
				texture.DepthImages[slot].Image = image;
				texture.DepthImages[slot].Memory = imageHeap.AllocateAndBind(image);
				ImageViewCreateInfo view = new ImageViewCreateInfo
				{
					SType = StructureType.ImageViewCreateInfo,
					Image = image,
					ViewType = ImageViewType.Type2D,
					Format = ShadowFormat(),
					SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1)
				};
				Check(vk.CreateImageView(device, in view, null, out texture.DepthImages[slot].View), "vkCreateImageView");
			}
		}

		private void ReleaseDepth(GLTexture texture)
		{
			if (texture.DepthImages == null)
			{
				return;
			}

			DepthImage[] images = texture.DepthImages;
			texture.DepthImages = null;
			context.Defer(() =>
			{
				foreach (DepthImage image in images)
				{
					vk.DestroyImageView(device, image.View, null);
					vk.DestroyImage(device, image.Image, null);
					imageHeap.Free(image.Memory);
				}
			});
		}

		private Framebuffer ShadowFramebuffer(GLFramebuffer framebuffer, int slot)
		{
			if (framebuffer.Handles[slot].Handle != 0)
			{
				return framebuffer.Handles[slot];
			}

			GLTexture depth = framebuffer.Depth;
			RealizeDepth(depth);
			ImageView view = depth.DepthImages[slot].View;
			FramebufferCreateInfo info = new FramebufferCreateInfo
			{
				SType = StructureType.FramebufferCreateInfo,
				RenderPass = ShadowRenderPass(),
				AttachmentCount = 1,
				PAttachments = &view,
				Width = (uint)depth.Width,
				Height = (uint)depth.Height,
				Layers = 1
			};
			Check(vk.CreateFramebuffer(device, in info, null, out framebuffer.Handles[slot]), "vkCreateFramebuffer");
			return framebuffer.Handles[slot];
		}

		// --- the scene reading the maps ---

		/// <summary>Whether a default program face now reads the maps: shadows on, and all the cascades drawn this frame.</summary>
		private bool ShadowsActive()
		{
			if (Unsafe.ReadUnaligned<int>(ref shadowData[ShadowEnabledOffset]) == 0)
			{
				return false;
			}

			int count = Unsafe.ReadUnaligned<int>(ref shadowData[ShadowCountOffset]);
			if (count <= 0 || count > MaxShadowPasses || framePasses.Count < count)
			{
				return false;
			}

			for (int i = 0; i < count; i++)
			{
				GLTexture map = units[ShadowFirstUnit + i];
				if (map == null || map.DepthImages == null)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>This frame's descriptor set of cascades: its block written when it has changed, its maps as bound.</summary>
		private DescriptorSet ShadowSet()
		{
			int slot = context.FrameSlot;
			if (shadowSets[slot].Handle == 0)
			{
				CreateShadowSet(slot);
			}

			if (shadowDataDirty || !shadowBlockCurrent[slot])
			{
				fixed (byte* data = shadowData)
				{
					// The world position from the view space one, per fragment: the inverse only needs working out once a frame.
					Matrix4x4 view = *(Matrix4x4*)(data + ViewOffset);
					*(Matrix4x4*)(data + InverseViewOffset) = Matrix4x4.Invert(view, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
					System.Buffer.MemoryCopy(data, shadowBlocks[slot].Pointer, ShadowBlockSize, ShadowBlockSize);
				}

				shadowDataDirty = false;
				shadowBlockCurrent[slot] = true;
			}

			// Maps the frame's draws read: the slot's last frame has finished (BeginFrame waited for its fence), so the set is free to change.
			bool changed = false;
			for (int i = 0; i < MaxShadowPasses; i++)
			{
				changed |= shadowSetMaps[slot, i] != units[ShadowFirstUnit + i];
			}

			if (changed)
			{
				WriteShadowMaps(slot);
			}

			return shadowSets[slot];
		}

		private void CreateShadowSet(int slot)
		{
			if (shadowPool.Handle == 0)
			{
				DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
				sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, VulkanContext.Slots);
				sizes[1] = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, VulkanContext.Slots * MaxShadowPasses);
				DescriptorPoolCreateInfo poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = VulkanContext.Slots, PoolSizeCount = 2, PPoolSizes = sizes };
				Check(vk.CreateDescriptorPool(device, in poolInfo, null, out shadowPool), "vkCreateDescriptorPool");
				bool linear = (Features(ShadowFormat()) & FormatFeatureFlags.SampledImageFilterLinearBit) != 0;
				SamplerCreateInfo samplerInfo = new SamplerCreateInfo
				{
					SType = StructureType.SamplerCreateInfo,
					MagFilter = linear ? Filter.Linear : Filter.Nearest,
					MinFilter = linear ? Filter.Linear : Filter.Nearest,
					MipmapMode = SamplerMipmapMode.Nearest,
					AddressModeU = SamplerAddressMode.ClampToBorder,
					AddressModeV = SamplerAddressMode.ClampToBorder,
					AddressModeW = SamplerAddressMode.ClampToBorder,
					CompareEnable = true,
					CompareOp = CompareOp.LessOrEqual,
					MaxLod = 0.0f,
					// Anything outside a map reads as fully lit.
					BorderColor = BorderColor.FloatOpaqueWhite
				};
				Check(vk.CreateSampler(device, in samplerInfo, null, out shadowSampler), "vkCreateSampler");
			}

			shadowBlocks[slot] = bufferHeap.Allocate(ShadowBlockSize, uniformAlignment);
			DescriptorSetLayout layout = shadowLayout;
			DescriptorSetAllocateInfo allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = shadowPool, DescriptorSetCount = 1, PSetLayouts = &layout };
			Check(vk.AllocateDescriptorSets(device, in allocate, out shadowSets[slot]), "vkAllocateDescriptorSets");
			DescriptorBufferInfo buffer = new DescriptorBufferInfo(shadowBlocks[slot].Buffer, shadowBlocks[slot].Offset, ShadowBlockSize);
			WriteDescriptorSet write = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = shadowSets[slot],
				DstBinding = 0,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.UniformBuffer,
				PBufferInfo = &buffer
			};
			vk.UpdateDescriptorSets(device, 1, &write, 0, null);
		}

		private void WriteShadowMaps(int slot)
		{
			// A cascade not in use (fewer than four) still needs a valid image: never read, so the first's will do.
			GLTexture first = units[ShadowFirstUnit];
			DescriptorImageInfo* images = stackalloc DescriptorImageInfo[MaxShadowPasses];
			WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[MaxShadowPasses];
			for (int i = 0; i < MaxShadowPasses; i++)
			{
				GLTexture map = units[ShadowFirstUnit + i];
				shadowSetMaps[slot, i] = map;
				if (map == null || map.DepthImages == null)
				{
					map = first;
				}

				images[i] = new DescriptorImageInfo(shadowSampler, map.DepthImages[slot].View, ImageLayout.ShaderReadOnlyOptimal);
				writes[i] = new WriteDescriptorSet
				{
					SType = StructureType.WriteDescriptorSet,
					DstSet = shadowSets[slot],
					DstBinding = (uint)(1 + i),
					DescriptorCount = 1,
					DescriptorType = DescriptorType.CombinedImageSampler,
					PImageInfo = &images[i]
				};
			}

			vk.UpdateDescriptorSets(device, MaxShadowPasses, writes, 0, null);
		}

		// --- the frame's cascades, before its render pass ---

		private sealed class ShadowRun
		{
			internal DrawRecord[] Records;
			internal int Count;
			internal int Chunks;
			internal int PerChunk;
			internal int FirstChunk;
			internal Framebuffer Framebuffer;
			internal Extent2D Extent;
		}

		/// <summary>On the render thread, at the swap: the frame's cascades as the submitter is to run them.</summary>
		private ShadowRun[] TakeShadowRuns(int slot)
		{
			if (framePasses.Count == 0)
			{
				return null;
			}

			ShadowRun[] runs = new ShadowRun[framePasses.Count];
			for (int i = 0; i < runs.Length; i++)
			{
				GLFramebuffer pass = framePasses[i];
				int chunks = pass.RecordCount == 0 ? 0 : Math.Max(1, Math.Min(ShadowChunksPerPass, pass.RecordCount / RecordsPerChunk));
				runs[i] = new ShadowRun
				{
					Records = pass.Records,
					Count = pass.RecordCount,
					Chunks = chunks,
					PerChunk = chunks == 0 ? 0 : (pass.RecordCount + chunks - 1) / chunks,
					FirstChunk = MaxChunks + i * ShadowChunksPerPass,
					Framebuffer = ShadowFramebuffer(pass, slot),
					Extent = new Extent2D((uint)pass.Depth.Width, (uint)pass.Depth.Height)
				};
				for (int chunk = 0; chunk < chunks; chunk++)
				{
					EnsureChunkBuffer(slot, runs[i].FirstChunk + chunk);
				}
			}

			return runs;
		}

		/// <summary>On the submitter: the cascades' chunks recorded, and each cascade's render pass, before the frame's own.</summary>
		private void RunShadowPasses(CommandBuffer commands, int slot, ShadowRun[] runs, Vector4 clip)
		{
			int total = 0;
			foreach (ShadowRun run in runs)
			{
				total += run.Chunks;
			}

			if (total > 0)
			{
				Action<int> record = index =>
				{
					int remaining = index;
					foreach (ShadowRun run in runs)
					{
						if (remaining < run.Chunks)
						{
							RecordChunk(slot, run.FirstChunk + remaining, run.Records, remaining * run.PerChunk, Math.Min(run.Count, (remaining + 1) * run.PerChunk), clip, run.Framebuffer, true, run.Extent);
							return;
						}

						remaining -= run.Chunks;
					}
				};
				if (total == 1 || workers == null)
				{
					for (int index = 0; index < total; index++)
					{
						record(index);
					}
				}
				else
				{
					workers.Run(total, record);
				}
			}

			ClearValue clear = new ClearValue(default, new ClearDepthStencilValue(1.0f, 0));
			CommandBuffer* buffers = stackalloc CommandBuffer[ShadowChunksPerPass];
			foreach (ShadowRun run in runs)
			{
				RenderPassBeginInfo pass = new RenderPassBeginInfo
				{
					SType = StructureType.RenderPassBeginInfo,
					RenderPass = shadowRenderPass,
					Framebuffer = run.Framebuffer,
					RenderArea = new Rect2D(new Offset2D(0, 0), run.Extent),
					ClearValueCount = 1,
					PClearValues = &clear
				};
				vk.CmdBeginRenderPass(commands, in pass, run.Chunks > 0 ? SubpassContents.SecondaryCommandBuffers : SubpassContents.Inline);
				if (run.Chunks > 0)
				{
					for (int chunk = 0; chunk < run.Chunks; chunk++)
					{
						buffers[chunk] = chunkBuffers[slot, run.FirstChunk + chunk];
					}

					vk.CmdExecuteCommands(commands, (uint)run.Chunks, buffers);
				}

				vk.CmdEndRenderPass(commands);
			}
		}

		// --- faces handed over directly (AndroidRenderer.NativeShadows) ---

		private readonly byte[] shadowFaceBlock = new byte[DepthBlockSize];
		private bool shadowFaceBlockDirty = true;
		private ulong shadowFaceBlockSerial;
		private BufferHeap.Block shadowFaceBlockRing;
		private uint shadowFaceBlockOffset;

		/// <summary>Whether the draw being prepared has no fragment shader: nothing about it is see-through.</summary>
		private bool shadowDepthOnly;

		/// <summary>The cascade's light space matrix, for the faces that follow: 16 floats, as upstream sends it.</summary>
		public void SetShadowCascade(ReadOnlySpan<float> light)
		{
			fixed (byte* block = shadowFaceBlock)
			{
				float* target = (float*)block;
				for (int i = 0; i < 16; i++)
				{
					target[i] = light[i];
				}
			}

			shadowFaceBlockDirty = true;
		}

		/// <summary>The object's model and texture matrices, for the faces that follow.</summary>
		public void SetShadowObject(ReadOnlySpan<float> model, ReadOnlySpan<float> texture)
		{
			fixed (byte* block = shadowFaceBlock)
			{
				float* m = (float*)(block + 64), t = (float*)(block + 128);
				for (int i = 0; i < 16; i++)
				{
					m[i] = model[i];
					t[i] = texture[i];
				}
			}

			shadowFaceBlockDirty = true;
		}

		/// <summary>
		/// Draws a range of a vertex array's indices into the bound cascade, with GL's current culling and the bound animation
		/// matrices. A texture name of 0 is a face with nothing see-through, drawn without a fragment shader.
		/// </summary>
		public void DrawShadowFace(int vertexArrayName, int textureName, int mode, bool intIndices, int firstIndex, int count, float materialAlpha, int materialFlags)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			GLProgram program = currentProgram;
			if (program == null || program.Kind != ProgramKind.ShadowDepth || !vertexArrays.TryGetValue(vertexArrayName, out GLVertexArray array))
			{
				return;
			}

			GLBuffer elements = array.Elements;
			if (elements == null || elements.Memory.IsNull)
			{
				return;
			}

			GLTexture texture = null;
			if (textureName != 0)
			{
				textures.TryGetValue(textureName, out texture);
			}

			ref byte block = ref shadowFaceBlock[0];
			int hasTexture = texture != null ? 1 : 0;
			if (Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref block, 192)) != hasTexture || Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref block, 200)) != materialAlpha || Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref block, 204)) != materialFlags)
			{
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 192), hasTexture);
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 196), 0.5f);
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 200), materialAlpha);
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, 204), materialFlags);
				shadowFaceBlockDirty = true;
			}

			int drawMode = mode == GL_POLYGON || mode == GL_QUADS ? GL_TRIANGLE_FAN : mode == GL_QUAD_STRIP ? GL_TRIANGLE_STRIP : mode;
			shadowDepthOnly = texture == null;
			bool prepared = PrepareDraw(drawMode, array, texture, shadowFaceBlock);
			shadowDepthOnly = false;
			if (prepared)
			{
				AddDraw((uint)count, (uint)firstIndex, elements, intIndices ? IndexType.Uint32 : IndexType.Uint16, mode == GL_QUADS);
			}

			drawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
		}

		private void DisposeShadows()
		{
			foreach (GLFramebuffer framebuffer in framebufferObjects.Values)
			{
				foreach (Framebuffer handle in framebuffer.Handles)
				{
					if (handle.Handle != 0)
					{
						vk.DestroyFramebuffer(device, handle, null);
					}
				}
			}

			framebufferObjects.Clear();
			foreach (ShaderModule module in new[] { shadowDepthVertex, shadowDepthFragment, defaultShadowVertex, defaultShadowFragment })
			{
				vk.DestroyShaderModule(device, module, null);
			}

			if (shadowRenderPass.Handle != 0)
			{
				vk.DestroyRenderPass(device, shadowRenderPass, null);
			}

			if (shadowPool.Handle != 0)
			{
				vk.DestroySampler(device, shadowSampler, null);
				vk.DestroyDescriptorPool(device, shadowPool, null);
			}

			vk.DestroyDescriptorSetLayout(device, shadowLayout, null);
		}
	}
}
