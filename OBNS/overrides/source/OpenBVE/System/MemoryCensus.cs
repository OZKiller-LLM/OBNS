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

// OBNS: not an upstream file. A census of what the loaded route and train hold in memory, logged once when the
// simulation starts with OBNS_MEMSTATS=1: the managed heap after a full collection, the decoded texture pixels still
// held, and the objects' meshes and states (counts, and their size as the 64-bit runtime lays them out, roughly).

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenBveApi.Objects;
using OpenBveApi.World;

namespace OpenBve
{
	internal static class MemoryCensus
	{
		// 1: the census; 2: the census after a full collection (the live heap).
		private static readonly string mode = Environment.GetEnvironmentVariable("OBNS_MEMSTATS");

		// Approximate sizes on a 64-bit runtime: an object's header is 16 bytes, a reference 8.
		private const long VertexBytes = 16 + 24 + 16 + 8; // Vertex: Coordinates (3 doubles), TextureCoordinates (2 doubles), its array slot
		private const long FaceVertexBytes = 32; // MeshFaceVertex: Index, Normal (3 doubles), padded
		private const long FaceBytes = 24 + 24; // MeshFace in its array, and its vertex array's header
		private const long StateBytes = 16 + 8 + 24 + 4 * 128 + 48; // ObjectState: Prototype, WorldPosition, four Matrix4D, the rest

		internal static void Log()
		{
			if (mode != "1" && mode != "2")
			{
				return;
			}

			long before = GC.GetTotalMemory(false);
			long live = mode == "2" ? GC.GetTotalMemory(true) : -1;
			long pixels = Program.Renderer.TextureManager.ObnsHeldPixelBytes(out int cached, out int onHandles, out string origins);

			HashSet<StaticObject> prototypes = new HashSet<StaticObject>();
			int states = 0;
			long vertices = 0, faceVertices = 0, faces = 0;
			Dictionary<string, long> vertexTypes = new Dictionary<string, long>();
			foreach (List<ObjectState> list in new[] { Program.Renderer.StaticObjectStates, Program.Renderer.DynamicObjectStates })
			{
				if (list == null)
				{
					continue;
				}

				foreach (ObjectState state in list)
				{
					states++;
					Mesh mesh = state?.Prototype?.Mesh;
					if (mesh == null || !prototypes.Add(state.Prototype))
					{
						continue;
					}

					vertices += mesh.Vertices?.Length ?? 0;
					if (mesh.Vertices != null)
					{
						foreach (VertexTemplate vertex in mesh.Vertices)
						{
							string type = vertex?.GetType().Name ?? "null";
							vertexTypes.TryGetValue(type, out long n);
							vertexTypes[type] = n + 1;
						}
					}

					if (mesh.Faces != null)
					{
						faces += mesh.Faces.Length;
						foreach (MeshFace face in mesh.Faces)
						{
							faceVertices += face.Vertices?.Length ?? 0;
						}
					}
				}
			}

			long meshBytes = vertices * VertexBytes + faceVertices * FaceVertexBytes + faces * FaceBytes;
			System.Text.StringBuilder types = new System.Text.StringBuilder();
			foreach (KeyValuePair<string, long> pair in vertexTypes)
			{
				types.Append(types.Length > 0 ? ", " : "").Append(pair.Key).Append(' ').Append(pair.Value.ToString("N0", CultureInfo.InvariantCulture));
			}

			Program.FileSystem.AppendToLogFile(string.Format(CultureInfo.InvariantCulture,
				"OBNS memory census: managed heap {0:N0} MB before a full collection, {1} live after it; decoded texture pixels held {2:N0} MB ({3} in the decode cache, {4} on texture handles, of {5} registered; origins: {15}); " +
				"{6:N0} object states (~{7:N0} MB), {8:N0} distinct meshes: {9:N0} vertices ({10}), {11:N0} faces, {12:N0} face vertices (~{13:N0} MB); process private {14:N0} MB",
				before / 1048576.0, live < 0 ? "not measured (OBNS_MEMSTATS=2 collects first)" : (live / 1048576.0).ToString("N0", CultureInfo.InvariantCulture) + " MB", pixels / 1048576.0, cached, onHandles, Program.Renderer.TextureManager.RegisteredTexturesCount,
				states, states * StateBytes / 1048576.0, prototypes.Count, vertices, types, faces, faceVertices, meshBytes / 1048576.0,
				System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, origins));
		}
	}
}
