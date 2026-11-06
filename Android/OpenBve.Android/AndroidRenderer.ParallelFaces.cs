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
using LibRender2;
using LibRender2.Objects;
using LibRender2.Shaders;

using OpenBveApi;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace OpenBve.Android
{
	/*
	 * A pass's faces prepared on the render thread and completed on all cores (OBNS/SWITCH.md, step 4, #1; the Switch:
	 * ParallelFaces). QueueFace does in order what must be: the culling and blending GL state, the textures (loaded,
	 * streamed), the animation matrices bound, the device's record prepared from its state (IParallelFaceDevice). The
	 * rest - each object's model view, texture and normal matrices, each face's material values and record - is pure,
	 * and FlushFaces shares it out over the cores (FrameWorkers) before the records go to the GPU in their order.
	 * Faces it can't take (glowing ones, a device that refuses) are drawn by DrawFace as before, after what is queued.
	 */
	public partial class AndroidRenderer
	{
		/// <summary>Whether DrawRuns' faces are completed on all cores (the Switch sets it).</summary>
		public static bool ParallelFaces;

		/// <summary>Faces drawn through the queue, and left out of it (a full ring), since the start: for the log.</summary>
		public long QueuedFacesDrawn, DroppedFaces;

		private struct QueuedFace
		{
			public ObjectState State;
			public int ObjectIndex, Material;
			public float Brightness, Opacity;
			public bool NoFog, Lighting;
		}

		private QueuedFace[] queuedFaces = new QueuedFace[2048];
		private readonly List<ObjectState> queuedObjects = new List<ObjectState>();
		private ObjectState queuedLastState;
		private int queuedCount;
		private IParallelFaceDevice queuedDevice;
		private Matrix4D queuedView, queuedCamera;
		private Action<int, int> writeObjects, completeFaces;

		/// <summary>The device to queue faces on now, or null (draw them one by one).</summary>
		private IParallelFaceDevice QueueDevice(Shader shader)
		{
			if (!ParallelFaces || !NativeFaces || shader != DefaultShader || OptionWireFrame || OptionNormals || !(GL.Device is IParallelFaceDevice device) ||
			    !(GL.Device is INativeFaceDevice native) || !native.CanDrawFaces)
			{
				return null;
			}

			return device;
		}

		/// <summary>DrawFace's work in order for one face, its record left to FlushFaces.</summary>
		private void QueueFace(IParallelFaceDevice device, Shader shader, ObjectState state, MeshFace face)
		{
			if (!(state.Prototype.Mesh.VAO is VertexArrayObject vao))
			{
				FlushFaces();
				DrawFace(shader, state, face);
				return;
			}

			if (state.Prototype.Mesh.Vertices.Length < 1)
			{
				return;
			}

			ref MeshMaterial material = ref state.Prototype.Mesh.Materials[face.Material];
			if (material.GlowAttenuationData != 0 || queuedCount + 2 > queuedFaces.Length || queuedCount + 2 > device.MaxFaces)
			{
				// Glow reads the face's vertices from the model matrix: drawn as before, after what is queued.
				FlushFaces();
				if (material.GlowAttenuationData != 0)
				{
					DrawFace(shader, state, face);
					return;
				}
			}

			if (queuedCount == 0)
			{
				queuedDevice = device;
				queuedCamera = Camera.TranslationMatrix;
				queuedView = CurrentViewMatrix;
			}

			if (state != queuedLastState)
			{
				queuedLastState = state;
				queuedObjects.Add(state);
				if (state.Matricies != null && state.Matricies.Length > 0)
				{
					// The animation matrices, as DrawFace binds them: the record prepared next takes them in.
					shader.SetCurrentAnimationMatricies(state);
					GL.BindBufferBase(BufferTarget.UniformBuffer, 0, state.MatrixBufferIndex);
				}
			}

			int objectIndex = queuedObjects.Count - 1;
			int cull = !OptionBackFaceCulling || (face.Flags & FaceFlags.Face2Mask) != 0 ? 0 : 1;
			if (cull != nativeCull)
			{
				if (cull == 0)
				{
					GL.Disable(EnableCap.CullFace);
				}
				else
				{
					GL.Enable(EnableCap.CullFace);
				}

				nativeCull = cull;
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

			float blendFactor = Math.Min(1.0f, inv255 * state.DaytimeNighttimeBlend + 1.0f - Lighting.OptionLightingResultingAmount);
			if (vao != nativeLastVao)
			{
				nativeLastVao = vao;
				nativeIntIndices = IntIndicesOf(vao);
			}

			OpenGlTextureWrapMode wrap = material.WrapMode ?? OpenGlTextureWrapMode.ClampClamp;
			int texture;
			if (material.DaytimeTexture != null && FaceTexture(ref material.DaytimeTexture, wrap))
			{
				texture = material.DaytimeTexture.OpenGlTextures[(int)wrap].Name;
			}
			else
			{
				FaceTexture(ref whitePixel, OpenGlTextureWrapMode.ClampClamp);
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

			float alphaFactor = material.NighttimeTexture != null && (material.Flags & MaterialFlags.CrossFadeTexture) != 0 ? 1.0f - blendFactor : 1.0f;
			if (!device.PrepareFace(queuedCount, vao.handle, texture, mode, nativeIntIndices, face.IboStartIndex, face.Vertices.Length, false))
			{
				if (additive)
				{
					RestoreBlendFunc();
				}

				FlushFaces();
				DrawFace(shader, state, face);
				return;
			}

			queuedFaces[queuedCount++] = new QueuedFace
			{
				State = state, ObjectIndex = objectIndex, Material = face.Material, Brightness = factor, Opacity = inv255 * material.Color.A * alphaFactor,
				NoFog = additive, Lighting = OptionLighting
			};
			LastBoundTexture = null;

			// The nighttime texture, blended over it.
			if (blendFactor != 0 && material.NighttimeTexture != null && material.NighttimeTexture != material.DaytimeTexture && FaceTexture(ref material.NighttimeTexture, wrap))
			{
				GL.Enable(EnableCap.Blend);
				if (device.PrepareFace(queuedCount, vao.handle, material.NighttimeTexture.OpenGlTextures[(int)wrap].Name, mode, nativeIntIndices, face.IboStartIndex, face.Vertices.Length, true))
				{
					queuedFaces[queuedCount++] = new QueuedFace
					{
						State = state, ObjectIndex = objectIndex, Material = face.Material, Brightness = factor, Opacity = inv255 * material.Color.A * blendFactor,
						NoFog = additive, Lighting = OptionLighting
					};
				}

				RestoreBlendFunc();
			}

			if (additive)
			{
				RestoreBlendFunc();
			}
		}

		/// <summary>The queued faces completed on all cores and drawn, in order.</summary>
		private void FlushFaces()
		{
			int count = queuedCount;
			IParallelFaceDevice device = queuedDevice;
			queuedCount = 0;
			queuedLastState = null;
			// A face drawn singly next sets its object afresh.
			nativeLastState = null;
			if (count == 0 || device == null)
			{
				queuedObjects.Clear();
				return;
			}

			if (!device.ReserveObjects(queuedObjects.Count))
			{
				// For the log: faces left out (the device's ring full).
				DroppedFaces += count;
				queuedObjects.Clear();
				return;
			}

			QueuedFacesDrawn += count;

			writeObjects ??= WriteObjects;
			completeFaces ??= CompleteFaces;
			FrameWorkers.Shared.Run(queuedObjects.Count, writeObjects, 16);
			FrameWorkers.Shared.Run(count, completeFaces, 64);
			device.CommitFaces(count);
			queuedObjects.Clear();
		}

		private unsafe void WriteObjects(int start, int end)
		{
			float* modelView = stackalloc float[16];
			float* texture = stackalloc float[16];
			for (int i = start; i < end; i++)
			{
				ObjectState state = queuedObjects[i];
				Matrix4D matrix = state.ModelMatrix * queuedCamera * queuedView;
				Rows(matrix, modelView);
				Rows(state.TextureTranslation, texture);
				queuedDevice.WriteObject(i, modelView, texture);
			}
		}

		private void CompleteFaces(int start, int end)
		{
			for (int i = start; i < end; i++)
			{
				ref QueuedFace queued = ref queuedFaces[i];
				MeshMaterial material = queued.State.Prototype.Mesh.Materials[queued.Material];
				NVector4 color = new NVector4(material.Color.R * inv255, material.Color.G * inv255, material.Color.B * inv255, material.Color.A * inv255);
				FaceValues values = new FaceValues
				{
					Flags = (int)material.Flags, Ambient = color, Diffuse = color, Shininess = 1.0f, Brightness = queued.Brightness, Opacity = queued.Opacity,
					NoFog = queued.NoFog
				};
				if (queued.Lighting)
				{
					values.Specular = (material.Flags & MaterialFlags.Specular) != 0
						? new NVector4(material.SpecularColor.R * inv255, material.SpecularColor.G * inv255, material.SpecularColor.B * inv255, 1.0f)
						: color;
					values.Emission = new NVector3(material.EmissiveColor.R * inv255, material.EmissiveColor.G * inv255, material.EmissiveColor.B * inv255);
				}
				else
				{
					values.Specular = color;
					values.Emission = NVector3.Zero;
				}

				queuedDevice.CompleteFace(i, queued.ObjectIndex, values);
			}
		}

		private static unsafe void Rows(Matrix4D matrix, float* target)
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
	}
}
