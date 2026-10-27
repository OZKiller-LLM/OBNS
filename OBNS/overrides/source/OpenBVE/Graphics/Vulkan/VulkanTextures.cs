// OBNS: not an upstream file. The Vulkan world layer's textures (OBNS/VULKAN.md): the images upstream's textures are
// stored in, shared with OpenGL (which writes them as upstream does), in one bindless array the draws index.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LibRender2;
using OpenBveApi.Graphics;
using OpenBveApi.Textures;

namespace OpenBve.Graphics.Vulkan
{
	internal sealed unsafe class VulkanTextures : IDisposable
	{
		/// <summary>The size of the bindless array (descriptors are only written for textures in use).</summary>
		internal const uint Capacity = 65536;

		/// <summary>The index of the white texture drawn when a face has none.</summary>
		internal const int White = 0;

		private sealed class Entry
		{
			internal int Index;
			internal ulong Image, View;
			internal GpuRange Memory;
			internal uint Levels;
			internal long FreedAt;
			internal OpenGlTexture Key;
		}

		/// <summary>Compares texture objects by reference (they are keys for as long as OpenGL has them).</summary>
		private sealed class ReferenceComparer : IEqualityComparer<OpenGlTexture>
		{
			public bool Equals(OpenGlTexture a, OpenGlTexture b) => ReferenceEquals(a, b);
			public int GetHashCode(OpenGlTexture texture) => RuntimeHelpers.GetHashCode(texture);
		}

		private readonly VulkanDevice device;
		private readonly SharedHeap heap;
		private readonly Dictionary<OpenGlTexture, Entry> entries = new Dictionary<OpenGlTexture, Entry>(new ReferenceComparer());
		private readonly Stack<int> freeIndices = new Stack<int>();
		private readonly List<Entry> freed = new List<Entry>();
		private int nextIndex = 1;
		private long frame = 1;
		private Entry white;
		private uint imageTypeBits;

		/// <summary>Textures OpenGL has written since they were last handed over to Vulkan.</summary>
		private readonly HashSet<Entry> written = new HashSet<Entry>();

		private readonly ulong[] descriptorSets;
		private readonly uint textureBinding;

		/// <summary>The samplers by OpenGL wrap mode (ClampClamp, ClampRepeat, RepeatClamp, RepeatRepeat).</summary>
		internal readonly ulong[] Samplers = new ulong[4];

		internal VulkanTextures(VulkanDevice device, Uploader uploader, ulong[] descriptorSets, uint textureBinding, InterpolationMode interpolation, int anisotropy)
		{
			this.device = device;
			this.descriptorSets = descriptorSets;
			this.textureBinding = textureBinding;
			heap = new SharedHeap(device, 256UL << 20, 0, "textures");
			CreateSamplers(interpolation, anisotropy);
			CreateWhite(uploader);
		}

		private void CreateSamplers(InterpolationMode interpolation, int anisotropy)
		{
			uint mag = Vk.FilterLinear, min = Vk.FilterLinear, mip = Vk.SamplerMipmapModeLinear;
			float maxLod = 1000.0f;
			switch (interpolation)
			{
				case InterpolationMode.NearestNeighbor:
					mag = min = Vk.FilterNearest;
					mip = Vk.SamplerMipmapModeNearest;
					maxLod = 0.0f;
					break;
				case InterpolationMode.Bilinear:
					maxLod = 0.0f;
					break;
				case InterpolationMode.NearestNeighborMipmapped:
					mag = min = Vk.FilterNearest;
					mip = Vk.SamplerMipmapModeNearest;
					break;
				case InterpolationMode.BilinearMipmapped:
					// As upstream: GL_NEAREST_MIPMAP_LINEAR.
					min = Vk.FilterNearest;
					break;
			}

			bool anisotropic = interpolation == InterpolationMode.AnisotropicFiltering && anisotropy > 0;
			for (int wrap = 0; wrap < 4; wrap++)
			{
				Vk.SamplerCreateInfo info = new Vk.SamplerCreateInfo
				{
					SType = Vk.StructureTypeSamplerCreateInfo,
					MagFilter = mag,
					MinFilter = min,
					MipmapMode = mip,
					// OpenGlTextureWrapMode: RepeatClamp (2) repeats S, ClampRepeat (1) repeats T.
					AddressModeU = (wrap & 2) != 0 ? Vk.SamplerAddressModeRepeat : Vk.SamplerAddressModeClampToEdge,
					AddressModeV = (wrap & 1) != 0 ? Vk.SamplerAddressModeRepeat : Vk.SamplerAddressModeClampToEdge,
					AddressModeW = Vk.SamplerAddressModeClampToEdge,
					AnisotropyEnable = anisotropic ? 1u : 0u,
					MaxAnisotropy = anisotropic ? anisotropy : 1.0f,
					MaxLod = maxLod
				};
				Vk.Check(Vk.vkCreateSampler(device.Device, &info, null, out Samplers[wrap]), "vkCreateSampler");
			}
		}

		/// <summary>The white texture: Vulkan's own (OpenGL has its white pixel), cleared to white.</summary>
		private void CreateWhite(Uploader uploader)
		{
			white = new Entry { Index = White, Levels = 1 };
			CreateImage(white, 1, 1, false, false, true);
			byte* pixel = stackalloc byte[4];
			pixel[0] = pixel[1] = pixel[2] = pixel[3] = 255;
			ulong staging = uploader.Stage(pixel, 4, out ulong offset);
			IntPtr commands = uploader.Commands;
			Vk.ImageMemoryBarrier toTransfer = Barrier(white, 0, Vk.AccessTransferWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutTransferDstOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTopOfPipe, Vk.PipelineStageTransfer, 0, 0, null, 0, null, 1, &toTransfer);
			Vk.BufferImageCopy copy = new Vk.BufferImageCopy
			{
				BufferOffset = offset,
				ImageSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, LayerCount = 1 },
				ImageExtent = new Vk.Extent3D { Width = 1, Height = 1, Depth = 1 }
			};
			Vk.vkCmdCopyBufferToImage(commands, staging, white.Image, Vk.ImageLayoutTransferDstOptimal, 1, &copy);
			Vk.ImageMemoryBarrier toRead = Barrier(white, Vk.AccessTransferWrite, Vk.AccessShaderRead, Vk.ImageLayoutTransferDstOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTransfer, Vk.PipelineStageFragmentShader, 0, 0, null, 0, null, 1, &toRead);
			WriteDescriptor(white);
		}

		/// <summary>The array index of a texture OpenGL has, or the white texture's if it has none here.</summary>
		internal int IndexOf(OpenGlTexture texture)
		{
			return texture != null && entries.TryGetValue(texture, out Entry entry) ? entry.Index : White;
		}

		/// <summary>
		/// Storage for a texture OpenGL is about to make: an image in the shared heap (its format and swizzle as OpenGL
		/// will have them), which OpenGL then makes its texture over and writes. Null to leave it to OpenGL alone.
		/// </summary>
		internal ObnsSharedMemory Allocate(OpenGlTexture key, int width, int height, int levels, bool r8, bool alphaOne)
		{
			if (key == null || width <= 0 || height <= 0)
			{
				return null;
			}

			Deleted(key);
			int index = freeIndices.Count > 0 ? freeIndices.Pop() : nextIndex;
			if (index >= Capacity)
			{
				return null;
			}

			if (index == nextIndex)
			{
				nextIndex++;
			}

			Entry entry = new Entry { Index = index, Levels = (uint)levels, Key = key };
			pixelBytes += (long)width * height * (r8 ? 1 : 4) * 4 / 3;
			CreateImage(entry, (uint)width, (uint)height, r8, alphaOne, true);
			WriteDescriptor(entry);
			entries[key] = entry;
			return new ObnsSharedMemory { GLMemory = heap.GLMemory(entry.Memory.Block), Offset = entry.Memory.Offset, Owner = entry };
		}

		private void CreateImage(Entry entry, uint width, uint height, bool r8, bool alphaOne, bool shared)
		{
			Vk.ExternalMemoryImageCreateInfo external = new Vk.ExternalMemoryImageCreateInfo
			{
				SType = Vk.StructureTypeExternalMemoryImageCreateInfo,
				HandleTypes = Vk.ExternalMemoryHandleTypeOpaqueWin32
			};
			uint format = r8 ? Vk.FormatR8Unorm : Vk.FormatR8G8B8A8Unorm;
			Vk.ImageCreateInfo imageInfo = new Vk.ImageCreateInfo
			{
				SType = Vk.StructureTypeImageCreateInfo,
				PNext = shared ? &external : null,
				ImageType = 1,
				Format = format,
				Extent = new Vk.Extent3D { Width = width, Height = height, Depth = 1 },
				MipLevels = entry.Levels,
				ArrayLayers = 1,
				Samples = 1,
				// What OpenGL may do with a texture: sample, copy, and render into (mipmap generation).
				Usage = Vk.ImageUsageSampled | Vk.ImageUsageTransferSrc | Vk.ImageUsageTransferDst | Vk.ImageUsageColorAttachment,
				InitialLayout = Vk.ImageLayoutUndefined
			};
			Vk.Check(Vk.vkCreateImage(device.Device, &imageInfo, null, out entry.Image), "vkCreateImage (texture)");
			Vk.vkGetImageMemoryRequirements(device.Device, entry.Image, out Vk.MemoryRequirements requirements);
			if (imageTypeBits == 0)
			{
				imageTypeBits = requirements.MemoryTypeBits;
			}

			entry.Memory = heap.Allocate(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits);
			Vk.Check(Vk.vkBindImageMemory(device.Device, entry.Image, heap.Memory(entry.Memory.Block), entry.Memory.Offset), "vkBindImageMemory (texture)");
			// As OpenGL samples it: one channel as grey (upstream's swizzle), and RGB8 textures (kept as RGBA8) opaque.
			Vk.ComponentMapping mapping = r8
				? new Vk.ComponentMapping { R = Vk.ComponentSwizzleR, G = Vk.ComponentSwizzleR, B = Vk.ComponentSwizzleR, A = Vk.ComponentSwizzleOne }
				: new Vk.ComponentMapping { A = alphaOne ? Vk.ComponentSwizzleOne : Vk.ComponentSwizzleIdentity };
			Vk.ImageViewCreateInfo viewInfo = new Vk.ImageViewCreateInfo
			{
				SType = Vk.StructureTypeImageViewCreateInfo,
				Image = entry.Image,
				ViewType = 1,
				Format = format,
				Components = mapping,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, LevelCount = entry.Levels, LayerCount = 1 }
			};
			Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out entry.View), "vkCreateImageView (texture)");
		}

		/// <summary>OpenGL has written a texture (it is handed over at the next frame).</summary>
		internal void Written(OpenGlTexture key)
		{
			if (key != null && entries.TryGetValue(key, out Entry entry))
			{
				written.Add(entry);
			}
		}

		/// <summary>The OpenGL names of the textures written since the last frame, for OpenGL's semaphore signal.</summary>
		internal void WrittenNames(List<int> names)
		{
			foreach (Entry entry in written)
			{
				names.Add(entry.Key.Name);
			}
		}

		/// <summary>Records taking the written textures over from OpenGL (in the layout it signalled them in), and forgets them.</summary>
		internal void AcquireWritten(IntPtr commandBuffer)
		{
			if (written.Count == 0)
			{
				return;
			}

			Vk.ImageMemoryBarrier[] barriers = new Vk.ImageMemoryBarrier[written.Count];
			int i = 0;
			foreach (Entry entry in written)
			{
				Vk.ImageMemoryBarrier barrier = Barrier(entry, 0, Vk.AccessShaderRead, Vk.ImageLayoutShaderReadOnlyOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
				barrier.SrcQueueFamilyIndex = Vk.QueueFamilyExternal;
				barrier.DstQueueFamilyIndex = device.QueueFamily;
				barriers[i++] = barrier;
			}

			fixed (Vk.ImageMemoryBarrier* data = barriers)
			{
				Vk.vkCmdPipelineBarrier(commandBuffer, Vk.PipelineStageTopOfPipe, Vk.PipelineStageFragmentShader, 0, 0, null, 0, null, (uint)barriers.Length, data);
			}

			written.Clear();
		}

		/// <summary>OpenGL has deleted a texture: its image goes once the frames in flight are done with it.</summary>
		internal void Deleted(OpenGlTexture key)
		{
			if (key != null && entries.TryGetValue(key, out Entry entry))
			{
				entries.Remove(key);
				written.Remove(entry);
				entry.FreedAt = frame;
				freed.Add(entry);
			}
		}

		/// <summary>Called each frame once the slot's last frame is done: frees what no frame in flight can still use.</summary>
		internal void FrameStarted(long frameNumber)
		{
			frame = frameNumber;
			for (int i = freed.Count - 1; i >= 0; i--)
			{
				Entry entry = freed[i];
				if (frameNumber - entry.FreedAt > 3)
				{
					Destroy(entry);
					freeIndices.Push(entry.Index);
					freed.RemoveAt(i);
				}
			}
		}

		private static Vk.ImageMemoryBarrier Barrier(Entry entry, uint srcAccess, uint dstAccess, uint oldLayout, uint newLayout)
		{
			return new Vk.ImageMemoryBarrier
			{
				SType = Vk.StructureTypeImageMemoryBarrier,
				SrcAccessMask = srcAccess,
				DstAccessMask = dstAccess,
				OldLayout = oldLayout,
				NewLayout = newLayout,
				SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
				DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
				Image = entry.Image,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, LevelCount = entry.Levels, LayerCount = 1 }
			};
		}

		/// <summary>Points the array's element at a texture, in every frame's descriptor set (update-after-bind).</summary>
		private void WriteDescriptor(Entry entry)
		{
			Vk.DescriptorImageInfo image = new Vk.DescriptorImageInfo { ImageView = entry.View, ImageLayout = Vk.ImageLayoutShaderReadOnlyOptimal };
			foreach (ulong set in descriptorSets)
			{
				Vk.WriteDescriptorSet write = new Vk.WriteDescriptorSet
				{
					SType = Vk.StructureTypeWriteDescriptorSet,
					DstSet = set,
					DstBinding = textureBinding,
					DstArrayElement = (uint)entry.Index,
					DescriptorCount = 1,
					DescriptorType = Vk.DescriptorTypeSampledImage,
					PImageInfo = &image
				};
				Vk.vkUpdateDescriptorSets(device.Device, 1, &write, 0, null);
			}
		}

		private void Destroy(Entry entry)
		{
			if (entry.View != 0) Vk.vkDestroyImageView(device.Device, entry.View, null);
			if (entry.Image != 0) Vk.vkDestroyImage(device.Device, entry.Image, null);
			heap.Free(entry.Memory);
			entry.View = entry.Image = 0;
		}

		/// <summary>The textures' pixels with their mipmaps, for comparing with the storage they take.</summary>
		private long pixelBytes;

		internal string Describe() => entries.Count + " textures (" + (pixelBytes >> 20) + " MB of pixels made), " + heap.Describe();

		public void Dispose()
		{
			foreach (Entry entry in entries.Values)
			{
				Destroy(entry);
			}

			foreach (Entry entry in freed)
			{
				Destroy(entry);
			}

			if (white != null)
			{
				Destroy(white);
			}

			entries.Clear();
			freed.Clear();
			foreach (ulong sampler in Samplers)
			{
				if (sampler != 0) Vk.vkDestroySampler(device.Device, sampler, null);
			}

			heap.Dispose();
		}
	}
}
