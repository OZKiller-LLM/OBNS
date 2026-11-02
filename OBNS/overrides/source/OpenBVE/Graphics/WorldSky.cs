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

// OBNS: not an upstream file. The sky cylinder as a mesh, for an API drawing the frame's background (OBNS/RENDERERS.md): the
// vertices of upstream's StaticBackground.CreateVAO, in the order it draws them.

using System;
using System.Collections.Generic;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Routes;
using OpenBveApi.Textures;

namespace OpenBve.Graphics
{
	internal static class WorldSky
	{
		private static readonly Dictionary<StaticBackground, ObjectState> states = new Dictionary<StaticBackground, ObjectState>();

		/// <summary>The sky's object, made the first time (the background's texture must be loaded: its size can decide the height).</summary>
		internal static ObjectState For(StaticBackground background)
		{
			if (!states.TryGetValue(background, out ObjectState state))
			{
				state = new ObjectState(Make(background));
				states[background] = state;
			}

			return state;
		}

		private static StaticObject Make(StaticBackground background)
		{
			float y0, y1;
			if (background.KeepAspectRatio)
			{
				double hh = Math.PI * background.BackgroundImageDistance * background.Texture.Height / (background.Texture.Width * background.Repetition);
				y0 = (float)(-0.5 * hh);
				y1 = (float)(1.5 * hh);
			}
			else
			{
				y0 = (float)(-0.125 * background.BackgroundImageDistance);
				y1 = (float)(0.375 * background.BackgroundImageDistance);
			}

			const int n = 32;
			Vector3f[] bottom = new Vector3f[n];
			Vector3f[] top = new Vector3f[n];
			double angleValue = 2.61799387799149 - 3.14159265358979 / n;
			const double angleIncrement = 6.28318530717958 / n;
			for (int i = 0; i < n; i++)
			{
				float x = (float)(background.BackgroundImageDistance * Math.Cos(angleValue));
				float z = (float)(background.BackgroundImageDistance * Math.Sin(angleValue));
				bottom[i] = new Vector3f(x, y0, z);
				top[i] = new Vector3f(x, y1, z);
				angleValue += angleIncrement;
			}

			float textureStart = 0.5f * (float)background.Repetition / n;
			float textureIncrement = -(float)background.Repetition / n;
			float textureX = textureStart;
			VertexTemplate[] vertices = new VertexTemplate[n * 6];
			MeshFace[] faces = new MeshFace[n * 3];
			for (int i = 0; i < n; i++)
			{
				int j = (i + 1) % n;
				int o = i * 6;
				vertices[o] = Vertex(top[i], textureX, 0.005f);
				vertices[o + 1] = Vertex(bottom[i], textureX, 0.995f);
				vertices[o + 2] = Vertex(bottom[j], textureX + textureIncrement, 0.995f);
				vertices[o + 3] = Vertex(top[j], textureX + textureIncrement, 0.005f);
				vertices[o + 4] = Vertex(new Vector3f(0.0f, top[i].Y, 0.0f), textureX + 0.5f * textureIncrement, 0.1f);
				vertices[o + 5] = Vertex(new Vector3f(0.0f, bottom[i].Y, 0.0f), textureX + 0.5f * textureIncrement, 0.9f);
				faces[i * 3] = new MeshFace(new[] { o, o + 1, o + 2, o, o + 2, o + 3 }, 0, FaceFlags.Triangles);
				faces[i * 3 + 1] = new MeshFace(new[] { o, o + 3, o + 4 }, 0, FaceFlags.Triangles);
				faces[i * 3 + 2] = new MeshFace(new[] { o + 5, o + 2, o + 1 }, 0, FaceFlags.Triangles);
				textureX += textureIncrement;
			}

			StaticObject sky = new StaticObject(Program.CurrentHost);
			sky.Mesh.Vertices = vertices;
			sky.Mesh.Faces = faces;
			sky.Mesh.Materials = new[]
			{
				new MeshMaterial
				{
					Color = OpenBveApi.Colors.Color32.White,
					DaytimeTexture = background.Texture,
					WrapMode = OpenGlTextureWrapMode.RepeatClamp,
					// Upstream resets the shader for it: no lighting.
					Flags = MaterialFlags.DisableLighting
				}
			};
			return sky;
		}

		private static Vertex Vertex(Vector3f position, float u, float v)
		{
			return new Vertex(new Vector3(position.X, position.Y, position.Z), new Vector2(u, v));
		}
	}
}
