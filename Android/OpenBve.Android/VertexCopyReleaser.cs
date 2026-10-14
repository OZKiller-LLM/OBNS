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
using System.Collections.Generic;
using System.Reflection;
using LibRender2;
using OpenBveApi.Objects;

namespace OpenBve.Android
{
	/*
	 * Upstream's VertexBufferObject keeps the LibRenderVertex[] it was made from (60 bytes a
	 * vertex) for as long as the buffer lives, but reads it only once, to upload it: a changed mesh
	 * gets a new buffer built from the mesh (VAOExtensions.UpdateVAO), and a lost context rebuilds
	 * from the meshes too. So every uploaded vertex stayed in managed memory as well as in the mesh
	 * and on the GPU - which on a phone is the same RAM. On the Tuen Ma Line that pushed the game
	 * past 4 GB phones' limit. This clears the copy once a buffer has been uploaded: the objects'
	 * buffers a few thousand states per frame, and the static batches' as they are built. The field
	 * is private upstream (and not to be edited there), hence reflection.
	 */
	internal static class VertexCopyReleaser
	{
		private static readonly FieldInfo VboField = typeof(VertexArrayObject).GetField("vbo", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo DataField = typeof(VertexBufferObject).GetField("vertexData", BindingFlags.Instance | BindingFlags.NonPublic);

		/// <summary>Bytes of vertex copies released so far, for the memory log line.</summary>
		public static long ReleasedBytes { get; private set; }

		/// <summary>Whether this is on (the release_vertex_copies intent extra; on unless false).</summary>
		public static bool Enabled = true;

		private static int cursor;

		/// <summary>Releases the vertex copy of a mesh whose buffers have been uploaded.</summary>
		public static void Release(Mesh mesh)
		{
			if (!Enabled || VboField == null || DataField == null || !(mesh?.VAO is VertexArrayObject vao))
			{
				return;
			}

			if (VboField.GetValue(vao) is VertexBufferObject vbo && DataField.GetValue(vbo) is LibRenderVertex[] data)
			{
				DataField.SetValue(vbo, null);
				ReleasedBytes += (long)data.Length * LibRenderVertex.SizeInBytes;
			}
		}

		/// <summary>Releases every uploaded object buffer's copy at once (after a load stage).</summary>
		public static void ReleaseAll(BaseRenderer renderer)
		{
			foreach (List<ObjectState> states in new[] { renderer.StaticObjectStates, renderer.DynamicObjectStates })
			{
				if (states == null)
				{
					continue;
				}

				foreach (ObjectState state in states)
				{
					Release(state?.Prototype?.Mesh);
				}
			}
		}

		/// <summary>
		/// Works through the renderer's object states, a thousand a frame, for buffers made later
		/// (objects first shown while driving). Render thread only.
		/// </summary>
		public static void Step(BaseRenderer renderer, int perFrame = 1000)
		{
			if (!Enabled)
			{
				return;
			}

			List<ObjectState> statics = renderer.StaticObjectStates;
			List<ObjectState> dynamics = renderer.DynamicObjectStates;
			int total = (statics?.Count ?? 0) + (dynamics?.Count ?? 0);
			if (total == 0)
			{
				return;
			}

			for (int i = 0; i < perFrame && i < total; i++)
			{
				if (cursor >= total)
				{
					cursor = 0;
				}

				ObjectState state = cursor < statics.Count ? statics[cursor] : dynamics[cursor - statics.Count];
				cursor++;
				Release(state?.Prototype?.Mesh);
			}
		}
	}
}
