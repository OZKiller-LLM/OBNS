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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LibRender2;
using LibRender2.Textures;
using LibRender2.Trains;
using OpenBveApi.Colors;
using OpenBveApi.Objects;
using OpenBveApi.Textures;

namespace OpenBve.Android
{
	/// <summary>
	/// Classifies textures' transparency on all cores before objects are first shown.
	/// </summary>
	/// <remarks>
	/// Upstream's ObjectLibrary.ShowObject decides whether a face is drawn opaque or transparent
	/// by the transparency of its texture, and the first time it meets a texture it decodes the
	/// whole image to find out - on the loading thread, one at a time - then throws the pixels
	/// away (they are decoded again when first drawn). Placing the Tuen Ma Line's SP1900 took 39 s
	/// of that for 2,372 PNGs. The answer depends only on the image, and ShowObject looks in
	/// TextureManager.textureCache first, so it is worked out here in parallel for the textures
	/// about to be shown, and put in that cache: ShowObject then finds every answer ready.
	///
	/// The answers are also kept on disk (the app's cache folder), so the next load of the same
	/// content decodes nothing: on the Tuen Ma Line that step took 30-44 s on a Galaxy A05s and
	/// 6-11 s on faster phones, every time. An answer is reused only for the same file (path,
	/// size, modification time) read the same way: the same texture parameters (clip region,
	/// colour key), the same old-transparency option and the same texture size limit (shrinking
	/// can turn a clear-or-solid texture into one with partial alpha).
	/// </remarks>
	internal static class TextureClassifier
	{
		private const string CacheFileName = "texture-transparency.v1.txt";

		/// <summary>Decodes at once while classifying, when set (the Switch: all three of its cores, the loading thread
		/// waiting meanwhile); otherwise as many as streaming decodes (AndroidHost.ParallelDecodes).</summary>
		public static int Threads;
		private static Dictionary<string, (long Length, long Ticks, TextureTransparencyType Type)> diskCache;
		private static string cacheFile;

		/// <summary>The disk cache's folder (the app's cache folder); classification works without it.</summary>
		public static string CacheFolder { get; set; }

		private static void LoadDiskCache()
		{
			if (diskCache != null)
			{
				return;
			}

			diskCache = new Dictionary<string, (long, long, TextureTransparencyType)>(StringComparer.Ordinal);
			if (string.IsNullOrEmpty(CacheFolder))
			{
				return;
			}

			cacheFile = System.IO.Path.Combine(CacheFolder, CacheFileName);
			try
			{
				if (!System.IO.File.Exists(cacheFile))
				{
					return;
				}

				foreach (string line in System.IO.File.ReadLines(cacheFile))
				{
					string[] parts = line.Split('\t');
					if (parts.Length == 4 && long.TryParse(parts[1], out long length) && long.TryParse(parts[2], out long ticks) && int.TryParse(parts[3], out int type))
					{
						diskCache[parts[0]] = (length, ticks, (TextureTransparencyType)type);
					}
				}
			}
			catch (Exception)
			{
				// Unreadable: classify as if there were none, and write a new one.
			}
		}

		private static void SaveDiskCache(IEnumerable<KeyValuePair<string, (long Length, long Ticks, TextureTransparencyType Type)>> entries)
		{
			if (cacheFile == null)
			{
				return;
			}

			try
			{
				string temporary = cacheFile + ".tmp";
				using (System.IO.StreamWriter writer = new System.IO.StreamWriter(temporary, false, System.Text.Encoding.UTF8))
				{
					foreach (KeyValuePair<string, (long Length, long Ticks, TextureTransparencyType Type)> entry in entries)
					{
						writer.Write(entry.Key);
						writer.Write('\t');
						writer.Write(entry.Value.Length);
						writer.Write('\t');
						writer.Write(entry.Value.Ticks);
						writer.Write('\t');
						writer.WriteLine((int)entry.Value.Type);
					}
				}

				// Deleted first: the Switch's file system doesn't rename over an existing file.
				if (System.IO.File.Exists(cacheFile))
				{
					System.IO.File.Delete(cacheFile);
				}

				System.IO.File.Move(temporary, cacheFile);
			}
			catch (Exception)
			{
				// No cache next time; nothing else depends on it.
			}
		}

		/// <summary>What decides a texture's answer besides the file itself.</summary>
		private static string Key(PathOrigin origin, string readMode)
		{
			TextureParameters parameters = origin.Parameters;
			TextureClipRegion clip = parameters?.ClipRegion;
			Color24? key = parameters?.TransparentColor;
			return origin.Path + "|" + (clip is null ? "-" : clip.Left + "," + clip.Top + "," + clip.Width + "," + clip.Height) + "|" +
			       (key.HasValue ? key.Value.R + "," + key.Value.G + "," + key.Value.B : "-") + "|" + readMode;
		}

		/// <summary>Guards the disk cache: the loading thread, the background pass and the render thread's lookups.</summary>
		private static readonly object diskLock = new object();

		/// <summary>The read mode of the current game's textures (set by <see cref="Classify"/>), for the disk cache's keys.</summary>
		private static string currentReadMode;

		/// <summary>Answers learnt since the disk cache was last saved.</summary>
		private static int unsaved;

		/// <summary>
		/// A file's size and modification time as its handle took them when registered (OBNS's PathOrigin asks once),
		/// so no answer needs the storage asked again; false if the file couldn't be read.
		/// </summary>
		private static bool FileStamp(PathOrigin origin, out long length, out long ticks)
		{
			try
			{
				length = origin.FileSize;
				ticks = origin.LastModificationTime.ToUniversalTime().Ticks;
				return true;
			}
			catch (Exception)
			{
				length = ticks = 0;
				return false;
			}
		}

		/// <summary>The disk cache's answer for a texture of the current game, if it has one (call under diskLock).</summary>
		private static bool RememberedLocked(TextureOrigin origin, out TextureTransparencyType type, out string key, out long length, out long ticks)
		{
			type = TextureTransparencyType.Opaque;
			key = null;
			length = ticks = 0;
			if (!(origin is PathOrigin pathOrigin) || pathOrigin.Path == null || currentReadMode == null || !FileStamp(pathOrigin, out length, out ticks))
			{
				return false;
			}

			LoadDiskCache();
			key = Key(pathOrigin, currentReadMode);
			if (diskCache.TryGetValue(key, out var known) && known.Length == length && known.Ticks == ticks)
			{
				type = known.Type;
				return true;
			}

			return false;
		}

		/// <summary>
		/// The disk cache's answer for a texture, if it has one (TextureManager.RememberedType: ObjectLibrary asks before
		/// decoding a texture no one classified).
		/// </summary>
		public static bool Remembered(TextureOrigin origin, out TextureTransparencyType type)
		{
			lock (diskLock)
			{
				return RememberedLocked(origin, out type, out _, out _, out _);
			}
		}

		/// <summary>An answer worked out elsewhere (TextureManager.TypeWorkedOut: ObjectLibrary's own decodes), kept on disk.</summary>
		public static void Learn(TextureOrigin origin, TextureTransparencyType type)
		{
			if (!(origin is PathOrigin pathOrigin) || pathOrigin.Path == null)
			{
				return;
			}

			lock (diskLock)
			{
				if (currentReadMode == null || !FileStamp(pathOrigin, out long length, out long ticks))
				{
					return;
				}

				LoadDiskCache();
				diskCache[Key(pathOrigin, currentReadMode)] = (length, ticks, type);
				unsaved++;
			}
		}

		/// <summary>Classifies the textures of these objects' materials that are not classified yet; returns how many, and the time taken.</summary>
		/// <param name="prototypes">The objects about to be shown.</param>
		/// <param name="readMode">How textures are read here, for the disk cache's key (old-transparency option, texture size limit).</param>
		public static string Classify(IEnumerable<StaticObject> prototypes, string readMode)
		{
			Stopwatch clock = Stopwatch.StartNew();
			lock (diskLock)
			{
				currentReadMode = readMode;
			}

			List<TextureOrigin> todo = Textures(prototypes);
			if (todo.Count == 0)
			{
				return "no textures to classify";
			}

			ConcurrentDictionary<TextureOrigin, TextureTransparencyType> results = new ConcurrentDictionary<TextureOrigin, TextureTransparencyType>();
			List<TextureOrigin> decode = new List<TextureOrigin>(todo.Count);
			Dictionary<TextureOrigin, (string Key, long Length, long Ticks)> files = new Dictionary<TextureOrigin, (string, long, long)>();
			lock (diskLock)
			{
				foreach (TextureOrigin origin in todo)
				{
					if (RememberedLocked(origin, out TextureTransparencyType known, out string key, out long length, out long ticks))
					{
						results[origin] = known;
						continue;
					}

					if (key != null)
					{
						files[origin] = (key, length, ticks);
					}

					decode.Add(origin);
				}
			}

			int fromCache = results.Count;
			// Phones short of memory decode as few at once as streaming does (AndroidHost.ParallelDecodes):
			// eight full-size decodes at a time got a 4 GB Galaxy A14 killed on the Tuen Ma Line's first load.
			int threads = Math.Max(1, Math.Min(Environment.ProcessorCount, Threads > 0 ? Threads : AndroidHost.ParallelDecodes));
			Parallel.ForEach(decode, new ParallelOptions { MaxDegreeOfParallelism = threads }, origin =>
			{
				try
				{
					// As ShowObject: a texture that fails to load counts as opaque.
					results[origin] = origin.GetTexture(out Texture texture) && texture != null ? texture.GetTransparencyType() : TextureTransparencyType.Opaque;
				}
				catch (Exception)
				{
					// Left for ShowObject to try, and report, as it always would.
				}
			});

			lock (diskLock)
			{
				foreach (KeyValuePair<TextureOrigin, (string Key, long Length, long Ticks)> file in files)
				{
					if (results.TryGetValue(file.Key, out TextureTransparencyType type))
					{
						diskCache[file.Value.Key] = (file.Value.Length, file.Value.Ticks, type);
						unsaved++;
					}
				}

			}

			SaveIfLearnt();
			foreach (KeyValuePair<TextureOrigin, TextureTransparencyType> result in results)
			{
				TextureManager.AddCachedType(result.Key, result.Value);
			}

			return results.Count + " textures classified (" + fromCache + " remembered) in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s on " + threads + " threads";
		}

		/// <summary>The textures of these objects' materials not classified yet, each once, in the objects' order.</summary>
		private static List<TextureOrigin> Textures(IEnumerable<StaticObject> prototypes)
		{
			HashSet<TextureOrigin> seen = new HashSet<TextureOrigin>();
			List<TextureOrigin> todo = new List<TextureOrigin>();
			foreach (StaticObject prototype in prototypes)
			{
				MeshMaterial[] materials = prototype?.Mesh?.Materials;
				if (materials == null)
				{
					continue;
				}

				foreach (MeshMaterial material in materials)
				{
					Add(material.DaytimeTexture, seen, todo);
					Add(material.NighttimeTexture, seen, todo);
				}
			}

			return todo;
		}

		private static readonly object saveLock = new object();

		/// <summary>
		/// Saves the disk cache if anything was learnt since the last save: a copy taken under diskLock, written outside it
		/// (the render thread's lookups don't wait for the SD card).
		/// </summary>
		private static void SaveIfLearnt()
		{
			lock (saveLock)
			{
				KeyValuePair<string, (long Length, long Ticks, TextureTransparencyType Type)>[] entries;
				lock (diskLock)
				{
					if (unsaved == 0 || diskCache == null)
					{
						return;
					}

					entries = new KeyValuePair<string, (long, long, TextureTransparencyType)>[diskCache.Count];
					((ICollection<KeyValuePair<string, (long, long, TextureTransparencyType)>>)diskCache).CopyTo(entries, 0);
					unsaved = 0;
				}

				SaveDiskCache(entries);
			}
		}

		/*
		 * The rest of the scenery, classified on a thread of its own while the game runs (OBNS 1.31N). Only the textures
		 * of what is in view at the start are classified while loading; every other static object's texture was decoded
		 * by ObjectLibrary.ShowObject on the render thread when it first came into view - on the Switch, stalls of up to
		 * 5.8 s on the Tuen Ma Line (2026-11-07). Nearest the start first; answers the disk cache has cost no decode, and
		 * new ones are kept there (with those ObjectLibrary still works out itself, TextureManager.TypeWorkedOut), saved
		 * every so often and when the game ends.
		 */
		private static int backgroundGeneration;
		private static string backgroundSummary = "not started";

		/// <summary>What the background pass has done so far, for the log.</summary>
		public static string BackgroundSummary => backgroundSummary;

		/// <summary>Starts classifying these objects' textures in the background (stopping any earlier pass).</summary>
		/// <param name="prototypes">The objects, nearest first; listed now, on the calling thread.</param>
		public static void StartBackground(IEnumerable<StaticObject> prototypes)
		{
			StopBackground();
			List<TextureOrigin> todo = Textures(prototypes);
			int generation = System.Threading.Interlocked.Increment(ref backgroundGeneration);
			backgroundSummary = todo.Count + " to classify";
			if (todo.Count == 0)
			{
				return;
			}

			System.Threading.Thread thread = new System.Threading.Thread(() => Background(todo, generation))
			{
				IsBackground = true,
				Name = "texture classification",
				Priority = System.Threading.ThreadPriority.Lowest
			};
			thread.Start();
		}

		/// <summary>Stops the background pass (a new game, or the end of this one), keeping what was learnt.</summary>
		public static void StopBackground()
		{
			System.Threading.Interlocked.Increment(ref backgroundGeneration);
			SaveIfLearnt();
		}

		private static void Background(List<TextureOrigin> todo, int generation)
		{
			Stopwatch clock = Stopwatch.StartNew();
			int remembered = 0, decoded = 0, done = 0;
			try
			{
				foreach (TextureOrigin origin in todo)
				{
					if (generation != System.Threading.Volatile.Read(ref backgroundGeneration))
					{
						return;
					}

					done++;
					if (TextureManager.TryGetCachedType(origin, out _))
					{
						continue;
					}

					TextureTransparencyType type;
					if (Remembered(origin, out type))
					{
						remembered++;
					}
					else
					{
						try
						{
							type = origin.GetTexture(out Texture texture) && texture != null ? texture.GetTransparencyType() : TextureTransparencyType.Opaque;
						}
						catch (Exception)
						{
							// Left for ShowObject, as it always would.
							continue;
						}

						decoded++;
						Learn(origin, type);
						// Room for the game's own threads between decodes.
						System.Threading.Thread.Sleep(1);
					}

					TextureManager.AddCachedType(origin, type);
					if (decoded % 200 == 199)
					{
						SaveIfLearnt();
					}

					if ((done & 63) == 0)
					{
						backgroundSummary = done + " of " + todo.Count + " (" + remembered + " remembered, " + decoded + " decoded) in " +
						                    clock.Elapsed.TotalSeconds.ToString("0") + " s";
					}
				}

				backgroundSummary = "all " + todo.Count + " (" + remembered + " remembered, " + decoded + " decoded) in " +
				                    clock.Elapsed.TotalSeconds.ToString("0") + " s";
				// Then what ObjectLibrary still learns itself, saved every half minute until the game ends.
				while (generation == System.Threading.Volatile.Read(ref backgroundGeneration))
				{
					SaveIfLearnt();
					System.Threading.Thread.Sleep(30000);
				}
			}
			catch (Exception e)
			{
				backgroundSummary = "stopped: " + e.GetType().Name + ": " + e.Message;
			}
		}

		private static void Add(Texture texture, HashSet<TextureOrigin> seen, List<TextureOrigin> todo)
		{
			if (texture?.Origin == null || TextureManager.TryGetCachedType(texture.Origin, out _) || !seen.Add(texture.Origin))
			{
				return;
			}

			todo.Add(texture.Origin);
		}

		/// <summary>Every object of a car section: each element's every state.</summary>
		public static IEnumerable<StaticObject> Prototypes(CarSection section)
		{
			if (section?.Groups == null)
			{
				yield break;
			}

			foreach (ElementsGroup group in section.Groups)
			{
				if (group?.Elements == null)
				{
					continue;
				}

				foreach (AnimatedObject element in group.Elements)
				{
					if (element?.States == null)
					{
						continue;
					}

					foreach (ObjectState state in element.States)
					{
						if (state?.Prototype != null)
						{
							yield return state.Prototype;
						}
					}
				}
			}
		}
	}
}
