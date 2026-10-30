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
using System.Runtime.CompilerServices;
using LibRender2;
using LibRender2.Objects;
using OpenBve.Vulkan;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Android
{
	/*
	 * The native Vulkan renderer's shadow pass: what upstream's Shadows.RenderPass does, as the scene's
	 * own drawing does it. Upstream's draws every visible face into every cascade through a dozen GL
	 * calls each, which took 17-22 ms of a phone's frame. Here:
	 *  - the static batches are drawn rather than the faces they cover;
	 *  - an object is only drawn into a cascade its bounding sphere reaches;
	 *  - a face is handed over directly (VulkanGL.DrawShadowFace), its object's block reused;
	 *  - a face with nothing see-through has no fragment shader, so the GPU only writes depth.
	 */
	public partial class AndroidRenderer
	{
		private static readonly ConditionalWeakTable<Mesh, double[]> meshSpheres = new ConditionalWeakTable<Mesh, double[]>();
		private readonly float[] shadowLight = new float[16], shadowModel = new float[16], shadowTexture = new float[16];
		private readonly HashSet<ObjectState> shadowAnimated = new HashSet<ObjectState>();
		private Matrix4D shadowLightMatrix;
		private ObjectState shadowLastState;
		private bool shadowLastVisible;
		private VulkanGL shadowDevice;
		private Action<ObjectState, MeshFace, int> shadowBatchFace;

		/// <summary>The cascades' depth maps, from the faces the scene is about to draw (the batches' faces already taken out).</summary>
		private void NativeShadowPass(VulkanGL device, List<FaceState> opaque, List<FaceState> alpha)
		{
			LibRender2.ShadowMapping.Shadows shadows = Shadows;
			if (!shadows.Enabled || shadows.Map == null || shadows.Caster == null || shadows.DepthShader == null)
			{
				return;
			}

			Vector3 lightDirection = new Vector3(-Lighting.OptionLightPosition.X, -Lighting.OptionLightPosition.Y, Lighting.OptionLightPosition.Z);
			if (lightDirection.IsNullVector())
			{
				return;
			}

			shadows.Caster.Resolution = shadows.Map.Resolution;
			if (currentOptions.ShadowDrawDistance == OpenBveApi.Interface.ShadowDistance.ViewingDistance)
			{
				shadows.Caster.ShadowDistance = currentOptions.ViewingDistance;
			}

			shadows.Caster.Update(lightDirection, CurrentViewMatrix, CurrentProjectionMatrix, 0.1, Camera.VerticalViewingAngle, Screen.AspectRatio);
			CurrentShader?.Deactivate();
			shadows.DepthShader.Activate();
			GL.Enable(EnableCap.DepthTest);
			GL.DepthFunc(DepthFunction.Less);
			if (OptionBackFaceCulling)
			{
				GL.Enable(EnableCap.CullFace);
				GL.CullFace(CullFaceMode.Front);
			}
			else
			{
				GL.Disable(EnableCap.CullFace);
			}

			GL.DepthMask(true);
			shadows.DepthShader.SetTexture(0);
			shadowDevice = device;
			shadowBatchFace ??= ShadowFace;
			shadowAnimated.Clear();
			for (int cascade = 0; cascade < shadows.Caster.CascadeCount; cascade++)
			{
				shadows.Map.BindCascadeForWriting(cascade);
				GL.Clear(ClearBufferMask.DepthBufferBit);
				shadowLightMatrix = shadows.Caster.LightSpaceMatrices[cascade];
				Rows(shadowLightMatrix, shadowLight);
				device.SetShadowCascade(shadowLight);
				shadowLastState = null;
				foreach (FaceState face in opaque)
				{
					ShadowFace(face.Object, face.Face, face.Face.Vertices.Length);
				}

				foreach (FaceState face in alpha)
				{
					ShadowFace(face.Object, face.Face, face.Face.Vertices.Length);
				}

				batcher.VisitFaces(shadowBatchFace);
				shadows.Map.Unbind();
			}

			GL.DepthFunc(DepthFunction.Lequal);
			GL.CullFace(CullFaceMode.Front);
			GL.Viewport(0, 0, Screen.Width, Screen.Height);
			LastBoundTexture = null;
			ResetNativeFaces();
		}

		private void ShadowFace(ObjectState state, MeshFace face, int count)
		{
			Mesh mesh = state.Prototype.Mesh;
			if (!(mesh.VAO is VertexArrayObject vao) || state.DisableShadowCasting || count <= 0)
			{
				return;
			}

			MeshMaterial material = mesh.Materials[face.Material];
			// What upstream's shader would discard all of, or never draws.
			if ((material.Flags & MaterialFlags.NoShadow) != 0 || material.BlendMode == MeshMaterialBlendMode.Additive || material.Color.A <= 127)
			{
				return;
			}

			if (state != shadowLastState)
			{
				shadowLastState = state;
				Matrix4D model = state.ModelMatrix * Camera.TranslationMatrix;
				bool animated = state.Matricies != null && state.Matricies.Length > 0;
				shadowLastVisible = animated || SphereInCascade(mesh, model);
				if (!shadowLastVisible)
				{
					return;
				}

				Rows(model, shadowModel);
				Rows(state.TextureTranslation, shadowTexture);
				shadowDevice.SetShadowObject(shadowModel, shadowTexture);
				if (animated)
				{
					// Once a frame, however many cascades it is in.
					if (shadowAnimated.Add(state))
					{
						DefaultShader.SetCurrentAnimationMatricies(state);
					}

					GL.BindBufferBase(BufferTarget.UniformBuffer, 0, state.MatrixBufferIndex);
				}
			}
			else if (!shadowLastVisible)
			{
				return;
			}

			if (!OptionBackFaceCulling || (face.Flags & FaceFlags.Face2Mask) != 0)
			{
				GL.Disable(EnableCap.CullFace);
			}
			else
			{
				GL.Enable(EnableCap.CullFace);
			}

			int texture = 0;
			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
			// Only what is loaded: the scene draws these faces and asks for their textures. Until then a face casts as solid.
			if (material.DaytimeTexture != null && material.DaytimeTexture.OpenGlTextures[(int)wrap].Valid && material.DaytimeTexture.Transparency != TextureTransparencyType.Opaque)
			{
				texture = material.DaytimeTexture.OpenGlTextures[(int)wrap].Name;
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

			shadowDevice.DrawShadowFace(vao.handle, texture, mode, IntIndicesOf(vao), face.IboStartIndex, count, inv255 * material.Color.A, (int)material.Flags);
		}

		/// <summary>Whether a mesh's bounding sphere, placed by this matrix, reaches the current cascade's box.</summary>
		private bool SphereInCascade(Mesh mesh, Matrix4D model)
		{
			double[] sphere = meshSpheres.GetValue(mesh, BoundingSphere);
			if (sphere[3] < 0.0)
			{
				return true;
			}

			Matrix4D m = model * shadowLightMatrix;
			// The shaders negate Z.
			double cx = sphere[0], cy = sphere[1], cz = -sphere[2], r = sphere[3];
			double x = cx * m.Row0.X + cy * m.Row1.X + cz * m.Row2.X + m.Row3.X;
			double y = cx * m.Row0.Y + cy * m.Row1.Y + cz * m.Row2.Y + m.Row3.Y;
			double z = cx * m.Row0.Z + cy * m.Row1.Z + cz * m.Row2.Z + m.Row3.Z;
			double rx = r * Math.Sqrt(m.Row0.X * m.Row0.X + m.Row1.X * m.Row1.X + m.Row2.X * m.Row2.X);
			double ry = r * Math.Sqrt(m.Row0.Y * m.Row0.Y + m.Row1.Y * m.Row1.Y + m.Row2.Y * m.Row2.Y);
			double rz = r * Math.Sqrt(m.Row0.Z * m.Row0.Z + m.Row1.Z * m.Row1.Z + m.Row2.Z * m.Row2.Z);
			return Math.Abs(x) <= 1.0 + rx && Math.Abs(y) <= 1.0 + ry && Math.Abs(z) <= 1.0 + rz;
		}

		/// <summary>A sphere around the mesh's vertices (not the smallest, which is not needed); a negative radius when it has none.</summary>
		private static double[] BoundingSphere(Mesh mesh)
		{
			VertexTemplate[] vertices = mesh.Vertices;
			if (vertices == null || vertices.Length == 0)
			{
				return new[] { 0.0, 0.0, 0.0, -1.0 };
			}

			Vector3 low = vertices[0].Coordinates, high = low;
			foreach (VertexTemplate vertex in vertices)
			{
				Vector3 c = vertex.Coordinates;
				low = new Vector3(Math.Min(low.X, c.X), Math.Min(low.Y, c.Y), Math.Min(low.Z, c.Z));
				high = new Vector3(Math.Max(high.X, c.X), Math.Max(high.Y, c.Y), Math.Max(high.Z, c.Z));
			}

			double x = 0.5 * (low.X + high.X), y = 0.5 * (low.Y + high.Y), z = 0.5 * (low.Z + high.Z), squared = 0.0;
			foreach (VertexTemplate vertex in vertices)
			{
				Vector3 c = vertex.Coordinates;
				squared = Math.Max(squared, (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y) + (c.Z - z) * (c.Z - z));
			}

			return new[] { x, y, z, Math.Sqrt(squared) };
		}
	}
}
