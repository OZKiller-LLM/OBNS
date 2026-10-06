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
using LibRender2.Textures;
using OpenBveApi.Textures;

namespace OpenBve.Android
{
	/// <summary>
	/// Diagnostic: what the textures on the GPU add up to. On a phone GPU memory is system RAM, and
	/// on the Tuen Ma Line graphics were 2.6 of the game's 3.4 GB. Upstream uploads a texture once
	/// per wrap mode it is drawn with, each a full mipmap chain, so the log also counts textures
	/// uploaded more than once.
	/// </summary>
	internal static class TextureCensus
	{
		/*
		 * Diagnostic: the meshes' share of the managed heap. Upstream keeps every mesh as objects -
		 * a Vertex class instance per vertex (~56 bytes with its array slot) and a MeshFaceVertex
		 * (16 bytes) per face corner - and on the Tuen Ma Line the route alone left ~580 MB live.
		 */
		public static string DescribeMeshes(LibRender2.BaseRenderer renderer)
		{
			System.Collections.Generic.HashSet<OpenBveApi.Objects.StaticObject> seen = new System.Collections.Generic.HashSet<OpenBveApi.Objects.StaticObject>();
			System.Collections.Generic.HashSet<OpenBveApi.Objects.Mesh> meshes = new System.Collections.Generic.HashSet<OpenBveApi.Objects.Mesh>();
			long vertices = 0, corners = 0, faces = 0;
			foreach (System.Collections.Generic.List<OpenBveApi.Objects.ObjectState> states in new[] { renderer.StaticObjectStates, renderer.DynamicObjectStates })
			{
				if (states == null)
				{
					continue;
				}

				foreach (OpenBveApi.Objects.ObjectState state in states)
				{
					OpenBveApi.Objects.StaticObject prototype = state?.Prototype;
					// Each mesh once: objects may share one (MeshSharing).
					if (prototype?.Mesh == null || !meshes.Add(prototype.Mesh) || !seen.Add(prototype))
					{
						continue;
					}

					vertices += prototype.Mesh.Vertices?.Length ?? 0;
					if (prototype.Mesh.Faces == null)
					{
						continue;
					}

					faces += prototype.Mesh.Faces.Length;
					foreach (OpenBveApi.Objects.MeshFace face in prototype.Mesh.Faces)
					{
						corners += face.Vertices?.Length ?? 0;
					}
				}
			}

			long bytes = vertices * 56 + corners * 16 + faces * 48;
			return "meshes: " + seen.Count + " distinct, " + vertices + " vertices, " + faces + " faces, " + corners + " face corners, ~" + (bytes >> 20) + " MB";
		}

		public static string Describe(TextureManager manager)
		{
			Texture[] textures = TextureManager.RegisteredTextures;
			if (textures == null || manager == null)
			{
				return "textures: none";
			}

			int uploads = 0, multiple = 0, large = 0;
			long bytes = 0;
			int count = System.Math.Min(manager.RegisteredTexturesCount, textures.Length);
			for (int i = 0; i < count; i++)
			{
				Texture texture = textures[i];
				if (texture?.OpenGlTextures == null)
				{
					continue;
				}

				int valid = 0;
				foreach (OpenGlTexture gl in texture.OpenGlTextures)
				{
					if (gl != null && gl.Valid)
					{
						valid++;
					}
				}

				if (valid == 0)
				{
					continue;
				}

				uploads += valid;
				if (valid > 1)
				{
					multiple++;
				}

				if (texture.Width >= 1024 || texture.Height >= 1024)
				{
					large++;
				}

				// RGBA8 with a full mipmap chain (4/3 of the base level).
				bytes += valid * (long)texture.Width * texture.Height * 4 * 4 / 3;
			}

			return "textures on the GPU: " + uploads + " uploads, ~" + (bytes >> 20) + " MB, " + multiple + " uploaded in more than one wrap mode, " +
			       large + " of 1024 px or more";
		}
	}
}
