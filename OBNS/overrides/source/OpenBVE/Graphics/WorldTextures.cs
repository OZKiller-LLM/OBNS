// OBNS: not an upstream file. The world layer's textures (OBNS/VULKAN.md, OBNS/DIRECT3D.md), whichever API draws it: its
// own copy of each texture the world uses, made from upstream's decode (not uploaded to OpenGL, which keeps textures
// only for what it draws), each at a place the draws name (an index); the images themselves are the API's
// (WorldTextures' subclasses).

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LibRender2.Textures;
using OpenBveApi.Colors;
using OpenBveApi.Graphics;
using OpenBveApi.Textures;

namespace OpenBve.Graphics
{
	internal abstract unsafe class WorldTextures : IDisposable
	{
		/// <summary>The most textures at once (the size of Vulkan's bindless array; its descriptors are only written for textures in use).</summary>
		internal const uint Capacity = 65536;

		/// <summary>The index of the white texture drawn when a face has none.</summary>
		internal const int White = 0;

		/// <summary>A texture's record; the API's records add its image.</summary>
		internal class Entry
		{
			internal int Index;
			internal uint Width, Height, Levels;
			internal long FreedAt;
			/// <summary>Whether its image is made.</summary>
			internal bool Made;
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

		private readonly string api;
		private readonly Dictionary<Texture, Entry> entries = new Dictionary<Texture, Entry>(new ReferenceComparer());
		private readonly Stack<int> freeIndices = new Stack<int>();
		private readonly List<Entry> freed = new List<Entry>();
		private int nextIndex = 1;
		private long frame = 1;
		private long pixelBytes;

		/// <summary>The white texture (made by the API's constructor, <see cref="MakeWhite"/>).</summary>
		protected Entry WhiteEntry { get; private set; }

		/// <summary>Each texture made, logged (OBNS_VULKAN_DEBUG=1, OBNS_D3D11_DEBUG=1).</summary>
		protected readonly bool logTextures;

		// For finding what a driver fails on (a GPU hang on Broadwell's hasvk): OBNS_VULKAN_WHITE=1 makes no textures (all
		// drawn white); OBNS_VULKAN_NOMIPS=1 makes textures without mipmaps (no blits). OBNS_D3D11_ the same for Direct3D 11.
		private readonly bool allWhite, noMipmaps;

		/// <summary>Changes whenever a texture's place may have changed (one made, deleted or unloaded).</summary>
		internal int Generation { get; private set; } = 1;

		/// <summary>The API's name for the log, and its switches' prefix (OBNS_VULKAN).</summary>
		protected WorldTextures(string api, string switches)
		{
			this.api = api;
			logTextures = Environment.GetEnvironmentVariable(switches + "_DEBUG") == "1";
			allWhite = Environment.GetEnvironmentVariable(switches + "_WHITE") == "1";
			noMipmaps = Environment.GetEnvironmentVariable(switches + "_NOMIPS") == "1";
		}

		/// <summary>The frame being made.</summary>
		protected long Frame => frame;

		/// <summary>A new record, of the API's kind.</summary>
		protected abstract Entry NewRecord();

		/// <summary>Makes a record's image (its size and levels set).</summary>
		protected abstract void MakeImage(Entry entry);

		/// <summary>
		/// Writes the pixels into the image with every mipmap level. With later, the pixels may be written when the
		/// uploads are sent, with the others then on every core; else they are taken now (upstream may reuse the bytes).
		/// </summary>
		protected abstract void Write(Entry entry, Pixels pixels, bool later);

		/// <summary>The image is made and written: the draws may name it (Vulkan: its descriptor).</summary>
		protected virtual void Published(Entry entry)
		{
		}

		/// <summary>The image is let go of (destroyed three frames later).</summary>
		protected virtual void Unpublished(Entry entry)
		{
		}

		/// <summary>Called after each upload (the API may send its uploads when it has enough).</summary>
		protected virtual void Uploaded()
		{
		}

		/// <summary>Sends the uploads made so far, whole.</summary>
		protected abstract void Flush();

		/// <summary>Destroys a record's image (any record: one that failed has none).</summary>
		protected abstract void Destroy(Entry entry);

		/// <summary>Where the images are, for the statistics.</summary>
		protected abstract string DescribeStorage();

		/// <summary>Makes the white texture (the API's constructor, once it can make images).</summary>
		protected void MakeWhite()
		{
			Entry white = NewRecord();
			white.Index = White;
			Upload(white, Pixels.Rgba(1, 1, new byte[] { 255, 255, 255, 255 }), false);
			WhiteEntry = white;
		}

		/// <summary>The place given for a texture not made yet because the frame's budget is spent: drawn white, asked for again.</summary>
		internal const int Later = -2;

		// The frame's budget for making textures: decoding and uploading all a new view needs at once held the main
		// thread for seconds; a few each frame, the view comes in over a few frames instead.
		private const double BudgetMilliseconds = 16.0;
		private const long BudgetBytes = 64L << 20;
		private long budgetStart, budgetBytes;

		/// <summary>Starts a frame's budget for making textures.</summary>
		internal void StartBudget()
		{
			budgetStart = System.Diagnostics.Stopwatch.GetTimestamp();
			budgetBytes = 0;
		}

		/// <summary>Whether textures are being made in advance (LoadAll): the whole load, no budget.</summary>
		private bool loadingAll;

		/// <summary>
		/// Textures being decoded on worker threads (the register-time decodes are let go, TextureManager): one first
		/// needed while driving is decoded off the main thread and made a few frames later, not decoded there at once (a
		/// large one took up to 0.4 s, a frame).
		/// </summary>
		private readonly Dictionary<Texture, System.Threading.Tasks.Task<Texture>> decoding = new Dictionary<Texture, System.Threading.Tasks.Task<Texture>>();

		private static readonly int maximumDecodes = Math.Max(2, Environment.ProcessorCount / 2);

		private bool BudgetSpent => !loadingAll && budgetBytes >= BudgetBytes || !loadingAll && (System.Diagnostics.Stopwatch.GetTimestamp() - budgetStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= BudgetMilliseconds;

		/// <summary>
		/// A texture's place in the layer's textures, made the first time (main thread): from upstream's decode for a still
		/// texture; through OpenGL for an animated one (OpenGL animates it, and its frames are copied here, so it is loaded
		/// every frame, which advances it). -1 if it cannot be made; Later if the frame's budget is spent. Whether it is
		/// animated is returned too.
		/// </summary>
		internal int Prepare(Texture texture, OpenGlTextureWrapMode wrap, out bool animated)
		{
			animated = false;
			if (texture == null || allWhite)
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
				long start = System.Diagnostics.Stopwatch.GetTimestamp();
				Texture decoded;
				bool haveDecode;
				if (loadingAll)
				{
					haveDecode = manager.ObnsDecoded(texture, out decoded);
				}
				else if (!decoding.TryGetValue(texture, out System.Threading.Tasks.Task<Texture> task))
				{
					if (decoding.Count < maximumDecodes)
					{
						decoding[texture] = System.Threading.Tasks.Task.Run(() => manager.ObnsDecoded(texture, out Texture result) ? result : null);
					}

					return Later;
				}
				else if (!task.IsCompleted)
				{
					return Later;
				}
				else
				{
					decoding.Remove(texture);
					decoded = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
					haveDecode = decoded != null;
				}

				if (haveDecode)
				{
					TextureTransparencyType transparency = decoded.GetTransparencyType();
					texture.Transparency = transparency;
					// The alpha of RGBA (as upstream's decoders give PNG, JPEG and BMP) is left as it is: opaque means every
					// alpha is 255 already, as the scan just made says of these very pixels.
					Pixels pixels = Pixels.Of(decoded, decoded.Bytes, transparency == TextureTransparencyType.Opaque && decoded.PixelFormat != PixelFormat.RGBAlpha);
					if (pixels != null)
					{
						entry = NewEntry(texture);
						if (entry != null)
						{
							Upload(entry, pixels, true);
						}
					}

					manager.ObnsReleaseDecoded(texture, decoded);
					if (logTextures)
					{
						Program.FileSystem.AppendToLogFile(string.Format(System.Globalization.CultureInfo.InvariantCulture, "OBNS " + api + " texture made at frame {0}: {1}x{2} {3}, {4:0.00} ms on the main thread", frame, decoded.Width, decoded.Height, decoded.PixelFormat, (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
					}

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
		/// Upstream's "load textures in advance" when the layer draws the world: every registered texture made here (one copy
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

			Flush();
		}

		/// <summary>
		/// Upstream's train texture preload (TrainBase.PreloadTextures) when the layer draws the world and the cab: the wrap
		/// modes upstream works out (AnimatedObject.LoadTextures) for every section. The exterior's textures are made when
		/// first drawn (a train may have hundreds it seldom shows, such as destination displays; OpenGL can page those out
		/// of video memory, but the layer's would stay in it); the cab's (interior and head-out views), shown from the start,
		/// are made now, as upstream preloads them.
		/// </summary>
		internal void PreloadTrain(global::TrainManager.Trains.TrainBase train)
		{
			List<KeyValuePair<Texture, OpenGlTextureWrapMode>> cab = new List<KeyValuePair<Texture, OpenGlTextureWrapMode>>();
			foreach (global::TrainManager.Car.CarBase car in train.Cars)
			{
				foreach (KeyValuePair<LibRender2.Trains.CarSectionType, LibRender2.Trains.CarSection> section in car.CarSections)
				{
					foreach (LibRender2.Trains.ElementsGroup group in section.Value.Groups)
					{
						foreach (OpenBveApi.Objects.AnimatedObject element in group.Elements)
						{
							PreloadObject(element, section.Key == LibRender2.Trains.CarSectionType.Exterior ? null : cab);
						}
					}
				}
			}

			loadingAll = true;
			try
			{
				foreach (KeyValuePair<Texture, OpenGlTextureWrapMode> texture in cab)
				{
					Prepare(texture.Key, texture.Value, out bool _);
				}
			}
			finally
			{
				loadingAll = false;
			}

			Flush();
		}

		/// <summary>
		/// AnimatedObject.LoadTextures (upstream) without the loads: the wrap modes, and the textures kept loaded; the
		/// textures with their wrap modes added to the list, if one is given, to be made now.
		/// </summary>
		private void PreloadObject(OpenBveApi.Objects.AnimatedObject element, List<KeyValuePair<Texture, OpenGlTextureWrapMode>> made)
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
						made?.Add(new KeyValuePair<Texture, OpenGlTextureWrapMode>(material.DaytimeTexture, wrap));
					}

					if (material.NighttimeTexture != null)
					{
						material.NighttimeTexture.AvailableToUnload = false;
						made?.Add(new KeyValuePair<Texture, OpenGlTextureWrapMode>(material.NighttimeTexture, wrap));
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

			Pixels pixels = Pixels.Of(decoded, bytes, opaque);
			if (pixels == null)
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

			// Written now: upstream may reuse the frame's bytes for the next.
			Upload(entry, pixels, false);
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

			Entry entry = NewRecord();
			entry.Index = index;
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
					Unpublished(entry);
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

		/// <summary>A decoded texture's pixels, to be written as RGBA (on any thread: nothing shared is touched).</summary>
		protected sealed class Pixels
		{
			internal uint Width, Height;
			private byte[] bytes;
			private PixelFormat format;
			private Color32[] palette;
			private bool opaque;

			/// <summary>The pixels, or null if the format is not known or there are too few bytes; opaque forces alpha to 255.</summary>
			internal static Pixels Of(Texture texture, byte[] bytes, bool opaque)
			{
				long count = (long)texture.Width * texture.Height;
				if (bytes == null || count <= 0)
				{
					return null;
				}

				long needed;
				switch (texture.PixelFormat)
				{
					case PixelFormat.RGBAlpha: needed = count * 4; break;
					case PixelFormat.RGB: needed = count * 3; break;
					case PixelFormat.GrayscaleAlpha: needed = count * 2; break;
					case PixelFormat.Paletted:
					case PixelFormat.Grayscale: needed = count; break;
					default: return null;
				}

				if (bytes.Length < needed)
				{
					return null;
				}

				return new Pixels { Width = (uint)texture.Width, Height = (uint)texture.Height, bytes = bytes, format = texture.PixelFormat, palette = texture.Palette32, opaque = opaque };
			}

			/// <summary>RGBA pixels as they are.</summary>
			internal static Pixels Rgba(uint width, uint height, byte[] bytes) => new Pixels { Width = width, Height = height, bytes = bytes, format = PixelFormat.RGBAlpha };

			/// <summary>Writes pixels [first, first + count) as RGBA into the texture's staging (Width * Height * 4 bytes).</summary>
			internal void Fill(byte* target, int first, int count)
			{
				uint* rgba = (uint*)target + first;
				fixed (byte* pixels = bytes)
				{
					switch (format)
					{
						case PixelFormat.RGBAlpha:
							Buffer.MemoryCopy(pixels + (long)first * 4, rgba, (long)count * 4, (long)count * 4);
							if (opaque)
							{
								for (int p = 0; p < count; p++) rgba[p] |= 0xFF000000;
							}

							break;
						case PixelFormat.RGB:
							byte* rgb = pixels + (long)first * 3;
							for (int p = 0; p < count; p++, rgb += 3)
							{
								rgba[p] = rgb[0] | (uint)rgb[1] << 8 | (uint)rgb[2] << 16 | 0xFF000000;
							}

							break;
						case PixelFormat.Paletted:
							uint* colours = stackalloc uint[256];
							for (int i = 0; i < 256; i++)
							{
								Color32 c = palette != null && i < palette.Length ? palette[i] : new Color32(0, 0, 0, 255);
								colours[i] = c.R | (uint)c.G << 8 | (uint)c.B << 16 | (uint)(opaque ? 255 : c.A) << 24;
							}

							for (int p = 0; p < count; p++) rgba[p] = colours[pixels[first + p]];
							break;
						case PixelFormat.Grayscale:
							for (int p = 0; p < count; p++) rgba[p] = pixels[first + p] * 0x010101u | 0xFF000000;
							break;
						case PixelFormat.GrayscaleAlpha:
							byte* grey = pixels + (long)first * 2;
							for (int p = 0; p < count; p++, grey += 2) rgba[p] = grey[0] * 0x010101u | (uint)(opaque ? 255 : grey[1]) << 24;
							break;
					}
				}
			}
		}

		// The pixels written per piece of work: a large texture (one of 4096 x 4096 took 57 ms on one core)
		// is shared out over every core too.
		protected const int FillPiece = 1 << 20;

		/// <summary>Uploads the pixels, making the image the first time.</summary>
		private void Upload(Entry entry, Pixels pixels, bool later)
		{
			uint width = pixels.Width, height = pixels.Height;
			bool created = !entry.Made;
			if (created)
			{
				entry.Width = width;
				entry.Height = height;
				entry.Levels = 1;
				for (uint size = Math.Max(width, height); size > 1 && !noMipmaps; size >>= 1)
				{
					entry.Levels++;
				}

				MakeImage(entry);
				entry.Made = true;
				pixelBytes += (long)width * height * 4 * 4 / 3;
			}

			budgetBytes += (long)width * height * 4;
			Write(entry, pixels, later);
			if (created)
			{
				Published(entry);
			}

			Uploaded();
		}

		internal string Describe() => entries.Count + " textures (" + (pixelBytes >> 20) + " MB of pixels made), " + DescribeStorage();

		public virtual void Dispose()
		{
			foreach (Entry entry in entries.Values)
			{
				Destroy(entry);
			}

			foreach (Entry entry in freed)
			{
				Destroy(entry);
			}

			if (WhiteEntry != null)
			{
				Destroy(WhiteEntry);
			}

			entries.Clear();
			freed.Clear();
		}
	}
}
