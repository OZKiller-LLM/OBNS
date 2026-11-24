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

// OBNS: not an upstream file. The world layer's textures (OBNS/VULKAN.md, OBNS/DIRECT3D.md), whichever API draws it: its
// own copy of each texture the world uses, made from upstream's decode (not uploaded to OpenGL, which keeps textures
// only for what it draws), each at a place the draws name (an index); the images themselves are the API's
// (WorldTextures' subclasses).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
			/// <summary>Its image's format: RGBA, or compressed (BC1 opaque, BC3 with alpha; the option, or Potato mode).</summary>
			internal TextureFormat Format;
		}

		/// <summary>A texture image's format.</summary>
		internal enum TextureFormat
		{
			Rgba,
			Bc1,
			Bc3
		}

		/// <summary>
		/// Whether textures are made compressed (BC1/BC3: the "compress textures" option, or Potato mode): a quarter to an
		/// eighth of the memory, so every texture a route has fits when they are loaded in advance (the user, 2026-11-25).
		/// </summary>
		internal static bool Compressing => ObnsBlockCompression.Enabled;

		/// <summary>Whether this API makes compressed images (one that doesn't makes them all RGBA).</summary>
		protected virtual bool MakesCompressed => false;

		/// <summary>Whether a compressed image of this size can be made (Direct3D: the first level a whole number of 4x4 blocks).</summary>
		protected virtual bool CompressedSizeAllowed(int width, int height) => true;

		/// <summary>Writes a compressed image's levels (made by <see cref="MakeImage"/> in the entry's format).</summary>
		protected virtual void WriteCompressed(Entry entry, ObnsBlockCompression.Image image)
		{
		}

		/// <summary>A decoded texture compressed (encoded on a worker thread ahead of this, or now), or null to make it RGBA.</summary>
		private ObnsBlockCompression.Image CompressedOf(Texture decoded, TextureTransparencyType transparency)
		{
			if (!Compressing || !MakesCompressed || noMipmaps || decoded == null || decoded.MultipleFrames)
			{
				return null;
			}

			ObnsBlockCompression.Image image;
			if (CompressedSizeAllowed(decoded.Width, decoded.Height))
			{
				image = ObnsBlockCompression.Get(decoded, transparency);
			}
			else
			{
				lock (resampledAhead)
				{
					if (resampledAhead.TryGetValue(decoded, out image))
					{
						resampledAhead.Remove(decoded);
					}
				}

				image = image ?? Resampled(decoded, transparency);
			}

			return image != null && image.Levels != null && image.Levels.Length > 0 ? image : null;
		}

		/// <summary>Compressed images of textures resampled to whole blocks, made on the workers (by the decode).</summary>
		private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture, ObnsBlockCompression.Image> resampledAhead = new System.Runtime.CompilerServices.ConditionalWeakTable<Texture, ObnsBlockCompression.Image>();

		/// <summary>
		/// A texture that is not whole 4x4 blocks where the API needs them, resampled to the next size that is and compressed
		/// (and cached as such): Direct3D's and Metal's; null if it can't be.
		/// </summary>
		private static ObnsBlockCompression.Image Resampled(Texture decoded, TextureTransparencyType transparency)
		{
			Texture whole = ObnsBlockCompression.ToWholeBlocks(decoded, transparency);
			return whole == null ? null : ObnsBlockCompression.Get(whole, transparency);
		}

		/// <summary>On a worker: a decode compressed ahead of its upload, where textures are made compressed.</summary>
		private void CompressAhead(Texture decoded)
		{
			if (!Compressing || !MakesCompressed || noMipmaps || decoded == null || decoded.MultipleFrames)
			{
				return;
			}

			if (CompressedSizeAllowed(decoded.Width, decoded.Height))
			{
				ObnsBlockCompression.Prepare(decoded);
				return;
			}

			// Resampled to whole blocks and compressed here too, for CompressedOf.
			try
			{
				ObnsBlockCompression.Image image = Resampled(decoded, decoded.GetTransparencyType());
				if (image != null)
				{
					lock (resampledAhead)
					{
						resampledAhead.Remove(decoded);
						resampledAhead.Add(decoded, image);
					}
				}
			}
			catch
			{
				// Resampled when it is made.
			}
		}

		/// <summary>Makes the image in a compressed format and writes its levels.</summary>
		private void UploadCompressed(Entry entry, ObnsBlockCompression.Image image)
		{
			entry.Width = (uint)image.Width;
			entry.Height = (uint)image.Height;
			entry.Levels = (uint)image.Levels.Length;
			entry.Format = image.Bc3 ? TextureFormat.Bc3 : TextureFormat.Bc1;
			MakeImage(entry);
			entry.Made = true;
			long bytes = 0;
			foreach (byte[] level in image.Levels)
			{
				bytes += level.Length;
			}

			pixelBytes += bytes;
			compressedBytes += bytes;
			compressedCount++;
			budgetBytes += bytes;
			WriteCompressed(entry, image);
			Published(entry);
			Uploaded();
		}

		private long compressedBytes;
		private int compressedCount;

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

		/// <summary>A place in the layer's textures for an image made elsewhere (it is never given back).</summary>
		internal int ReserveIndex() => nextIndex++;

		/// <summary>The white texture's place in the layer's textures, for what is drawn without one.</summary>
		internal int WhiteIndex => WhiteEntry.Index;

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
			if (WholeGame.Full)
			{
				handOver = HandOver;
				LibRender2.ObnsSharing.AloneDecoded = handOver;
			}
		}

		/// <summary>
		/// Decodes upstream's texture load made (the HUD's, the panel's, the menus' textures, loaded before they are drawn), handed
		/// over while this API draws the whole game, with the frame they came; taken by Prepare rather than decoded again.
		/// </summary>
		private readonly Action<Texture, Texture> handOver;

		private readonly Dictionary<Texture, (Texture Decoded, long Frame)> handedOver = new Dictionary<Texture, (Texture, long)>();

		private void HandOver(Texture handle, Texture decoded)
		{
			// Kept until Prepare takes it; one not taken (its texture made already, or not drawn) is let go after a while.
			lock (handedOver)
			{
				handedOver[handle] = (decoded, frame);
			}
		}

		private bool TakeHandedOver(Texture handle, out Texture decoded)
		{
			lock (handedOver)
			{
				if (handedOver.TryGetValue(handle, out (Texture Decoded, long Frame) item))
				{
					handedOver.Remove(handle);
					decoded = item.Decoded;
					return true;
				}
			}

			decoded = null;
			return false;
		}

		/// <summary>Lets go of decodes handed over but not drawn for a while (their pixels; decoded again if they're drawn later).</summary>
		private void ReleaseStaleHandedOver()
		{
			List<KeyValuePair<Texture, (Texture Decoded, long Frame)>> stale = null;
			lock (handedOver)
			{
				foreach (KeyValuePair<Texture, (Texture Decoded, long Frame)> item in handedOver)
				{
					if (frame - item.Value.Frame > DecodeKeptFrames)
					{
						(stale = stale ?? new List<KeyValuePair<Texture, (Texture, long)>>()).Add(item);
					}
				}

				if (stale != null)
				{
					foreach (KeyValuePair<Texture, (Texture Decoded, long Frame)> item in stale)
					{
						handedOver.Remove(item.Key);
					}
				}
			}

			if (stale != null)
			{
				foreach (KeyValuePair<Texture, (Texture Decoded, long Frame)> item in stale)
				{
					Program.Renderer.TextureManager.ObnsReleaseDecoded(item.Key, item.Value.Decoded);
				}
			}
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

		/// <summary>The frame each decode's texture was last asked for.</summary>
		private readonly Dictionary<Texture, long> decodeAsked = new Dictionary<Texture, long>();

		/// <summary>How many frames a finished decode waits for its texture to be asked for again.</summary>
		private const int DecodeKeptFrames = 120;

		/// <summary>
		/// The decodes still running. Finished ones whose textures have gone out of view are let go (decoded again if they return): held
		/// on to, they used up the places, and after a jump to a station no more textures were made, for as long as the game ran.
		/// </summary>
		private int DecodesRunning()
		{
			int running = 0;
			List<Texture> stale = null;
			foreach (KeyValuePair<Texture, System.Threading.Tasks.Task<Texture>> decode in decoding)
			{
				if (!decode.Value.IsCompleted)
				{
					running++;
				}
				else if (frame - decodeAsked[decode.Key] > DecodeKeptFrames)
				{
					if (stale == null)
					{
						stale = new List<Texture>();
					}

					stale.Add(decode.Key);
				}
			}

			if (stale != null)
			{
				foreach (Texture texture in stale)
				{
					decoding.Remove(texture);
					decodeAsked.Remove(texture);
				}
			}

			return running;
		}

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

			// Used now, as upstream's LoadTexture marks a texture it binds: with unused textures unloaded (the option, or
			// Potato mode), one the layer draws is not taken for unused 20 s after OpenGL last saw it. (Not an animated one:
			// upstream's load, below, advances its frames by the time since then.)
			if (!texture.MultipleFrames)
			{
				texture.LastAccess = Environment.TickCount;
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
				if (TakeHandedOver(texture, out decoded))
				{
					// Upstream's load decoded it just now (alone).
					haveDecode = decoded != null;
				}
				else if (loadingAll)
				{
					// Decoded ahead on a worker (PreloadTrain), or here.
					if (decodedAhead.TryGetValue(texture, out decoded))
					{
						decodedAhead.Remove(texture);
						haveDecode = decoded != null;
					}
					else
					{
						haveDecode = manager.ObnsDecoded(texture, out decoded);
					}
				}
				else if (!decoding.TryGetValue(texture, out System.Threading.Tasks.Task<Texture> task))
				{
					if (DecodesRunning() < maximumDecodes)
					{
						decoding[texture] = System.Threading.Tasks.Task.Run(() =>
						{
							if (!manager.ObnsDecoded(texture, out Texture result))
							{
								return null;
							}

							// Compressed here too, where textures are made compressed (the encoder is slower than the decoder).
							if (Compressing)
							{
								result.GetTransparencyType();
								CompressAhead(result);
							}

							return result;
						});
						decodeAsked[texture] = frame;
					}

					return Later;
				}
				else if (!task.IsCompleted)
				{
					decodeAsked[texture] = frame;
					return Later;
				}
				else
				{
					decoding.Remove(texture);
					decodeAsked.Remove(texture);
					decoded = task.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? task.Result : null;
					haveDecode = decoded != null;
				}

				if (haveDecode)
				{
					TextureTransparencyType transparency = decoded.GetTransparencyType();
					texture.Transparency = transparency;
					ObnsBlockCompression.Image compressed = CompressedOf(decoded, transparency);
					if (compressed != null)
					{
						entry = NewEntry(texture);
						if (entry != null)
						{
							UploadCompressed(entry, compressed);
						}

						manager.ObnsReleaseDecoded(texture, decoded);
						// (No place left for it: drawn white, as below.)
						return entry != null ? entry.Index : -1;
					}
				}

				if (haveDecode)
				{
					TextureTransparencyType transparency = decoded.GetTransparencyType();
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
			System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
			List<KeyValuePair<Texture, OpenGlTextureWrapMode>> textures = WorldInUse();
			int before = entries.Count, compressedBefore = compressedCount;
			long startBytes = pixelBytes;
			// All of them (the user, 2026-11-25: "ALL textures"). The Tuen Ma Line's 7,706 come to 6.4 GB uncompressed, more
			// than an 8 GB card has room for beside the rest: what doesn't fit goes in system memory (Direct3D and OpenGL's
			// drivers page it; Vulkan's textures take system memory once the GPU's is full, GpuPool), and the "compress
			// textures" option makes them a quarter to an eighth of that.
			loadingAll = true;
			try
			{
				// As the cab's in PreloadTrain: a batch decoded together on worker threads, then made in order here.
				for (int first = 0; first < textures.Count; first += PreloadBatch)
				{
					int last = Math.Min(first + PreloadBatch, textures.Count);
					DecodeAhead(textures, first, last);
					for (int i = first; i < last; i++)
					{
						Prepare(textures[i].Key, textures[i].Value, out bool _);
					}

					Flush();
				}
			}
			finally
			{
				loadingAll = false;
				decodedAhead.Clear();
			}

			Flush();
			preloaded = true;
			long made = pixelBytes - startBytes;
			Program.FileSystem.AppendToLogFile(string.Format(System.Globalization.CultureInfo.InvariantCulture, "OBNS " + api + ": textures loaded in advance: {0} of the route's {1} in {2:0.0} s, {3} MB of images ({4} of them compressed){5}",
				entries.Count - before, textures.Count, clock.Elapsed.TotalSeconds, made >> 20, compressedCount - compressedBefore,
				VideoMemory > 0 ? ", the GPU has " + (VideoMemory >> 20) + " MB" + ((ulong)made > VideoMemory * 7 / 10 ? ": some may be in system memory" : string.Empty) : string.Empty));
		}

		/// <summary>The GPU's own memory in bytes, as the API's device found it (0 if not known), for the log.</summary>
		internal static ulong VideoMemory;

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
				// A batch at a time: decoded together on worker threads, then made in order on this one. Only a batch's pixels
				// are held at once, whatever the cab's size.
				for (int first = 0; first < cab.Count; first += PreloadBatch)
				{
					int last = Math.Min(first + PreloadBatch, cab.Count);
					DecodeAhead(cab, first, last);
					for (int i = first; i < last; i++)
					{
						Prepare(cab[i].Key, cab[i].Value, out bool _);
					}
				}
			}
			finally
			{
				loadingAll = false;
				decodedAhead.Clear();
			}

			Flush();
		}

		/// <summary>How many of the cab's textures are decoded together before they are made.</summary>
		private const int PreloadBatch = 16;

		/// <summary>Decodes made ahead for Prepare, on this thread's side of the workers.</summary>
		private readonly Dictionary<Texture, Texture> decodedAhead = new Dictionary<Texture, Texture>();

		/// <summary>Decodes a batch of textures not yet made on worker threads, with their transparency, which Prepare would scan for next.</summary>
		private void DecodeAhead(List<KeyValuePair<Texture, OpenGlTextureWrapMode>> textures, int first, int last)
		{
			TextureManager manager = Program.Renderer.TextureManager;
			HashSet<Texture> wanted = new HashSet<Texture>();
			for (int i = first; i < last; i++)
			{
				Texture texture = textures[i].Key;
				if (texture != null && !texture.MultipleFrames && !entries.ContainsKey(texture))
				{
					wanted.Add(texture);
				}
			}

			Texture[] batch = new Texture[wanted.Count];
			wanted.CopyTo(batch);
			Texture[] results = new Texture[batch.Length];
			Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
			{
				if (manager.ObnsDecoded(batch[i], out Texture decoded))
				{
					decoded.GetTransparencyType();
					// Compressed here too, where textures are made compressed: the encoding on every core, not the main thread.
					CompressAhead(decoded);
					results[i] = decoded;
				}
			});

			for (int i = 0; i < batch.Length; i++)
			{
				decodedAhead[batch[i]] = results[i];
			}
		}

		/// <summary>
		/// AnimatedObject.LoadTextures (upstream) without the loads: the wrap modes, and the textures kept loaded; the
		/// textures with their wrap modes added to the list, if one is given, to be made now.
		/// </summary>
		private void PreloadObject(OpenBveApi.Objects.AnimatedObject element, List<KeyValuePair<Texture, OpenGlTextureWrapMode>> made)
		{
			foreach (OpenBveApi.Objects.ObjectState state in element.States)
			{
				if (state.Prototype?.Mesh != null)
				{
					AddMesh(state.Prototype.Mesh, made);
				}
			}
		}

		/// <summary>
		/// Every texture the route's objects draw (static and animated, every state), with the wrap mode each is drawn with,
		/// once each: what "load textures in advance" loads. Upstream's LoadAllTextures loads only the wrap modes marked
		/// used, and nothing marks them (in upstream either), so the option loaded nothing at all (the user, 2026-11-25:
		/// "allow preloading textures to function properly").
		/// </summary>
		internal static List<KeyValuePair<Texture, OpenGlTextureWrapMode>> WorldInUse()
		{
			List<KeyValuePair<Texture, OpenGlTextureWrapMode>> all = new List<KeyValuePair<Texture, OpenGlTextureWrapMode>>();
			HashSet<OpenBveApi.Objects.Mesh> meshes = new HashSet<OpenBveApi.Objects.Mesh>();
			void Add(OpenBveApi.Objects.Mesh mesh)
			{
				// Many objects share a prototype: each mesh once.
				if (mesh != null && meshes.Add(mesh))
				{
					AddMesh(mesh, all);
				}
			}

			List<OpenBveApi.Objects.ObjectState> statics = Program.Renderer.StaticObjectStates;
			if (statics != null)
			{
				foreach (OpenBveApi.Objects.ObjectState state in statics)
				{
					Add(state?.Prototype?.Mesh);
				}
			}

			OpenBveApi.Objects.WorldObject[] animated = ObjectManager.AnimatedWorldObjects;
			for (int i = 0; animated != null && i < ObjectManager.AnimatedWorldObjectsUsed && i < animated.Length; i++)
			{
				OpenBveApi.Objects.ObjectState[] states = animated[i]?.Object?.States;
				for (int j = 0; states != null && j < states.Length; j++)
				{
					Add(states[j]?.Prototype?.Mesh);
				}
			}

			/*
			 * Every train's too - the player's exterior, the preceding and scripted trains, their bogies and couplers - and the
			 * backgrounds the route changes to: what was left to be made when first drawn was drawn white until then (a tester,
			 * 2026-11-25: white textures before the actual texture loads in).
			 */
			List<global::TrainManager.Trains.TrainBase> trains = new List<global::TrainManager.Trains.TrainBase>();
			if (Program.TrainManager?.Trains != null)
			{
				trains.AddRange(Program.TrainManager.Trains);
			}

			if (Program.TrainManager?.TFOs != null)
			{
				foreach (OpenBveApi.Trains.AbstractTrain train in Program.TrainManager.TFOs)
				{
					if (train is global::TrainManager.Trains.TrainBase scripted)
					{
						trains.Add(scripted);
					}
				}
			}

			foreach (global::TrainManager.Trains.TrainBase train in trains)
			{
				foreach (global::TrainManager.Car.CarBase car in train?.Cars ?? new global::TrainManager.Car.CarBase[0])
				{
					if (car == null)
					{
						continue;
					}

					if (car.CarSections != null)
					{
						foreach (LibRender2.Trains.CarSection section in car.CarSections.Values)
						{
							AddSection(section, Add);
						}
					}

					AddSections(car.FrontBogie?.CarSections, Add);
					AddSections(car.RearBogie?.CarSections, Add);
					// (The coupler's sections are TrainManager's own.)
					AddSections(car.Coupler == null ? null : CouplerSections.GetValue(car.Coupler) as LibRender2.Trains.CarSection[], Add);
				}
			}

			HashSet<OpenBveApi.Routes.BackgroundHandle> backgrounds = new HashSet<OpenBveApi.Routes.BackgroundHandle>();
			if (Program.CurrentRoute?.CurrentBackground != null)
			{
				backgrounds.Add(Program.CurrentRoute.CurrentBackground);
			}

			if (Program.CurrentRoute?.Tracks != null)
			{
				foreach (OpenBveApi.Routes.Track track in Program.CurrentRoute.Tracks.Values)
				{
					foreach (OpenBveApi.Routes.TrackElement element in track?.Elements ?? new OpenBveApi.Routes.TrackElement[0])
					{
						if (element.Events == null)
						{
							continue;
						}

						foreach (OpenBveApi.Routes.GeneralEvent trackEvent in element.Events)
						{
							if (trackEvent is RouteManager2.Events.BackgroundChangeEvent change)
							{
								foreach (System.Reflection.FieldInfo field in BackgroundFields)
								{
									if (field.GetValue(change) is OpenBveApi.Routes.BackgroundHandle background)
									{
										backgrounds.Add(background);
									}
								}
							}
						}
					}
				}
			}

			foreach (OpenBveApi.Routes.BackgroundHandle background in backgrounds)
			{
				AddBackground(background, all, Add);
			}

			// The game's own images (the HUD's, from the program's Data folder), registered by now: in the 2026-11-25 test the only
			// ones still made when first drawn. (Not every registered texture: the Tuen Ma Line registers some 31,000, most
			// on objects it never places.)
			Texture[] registered = TextureManager.RegisteredTextures;
			string data = Program.FileSystem?.DataFolder;
			int registeredCount = Math.Min(Program.Renderer.TextureManager.RegisteredTexturesCount, registered?.Length ?? 0);
			for (int i = 0; !string.IsNullOrEmpty(data) && i < registeredCount; i++)
			{
				if (registered[i]?.Origin is PathOrigin origin && origin.Path != null && origin.Path.StartsWith(data, StringComparison.OrdinalIgnoreCase))
				{
					all.Add(new KeyValuePair<Texture, OpenGlTextureWrapMode>(registered[i], OpenGlTextureWrapMode.ClampClamp));
				}
			}

			// Each texture and wrap mode once.
			HashSet<KeyValuePair<Texture, OpenGlTextureWrapMode>> seen = new HashSet<KeyValuePair<Texture, OpenGlTextureWrapMode>>(new PairComparer());
			List<KeyValuePair<Texture, OpenGlTextureWrapMode>> distinct = new List<KeyValuePair<Texture, OpenGlTextureWrapMode>>();
			foreach (KeyValuePair<Texture, OpenGlTextureWrapMode> pair in all)
			{
				if (seen.Add(pair))
				{
					distinct.Add(pair);
				}
			}

			return distinct;
		}

		/// <summary>The coupler's car sections (TrainManager's own field).</summary>
		private static readonly System.Reflection.FieldInfo CouplerSections = typeof(global::TrainManager.Car.Coupler).GetField("CarSections", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		/// <summary>A background change's backgrounds (RouteManager2's own fields).</summary>
		private static readonly System.Reflection.FieldInfo[] BackgroundFields = System.Array.FindAll(typeof(RouteManager2.Events.BackgroundChangeEvent).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public), field => typeof(OpenBveApi.Routes.BackgroundHandle).IsAssignableFrom(field.FieldType));

		private static void AddSections(LibRender2.Trains.CarSection[] sections, Action<OpenBveApi.Objects.Mesh> add)
		{
			foreach (LibRender2.Trains.CarSection section in sections ?? new LibRender2.Trains.CarSection[0])
			{
				AddSection(section, add);
			}
		}

		/// <summary>A car section's meshes: every element's every state.</summary>
		private static void AddSection(LibRender2.Trains.CarSection section, Action<OpenBveApi.Objects.Mesh> add)
		{
			foreach (LibRender2.Trains.ElementsGroup group in section?.Groups ?? new LibRender2.Trains.ElementsGroup[0])
			{
				foreach (OpenBveApi.Objects.AnimatedObject element in group?.Elements ?? new OpenBveApi.Objects.AnimatedObject[0])
				{
					foreach (OpenBveApi.Objects.ObjectState state in element?.States ?? new OpenBveApi.Objects.ObjectState[0])
					{
						add(state?.Prototype?.Mesh);
					}
				}
			}
		}

		/// <summary>A background's textures: its image (repeated across, as the background is drawn), or its object's meshes.</summary>
		private static void AddBackground(OpenBveApi.Routes.BackgroundHandle background, List<KeyValuePair<Texture, OpenGlTextureWrapMode>> all, Action<OpenBveApi.Objects.Mesh> add)
		{
			switch (background)
			{
				case OpenBveApi.Routes.StaticBackground image:
					if (image.Texture != null)
					{
						all.Add(new KeyValuePair<Texture, OpenGlTextureWrapMode>(image.Texture, OpenGlTextureWrapMode.RepeatClamp));
					}

					break;
				case OpenBveApi.Routes.DynamicBackground dynamic:
					foreach (OpenBveApi.Routes.StaticBackground image in dynamic.StaticBackgrounds ?? new OpenBveApi.Routes.StaticBackground[0])
					{
						AddBackground(image, all, add);
					}

					break;
				case OpenBveApi.Routes.BackgroundObject backgroundObject:
					add(backgroundObject.ObjectState?.Prototype?.Mesh);
					break;
			}
		}

		/// <summary>Compares (texture, wrap mode) pairs by the texture's reference.</summary>
		private sealed class PairComparer : IEqualityComparer<KeyValuePair<Texture, OpenGlTextureWrapMode>>
		{
			public bool Equals(KeyValuePair<Texture, OpenGlTextureWrapMode> a, KeyValuePair<Texture, OpenGlTextureWrapMode> b) => ReferenceEquals(a.Key, b.Key) && a.Value == b.Value;
			public int GetHashCode(KeyValuePair<Texture, OpenGlTextureWrapMode> pair) => RuntimeHelpers.GetHashCode(pair.Key) * 4 + (int)pair.Value;
		}

		/// <summary>A mesh's textures with their wrap modes (worked out as upstream does, and kept in the material), kept loaded.</summary>
		private static void AddMesh(OpenBveApi.Objects.Mesh mesh, List<KeyValuePair<Texture, OpenGlTextureWrapMode>> made)
		{
			{
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
			if (preloaded && !loadingAll)
			{
				// Loaded in advance, yet one made now - drawn white until then: counted, and the first named (the user,
				// 2026-11-25: "make sure preloaded textures are loaded").
				MadeAfterPreload++;
				if (MadeAfterPreload <= 20)
				{
					Program.FileSystem.AppendToLogFile("OBNS " + api + ": a texture not loaded in advance, made when first drawn: " + (texture.Origin?.ToString() ?? "(no file)"));
				}
			}

			return entry;
		}

		/// <summary>Whether the textures were loaded in advance (LoadAll).</summary>
		private bool preloaded;

		/// <summary>Textures made after they were loaded in advance (when first drawn): none if the preload took them all.</summary>
		internal int MadeAfterPreload { get; private set; }

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
			ReleaseStaleHandedOver();
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

		// --- mipmap levels made on the CPU (APIs that make none themselves: Direct3D 12 and 9) ---

		/// <summary>A level's width or height.</summary>
		protected static uint LevelSize(uint size, int level) => Math.Max(1u, size >> level);

		/// <summary>The bytes of every level of an image, packed one after another (a chain).</summary>
		protected static long ChainBytes(uint width, uint height, uint levels)
		{
			long bytes = 0;
			for (int level = 0; level < levels; level++)
			{
				bytes += (long)LevelSize(width, level) * LevelSize(height, level) * 4;
			}

			return bytes;
		}

		/// <summary>Where a level starts in a chain.</summary>
		protected static long LevelOffset(uint width, uint height, int level)
		{
			long offset = 0;
			for (int k = 0; k < level; k++)
			{
				offset += (long)LevelSize(width, k) * LevelSize(height, k) * 4;
			}

			return offset;
		}

		/// <summary>
		/// Rows [first, end) of a level made from the level above it in a chain: each texel the mean of the 2x2 above, as
		/// Direct3D 11's GenerateMips (and OpenGL's) box filter.
		/// </summary>
		private static void MakeLevelRows(byte* chain, uint width, uint height, int level, uint first, uint end)
		{
			uint sourceWidth = LevelSize(width, level - 1), sourceHeight = LevelSize(height, level - 1);
			uint targetWidth = LevelSize(width, level);
			byte* source = chain + LevelOffset(width, height, level - 1);
			byte* target = chain + LevelOffset(width, height, level);
			for (uint y = first; y < end; y++)
			{
				byte* row0 = source + (long)Math.Min(2 * y, sourceHeight - 1) * sourceWidth * 4;
				byte* row1 = source + (long)Math.Min(2 * y + 1, sourceHeight - 1) * sourceWidth * 4;
				byte* to = target + (long)y * targetWidth * 4;
				for (uint x = 0; x < targetWidth; x++)
				{
					uint x0 = Math.Min(2 * x, sourceWidth - 1) * 4, x1 = Math.Min(2 * x + 1, sourceWidth - 1) * 4;
					for (int c = 0; c < 4; c++)
					{
						to[x * 4 + c] = (byte)((row0[x0 + c] + row0[x1 + c] + row1[x0 + c] + row1[x1 + c] + 2) >> 2);
					}
				}
			}
		}

		/// <summary>Swaps red and blue: RGBA to BGRA (Direct3D 9's A8R8G8B8).</summary>
		private static void SwapRedBlue(byte* pixels, long first, long count)
		{
			uint* p = (uint*)pixels + first;
			for (long i = 0; i < count; i++)
			{
				uint v = p[i];
				p[i] = (v & 0xFF00FF00u) | ((v & 0xFFu) << 16) | ((v >> 16) & 0xFFu);
			}
		}

		/// <summary>A chain to write: the texture (its size and levels), its pixels, and where.</summary>
		protected struct Chain
		{
			internal Entry Entry;
			internal Pixels Pixels;
			internal byte* Data;
		}

		/// <summary>Writes a texture's pixels and makes its levels, on this thread (RGBA, or BGRA).</summary>
		protected static void FillChain(Entry entry, Pixels pixels, byte* chain, bool bgra)
		{
			long count = (long)pixels.Width * pixels.Height;
			pixels.Fill(chain, 0, (int)count);
			if (bgra)
			{
				SwapRedBlue(chain, 0, count);
			}

			for (int level = 1; level < entry.Levels; level++)
			{
				MakeLevelRows(chain, entry.Width, entry.Height, level, 0, LevelSize(entry.Height, level));
			}
		}

		/// <summary>Writes textures' pixels on every core, then their levels on every core (a level at a time; RGBA, or BGRA).</summary>
		protected static void FillChains(Chain[] chains, bool bgra)
		{
			List<KeyValuePair<int, int>> pieces = new List<KeyValuePair<int, int>>();
			for (int c = 0; c < chains.Length; c++)
			{
				int count = (int)(chains[c].Pixels.Width * chains[c].Pixels.Height);
				for (int first = 0; first < count; first += FillPiece)
				{
					pieces.Add(new KeyValuePair<int, int>(c, first));
				}
			}

			Parallel.For(0, pieces.Count, i =>
			{
				Chain chain = chains[pieces[i].Key];
				int first = pieces[i].Value;
				int count = Math.Min(FillPiece, (int)(chain.Pixels.Width * chain.Pixels.Height) - first);
				chain.Pixels.Fill(chain.Data, first, count);
				if (bgra)
				{
					SwapRedBlue(chain.Data, first, count);
				}
			});

			// Each level from the one above: the rows shared out in pieces of about FillPiece texels.
			uint most = 0;
			foreach (Chain chain in chains)
			{
				most = Math.Max(most, chain.Entry.Levels);
			}

			for (int level = 1; level < most; level++)
			{
				pieces.Clear();
				for (int c = 0; c < chains.Length; c++)
				{
					Entry entry = chains[c].Entry;
					if (entry.Levels <= level)
					{
						continue;
					}

					uint rows = LevelSize(entry.Height, level), rowsPerPiece = (uint)Math.Max(1, FillPiece / (int)LevelSize(entry.Width, level));
					for (uint first = 0; first < rows; first += rowsPerPiece)
					{
						pieces.Add(new KeyValuePair<int, int>(c, (int)first));
					}
				}

				int current = level;
				Parallel.For(0, pieces.Count, i =>
				{
					Chain chain = chains[pieces[i].Key];
					Entry entry = chain.Entry;
					uint first = (uint)pieces[i].Value, rows = LevelSize(entry.Height, current), rowsPerPiece = (uint)Math.Max(1, FillPiece / (int)LevelSize(entry.Width, current));
					MakeLevelRows(chain.Data, entry.Width, entry.Height, current, first, Math.Min(rows, first + rowsPerPiece));
				});
			}
		}

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

		internal string Describe() => entries.Count + " textures (" + (pixelBytes >> 20) + " MB of pixels made" + (compressedCount > 0 ? ", " + compressedCount + " compressed in " + (compressedBytes >> 20) + " MB" : string.Empty) + (preloaded ? ", " + MadeAfterPreload + " made after the preload" : string.Empty) + "), " + DescribeStorage();

		public virtual void Dispose()
		{
			if (handOver != null && LibRender2.ObnsSharing.AloneDecoded == handOver)
			{
				LibRender2.ObnsSharing.AloneDecoded = null;
			}

			lock (handedOver)
			{
				handedOver.Clear();
			}

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
