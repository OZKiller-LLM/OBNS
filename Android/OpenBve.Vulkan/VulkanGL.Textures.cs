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
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * Textures. glTexImage2D copies the image (converted to a Vulkan format where it differs)
	 * into staging memory; the image itself is made when the texture is first drawn with, by
	 * then knowing whether glGenerateMipmap asked for mipmaps (upstream asks straight after every
	 * upload). The copy and the mipmap blits go in the frame's upload commands, which run before
	 * its drawing. A texture is drawn through its own descriptor set (image and sampler); GL's
	 * sampler state lives on the texture, so a change of it makes a new set.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int Units = 8;
		private const int GL_TEXTURE_MIN_FILTER = 0x2801, GL_TEXTURE_MAG_FILTER = 0x2800, GL_TEXTURE_WRAP_S = 0x2802, GL_TEXTURE_WRAP_T = 0x2803, GL_TEXTURE_MAX_ANISOTROPY = 0x84FE;
		private const int GL_NEAREST = 0x2600, GL_LINEAR = 0x2601;
		private const int GL_NEAREST_MIPMAP_NEAREST = 0x2700, GL_LINEAR_MIPMAP_NEAREST = 0x2701, GL_NEAREST_MIPMAP_LINEAR = 0x2702, GL_LINEAR_MIPMAP_LINEAR = 0x2703;
		private const int GL_REPEAT = 0x2901, GL_MIRRORED_REPEAT = 0x8370;
		private const int GL_TEXTURE_SWIZZLE_R = 0x8E42, GL_TEXTURE_SWIZZLE_RGBA = 0x8E46;
		private const int GL_GREEN = 0x1904, GL_BLUE = 0x1905;
		private const int GL_ALPHA = 0x1906, GL_RGB = 0x1907, GL_RGBA = 0x1908, GL_LUMINANCE = 0x1909, GL_LUMINANCE_ALPHA = 0x190A, GL_RED = 0x1903, GL_RG = 0x8227;
		private const int GL_UNSIGNED_BYTE = 0x1401, GL_UNSIGNED_SHORT_5_6_5 = 0x8363, GL_UNSIGNED_SHORT_4_4_4_4 = 0x8033, GL_UNSIGNED_SHORT_5_5_5_1 = 0x8034;

		private sealed class GLTexture
		{
			internal int MinFilter = GL_NEAREST_MIPMAP_LINEAR, MagFilter = GL_LINEAR, WrapS = GL_REPEAT, WrapT = GL_REPEAT;
			internal float Anisotropy = 1.0f;

			// Level 0, waiting to be uploaded.
			internal BufferHeap.Allocation Staging;
			internal bool Pending;
			internal bool WantMips;
			internal int Width, Height;
			internal Format Format;
			internal ComponentMapping Swizzle;
			// GL's own swizzle (GL_TEXTURE_SWIZZLE_R..A), on top of the format's; null for none.
			internal int[] GLSwizzle;

			// On the GPU.
			internal Image Image;
			internal ImageHeap.Allocation Memory;
			internal ImageView View;
			internal bool HasMips;
			internal DescriptorSet Set;
			internal DescriptorPool SetPool;
			internal int SetKey = -1;

			// A shadow map (VulkanGL.Shadows): no image until a framebuffer draws into it, then one per frame slot.
			internal bool IsDepth;
			internal DepthImage[] DepthImages;
		}

		private readonly Dictionary<int, GLTexture> textures = new Dictionary<int, GLTexture>();

		/*
		 * Textures given an image but not drawn with yet hold it in staging memory. Upstream loads
		 * textures ahead of drawing them (TML's exterior: hundreds at once), so past a budget the
		 * waiting ones are made there and then - each has had its glGenerateMipmap by the time the
		 * next texture's image comes - and their staging goes with their uploads.
		 */
		private readonly List<GLTexture> waitingTextures = new List<GLTexture>();
		private ulong waitingBytes;

		private void RealizeWaiting()
		{
			if (waitingBytes <= StagingBudget)
			{
				return;
			}

			if (!context.InFrame)
			{
				// Between frames the submitter may be recording the last one, from the same command pool.
				WaitSubmitter();
			}

			foreach (GLTexture waiting in waitingTextures)
			{
				if (waiting.Pending)
				{
					Realize(waiting);
				}
			}

			waitingTextures.Clear();
			waitingBytes = 0;
		}
		private readonly GLTexture[] units = new GLTexture[Units];
		private int activeUnit;

		/// <summary>What an unbound or empty texture samples as in GL: opaque black.</summary>
		private GLTexture blackTexture;

		private readonly Dictionary<int, Sampler> samplers = new Dictionary<int, Sampler>();
		private readonly Dictionary<Format, FormatFeatureFlags> formatFeatures = new Dictionary<Format, FormatFeatureFlags>();
		private readonly List<DescriptorPool> texturePools = new List<DescriptorPool>();
		private DescriptorPool currentTexturePool;

		public void glGenTextures(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				textures[names[i]] = new GLTexture();
			}
		}

		public void glDeleteTextures(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (names[i] == 0 || !textures.TryGetValue(names[i], out GLTexture texture))
				{
					continue;
				}

				textures.Remove(names[i]);
				for (int unit = 0; unit < Units; unit++)
				{
					if (units[unit] == texture)
					{
						units[unit] = null;
					}
				}

				ReleaseTexture(texture);
			}
		}

		/// <summary>Frees a texture's GPU objects once the GPU is done with them, and its staging now.</summary>
		private void ReleaseTexture(GLTexture texture)
		{
			if (texture.Pending)
			{
				// Never recorded into a command: free now.
				bufferHeap.Free(texture.Staging);
				texture.Staging = default;
				texture.Pending = false;
			}

			ReleaseDepth(texture);
			ReleaseImage(texture);
		}

		private void ReleaseImage(GLTexture texture)
		{
			if (texture.Image.Handle == 0)
			{
				return;
			}

			Image image = texture.Image;
			ImageView view = texture.View;
			ImageHeap.Allocation memory = texture.Memory;
			DescriptorSet set = texture.Set;
			DescriptorPool pool = texture.SetPool;
			context.Defer(() =>
			{
				if (set.Handle != 0)
				{
					vk.FreeDescriptorSets(device, pool, 1, in set);
				}

				vk.DestroyImageView(device, view, null);
				vk.DestroyImage(device, image, null);
				imageHeap.Free(memory);
			});
			texture.Image = default;
			texture.View = default;
			texture.Memory = default;
			texture.Set = default;
			texture.SetPool = default;
			texture.SetKey = -1;
			texture.HasMips = false;
		}

		public void glBindTexture(int target, int texture)
		{
			if (target != GL_TEXTURE_2D)
			{
				return;
			}

			GLTexture bound = null;
			if (texture != 0 && !textures.TryGetValue(texture, out bound))
			{
				bound = new GLTexture();
				textures[texture] = bound;
			}

			units[activeUnit] = bound;
		}

		public void glActiveTexture(int texture)
		{
			activeUnit = Math.Max(0, Math.Min(Units - 1, texture - GL_TEXTURE0));
		}

		public void glGenerateMipmap(int target)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null)
			{
				return;
			}

			if (texture.Pending)
			{
				texture.WantMips = true;
			}
			else if (texture.Image.Handle != 0 && !texture.HasMips)
			{
				ReportOnce("glGenerateMipmap on a texture already drawn with is not supported by the Vulkan renderer");
			}
		}

		public void glTexParameteri(int target, int pname, int param) => TexParameter(pname, param);

		public void glTexParameterf(int target, int pname, float param) => TexParameter(pname, param);

		public void glTexParameteriv(int target, int pname, int[] parameters)
		{
			if (pname == GL_TEXTURE_SWIZZLE_RGBA && parameters.Length >= 4)
			{
				for (int i = 0; i < 4; i++)
				{
					TexParameter(GL_TEXTURE_SWIZZLE_R + i, parameters[i]);
				}

				return;
			}

			if (parameters.Length > 0)
			{
				TexParameter(pname, parameters[0]);
			}
		}

		public void glTexParameterfv(int target, int pname, float[] parameters)
		{
			if (parameters.Length > 0)
			{
				TexParameter(pname, parameters[0]);
			}
		}

		private void TexParameter(int pname, float value)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null)
			{
				return;
			}

			switch (pname)
			{
				case GL_TEXTURE_MIN_FILTER:
					texture.MinFilter = (int)value;
					break;
				case GL_TEXTURE_MAG_FILTER:
					texture.MagFilter = (int)value;
					break;
				case GL_TEXTURE_WRAP_S:
					texture.WrapS = (int)value;
					break;
				case GL_TEXTURE_WRAP_T:
					texture.WrapT = (int)value;
					break;
				case GL_TEXTURE_MAX_ANISOTROPY:
					texture.Anisotropy = Math.Max(1.0f, value);
					break;
				case GL_TEXTURE_SWIZZLE_R:
				case GL_TEXTURE_SWIZZLE_R + 1:
				case GL_TEXTURE_SWIZZLE_R + 2:
				case GL_TEXTURE_SWIZZLE_R + 3:
					// Upstream 1.14.1.2 shows R8 grayscale as (R, R, R, 1), set after the upload.
					if (texture.Image.Handle != 0 && !texture.Pending)
					{
						ReportOnce("a texture swizzle set on a texture already drawn with is not supported by the Vulkan renderer");
					}

					texture.GLSwizzle = texture.GLSwizzle ?? new[] { GL_RED, GL_GREEN, GL_BLUE, GL_ALPHA };
					texture.GLSwizzle[pname - GL_TEXTURE_SWIZZLE_R] = (int)value;
					break;
			}
		}

		/// <summary>The view's components: GL's swizzle (if any) read through the format's own.</summary>
		private static ComponentMapping ViewComponents(GLTexture texture)
		{
			if (texture.GLSwizzle == null)
			{
				return texture.Swizzle;
			}

			ComponentSwizzle[] format =
			{
				Resolve(texture.Swizzle.R, ComponentSwizzle.R), Resolve(texture.Swizzle.G, ComponentSwizzle.G),
				Resolve(texture.Swizzle.B, ComponentSwizzle.B), Resolve(texture.Swizzle.A, ComponentSwizzle.A)
			};
			ComponentSwizzle Pick(int gl)
			{
				switch (gl)
				{
					case GL_RED: return format[0];
					case GL_GREEN: return format[1];
					case GL_BLUE: return format[2];
					case GL_ALPHA: return format[3];
					case GL_ONE: return ComponentSwizzle.One;
					default: return ComponentSwizzle.Zero;
				}
			}

			return new ComponentMapping(Pick(texture.GLSwizzle[0]), Pick(texture.GLSwizzle[1]), Pick(texture.GLSwizzle[2]), Pick(texture.GLSwizzle[3]));
		}

		private static ComponentSwizzle Resolve(ComponentSwizzle swizzle, ComponentSwizzle identity) => swizzle == ComponentSwizzle.Identity ? identity : swizzle;

		/// <summary>Replaces a texture's image: only the whole of level 0 (an animated GIF's next frame), as a new upload.</summary>
		public void glTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null || target != GL_TEXTURE_2D)
			{
				return;
			}

			if (level != 0 || xoffset != 0 || yoffset != 0 || width != texture.Width || height != texture.Height)
			{
				ReportOnce("glTexSubImage2D other than the whole of level 0 is not supported by the Vulkan renderer");
				return;
			}

			glTexImage2D(target, level, format, width, height, 0, format, type, pixels);
		}

		public void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels)
		{
			GLTexture texture = units[activeUnit];
			if (texture == null || target != GL_TEXTURE_2D)
			{
				return;
			}

			if (level != 0)
			{
				ReportOnce("texture levels other than 0 are not supported by the Vulkan renderer");
				return;
			}

			if (width <= 0 || height <= 0)
			{
				return;
			}

			if (format == GL_DEPTH_COMPONENT)
			{
				ReleaseTexture(texture);
				texture.IsDepth = true;
				texture.Width = width;
				texture.Height = height;
				return;
			}

			RealizeWaiting();
			if (texture.Pending)
			{
				bufferHeap.Free(texture.Staging);
				texture.Staging = default;
			}

			texture.Staging = Convert(format, type, width, height, pixels, out texture.Format, out texture.Swizzle);
			texture.Width = width;
			texture.Height = height;
			texture.Pending = true;
			texture.WantMips = false;
			waitingTextures.Add(texture);
			waitingBytes += texture.Staging.Size;
		}

		/// <summary>The format's optimal tiling features (cached).</summary>
		private FormatFeatureFlags Features(Format format)
		{
			if (!formatFeatures.TryGetValue(format, out FormatFeatureFlags features))
			{
				vk.GetPhysicalDeviceFormatProperties(context.PhysicalDevice, format, out FormatProperties properties);
				features = properties.OptimalTilingFeatures;
				formatFeatures[format] = features;
			}

			return features;
		}

		private bool Sampleable(Format format) => (Features(format) & (FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit)) ==
		                                          (FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit);

		private static readonly ComponentMapping Identity = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity);
		private static readonly ComponentMapping SwapRedBlue = new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.A);

		/// <summary>
		/// Copies a GL image into staging memory in a format Vulkan samples the same way: tightly
		/// packed rows, RGB widened to RGBA, luminance and alpha as one- and two-channel images read
		/// through a swizzle, 16-bit packings kept where the device has them.
		/// </summary>
		private BufferHeap.Allocation Convert(int format, int type, int width, int height, IntPtr pixels, out Format vkFormat, out ComponentMapping swizzle)
		{
			int sourceBytes;
			int targetBytes;
			swizzle = Identity;
			// 0: copy; 1: RGB to RGBA; 2: 5551 to RGBA8
			int conversion = 0;
			if (type == GL_UNSIGNED_BYTE)
			{
				switch (format)
				{
					case GL_RGBA:
						vkFormat = Format.R8G8B8A8Unorm;
						sourceBytes = targetBytes = 4;
						break;
					case GL_RGB:
						vkFormat = Format.R8G8B8A8Unorm;
						sourceBytes = 3;
						targetBytes = 4;
						conversion = 1;
						break;
					case GL_LUMINANCE:
						vkFormat = Format.R8Unorm;
						sourceBytes = targetBytes = 1;
						swizzle = new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.One);
						break;
					case GL_LUMINANCE_ALPHA:
						vkFormat = Format.R8G8Unorm;
						sourceBytes = targetBytes = 2;
						swizzle = new ComponentMapping(ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.R, ComponentSwizzle.G);
						break;
					case GL_ALPHA:
						vkFormat = Format.R8Unorm;
						sourceBytes = targetBytes = 1;
						swizzle = new ComponentMapping(ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.R);
						break;
					case GL_RED:
						vkFormat = Format.R8Unorm;
						sourceBytes = targetBytes = 1;
						break;
					case GL_RG:
						vkFormat = Format.R8G8Unorm;
						sourceBytes = targetBytes = 2;
						break;
					default:
						return Blank(format, type, width, height, out vkFormat, out swizzle);
				}
			}
			else if (type == GL_UNSIGNED_SHORT_5_6_5)
			{
				vkFormat = Format.R5G6B5UnormPack16;
				sourceBytes = targetBytes = 2;
			}
			else if (type == GL_UNSIGNED_SHORT_4_4_4_4)
			{
				sourceBytes = targetBytes = 2;
				if (Sampleable(Format.R4G4B4A4UnormPack16))
				{
					vkFormat = Format.R4G4B4A4UnormPack16;
				}
				else
				{
					// The same bits read as B4G4R4A4 (which every device has), red and blue swapped back.
					vkFormat = Format.B4G4R4A4UnormPack16;
					swizzle = SwapRedBlue;
				}
			}
			else if (type == GL_UNSIGNED_SHORT_5_5_5_1)
			{
				sourceBytes = 2;
				targetBytes = 2;
				if (Sampleable(Format.R5G5B5A1UnormPack16))
				{
					vkFormat = Format.R5G5B5A1UnormPack16;
				}
				else if (Sampleable(Format.B5G5R5A1UnormPack16))
				{
					vkFormat = Format.B5G5R5A1UnormPack16;
					swizzle = SwapRedBlue;
				}
				else
				{
					vkFormat = Format.R8G8B8A8Unorm;
					targetBytes = 4;
					conversion = 2;
				}
			}
			else
			{
				return Blank(format, type, width, height, out vkFormat, out swizzle);
			}

			ulong size = (ulong)width * (ulong)height * (ulong)targetBytes;
			BufferHeap.Allocation staging = bufferHeap.Allocate(size, 16);
			byte* target = staging.Pointer;
			if (pixels == IntPtr.Zero)
			{
				new Span<byte>(target, (int)size).Clear();
				return staging;
			}

			int sourceStride = (width * sourceBytes + unpackAlignment - 1) / unpackAlignment * unpackAlignment;
			int targetStride = width * targetBytes;
			byte* source = (byte*)pixels;
			for (int y = 0; y < height; y++)
			{
				byte* from = source + (long)y * sourceStride;
				byte* to = target + (long)y * targetStride;
				switch (conversion)
				{
					case 0:
						System.Buffer.MemoryCopy(from, to, targetStride, targetStride);
						break;
					case 1:
						for (int x = 0; x < width; x++)
						{
							to[0] = from[0];
							to[1] = from[1];
							to[2] = from[2];
							to[3] = 255;
							from += 3;
							to += 4;
						}

						break;
					default:
						for (int x = 0; x < width; x++)
						{
							int value = from[0] | from[1] << 8;
							to[0] = (byte)(((value >> 11) & 31) * 255 / 31);
							to[1] = (byte)(((value >> 6) & 31) * 255 / 31);
							to[2] = (byte)(((value >> 1) & 31) * 255 / 31);
							to[3] = (byte)((value & 1) * 255);
							from += 2;
							to += 4;
						}

						break;
				}
			}

			return staging;
		}

		/// <summary>A format this renderer does not draw with (depth, float): a black image of the size, so the texture stays valid.</summary>
		private BufferHeap.Allocation Blank(int format, int type, int width, int height, out Format vkFormat, out ComponentMapping swizzle)
		{
			ReportOnce("texture format 0x" + format.ToString("X") + "/0x" + type.ToString("X") + " is not supported by the Vulkan renderer: drawn black");
			vkFormat = Format.R8G8B8A8Unorm;
			swizzle = new ComponentMapping(ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.Zero, ComponentSwizzle.One);
			BufferHeap.Allocation staging = bufferHeap.Allocate((ulong)width * (ulong)height * 4, 16);
			new Span<byte>(staging.Pointer, width * height * 4).Clear();
			return staging;
		}

		/// <summary>Makes a pending texture's image: copies level 0 and generates the mipmaps, in the upload commands.</summary>
		private void Realize(GLTexture texture)
		{
			ReleaseImage(texture);
			FormatFeatureFlags features = Features(texture.Format);
			bool canBlit = (features & (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit)) == (FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit);
			uint levels = 1;
			if (texture.WantMips && canBlit)
			{
				int largest = Math.Max(texture.Width, texture.Height);
				while ((largest >>= 1) > 0)
				{
					levels++;
				}
			}

			ImageCreateInfo info = new ImageCreateInfo
			{
				SType = StructureType.ImageCreateInfo,
				ImageType = ImageType.Type2D,
				Format = texture.Format,
				Extent = new Extent3D((uint)texture.Width, (uint)texture.Height, 1),
				MipLevels = levels,
				ArrayLayers = 1,
				Samples = SampleCountFlags.Count1Bit,
				Tiling = ImageTiling.Optimal,
				Usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | (levels > 1 ? ImageUsageFlags.TransferSrcBit : 0),
				SharingMode = SharingMode.Exclusive,
				InitialLayout = ImageLayout.Undefined
			};
			Check(vk.CreateImage(device, in info, null, out Image image), "vkCreateImage");
			texture.Image = image;
			texture.Memory = imageHeap.AllocateAndBind(image);

			CommandBuffer commands = context.BeginUploads();
			Barrier(commands, image, 0, levels, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit,
				PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
			BufferImageCopy copy = new BufferImageCopy
			{
				BufferOffset = texture.Staging.Offset,
				ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
				ImageExtent = new Extent3D((uint)texture.Width, (uint)texture.Height, 1)
			};
			vk.CmdCopyBufferToImage(commands, texture.Staging.Buffer, image, ImageLayout.TransferDstOptimal, 1, &copy);
			if (levels > 1)
			{
				Filter filter = (features & FormatFeatureFlags.SampledImageFilterLinearBit) != 0 ? Filter.Linear : Filter.Nearest;
				int width = texture.Width, height = texture.Height;
				for (uint level = 1; level < levels; level++)
				{
					Barrier(commands, image, level - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit,
						PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
					int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
					ImageBlit blit = new ImageBlit
					{
						SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level - 1, 0, 1),
						DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, level, 0, 1)
					};
					blit.SrcOffsets.Element1 = new Offset3D(width, height, 1);
					blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);
					vk.CmdBlitImage(commands, image, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal, 1, &blit, filter);
					width = nextWidth;
					height = nextHeight;
				}

				Barrier(commands, image, 0, levels - 1, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit,
					PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
				Barrier(commands, image, levels - 1, 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
					PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
			}
			else
			{
				Barrier(commands, image, 0, 1, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit,
					PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);
			}

			ImageViewCreateInfo viewInfo = new ImageViewCreateInfo
			{
				SType = StructureType.ImageViewCreateInfo,
				Image = image,
				ViewType = ImageViewType.Type2D,
				Format = texture.Format,
				Components = ViewComponents(texture),
				SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, levels, 0, 1)
			};
			Check(vk.CreateImageView(device, in viewInfo, null, out texture.View), "vkCreateImageView");

			stagingRecorded.Add(texture.Staging);
			StagingAdded(texture.Staging.Size);
			texture.Staging = default;
			texture.Pending = false;
			texture.HasMips = levels > 1;
			texture.SetKey = -1;
		}

		private void Barrier(CommandBuffer commands, Image image, uint firstLevel, uint levels, ImageLayout from, ImageLayout to, AccessFlags fromAccess, AccessFlags toAccess,
			PipelineStageFlags fromStage, PipelineStageFlags toStage)
		{
			ImageMemoryBarrier barrier = new ImageMemoryBarrier
			{
				SType = StructureType.ImageMemoryBarrier,
				SrcAccessMask = fromAccess,
				DstAccessMask = toAccess,
				OldLayout = from,
				NewLayout = to,
				SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
				DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
				Image = image,
				SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, firstLevel, levels, 0, 1)
			};
			vk.CmdPipelineBarrier(commands, fromStage, toStage, 0, 0, null, 0, null, 1, &barrier);
		}

		/// <summary>The texture's sampler state as a number: filters, wrapping, anisotropy and whether it has mipmaps.</summary>
		private int SamplerKey(GLTexture texture)
		{
			int min;
			switch (texture.MinFilter)
			{
				case GL_NEAREST:
					min = 0;
					break;
				case GL_LINEAR:
					min = 1;
					break;
				case GL_NEAREST_MIPMAP_NEAREST:
					min = 2;
					break;
				case GL_LINEAR_MIPMAP_NEAREST:
					min = 3;
					break;
				case GL_NEAREST_MIPMAP_LINEAR:
					min = 4;
					break;
				default:
					min = 5;
					break;
			}

			int anisotropy = texture.HasMips ? Math.Min(16, (int)Math.Round(Math.Min(texture.Anisotropy, context.MaxAnisotropy))) : 1;
			return min | (texture.MagFilter == GL_NEAREST ? 0 : 1) << 3 | Wrap(texture.WrapS) << 4 | Wrap(texture.WrapT) << 6 | anisotropy << 8 | (texture.HasMips ? 1 : 0) << 13;
		}

		private static int Wrap(int mode) => mode == GL_REPEAT ? 0 : mode == GL_MIRRORED_REPEAT ? 2 : 1;

		private Sampler GetSampler(int key)
		{
			if (samplers.TryGetValue(key, out Sampler sampler))
			{
				return sampler;
			}

			int min = key & 7;
			bool mips = (key >> 13 & 1) != 0 && min >= 2;
			int anisotropy = key >> 8 & 31;
			SamplerAddressMode[] modes = { SamplerAddressMode.Repeat, SamplerAddressMode.ClampToEdge, SamplerAddressMode.MirroredRepeat };
			SamplerCreateInfo info = new SamplerCreateInfo
			{
				SType = StructureType.SamplerCreateInfo,
				MagFilter = (key >> 3 & 1) != 0 ? Filter.Linear : Filter.Nearest,
				MinFilter = min == 1 || min == 3 || min == 5 ? Filter.Linear : Filter.Nearest,
				MipmapMode = min == 4 || min == 5 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
				AddressModeU = modes[key >> 4 & 3],
				AddressModeV = modes[key >> 6 & 3],
				AddressModeW = SamplerAddressMode.Repeat,
				AnisotropyEnable = mips && anisotropy > 1,
				MaxAnisotropy = Math.Max(1, anisotropy),
				MinLod = 0.0f,
				// GL's non-mipmap minification filters read level 0 only.
				MaxLod = mips ? Vk.LodClampNone : 0.0f,
				BorderColor = BorderColor.FloatOpaqueBlack
			};
			Check(vk.CreateSampler(device, in info, null, out sampler), "vkCreateSampler");
			samplers[key] = sampler;
			return sampler;
		}

		/// <summary>The descriptor set to draw a texture with: realized and up to date with its sampler state.</summary>
		private DescriptorSet TextureSet(GLTexture texture)
		{
			if (texture == null || texture.IsDepth || (!texture.Pending && texture.Image.Handle == 0))
			{
				texture = blackTexture;
			}

			if (texture.Pending)
			{
				Realize(texture);
			}

			int key = SamplerKey(texture);
			if (texture.SetKey == key && texture.Set.Handle != 0)
			{
				return texture.Set;
			}

			if (texture.Set.Handle != 0)
			{
				DescriptorSet old = texture.Set;
				DescriptorPool oldPool = texture.SetPool;
				context.Defer(() => vk.FreeDescriptorSets(device, oldPool, 1, in old));
			}

			texture.Set = AllocateTextureSet(out texture.SetPool);
			texture.SetKey = key;
			DescriptorImageInfo image = new DescriptorImageInfo(GetSampler(key), texture.View, ImageLayout.ShaderReadOnlyOptimal);
			WriteDescriptorSet write = new WriteDescriptorSet
			{
				SType = StructureType.WriteDescriptorSet,
				DstSet = texture.Set,
				DstBinding = 0,
				DescriptorCount = 1,
				DescriptorType = DescriptorType.CombinedImageSampler,
				PImageInfo = &image
			};
			vk.UpdateDescriptorSets(device, 1, &write, 0, null);
			return texture.Set;
		}

		private DescriptorSet AllocateTextureSet(out DescriptorPool pool)
		{
			for (int attempt = 0; attempt < 2; attempt++)
			{
				if (currentTexturePool.Handle == 0)
				{
					DescriptorPoolSize size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1024);
					DescriptorPoolCreateInfo info = new DescriptorPoolCreateInfo
					{
						SType = StructureType.DescriptorPoolCreateInfo,
						Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
						MaxSets = 1024,
						PoolSizeCount = 1,
						PPoolSizes = &size
					};
					Check(vk.CreateDescriptorPool(device, in info, null, out currentTexturePool), "vkCreateDescriptorPool");
					texturePools.Add(currentTexturePool);
				}

				DescriptorSetLayout layout = textureLayout;
				DescriptorSetAllocateInfo allocate = new DescriptorSetAllocateInfo
				{
					SType = StructureType.DescriptorSetAllocateInfo,
					DescriptorPool = currentTexturePool,
					DescriptorSetCount = 1,
					PSetLayouts = &layout
				};
				Result result = vk.AllocateDescriptorSets(device, in allocate, out DescriptorSet set);
				if (result == Result.Success)
				{
					pool = currentTexturePool;
					return set;
				}

				// This pool is full (or fragmented): the next one.
				currentTexturePool = default;
			}

			throw new InvalidOperationException("no descriptor set for a texture");
		}

		/// <summary>The 1x1 opaque black texture.</summary>
		private void CreateBlackTexture()
		{
			blackTexture = new GLTexture { MinFilter = GL_NEAREST, MagFilter = GL_NEAREST };
			blackTexture.Staging = bufferHeap.Allocate(4, 16);
			byte* pixel = blackTexture.Staging.Pointer;
			pixel[0] = pixel[1] = pixel[2] = 0;
			pixel[3] = 255;
			blackTexture.Width = blackTexture.Height = 1;
			blackTexture.Format = Format.R8G8B8A8Unorm;
			blackTexture.Swizzle = Identity;
			blackTexture.Pending = true;
		}

		private void DisposeTextures()
		{
			foreach (GLTexture texture in textures.Values)
			{
				ReleaseTexture(texture);
			}

			ReleaseTexture(blackTexture);
			textures.Clear();
			context.WaitIdle();
			foreach (Sampler sampler in samplers.Values)
			{
				vk.DestroySampler(device, sampler, null);
			}

			samplers.Clear();
			foreach (DescriptorPool pool in texturePools)
			{
				vk.DestroyDescriptorPool(device, pool, null);
			}

			texturePools.Clear();
		}

		private static void Check(Result result, string what)
		{
			if (result != Result.Success)
			{
				throw new InvalidOperationException(what + " failed: " + result);
			}
		}
	}
}
