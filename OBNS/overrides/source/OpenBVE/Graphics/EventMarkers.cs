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

// OBNS: not an upstream file. Upstream's event markers (Interface.CurrentOptions.ShowEvents), for an API drawing the game alone
// (OBNS/RENDERERS.md): upstream draws each as a cube through OpenGL (Cube.Draw), which draws nothing then.

using System.Collections.Generic;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using OpenBveApi.World;

namespace OpenBve.Graphics
{
	/// <summary>
	/// The event markers as objects of the world: a cube a texture, placed as upstream's Cube.Draw places it (0.2 in size, turned to
	/// face the camera, unlit and textured), its faces given to the world scene with the world's opaque faces.
	/// </summary>
	internal sealed class EventMarkers
	{
		private readonly Dictionary<Texture, StaticObject> cubes = new Dictionary<Texture, StaticObject>();
		private readonly List<ObjectState> states = new List<ObjectState>();

		/// <summary>Gives the scene this frame's markers (the renderer's CubesToDraw, which upstream's events update).</summary>
		internal void Gather(IWorldScene scene, NewRenderer renderer)
		{
			Matrix4D rotate = (Matrix4D)new Transformation(renderer.Camera.AbsoluteDirection, renderer.Camera.AbsoluteUp, renderer.Camera.AbsoluteSide);
			Matrix4D scale = Matrix4D.Scale(new Vector3(0.2, 0.2, 0.2));
			int used = 0;
			foreach (KeyValuePair<Texture, HashSet<Vector3>> markers in renderer.CubesToDraw)
			{
				StaticObject cube = Cube(markers.Key);
				foreach (Vector3 position in markers.Value)
				{
					if (used == states.Count)
					{
						states.Add(new ObjectState(cube));
					}

					ObjectState state = states[used++];
					state.Prototype = cube;
					state.Scale = scale;
					state.Rotate = rotate;
					state.Translation = Matrix4D.CreateTranslation(position.X, position.Y, -position.Z);
					foreach (MeshFace face in cube.Mesh.Faces)
					{
						scene.Face(state, face);
					}
				}
			}
		}

		/// <summary>The cube of a texture, as upstream's (a nominal 2 across, each side's texture whole).</summary>
		private StaticObject Cube(Texture texture)
		{
			if (cubes.TryGetValue(texture, out StaticObject cube))
			{
				return cube;
			}

			// Each side's corners, counter-clockwise from the outside, and the side's texture coordinates.
			int[][] sides =
			{
				new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 8, 9, 10, 11 },
				new[] { 12, 13, 14, 15 }, new[] { 16, 17, 18, 19 }, new[] { 20, 21, 22, 23 }
			};
			Vector3[] corners =
			{
				new Vector3(1, 1, 1), new Vector3(-1, 1, 1), new Vector3(-1, -1, 1), new Vector3(1, -1, 1),       // back
				new Vector3(1, 1, -1), new Vector3(1, 1, 1), new Vector3(1, -1, 1), new Vector3(1, -1, -1),       // right
				new Vector3(-1, 1, -1), new Vector3(1, 1, -1), new Vector3(1, -1, -1), new Vector3(-1, -1, -1),   // front
				new Vector3(-1, 1, 1), new Vector3(-1, 1, -1), new Vector3(-1, -1, -1), new Vector3(-1, -1, 1),   // left
				new Vector3(1, 1, -1), new Vector3(-1, 1, -1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1),       // top
				new Vector3(1, -1, 1), new Vector3(-1, -1, 1), new Vector3(-1, -1, -1), new Vector3(1, -1, -1)    // bottom
			};
			Vector2[] uv = { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
			VertexTemplate[] vertices = new VertexTemplate[corners.Length];
			for (int i = 0; i < corners.Length; i++)
			{
				vertices[i] = new Vertex(corners[i], uv[i % 4]);
			}

			Mesh mesh = new Mesh(vertices, sides, Color32.White);
			for (int i = 0; i < mesh.Faces.Length; i++)
			{
				// Both sides drawn, whichever way the cube is turned.
				mesh.Faces[i].Flags = (FaceFlags)((byte)FaceFlags.Polygon | (byte)FaceFlags.Face2Mask);
			}

			mesh.Materials[0].DaytimeTexture = texture;
			mesh.Materials[0].Flags = MaterialFlags.DisableLighting;
			mesh.Materials[0].WrapMode = OpenGlTextureWrapMode.ClampClamp;
			mesh.Materials[0].BlendMode = MeshMaterialBlendMode.Normal;
			cube = new StaticObject(Program.CurrentHost) { Mesh = mesh };
			cube.Mesh.FinalizeMesh();
			cubes[texture] = cube;
			return cube;
		}
	}
}
