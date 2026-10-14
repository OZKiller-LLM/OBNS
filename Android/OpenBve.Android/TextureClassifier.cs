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

		private static void SaveDiskCache()
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
					foreach (KeyValuePair<string, (long Length, long Ticks, TextureTransparencyType Type)> entry in diskCache)
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

				System.IO.File.Move(temporary, cacheFile, true);
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

		/// <summary>Classifies the textures of these objects' materials that are not classified yet; returns how many, and the time taken.</summary>
		/// <param name="prototypes">The objects about to be shown.</param>
		/// <param name="readMode">How textures are read here, for the disk cache's key (old-transparency option, texture size limit).</param>
		public static string Classify(IEnumerable<StaticObject> prototypes, string readMode)
		{
			Stopwatch clock = Stopwatch.StartNew();
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

			if (todo.Count == 0)
			{
				return "no textures to classify";
			}

			ConcurrentDictionary<TextureOrigin, TextureTransparencyType> results = new ConcurrentDictionary<TextureOrigin, TextureTransparencyType>();
			LoadDiskCache();
			List<TextureOrigin> decode = new List<TextureOrigin>(todo.Count);
			Dictionary<TextureOrigin, (string Key, long Length, long Ticks)> files = new Dictionary<TextureOrigin, (string, long, long)>();
			foreach (TextureOrigin origin in todo)
			{
				if (origin is PathOrigin pathOrigin)
				{
					try
					{
						System.IO.FileInfo info = new System.IO.FileInfo(pathOrigin.Path);
						if (info.Exists)
						{
							string key = Key(pathOrigin, readMode);
							long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
							if (diskCache.TryGetValue(key, out var known) && known.Length == length && known.Ticks == ticks)
							{
								results[origin] = known.Type;
								continue;
							}

							files[origin] = (key, length, ticks);
						}
					}
					catch (Exception)
					{
						// Decoded below, as without the cache.
					}
				}

				decode.Add(origin);
			}

			int fromCache = results.Count;
			// Phones short of memory decode as few at once as streaming does (AndroidHost.ParallelDecodes):
			// eight full-size decodes at a time got a 4 GB Galaxy A14 killed on the Tuen Ma Line's first load.
			int threads = Math.Max(1, Math.Min(Environment.ProcessorCount, AndroidHost.ParallelDecodes));
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

			bool learnt = false;
			foreach (KeyValuePair<TextureOrigin, (string Key, long Length, long Ticks)> file in files)
			{
				if (results.TryGetValue(file.Key, out TextureTransparencyType type))
				{
					diskCache[file.Value.Key] = (file.Value.Length, file.Value.Ticks, type);
					learnt = true;
				}
			}

			if (learnt)
			{
				SaveDiskCache();
			}

			foreach (KeyValuePair<TextureOrigin, TextureTransparencyType> result in results)
			{
				if (!TextureManager.textureCache.ContainsKey(result.Key))
				{
					TextureManager.textureCache.Add(result.Key, result.Value);
				}
			}

			return results.Count + " textures classified (" + fromCache + " remembered) in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s on " + Math.Min(Environment.ProcessorCount, AndroidHost.ParallelDecodes) + " threads";
		}

		private static void Add(Texture texture, HashSet<TextureOrigin> seen, List<TextureOrigin> todo)
		{
			if (texture?.Origin == null || TextureManager.textureCache.ContainsKey(texture.Origin) || !seen.Add(texture.Origin))
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
