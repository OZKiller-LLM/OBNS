// OBNS: not an upstream file. The Vulkan world layer's textures (OBNS/VULKAN.md): its own copy of each texture the
// world uses, made from upstream's decode (not uploaded to OpenGL, which keeps textures only for what it draws), in one
// bindless array the draws index.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LibRender2.Textures;
using OpenBveApi.Colors;
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

		internal sealed class Entry
		{
			internal int Index;
			internal ulong Image, View;
			internal GpuRange Memory;
			internal uint Width, Height, Levels;
			internal long FreedAt;
			/// <summary>Could not be made (its decode failed): drawn white, not tried again.</summary>
			internal bool Failed;
			/// <summary>Animated: OpenGL loads and animates it, and each frame is copied here.</summary>
			internal bool Animated;
		}

		/// <summary>Compares textures by reference (the handles the materials hold).</summary>
		private sealed class ReferenceComparer : IEqualityComparer<Texture>
		{
			public bool Equals(Texture a, Texture b) => ReferenceEquals(a, b);
			public int GetHashCode(Texture texture) => RuntimeHelpers.GetHashCode(texture);
		}

		private readonly VulkanDevice device;
		private readonly Uploader uploader;
		private readonly GpuPool memory;
		private readonly Dictionary<Texture, Entry> entries = new Dictionary<Texture, Entry>(new ReferenceComparer());
		private readonly Stack<int> freeIndices = new Stack<int>();
		private readonly List<Entry> freed = new List<Entry>();
		private int nextIndex = 1;
		private long frame = 1;
		private Entry white;
		private long pixelBytes;

		/// <summary>For finding faults: textures uploaded without mipmaps (OBNS_VULKAN_NOMIPS=1).</summary>
		private static readonly bool noMipmaps = Environment.GetEnvironmentVariable("OBNS_VULKAN_NOMIPS") == "1";

		/// <summary>For finding faults: each texture made, logged (OBNS_VULKAN_DEBUG=1).</summary>
		private static readonly bool levelZeroOnly = Environment.GetEnvironmentVariable("OBNS_VULKAN_LEVEL0") == "1";

		private static readonly bool skipHuge = Environment.GetEnvironmentVariable("OBNS_VULKAN_SKIPHUGE") == "1";

		private static readonly bool logTextures = Environment.GetEnvironmentVariable("OBNS_VULKAN_DEBUG") == "1";

		private readonly ulong[] descriptorSets;
		private readonly uint textureBinding;

		/// <summary>The samplers by OpenGL wrap mode (ClampClamp, ClampRepeat, RepeatClamp, RepeatRepeat).</summary>
		internal readonly ulong[] Samplers = new ulong[4];

		/// <summary>Changes whenever a texture's place in the array may have changed (one made, deleted or unloaded).</summary>
		internal int Generation { get; private set; } = 1;

		internal VulkanTextures(VulkanDevice device, Uploader uploader, ulong[] descriptorSets, uint textureBinding, InterpolationMode interpolation, int anisotropy)
		{
			this.device = device;
			this.uploader = uploader;
			this.descriptorSets = descriptorSets;
			this.textureBinding = textureBinding;
			// (OBNS_VULKAN_DEDICATED=1, for finding faults: each texture in an allocation of its own.)
			memory = new GpuPool(device, Environment.GetEnvironmentVariable("OBNS_VULKAN_DEDICATED") == "1" ? 1UL : 256UL << 20, 0, "textures");
			CreateSamplers(interpolation, anisotropy);
			white = new Entry { Index = White };
			Upload(white, 1, 1, new byte[] { 255, 255, 255, 255 });
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

		/// <summary>The place given for a texture not made yet because the frame's budget is spent: drawn white, asked for again.</summary>
		internal const int Later = -2;

		// The frame's budget for making textures: decoding and uploading many at once (a new view) held the main thread
		// for seconds, and with OpenGL and Vulkan exchanging frames meanwhile the driver was reset (OBNS/VULKAN.md).
		private static readonly double BudgetMilliseconds = double.TryParse(Environment.GetEnvironmentVariable("OBNS_VULKAN_BUDGET_MS"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) ? ms : 16.0;
		private static readonly long BudgetBytes = (long.TryParse(Environment.GetEnvironmentVariable("OBNS_VULKAN_BUDGET_MB"), out long mb) ? mb : 64L) << 20;
		private long budgetStart, budgetBytes;

		/// <summary>Starts a frame's budget for making textures.</summary>
		internal void StartBudget()
		{
			budgetStart = System.Diagnostics.Stopwatch.GetTimestamp();
			budgetBytes = 0;
		}

		/// <summary>Whether textures are being made in advance (LoadAll): the whole load, no budget.</summary>
		private bool loadingAll;

		private bool BudgetSpent => !loadingAll && budgetBytes >= BudgetBytes || !loadingAll && (System.Diagnostics.Stopwatch.GetTimestamp() - budgetStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= BudgetMilliseconds;

		/// <summary>
		/// A texture's place in the bindless array, made the first time (main thread): from upstream's decode for a still
		/// texture; through OpenGL for an animated one (OpenGL animates it, and its frames are copied here, so it is loaded
		/// every frame, which advances it). -1 if it cannot be made; Later if the frame's budget is spent. Whether it is
		/// animated is returned too.
		/// </summary>
		internal int Prepare(Texture texture, OpenGlTextureWrapMode wrap, out bool animated)
		{
			animated = false;
			if (texture == null)
			{
				return -1;
			}

			if (entries.TryGetValue(texture, out Entry entry) && !entry.Animated)
			{
				return entry.Failed ? -1 : entry.Index;
			}

			if (entry == null && BudgetSpent)
			{
				return Later;
			}

			if (entry == null && !texture.MultipleFrames)
			{
				TextureManager manager = Program.Renderer.TextureManager;
				bool decodedHere = manager.ObnsDecoded(texture, out Texture decoded);
				if (decodedHere && skipHuge && Math.Max(decoded.Width, decoded.Height) > 8192)
				{
					entries[texture] = new Entry { Index = -1, Failed = true };
					return -1;
				}

				if (decodedHere)
				{
					TextureTransparencyType transparency = decoded.GetTransparencyType();
					texture.Transparency = transparency;
					byte[] rgba = ToRgba(decoded, decoded.Bytes, transparency == TextureTransparencyType.Opaque);
					if (rgba != null)
					{
						entry = NewEntry(texture);
						if (entry != null)
						{
							Upload(entry, (uint)decoded.Width, (uint)decoded.Height, rgba);
						}
					}

					manager.ObnsReleaseDecoded(texture, decoded);
					if (entry != null)
					{
						return entry.Index;
					}
				}
			}

			// Animated (or not decodable here): upstream's load, through OpenGL; its frames come back to AnimatedFrame.
			Texture handle = texture;
			bool loaded = Program.CurrentHost.LoadTexture(ref handle, wrap);
			if (entries.TryGetValue(texture, out entry) && entry.Animated)
			{
				animated = true;
				return entry.Index;
			}

			if (!loaded || !entries.ContainsKey(texture))
			{
				// Drawn white from now on (as OpenGL does with a texture it cannot load).
				entries[texture] = new Entry { Index = -1, Failed = true };
			}

			return -1;
		}

		/// <summary>
		/// Upstream's "load textures in advance" when Vulkan draws the world: every registered texture made here (one copy
		/// each; OpenGL loads only what it draws itself, as it comes to it), rather than all of them uploaded to OpenGL.
		/// </summary>
		internal void LoadAll()
		{
			Texture[] registered = TextureManager.RegisteredTextures;
			int count = Math.Min(Program.Renderer.TextureManager.RegisteredTexturesCount, registered?.Length ?? 0);
			loadingAll = true;
			try
			{
				for (int i = 0; i < count; i++)
				{
					Texture texture = registered[i];
					for (int wrap = 0; texture != null && wrap < 4; wrap++)
					{
						if (texture.OpenGlTextures[wrap].Used)
						{
							Prepare(texture, (OpenGlTextureWrapMode)wrap, out bool _);
							break;
						}
					}
				}
			}
			finally
			{
				loadingAll = false;
			}

			uploader.Submit(true);
		}

		/// <summary>
		/// Upstream's train texture preload (TrainBase.PreloadTextures) when Vulkan draws the world: for the exterior, the
		/// wrap modes upstream works out (AnimatedObject.LoadTextures), its textures made when first drawn (a train may
		/// have hundreds it seldom shows, such as destination displays; OpenGL can page those out of video memory, but
		/// Vulkan's would stay in it); the cab's (interior and head-out views, which OpenGL draws) preloaded into OpenGL
		/// as upstream does.
		/// </summary>
		internal void PreloadTrain(global::TrainManager.Trains.TrainBase train)
		{
			{
				foreach (global::TrainManager.Car.CarBase car in train.Cars)
				{
					foreach (System.Collections.Generic.KeyValuePair<LibRender2.Trains.CarSectionType, LibRender2.Trains.CarSection> section in car.CarSections)
					{
						foreach (LibRender2.Trains.ElementsGroup group in section.Value.Groups)
						{
							if (section.Key != LibRender2.Trains.CarSectionType.Exterior)
							{
								group.PreloadTextures();
								continue;
							}

							foreach (OpenBveApi.Objects.AnimatedObject element in group.Elements)
							{
								PreloadObject(element);
							}
						}
					}
				}
			}
		}

		/// <summary>AnimatedObject.LoadTextures (upstream) without the loads: the wrap modes, and the textures kept loaded.</summary>
		private void PreloadObject(OpenBveApi.Objects.AnimatedObject element)
		{
			foreach (OpenBveApi.Objects.ObjectState state in element.States)
			{
				if (state.Prototype?.Mesh == null)
				{
					continue;
				}

				OpenBveApi.Objects.Mesh mesh = state.Prototype.Mesh;
				foreach (OpenBveApi.Objects.MeshFace face in mesh.Faces)
				{
					ref OpenBveApi.Objects.MeshMaterial material = ref mesh.Materials[face.Material];
					if (material.DaytimeTexture == null && material.NighttimeTexture == null)
					{
						continue;
					}

					OpenGlTextureWrapMode wrap = OpenGlTextureWrapMode.ClampClamp;
					if (material.WrapMode == null)
					{
						// As upstream: repeated where the coordinates go beyond the texture.
						foreach (OpenBveApi.Objects.VertexTemplate vertex in mesh.Vertices)
						{
							if (vertex.TextureCoordinates.X < 0.0f || vertex.TextureCoordinates.X > 1.0f)
							{
								wrap |= OpenGlTextureWrapMode.RepeatClamp;
							}

							if (vertex.TextureCoordinates.Y < 0.0f || vertex.TextureCoordinates.Y > 1.0f)
							{
								wrap |= OpenGlTextureWrapMode.ClampRepeat;
							}

							if (wrap == OpenGlTextureWrapMode.RepeatRepeat)
							{
								break;
							}
						}
					}
					else
					{
						wrap = material.WrapMode.Value;
					}

					material.WrapMode = wrap;
					if (material.DaytimeTexture != null)
					{
						material.DaytimeTexture.AvailableToUnload = false;
					}

					if (material.NighttimeTexture != null)
					{
						material.NighttimeTexture.AvailableToUnload = false;
					}
				}
			}
		}

		/// <summary>An animated texture's frame, uploaded to OpenGL: copied here (its first frame makes its entry).</summary>
		internal void AnimatedFrame(Texture handle, Texture decoded, byte[] bytes, bool opaque)
		{
			if (handle == null || decoded == null || bytes == null || decoded.Width <= 0 || decoded.Height <= 0)
			{
				return;
			}

			byte[] rgba = ToRgba(decoded, bytes, opaque);
			if (rgba == null)
			{
				return;
			}

			uint width = (uint)decoded.Width, height = (uint)decoded.Height;
			if (entries.TryGetValue(handle, out Entry entry) && (entry.Failed || entry.Width != width || entry.Height != height))
			{
				Unloading(handle);
				entry = null;
			}

			if (entry == null)
			{
				entry = NewEntry(handle);
				if (entry == null)
				{
					return;
				}

				entry.Animated = true;
			}

			Upload(entry, width, height, rgba);
		}

		private Entry NewEntry(Texture texture)
		{
			int index = freeIndices.Count > 0 ? freeIndices.Pop() : nextIndex;
			if (index >= Capacity)
			{
				return null;
			}

			if (index == nextIndex)
			{
				nextIndex++;
			}

			Entry entry = new Entry { Index = index };
			entries[texture] = entry;
			Generation++;
			return entry;
		}

		/// <summary>Upstream is unloading a texture: its copy goes once the frames in flight are done with it.</summary>
		internal void Unloading(Texture texture)
		{
			if (texture != null && entries.TryGetValue(texture, out Entry entry))
			{
				entries.Remove(texture);
				Generation++;
				if (!entry.Failed)
				{
					entry.FreedAt = frame;
					freed.Add(entry);
				}
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

		/// <summary>The pixels as RGBA, as upstream's upload has OpenGL keep them (alpha 255 for textures used as opaque).</summary>
		private static byte[] ToRgba(Texture texture, byte[] bytes, bool opaque)
		{
			if (bytes == null)
			{
				return null;
			}

			int count = texture.Width * texture.Height;
			byte[] rgba = new byte[count * 4];
			switch (texture.PixelFormat)
			{
				case PixelFormat.RGBAlpha:
					if (bytes.Length < count * 4) return null;
					Buffer.BlockCopy(bytes, 0, rgba, 0, count * 4);
					if (opaque)
					{
						for (int p = 3; p < rgba.Length; p += 4) rgba[p] = 255;
					}

					break;
				case PixelFormat.RGB:
					if (bytes.Length < count * 3) return null;
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = bytes[p * 3];
						rgba[p * 4 + 1] = bytes[p * 3 + 1];
						rgba[p * 4 + 2] = bytes[p * 3 + 2];
						rgba[p * 4 + 3] = 255;
					}

					break;
				case PixelFormat.Paletted:
					if (bytes.Length < count) return null;
					Color32[] palette = texture.Palette32;
					for (int p = 0; p < count; p++)
					{
						int index = bytes[p];
						Color32 c = palette != null && index < palette.Length ? palette[index] : new Color32(0, 0, 0, 255);
						rgba[p * 4] = c.R;
						rgba[p * 4 + 1] = c.G;
						rgba[p * 4 + 2] = c.B;
						rgba[p * 4 + 3] = opaque ? (byte)255 : c.A;
					}

					break;
				case PixelFormat.Grayscale:
					if (bytes.Length < count) return null;
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = bytes[p];
						rgba[p * 4 + 3] = 255;
					}

					break;
				case PixelFormat.GrayscaleAlpha:
					if (bytes.Length < count * 2) return null;
					for (int p = 0; p < count; p++)
					{
						rgba[p * 4] = rgba[p * 4 + 1] = rgba[p * 4 + 2] = bytes[p * 2];
						rgba[p * 4 + 3] = opaque ? (byte)255 : bytes[p * 2 + 1];
					}

					break;
				default:
					return null;
			}

			return rgba;
		}

		/// <summary>Uploads the pixels with every mipmap level (made on the GPU), making the image the first time.</summary>
		private void Upload(Entry entry, uint width, uint height, byte[] rgba)
		{
			bool created = entry.Image == 0;
			if (created)
			{
				entry.Width = width;
				entry.Height = height;
				entry.Levels = 1;
				for (uint size = Math.Max(width, height); size > 1 && !noMipmaps; size >>= 1)
				{
					entry.Levels++;
				}

				Vk.ImageCreateInfo imageInfo = new Vk.ImageCreateInfo
				{
					SType = Vk.StructureTypeImageCreateInfo,
					ImageType = 1,
					Format = Vk.FormatR8G8B8A8Unorm,
					Extent = new Vk.Extent3D { Width = width, Height = height, Depth = 1 },
					MipLevels = entry.Levels,
					ArrayLayers = 1,
					Samples = 1,
					Usage = Vk.ImageUsageSampled | Vk.ImageUsageTransferDst | Vk.ImageUsageTransferSrc,
					InitialLayout = Vk.ImageLayoutUndefined
				};
				Vk.Check(Vk.vkCreateImage(device.Device, &imageInfo, null, out entry.Image), "vkCreateImage (texture)");
				Vk.vkGetImageMemoryRequirements(device.Device, entry.Image, out Vk.MemoryRequirements requirements);
				entry.Memory = memory.Allocate(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits);
				Vk.Check(Vk.vkBindImageMemory(device.Device, entry.Image, memory.Memory(entry.Memory.Block), entry.Memory.Offset), "vkBindImageMemory (texture)");
				Vk.ImageViewCreateInfo viewInfo = new Vk.ImageViewCreateInfo
				{
					SType = Vk.StructureTypeImageViewCreateInfo,
					Image = entry.Image,
					ViewType = 1,
					Format = Vk.FormatR8G8B8A8Unorm,
					SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, LevelCount = entry.Levels, LayerCount = 1 }
				};
				Vk.Check(Vk.vkCreateImageView(device.Device, &viewInfo, null, out entry.View), "vkCreateImageView (texture)");
				pixelBytes += (long)width * height * 4 * 4 / 3;
				if (logTextures)
				{
					Program.FileSystem.AppendToLogFile("OBNS Vulkan texture " + entry.Index + ": " + width + "x" + height + ", " + entry.Levels + " levels, " + requirements.Size + " bytes at block " + entry.Memory.Block + " offset " + entry.Memory.Offset + " (alignment " + requirements.Alignment + ")");
				}
			}

			// The first level staged and copied; the others made from it on the GPU, each from the one before (image blits,
			// as OpenGL's mipmap generation does).
			budgetBytes += rgba.Length;
			ulong stagingOffset;
			ulong staging;
			fixed (byte* data = rgba)
			{
				staging = uploader.Stage(data, (ulong)rgba.Length, out stagingOffset);
			}

			IntPtr commands = uploader.Commands;
			// Discarding what was there (a new image, or an animated one's last frame: frames reading it are earlier on the queue).
			Vk.ImageMemoryBarrier toTransfer = ImageBarrier(entry.Image, 0, entry.Levels, Vk.AccessShaderRead, Vk.AccessTransferWrite, Vk.ImageLayoutUndefined, Vk.ImageLayoutTransferDstOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageFragmentShader | Vk.PipelineStageTransfer, Vk.PipelineStageTransfer, 0, 0, null, 0, null, 1, &toTransfer);
			Vk.BufferImageCopy copy = new Vk.BufferImageCopy
			{
				BufferOffset = stagingOffset,
				ImageSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, LayerCount = 1 },
				ImageExtent = new Vk.Extent3D { Width = width, Height = height, Depth = 1 }
			};
			Vk.vkCmdCopyBufferToImage(commands, staging, entry.Image, Vk.ImageLayoutTransferDstOptimal, 1, &copy);

			int levelWidth = (int)width, levelHeight = (int)height;
			for (uint level = 1; level < entry.Levels; level++)
			{
				Vk.ImageMemoryBarrier toSource = ImageBarrier(entry.Image, level - 1, 1, Vk.AccessTransferWrite, Vk.AccessTransferRead, Vk.ImageLayoutTransferDstOptimal, Vk.ImageLayoutTransferSrcOptimal);
				Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTransfer, Vk.PipelineStageTransfer, 0, 0, null, 0, null, 1, &toSource);
				int nextWidth = Math.Max(1, levelWidth / 2), nextHeight = Math.Max(1, levelHeight / 2);
				Vk.ImageBlit blit = new Vk.ImageBlit
				{
					SrcSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, MipLevel = level - 1, LayerCount = 1 },
					SrcOffset1 = new Vk.Offset3D { X = levelWidth, Y = levelHeight, Z = 1 },
					DstSubresource = new Vk.ImageSubresourceLayers { AspectMask = Vk.ImageAspectColor, MipLevel = level, LayerCount = 1 },
					DstOffset1 = new Vk.Offset3D { X = nextWidth, Y = nextHeight, Z = 1 }
				};
				Vk.vkCmdBlitImage(commands, entry.Image, Vk.ImageLayoutTransferSrcOptimal, entry.Image, Vk.ImageLayoutTransferDstOptimal, 1, &blit, Vk.FilterLinear);
				levelWidth = nextWidth;
				levelHeight = nextHeight;
			}

			// Levels before the last are transfer sources now; the last is still a transfer destination.
			Vk.ImageMemoryBarrier* toRead = stackalloc Vk.ImageMemoryBarrier[2];
			int barriers = 0;
			if (entry.Levels > 1)
			{
				toRead[barriers++] = ImageBarrier(entry.Image, 0, entry.Levels - 1, Vk.AccessTransferRead, Vk.AccessShaderRead, Vk.ImageLayoutTransferSrcOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			}

			toRead[barriers++] = ImageBarrier(entry.Image, entry.Levels - 1, 1, Vk.AccessTransferWrite, Vk.AccessShaderRead, Vk.ImageLayoutTransferDstOptimal, Vk.ImageLayoutShaderReadOnlyOptimal);
			Vk.vkCmdPipelineBarrier(commands, Vk.PipelineStageTransfer, Vk.PipelineStageFragmentShader, 0, 0, null, 0, null, (uint)barriers, toRead);

			if (created)
			{
				WriteDescriptor(entry);
			}

			if (uploader.Full)
			{
				uploader.Submit(true);
			}
		}

		private static Vk.ImageMemoryBarrier ImageBarrier(ulong image, uint baseLevel, uint levels, uint srcAccess, uint dstAccess, uint oldLayout, uint newLayout)
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
				Image = image,
				SubresourceRange = new Vk.ImageSubresourceRange { AspectMask = Vk.ImageAspectColor, BaseMipLevel = baseLevel, LevelCount = levels, LayerCount = 1 }
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
			memory.Free(entry.Memory);
			entry.View = entry.Image = 0;
		}

		internal string Describe() => entries.Count + " textures (" + (pixelBytes >> 20) + " MB of pixels made), " + memory.Describe();

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

			memory.Dispose();
		}
	}
}
