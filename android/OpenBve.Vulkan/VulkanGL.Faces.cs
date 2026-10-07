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
using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * Native face drawing: the fast path for the world and the cab. Upstream draws each face
	 * through some fifteen uniform calls (model view, texture matrix, material colours, flags,
	 * brightness, opacity...), each passing through GL's caches and this class's uniform setters,
	 * and then a GL draw. Here the port's face drawer (AndroidRenderer.DrawFace, which does what
	 * upstream's RenderFace does) hands those values over directly: a face's uniform block is the
	 * default program's current block - projection, lights, fog, alpha test, as the renderer set
	 * them - with the face's own values written over it. The program's block itself is not
	 * changed, so GL's uniform cache and upstream's own caches stay true.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int GL_QUADS = 7, GL_QUAD_STRIP = 8, GL_POLYGON = 9;

		// Byte offsets in default.vert's DrawBlock (see VulkanGL.Programs).
		private const int TextureMatrixOffset = 128, MaterialAmbientOffset = 336, MaterialDiffuseOffset = 352, MaterialSpecularOffset = 368,
			MaterialEmissionOffset = 384, ShininessOffset = 396, FlagsOffset = 400, IsFogOffset = 408, BrightnessOffset = 416, OpacityOffset = 420, AlphaTestOffset = 424;

		private readonly byte[] faceBlock = new byte[DefaultBlockSize];

		// The face's own values: transforms (with the normal matrix worked out from the model view), material, drawing.
		private readonly byte[] faceTransforms = new byte[DefaultBlockSize];
		private int faceFlags;
		private Vector4 faceAmbient, faceDiffuse, faceSpecular;
		private Vector3 faceEmission;
		private float faceShininess = 1.0f;
		private float faceBrightness = 1.0f, faceOpacity = 1.0f;
		private bool faceNoFog, faceAlphaAboveZero;

		/// <summary>Native faces can be drawn now: the default program is current (the world and cab passes).</summary>
		public bool CanDrawFaces => currentProgram != null && currentProgram.Kind == ProgramKind.Default && !offscreenBound;

		/// <summary>
		/// The face's model view and texture matrices, as upstream sends them: 16 floats each,
		/// row by row of OpenBVE's matrix (which GL reads as columns).
		/// </summary>
		public void SetFaceTransforms(ReadOnlySpan<float> modelView, ReadOnlySpan<float> texture)
		{
			fixed (byte* block = faceTransforms)
			{
				float* m = (float*)(block + ModelViewOffset);
				float* t = (float*)(block + TextureMatrixOffset);
				for (int i = 0; i < 16; i++)
				{
					m[i] = modelView[i];
					t[i] = texture[i];
				}
			}

			// The normal matrix, from the model view just written.
			GLProgram scratch = faceTransformProgram;
			scratch.Data = faceTransforms;
			UpdateNormalMatrix(scratch);
		}

		/// <summary>A program holding the face's transform block, so the normal matrix code can work on it.</summary>
		private readonly GLProgram faceTransformProgram = new GLProgram { Kind = ProgramKind.Default };

		/// <summary>The face's material, as upstream's RenderFace sets it (colours 0 to 1).</summary>
		public void SetFaceMaterial(int flags, Vector4 ambient, Vector4 diffuse, Vector4 specular, Vector3 emission, float shininess)
		{
			faceFlags = flags;
			faceAmbient = ambient;
			faceDiffuse = diffuse;
			faceSpecular = specular;
			faceEmission = emission;
			faceShininess = shininess;
		}

		/// <summary>The face's brightness and opacity; no fog (additive faces); alpha test "greater than 0" (nighttime textures).</summary>
		public void SetFaceDrawing(float brightness, float opacity, bool noFog, bool alphaAboveZero)
		{
			faceBrightness = brightness;
			faceOpacity = opacity;
			faceNoFog = noFog;
			faceAlphaAboveZero = alphaAboveZero;
		}

		/// <summary>
		/// Draws a range of a vertex array's indices as a face, with the values set above and GL's
		/// current state (blending, depth, culling, the bound animation matrices). The primitive
		/// is upstream's: quads, quad strips and polygons are drawn as GL draws them here.
		/// False if it cannot be drawn this way (the caller then draws it through GL).
		/// </summary>
		public bool DrawFace(int vertexArrayName, int textureName, int mode, bool intIndices, int firstIndex, int count)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				GLProgram program = currentProgram;
				if (program == null || program.Kind != ProgramKind.Default || count <= 0 || !vertexArrays.TryGetValue(vertexArrayName, out GLVertexArray array))
				{
					return false;
				}

				GLBuffer elements = array.Elements;
				if (elements == null || elements.Memory.IsNull)
				{
					return false;
				}

				GLTexture texture = null;
				if (textureName != 0)
				{
					textures.TryGetValue(textureName, out texture);
				}

				BuildFaceBlock(program);
				int drawMode = mode == GL_POLYGON || mode == GL_QUADS ? GL_TRIANGLE_FAN : mode == GL_QUAD_STRIP ? GL_TRIANGLE_STRIP : mode;
				if (!PrepareDraw(drawMode, array, texture, faceBlock))
				{
					return true;
				}

				CommandBuffer commands = context.DrawCommands;
				BindElements(commands, elements, intIndices ? IndexType.Uint32 : IndexType.Uint16);
				if (mode == GL_QUADS)
				{
					// One fan per quad, as GL.DrawElements does on GLES.
					for (int quad = 0; quad + 4 <= count; quad += 4)
					{
						cmdDrawIndexed(commands, 4, 1, (uint)(firstIndex + quad), 0, 0);
						frameDraws++;
					}
				}
				else
				{
					cmdDrawIndexed(commands, (uint)count, 1, (uint)firstIndex, 0, 0);
					frameDraws++;
				}

				return true;
			}
			finally
			{
				drawTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			}
		}

		/// <summary>The face's uniform block: the program's, with the face's values over it.</summary>
		private void BuildFaceBlock(GLProgram program)
		{
			System.Buffer.BlockCopy(program.Data, 0, faceBlock, 0, DefaultBlockSize);
			// Model view, texture matrix, normal matrix: 64 bytes each from ModelViewOffset.
			System.Buffer.BlockCopy(faceTransforms, ModelViewOffset, faceBlock, ModelViewOffset, 192);
			ref byte block = ref faceBlock[0];
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, MaterialAmbientOffset), faceAmbient);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, MaterialDiffuseOffset), faceDiffuse);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, MaterialSpecularOffset), faceSpecular);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, MaterialEmissionOffset), faceEmission);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, ShininessOffset), faceShininess);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, FlagsOffset), faceFlags);
			if (faceNoFog)
			{
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, IsFogOffset), 0);
			}

			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, BrightnessOffset), faceBrightness);
			Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, OpacityOffset), faceOpacity);
			if (faceAlphaAboveZero)
			{
				// GL's AlphaFunction.Greater, against 0.
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, AlphaTestOffset), 516.0f);
				Unsafe.WriteUnaligned(ref Unsafe.Add(ref block, AlphaTestOffset + 4), 0.0f);
			}
		}
	}
}
