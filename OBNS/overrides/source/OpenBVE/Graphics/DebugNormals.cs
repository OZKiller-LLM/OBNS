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
using LibRender2;
using LibRender2.Objects;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;

namespace OpenBve.Graphics
{
	/// <summary>
	/// The normals view as objects of the world: for each mesh drawn near the camera, a mesh of its normals as upstream's (purple,
	/// from each vertex along its normal, a hundredth of the mesh's size long), each normal a sliver of a triangle drawn as lines
	/// (the scene's lines pass), all of a mesh's in one face (one draw), placed as the object it belongs to.
	/// </summary>
	internal sealed class DebugNormals
	{
		/// <summary>
		/// How near an object's nearest part must be to have its normals drawn: on a large route every mesh's normals were 595,000
		/// draws a frame and most of the vertex memory (Tuen Ma Line, on Metal), and lines that far off are too short to see.
		/// </summary>
		private const double Range = 150.0;

		private sealed class Normals
		{
			internal StaticObject Lines;
			internal double Radius;
		}

		private ConditionalWeakTable<Mesh, Normals> normals = new ConditionalWeakTable<Mesh, Normals>();
		private ConditionalWeakTable<ObjectState, ObjectState> states = new ConditionalWeakTable<ObjectState, ObjectState>();
		private readonly List<Mesh> made = new List<Mesh>();

		/// <summary>Gives the scene the normals of these objects (near the camera, given one), in a lines pass of the current layer.</summary>
		internal void Gather(IWorldScene scene, IEnumerable<ObjectState> objects, Vector3? camera)
		{
			scene.Pass(WorldBlend.None, WorldLayer.AlphaNever, 0.0f, true, false, true, true);
			foreach (ObjectState source in objects)
			{
				Normals mesh = Of(source.Prototype.Mesh);
				if (mesh == null)
				{
					continue;
				}

				// The object's origin in the world (upstream's translations flip Z), less the mesh's own size.
				Matrix4D model = source.ModelMatrix;
				Vector3 origin = new Vector3(model.Row3.X, model.Row3.Y, -model.Row3.Z);
				if (camera.HasValue && (origin - camera.Value).Norm() - mesh.Radius > Range)
				{
					continue;
				}

				ObjectState state = states.GetValue(source, key => new ObjectState(mesh.Lines));
				state.Prototype = mesh.Lines;
				state.Scale = source.Scale;
				state.Rotate = source.Rotate;
				state.Translation = source.Translation;
				scene.Face(state, mesh.Lines.Mesh.Faces[0]);
			}
		}

		/// <summary>Lets go of the normals' meshes, their vertex buffers included (the view turned off).</summary>
		internal void Release()
		{
			foreach (Mesh mesh in made)
			{
				(mesh.VAO as VertexArrayObject)?.Dispose();
				mesh.VAO = null;
			}

			made.Clear();
			normals = new ConditionalWeakTable<Mesh, Normals>();
			states = new ConditionalWeakTable<ObjectState, ObjectState>();
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

		private Normals Of(Mesh mesh)
		{
			if (mesh == null || mesh.Vertices.Length == 0 || mesh.Faces.Length == 0)
			{
				return null;
			}

			if (normals.TryGetValue(mesh, out Normals found))
			{
				return found;
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
			// The farthest a vertex is from the origin, for the range test.
			double radius = Math.Max(Math.Max(min.Norm(), max.Norm()), (min + max).Norm() * 0.5 + size);
			List<VertexTemplate> vertices = new List<VertexTemplate>();
			HashSet<(int, Vector3)> drawn = new HashSet<(int, Vector3)>();
			foreach (MeshFace face in mesh.Faces)
			{
				foreach (MeshFaceVertex corner in face.Vertices)
				{
					// A vertex's normal once, however many faces share it.
					Vector3 from = mesh.Vertices[corner.Index].Coordinates, normal = corner.Normal;
					if (normal.IsNullVector() || !drawn.Add((corner.Index, normal)))
					{
						continue;
					}

					// A sliver: from the vertex to the normal's tip and back beside it, a fiftieth of its length off the line,
					// so that no API takes it for a triangle with no area.
					Vector3 side = Vector3.Cross(normal, Math.Abs(normal.Y) < 0.9 ? Vector3.Up : Vector3.Right);
					side.Normalize();
					vertices.Add(new Vertex(from));
					vertices.Add(new Vertex(from + normal * length));
					vertices.Add(new Vertex(from + side * (length * 0.02)));
				}
			}

			if (vertices.Count == 0)
			{
				return null;
			}

			int[] indices = new int[vertices.Count];
			for (int i = 0; i < indices.Length; i++)
			{
				indices[i] = i;
			}

			Mesh lines = new Mesh(vertices.ToArray(), new[] { indices }, new Color32(153, 51, 255, 255));
			lines.Faces[0].Flags = (FaceFlags)((byte)FaceFlags.Triangles | (byte)FaceFlags.Face2Mask);
			lines.Materials[0].Flags = MaterialFlags.DisableLighting;
			found = new Normals { Lines = new StaticObject(Program.CurrentHost) { Mesh = lines }, Radius = radius };
			found.Lines.Mesh.FinalizeMesh();
			normals.Add(mesh, found);
			made.Add(lines);
			return found;
		}
	}
}
