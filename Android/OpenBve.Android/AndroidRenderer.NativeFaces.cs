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
using System.Reflection;
using System.Runtime.CompilerServices;
using LibRender2;
using LibRender2.Objects;
using LibRender2.Shaders;
using OpenBve.Vulkan;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace OpenBve.Android
{
	/*
	 * Faces on the native Vulkan renderer: what upstream's BaseRenderer.RenderFace does for a
	 * face - model view, animation matrices, culling, material colours, daytime and nighttime
	 * textures, brightness, opacity, glow, additive blending - worked out the same way, and
	 * handed to VulkanGL.DrawFace in one go instead of as some fifteen uniform calls and a GL draw.
	 * On TML's exterior view (about 7,000 draws a frame) the uniform calls were most of the
	 * cost of drawing. Upstream's own state (its uniform values, bound texture and vertex array)
	 * is left as it was, so the faces still drawn through RenderFace - the overlays, anything
	 * this declines - see what they expect. `--ez native_faces false` turns it off for comparisons.
	 */
	public partial class AndroidRenderer
	{
		/// <summary>Whether the native Vulkan renderer draws faces directly (VulkanGL.DrawFace).</summary>
		public static bool NativeFaces = true;

		private ObjectState nativeLastState;
		private Matrix4D nativeModelMatrix;
		private readonly float[] nativeModelView = new float[16];
		private readonly float[] nativeTexture = new float[16];

		/// <summary>Index type per vertex array (upstream keeps its index buffer private): true for 32-bit indices.</summary>
		private static readonly ConditionalWeakTable<VertexArrayObject, object> IntIndices = new ConditionalWeakTable<VertexArrayObject, object>();
		private static readonly FieldInfo IndexBufferField = typeof(VertexArrayObject).GetField("ibo", BindingFlags.NonPublic | BindingFlags.Instance);

		/// <summary>Forgets the last face's object, as upstream's state reset does.</summary>
		private void ResetNativeFaces()
		{
			nativeLastState = null;
		}

		/// <summary>Draws a face: natively on the Vulkan renderer when it can, through upstream's RenderFace otherwise.</summary>
		internal void DrawFace(Shader shader, ObjectState state, MeshFace face)
		{
			VulkanGL device = NativeFaces ? GL.Device as VulkanGL : null;
			if (device == null || shader != DefaultShader || OptionWireFrame || !device.CanDrawFaces || !(state.Prototype.Mesh.VAO is VertexArrayObject vao))
			{
				RenderFace(shader, state, face);
				return;
			}

			if (state.Prototype.Mesh.Vertices.Length < 1)
			{
				return;
			}

			if (state != nativeLastState || state.Prototype.Dynamic)
			{
				nativeModelMatrix = state.ModelMatrix * Camera.TranslationMatrix;
				Matrix4D modelView = nativeModelMatrix * CurrentViewMatrix;
				Rows(modelView, nativeModelView);
				Rows(state.TextureTranslation, nativeTexture);
				device.SetFaceTransforms(nativeModelView, nativeTexture);
				if (state != nativeLastState && state.Matricies != null && state.Matricies.Length > 0)
				{
					// The animation matrices, as upstream: into the object's uniform buffer, bound at 0.
					shader.SetCurrentAnimationMatricies(state);
					GL.BindBufferBase(BufferTarget.UniformBuffer, 0, state.MatrixBufferIndex);
				}

				nativeLastState = state;
			}

			if (!OptionBackFaceCulling || (face.Flags & FaceFlags.Face2Mask) != 0)
			{
				GL.Disable(EnableCap.CullFace);
			}
			else
			{
				GL.Enable(EnableCap.CullFace);
			}

			MeshMaterial material = state.Prototype.Mesh.Materials[face.Material];
			NVector4 color = Colour(material.Color);
			if (OptionLighting)
			{
				NVector4 specular = (material.Flags & MaterialFlags.Specular) != 0 ? Colour(material.SpecularColor) : color;
				NVector3 emission = new NVector3(material.EmissiveColor.R * inv255, material.EmissiveColor.G * inv255, material.EmissiveColor.B * inv255);
				device.SetFaceMaterial((int)material.Flags, color, color, specular, emission, 1.0f);
			}
			else
			{
				device.SetFaceMaterial((int)material.Flags, color, color, color, NVector3.Zero, 1.0f);
			}

			int mode;
			switch (face.Flags & FaceFlags.FaceTypeMask)
			{
				case FaceFlags.Triangles:
					mode = (int)PrimitiveType.Triangles;
					break;
				case FaceFlags.TriangleStrip:
					mode = (int)PrimitiveType.TriangleStrip;
					break;
				case FaceFlags.Quads:
					mode = (int)PrimitiveType.Quads;
					break;
				case FaceFlags.QuadStrip:
					mode = (int)PrimitiveType.QuadStrip;
					break;
				default:
					mode = (int)PrimitiveType.Polygon;
					break;
			}

			float distanceFactor = material.GlowAttenuationData != 0
				? (float)Glow.GetDistanceFactor(nativeModelMatrix, state.Prototype.Mesh.Vertices, ref face, material.GlowAttenuationData)
				: 1.0f;
			float blendFactor = Math.Min(1.0f, inv255 * state.DaytimeNighttimeBlend + 1.0f - Lighting.OptionLightingResultingAmount);
			bool intIndices = IntIndicesOf(vao);
			// Upstream reads the wrap mode only for a material with a texture; one without has none set.
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;

			// Daytime polygon.
			int texture;
			if (material.DaytimeTexture != null && currentHost.LoadTexture(ref material.DaytimeTexture, wrap))
			{
				texture = material.DaytimeTexture.OpenGlTextures[(int)wrap].Name;
			}
			else
			{
				// Untextured: a single white pixel, as Shader.DisableTexturing.
				currentHost.LoadTexture(ref whitePixel, OpenGlTextureWrapMode.ClampClamp);
				texture = whitePixel.OpenGlTextures[(int)OpenGlTextureWrapMode.ClampClamp].Name;
			}

			float factor;
			bool additive = material.BlendMode == MeshMaterialBlendMode.Additive;
			if (additive)
			{
				factor = 1.0f;
				GL.Enable(EnableCap.Blend);
				GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
			}
			else if (material.NighttimeTexture == null || material.NighttimeTexture == material.DaytimeTexture)
			{
				factor = 1.0f - 0.7f * blendFactor;
			}
			else
			{
				factor = 1.0f;
			}

			float alphaFactor = distanceFactor;
			if (material.NighttimeTexture != null && (material.Flags & MaterialFlags.CrossFadeTexture) != 0)
			{
				alphaFactor *= 1.0f - blendFactor;
			}

			device.SetFaceDrawing(factor, inv255 * material.Color.A * alphaFactor, additive, false);
			if (!device.DrawFace(vao.handle, texture, mode, intIndices, face.IboStartIndex, face.Vertices.Length))
			{
				// Not drawable natively after all: upstream's way.
				if (additive)
				{
					RestoreBlendFunc();
				}

				RenderFace(shader, state, face);
				return;
			}

			// Nighttime polygon, blended over it.
			if (blendFactor != 0 && material.NighttimeTexture != null && material.NighttimeTexture != material.DaytimeTexture && currentHost.LoadTexture(ref material.NighttimeTexture, wrap))
			{
				GL.Enable(EnableCap.Blend);
				device.SetFaceDrawing(factor, inv255 * material.Color.A * distanceFactor * blendFactor, additive, true);
				device.DrawFace(vao.handle, material.NighttimeTexture.OpenGlTextures[(int)wrap].Name, mode, intIndices, face.IboStartIndex, face.Vertices.Length);
				RestoreBlendFunc();
			}

			if (additive)
			{
				RestoreBlendFunc();
			}
		}

		private static NVector4 Colour(Color32 colour) => new NVector4(colour.R * inv255, colour.G * inv255, colour.B * inv255, colour.A * inv255);

		private static NVector4 Colour(Color24 colour) => new NVector4(colour.R * inv255, colour.G * inv255, colour.B * inv255, 1.0f);

		/// <summary>A matrix's elements row by row, as floats - what upstream sends to its shader.</summary>
		private static void Rows(Matrix4D matrix, float[] target)
		{
			target[0] = (float)matrix.Row0.X;
			target[1] = (float)matrix.Row0.Y;
			target[2] = (float)matrix.Row0.Z;
			target[3] = (float)matrix.Row0.W;
			target[4] = (float)matrix.Row1.X;
			target[5] = (float)matrix.Row1.Y;
			target[6] = (float)matrix.Row1.Z;
			target[7] = (float)matrix.Row1.W;
			target[8] = (float)matrix.Row2.X;
			target[9] = (float)matrix.Row2.Y;
			target[10] = (float)matrix.Row2.Z;
			target[11] = (float)matrix.Row2.W;
			target[12] = (float)matrix.Row3.X;
			target[13] = (float)matrix.Row3.Y;
			target[14] = (float)matrix.Row3.Z;
			target[15] = (float)matrix.Row3.W;
		}

		/// <summary>Whether a vertex array's index buffer holds 32-bit indices (IndexBufferObjectUI), looked up once.</summary>
		private static bool IntIndicesOf(VertexArrayObject vao)
		{
			if (!IntIndices.TryGetValue(vao, out object known))
			{
				known = IndexBufferField?.GetValue(vao) is IndexBufferObjectUI;
				IntIndices.AddOrUpdate(vao, known);
			}

			return (bool)known;
		}
	}
}
