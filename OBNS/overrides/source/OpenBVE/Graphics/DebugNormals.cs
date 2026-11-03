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

// OBNS: not an upstream file. Upstream's normals view (OptionNormals), for an API drawing the game alone (OBNS/RENDERERS.md):
// upstream draws each mesh's normals as OpenGL lines (VAOExtensions.CreateNormalsVAO), which draw nothing then.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LibRender2.Objects;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;

namespace OpenBve.Graphics
{
	/// <summary>
	/// The normals view as objects of the world: for each mesh drawn, a mesh of its normals as upstream's (purple, from each face's
	/// vertex along its normal, a hundredth of the mesh's size long), each normal a sliver of a triangle drawn as lines (the
	/// scene's lines pass), placed as the object it belongs to.
	/// </summary>
	internal sealed class DebugNormals
	{
		private readonly ConditionalWeakTable<Mesh, StaticObject> normals = new ConditionalWeakTable<Mesh, StaticObject>();
		private readonly ConditionalWeakTable<ObjectState, ObjectState> states = new ConditionalWeakTable<ObjectState, ObjectState>();

		/// <summary>Gives the scene the normals of these objects, in a lines pass of the current layer.</summary>
		internal void Gather(IWorldScene scene, IEnumerable<ObjectState> objects)
		{
			scene.Pass(WorldBlend.None, WorldLayer.AlphaNever, 0.0f, true, false, true, true);
			foreach (ObjectState source in objects)
			{
				StaticObject lines = Normals(source.Prototype.Mesh);
				if (lines == null)
				{
					continue;
				}

				ObjectState state = states.GetValue(source, key => new ObjectState(lines));
				state.Prototype = lines;
				state.Scale = source.Scale;
				state.Rotate = source.Rotate;
				state.Translation = source.Translation;
				foreach (MeshFace face in lines.Mesh.Faces)
				{
					scene.Face(state, face);
				}
			}
		}

		/// <summary>The objects of these faces, each once.</summary>
		internal static List<ObjectState> Objects(params IEnumerable<FaceState>[] lists)
		{
			HashSet<ObjectState> seen = new HashSet<ObjectState>();
			List<ObjectState> objects = new List<ObjectState>();
			foreach (IEnumerable<FaceState> list in lists)
			{
				foreach (FaceState face in list)
				{
					if (face.Object?.Prototype?.Mesh != null && seen.Add(face.Object))
					{
						objects.Add(face.Object);
					}
				}
			}

			return objects;
		}

		private StaticObject Normals(Mesh mesh)
		{
			if (mesh == null || mesh.Vertices.Length == 0 || mesh.Faces.Length == 0)
			{
				return null;
			}

			if (normals.TryGetValue(mesh, out StaticObject made))
			{
				return made;
			}

			// As upstream: a hundredth of the mesh's largest side.
			Vector3 min = mesh.Vertices[0].Coordinates, max = min;
			foreach (VertexTemplate vertex in mesh.Vertices)
			{
				Vector3 c = vertex.Coordinates;
				min = new Vector3(Math.Min(min.X, c.X), Math.Min(min.Y, c.Y), Math.Min(min.Z, c.Z));
				max = new Vector3(Math.Max(max.X, c.X), Math.Max(max.Y, c.Y), Math.Max(max.Z, c.Z));
			}

			double size = Math.Max(max.X - min.X, Math.Max(max.Y - min.Y, max.Z - min.Z));
			double length = size > 0.0 ? size * 0.01 : 0.01;
			List<VertexTemplate> vertices = new List<VertexTemplate>();
			List<int[]> faces = new List<int[]>();
			foreach (MeshFace face in mesh.Faces)
			{
				foreach (MeshFaceVertex corner in face.Vertices)
				{
					Vector3 from = mesh.Vertices[corner.Index].Coordinates, normal = corner.Normal;
					if (normal.IsNullVector())
					{
						continue;
					}

					// A sliver: from the vertex to the normal's tip and back beside it, a fiftieth of its length off the line,
					// so that no API takes it for a triangle with no area.
					Vector3 side = Vector3.Cross(normal, Math.Abs(normal.Y) < 0.9 ? Vector3.Up : Vector3.Right);
					side.Normalize();
					Vector3 tip = from + normal * length;
					int first = vertices.Count;
					vertices.Add(new Vertex(from));
					vertices.Add(new Vertex(tip));
					vertices.Add(new Vertex(from + side * (length * 0.02)));
					faces.Add(new[] { first, first + 1, first + 2 });
				}
			}

			if (faces.Count == 0)
			{
				return null;
			}

			Mesh lines = new Mesh(vertices.ToArray(), faces.ToArray(), new Color32(153, 51, 255, 255));
			for (int i = 0; i < lines.Faces.Length; i++)
			{
				lines.Faces[i].Flags = (FaceFlags)((byte)FaceFlags.Triangles | (byte)FaceFlags.Face2Mask);
			}

			lines.Materials[0].Flags = MaterialFlags.DisableLighting;
			made = new StaticObject(Program.CurrentHost) { Mesh = lines };
			made.Mesh.FinalizeMesh();
			normals.Add(mesh, made);
			return made;
		}
	}
}
