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
using System.IO;
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * The pipeline cache, kept between games. Making a pipeline compiles its shaders for the GPU,
	 * which takes tens of milliseconds on a phone - a stutter the first time each kind of object is
	 * drawn. Two files in the app's cache folder keep that work:
	 *
	 *   vulkan-pipelines.bin   the driver's pipeline cache data (vkGetPipelineCacheData);
	 *   vulkan-pipelines.keys  which pipelines the game used: their keys and vertex layouts.
	 *
	 * At the start of the next game every listed pipeline is made again, from the cached data, before
	 * the first frame. Both files start with the GPU's vendor, device, driver version and pipeline
	 * cache UUID; a file from another driver (an update, a restored backup) is ignored rather than
	 * handed to the driver, which some drivers do not survive. Saved when new pipelines have been
	 * made and none for a few seconds, when the window goes, and when the game ends.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const uint CacheMagic = 0x4B56424F; // "OBVK"
		private const uint CacheFormat = 1;
		private const int SaveAfterQuietFrames = 300;

		private string cacheDirectory;
		private int pipelinesUnsaved;
		private int framesSincePipeline;

		/// <summary>Pipelines made at start from the saved list, and how long that took, for the log.</summary>
		public string PipelineCacheReport { get; private set; } = "no pipeline cache";

		/// <summary>The header both files start with: this device and driver.</summary>
		private byte[] CacheHeader()
		{
			PhysicalDeviceProperties properties = context.Properties;
			using MemoryStream stream = new MemoryStream();
			using BinaryWriter writer = new BinaryWriter(stream);
			writer.Write(CacheMagic);
			writer.Write(CacheFormat);
			writer.Write(properties.VendorID);
			writer.Write(properties.DeviceID);
			writer.Write(properties.DriverVersion);
			// The 1.1 and 1.3 paths make different pipelines (and keys) on the same device.
			writer.Write(context.Vulkan13 ? 13 : 11);
			for (int i = 0; i < 16; i++)
			{
				writer.Write(properties.PipelineCacheUuid[i]);
			}

			writer.Flush();
			return stream.ToArray();
		}

		/// <summary>A file's contents after the header, or null if it is missing or from another device or driver.</summary>
		private byte[] ReadCacheFile(string name)
		{
			if (cacheDirectory == null)
			{
				return null;
			}

			try
			{
				string path = Path.Combine(cacheDirectory, name);
				if (!File.Exists(path))
				{
					return null;
				}

				byte[] data = File.ReadAllBytes(path);
				byte[] header = CacheHeader();
				if (data.Length < header.Length || !data.AsSpan(0, header.Length).SequenceEqual(header))
				{
					return null;
				}

				return data.AsSpan(header.Length).ToArray();
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return null;
			}
		}

		private void WriteCacheFile(string name, byte[] contents)
		{
			string path = Path.Combine(cacheDirectory, name);
			string temporary = path + ".new";
			using (FileStream stream = File.Create(temporary))
			{
				byte[] header = CacheHeader();
				stream.Write(header, 0, header.Length);
				stream.Write(contents, 0, contents.Length);
			}

			File.Move(temporary, path, true);
		}

		/// <summary>The pipeline cache, with the saved data if there is any for this device and driver.</summary>
		private void CreatePipelineCache()
		{
			byte[] saved = ReadCacheFile("vulkan-pipelines.bin");
			fixed (byte* data = saved)
			{
				PipelineCacheCreateInfo info = new PipelineCacheCreateInfo
				{
					SType = StructureType.PipelineCacheCreateInfo,
					InitialDataSize = (nuint)(saved?.Length ?? 0),
					PInitialData = data
				};
				Result result = vk.CreatePipelineCache(device, in info, null, out pipelineCache);
				if (result != Result.Success && saved != null)
				{
					// The driver would not take the saved data: start empty.
					info.InitialDataSize = 0;
					info.PInitialData = null;
					result = vk.CreatePipelineCache(device, in info, null, out pipelineCache);
				}

				Check(result, "vkCreatePipelineCache");
			}
		}

		/// <summary>Makes the pipelines the last games used (from the cached data), so their first draws do not stutter.</summary>
		private void PrewarmPipelines()
		{
			byte[] keys = ReadCacheFile("vulkan-pipelines.keys");
			if (keys == null)
			{
				PipelineCacheReport = cacheDirectory == null ? "no pipeline cache" : "pipeline cache empty";
				return;
			}

			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			int made = 0;
			try
			{
				using BinaryReader reader = new BinaryReader(new MemoryStream(keys));
				int count = reader.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					ulong state = reader.ReadUInt64();
					VertexLayout layout = default;
					for (int location = 0; location < DefaultLocations; location++)
					{
						Format format = (Format)reader.ReadInt32();
						int stride = reader.ReadInt32();
						layout.Set(location, format, stride);
					}

					ProgramKind kind = (ProgramKind)(state & 3);
					int layoutId = kind == ProgramKind.Default ? LayoutIdOf(layout) : 0;
					ulong key = state | (ulong)layoutId << 22;
					if (!pipelines.ContainsKey(key))
					{
						pipelines[key] = CreatePipeline(key);
						made++;
					}
				}
			}
			catch (Exception e) when (e is EndOfStreamException || e is IOException)
			{
				ReportOnce("the saved pipeline list is damaged: " + e.Message);
			}

			double milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			PipelineCacheReport = made + " pipelines made from the cache in " + milliseconds.ToString("0") + " ms";
			pipelinesUnsaved = 0;
		}

		/// <summary>A vertex layout's number, given it a new one if it is not known yet.</summary>
		private int LayoutIdOf(VertexLayout layout)
		{
			if (!vertexLayouts.TryGetValue(layout, out int id))
			{
				id = vertexLayoutList.Count;
				vertexLayouts[layout] = id;
				vertexLayoutList.Add(layout);
			}

			return id;
		}

		/// <summary>Counts a new pipeline (to be saved).</summary>
		private void PipelineMade()
		{
			pipelinesUnsaved++;
			framesSincePipeline = 0;
		}

		/// <summary>After a frame: saves the cache once new pipelines have been made and none for a few seconds.</summary>
		private void PipelineCacheFrame()
		{
			if (pipelinesUnsaved > 0 && ++framesSincePipeline >= SaveAfterQuietFrames)
			{
				SavePipelineCache();
			}
		}

		/// <summary>Saves the pipeline cache and the list of pipelines, if anything new was made.</summary>
		public void SavePipelineCache()
		{
			if (cacheDirectory == null || pipelinesUnsaved == 0)
			{
				return;
			}

			try
			{
				nuint size = 0;
				Check(vk.GetPipelineCacheData(device, pipelineCache, &size, null), "vkGetPipelineCacheData");
				byte[] data = new byte[(int)size];
				fixed (byte* p = data)
				{
					Check(vk.GetPipelineCacheData(device, pipelineCache, &size, p), "vkGetPipelineCacheData");
				}

				if ((int)size < data.Length)
				{
					Array.Resize(ref data, (int)size);
				}

				using MemoryStream keys = new MemoryStream();
				using (BinaryWriter writer = new BinaryWriter(keys))
				{
					writer.Write(pipelines.Count);
					foreach (ulong key in pipelines.Keys)
					{
						writer.Write(key & ((1UL << 22) - 1));
						VertexLayout layout = (ProgramKind)(key & 3) == ProgramKind.Default ? vertexLayoutList[(int)(key >> 22)] : default;
						for (int location = 0; location < DefaultLocations; location++)
						{
							writer.Write((int)layout.Format(location));
							writer.Write(layout.Stride(location));
						}
					}
				}

				WriteCacheFile("vulkan-pipelines.bin", data);
				WriteCacheFile("vulkan-pipelines.keys", keys.ToArray());
				pipelinesUnsaved = 0;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
			{
				ReportOnce("the pipeline cache could not be saved: " + e.Message);
			}
		}
	}
}
