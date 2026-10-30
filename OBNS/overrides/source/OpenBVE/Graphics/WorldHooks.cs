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

// OBNS: not an upstream file. The world layer told of upstream's meshes and textures as they are made, updated and let
// go of (OBNS's LibRender2 copies' hooks), whichever API draws it (OBNS/VULKAN.md, OBNS/DIRECT3D.md).

using System;
using System.Diagnostics;
using System.Text;
using LibRender2;

namespace OpenBve.Graphics
{
	internal static class WorldHooks
	{
		private static string api;
		private static WorldMeshes meshes;
		private static WorldTextures textures;
		private static Func<string> extra;
		private static bool failed;

		// The hooks' calls and time, logged every 5 seconds while they are busy (OBNS_VULKAN_DEBUG=1, OBNS_D3D11_DEBUG=1).
		private static bool logHooks;
		private static readonly long[] calls = new long[7], ticks = new long[7];
		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static long lastLog;
		private static readonly string[] names = { "meshes indexed", "VAOs disposed", "animated texture frames", "textures unloaded", "vertex buffers made", "vertex buffers freed", "vertex writes" };

		private static void Log(string text) => Program.FileSystem.AppendToLogFile(text);

		/// <summary>
		/// Has upstream's VAOs and textures copied by the layer's meshes and textures: the API's name for the log, its
		/// switches' prefix, and more for the hooks' log line.
		/// </summary>
		internal static void Install(string apiName, string switches, WorldMeshes worldMeshes, WorldTextures worldTextures, Func<string> more = null)
		{
			api = apiName;
			meshes = worldMeshes;
			textures = worldTextures;
			extra = more;
			logHooks = Environment.GetEnvironmentVariable(switches + "_DEBUG") == "1";
			ObnsSharing.Error = message => Log("OBNS " + api + " world layer: OpenGL error on shared storage: " + message);
			// Vertex buffers: one copy in storage both APIs use (Vulkan), or a copy of the layer's own (Direct3D 11).
			ObnsSharing.AllocateBuffer = bytes => Guard(() => meshes.AllocateVertices(bytes), 4);
			ObnsSharing.FreeBuffer = shared => Guard(() => { meshes.FreeVertices(shared); return null; }, 5);
			ObnsSharing.BufferWritten = buffer => meshes.Written(buffer);
			ObnsSharing.WriteVertices = (shared, data, offset) => Guard(() => meshes.WriteVertices(shared, data, offset) ? shared : null, 6) != null;
			// Textures are not shared (OpenGL draws far more slowly with textures over shared memory): the layer keeps its
			// own copy of the world's (WorldTextures), and of animated ones' frames as OpenGL uploads them.
			ObnsSharing.AnimatedFrame = (handle, decoded, bytes, opaque) => Guard(() => { textures.AnimatedFrame(handle, decoded, bytes, opaque); return null; }, 2);
			ObnsSharing.TextureUnloading = handle => Guard(() => { textures.Unloading(handle); return null; }, 3);

			// The meshes' faces, indexed for the layer's triangle lists.
			VAOExtensions.ObnsMeshUploaded = mesh => Guard(() => { meshes.Uploaded(mesh); return null; }, 0);
			VAOExtensions.ObnsVaoDisposed = vao => Guard(() => { meshes.Disposed(vao); return null; }, 1);
		}

		/// <summary>
		/// Runs a hook, so a failure is logged (once) and does not break upstream's drawing: storage not given is left to
		/// OpenGL (null).
		/// </summary>
		private static ObnsSharedMemory Guard(Func<ObnsSharedMemory> action, int hook)
		{
			long start = clock.ElapsedTicks;
			ObnsSharedMemory result = null;
			try
			{
				result = action();
			}
			catch (Exception ex)
			{
				if (!failed)
				{
					failed = true;
					Log("OBNS " + api + " world layer: a copy failed: " + ex);
				}
			}

			long now = clock.ElapsedTicks;
			calls[hook]++;
			ticks[hook] += now - start;
			if (logHooks && now - lastLog > 5 * Stopwatch.Frequency)
			{
				lastLog = now;
				StringBuilder line = new StringBuilder("OBNS " + api + " copies:");
				for (int i = 0; i < calls.Length; i++)
				{
					line.Append(' ').Append(calls[i]).Append(' ').Append(names[i]).Append(" (").Append((ticks[i] * 1000 / Stopwatch.Frequency)).Append(" ms)").Append(i < calls.Length - 1 ? "," : "");
				}

				line.Append("; ");
				if (extra != null)
				{
					line.Append(extra()).Append("; ");
				}

				line.Append(meshes.Describe()).Append("; ").Append(textures.Describe());
				Log(line.ToString());
			}

			return result;
		}
	}
}
