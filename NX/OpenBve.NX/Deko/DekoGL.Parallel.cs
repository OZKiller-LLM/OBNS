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
using OpenTK.Graphics.OpenGL;

namespace OpenBve.NX.Deko
{
	/*
	 * Faces in three steps (IParallelFaceDevice; OBNS/SWITCH.md, step 4, #1): PrepareFace on the render thread fills a
	 * staged record with everything the device's state decides - texture (made if pending), sampler, vertex and index
	 * buffers, GL's state, the frame block, the animation matrices, the alpha test - as DrawFace does; WriteObject and
	 * CompleteFace, on any thread, the object blocks (model view, texture and normal matrices, in a stretch of the
	 * uniform ring reserved by ReserveObjects) and each record's material; CommitFaces hands the records on in order,
	 * after whatever DrawFace kept before them.
	 */
	public sealed unsafe partial class DekoGL : IParallelFaceDevice
	{
		private const int StagedFaces = 2048, ObjectStride = 256;

		private readonly WorldDraw[] staged = new WorldDraw[StagedFaces];
		private byte* stagedObjects;
		private ulong stagedObjectsGpu;

		public int MaxFaces => StagedFaces;

		public bool PrepareFace(int index, int vertexArrayName, int textureName, int mode, bool intIndices, int firstIndex, int count, bool alphaAboveZero)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				GLProgram program = currentProgram;
				GLVertexArray array;
				if (vertexArrayName == lastDrawArrayName && lastDrawArray != null)
				{
					array = lastDrawArray;
				}
				else if ((array = ArrayNamed(vertexArrayName)) != null)
				{
					lastDrawArrayName = vertexArrayName;
					lastDrawArray = array;
				}

				if ((uint)index >= StagedFaces || !CanDrawFaces || count <= 0 || mode == GL_POINTS || array == null)
				{
					return false;
				}

				GLBuffer elements = array.Elements;
				if (elements == null || elements.Memory.IsNull || !VertexSource(array, out GLBuffer vertices, out int layout, out uint stride) || !EnsureFrame())
				{
					return false;
				}

				ref WorldDraw draw = ref staged[index];
				draw = default;
				GLTexture texture = null;
				if (textureName != 0)
				{
					if (textureName == lastDrawTextureName && lastDrawTexture != null)
					{
						texture = lastDrawTexture;
					}
					else if ((texture = TextureNamed(textureName)) != null)
					{
						lastDrawTextureName = textureName;
						lastDrawTexture = texture;
					}
				}

				if (texture == null || (!texture.Pending && texture.Slot == 0))
				{
					texture = blackTexture;
				}

				if (texture.Pending)
				{
					long realizing = System.Diagnostics.Stopwatch.GetTimestamp();
					Realize(texture);
					realizeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - realizing;
				}

				draw.Texture = (uint)texture.Slot;
				if (texture.SamplerSlotCache < 0)
				{
					texture.SamplerSlotCache = SamplerSlot(SamplerKey(texture));
				}

				draw.Sampler = (uint)texture.SamplerSlotCache;
				if (worldFrameDirty || frameSerial != serial || frameAddress == 0)
				{
					BuildFrameBlock(program);
					frameAddress = RingWrite(frameBlock, FrameBlockSize, 256);
					frameSerial = serial;
					worldFrameDirty = false;
				}

				if (frameAddress == 0)
				{
					return false;
				}

				draw.FrameAddress = frameAddress;
				GLBuffer matrices = uniformBindings[Math.Max(0, Math.Min(uniformBindings.Length - 1, program.BlockBinding))];
				if (matrices != null && matrices.Uniform && matrices.Shadow != null && matrices.Shadow.Length > 0)
				{
					if (matrices.RingSerial != serial || matrices.RingVersion != matrices.Version)
					{
						matrices.RingAddress = RingWrite(matrices.Shadow, (int)Math.Min((uint)matrices.Shadow.Length, AnimationBlockSize), AnimationBlockSize);
						matrices.RingSerial = serial;
						matrices.RingVersion = matrices.Version;
					}

					draw.MatricesAddress = matrices.RingAddress != 0 ? matrices.RingAddress : zeroBlock.Gpu;
				}
				else
				{
					draw.MatricesAddress = zeroBlock.Gpu;
				}

				fixed (float* d = draw.Draw)
				fixed (byte* data = program.Data)
				{
					d[22] = alphaAboveZero ? 516.0f : *(float*)(data + AlphaFunctionOffset);
					d[23] = alphaAboveZero ? 0.0f : *(float*)(data + AlphaFunctionOffset + 4);
				}

				vertices.LastUse = serial;
				elements.LastUse = serial;
				draw.VertexAddress = vertices.Memory.Gpu;
				draw.VertexSize = (uint)vertices.Size;
				draw.Stride = stride;
				draw.Layout = (uint)layout;
				draw.IndexAddress = elements.Memory.Gpu;
				draw.IndexFormat = intIndices ? 3u : 2u;
				draw.Primitive = (uint)mode;
				draw.First = (uint)firstIndex;
				draw.Count = (uint)count;
				uint compare = depthTest ? DekoNative.Compare(depthFunc) : 8u;
				uint source = DekoNative.BlendFactor(blend ? blendSource : GL_ONE), destination = DekoNative.BlendFactor(blend ? blendDestination : GL_ZERO);
				uint cull = !cullFace ? DekoNative.FaceNone : cullMode == GL_FRONT ? DekoNative.FaceFront : cullMode == GL_BACK ? DekoNative.FaceBack : DekoNative.FaceFrontAndBack;
				draw.State = (depthTest ? 1u : 0u) | (depthTest && depthMask ? 2u : 0u) | compare << 2 | (blend ? 1u : 0u) << 6 | source << 7 | destination << 11 | cull << 15;
				draw.Viewport[0] = viewportX;
				draw.Viewport[1] = framebufferHeight - (viewportY + Math.Max(1, viewportHeight));
				draw.Viewport[2] = Math.Max(1, viewportWidth);
				draw.Viewport[3] = Math.Max(1, viewportHeight);
				return true;
			}
			finally
			{
				frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			}
		}

		public bool ReserveObjects(int count)
		{
			if (count <= 0)
			{
				return true;
			}

			if (!EnsureFrame())
			{
				return false;
			}

			uint start = (ringPosition + 255u) & ~255u;
			uint size = (uint)count * ObjectStride;
			if (start + size > ringLimit)
			{
				ReportOnce("deko3d: the frame's uniform ring is full; further draws are left out");
				return false;
			}

			stagedObjects = ring.Pointer + start;
			stagedObjectsGpu = ring.Gpu + start;
			ringPosition = start + size;
			return true;
		}

		public void WriteObject(int index, float* modelView, float* texture)
		{
			float* o = (float*)(stagedObjects + (long)index * ObjectStride);
			for (int i = 0; i < 16; i++)
			{
				o[i] = modelView[i];
				o[16 + i] = texture[i];
			}

			// The normal matrix, as UpdateNormalMatrix makes it: the inverse transpose of the model view's 3x3 part.
			float a00 = modelView[0], a10 = modelView[1], a20 = modelView[2];
			float a01 = modelView[4], a11 = modelView[5], a21 = modelView[6];
			float a02 = modelView[8], a12 = modelView[9], a22 = modelView[10];
			float c00 = a11 * a22 - a12 * a21, c01 = a12 * a20 - a10 * a22, c02 = a10 * a21 - a11 * a20;
			float c10 = a02 * a21 - a01 * a22, c11 = a00 * a22 - a02 * a20, c12 = a01 * a20 - a00 * a21;
			float c20 = a01 * a12 - a02 * a11, c21 = a02 * a10 - a00 * a12, c22 = a00 * a11 - a01 * a10;
			float determinant = a00 * c00 + a01 * c01 + a02 * c02;
			float scale = Math.Abs(determinant) > 1e-20f ? 1.0f / determinant : 1.0f;
			float* n = o + 32;
			n[0] = c00 * scale;
			n[1] = c10 * scale;
			n[2] = c20 * scale;
			n[3] = 0.0f;
			n[4] = c01 * scale;
			n[5] = c11 * scale;
			n[6] = c21 * scale;
			n[7] = 0.0f;
			n[8] = c02 * scale;
			n[9] = c12 * scale;
			n[10] = c22 * scale;
			n[11] = 0.0f;
			n[12] = n[13] = n[14] = 0.0f;
			n[15] = 1.0f;
		}

		public void CompleteFace(int index, int objectIndex, in FaceValues values)
		{
			ref WorldDraw draw = ref staged[index];
			draw.ObjectAddress = stagedObjectsGpu + (ulong)objectIndex * ObjectStride;
			fixed (float* d = draw.Draw)
			{
				d[0] = values.Ambient.X;
				d[1] = values.Ambient.Y;
				d[2] = values.Ambient.Z;
				d[3] = values.Ambient.W;
				d[4] = values.Diffuse.X;
				d[5] = values.Diffuse.Y;
				d[6] = values.Diffuse.Z;
				d[7] = values.Diffuse.W;
				d[8] = values.Specular.X;
				d[9] = values.Specular.Y;
				d[10] = values.Specular.Z;
				d[11] = values.Specular.W;
				d[12] = values.Emission.X;
				d[13] = values.Emission.Y;
				d[14] = values.Emission.Z;
				d[15] = values.Shininess;
				((int*)d)[16] = values.Flags;
				((int*)d)[17] = values.NoFog ? 1 : 0;
				d[20] = values.Brightness;
				d[21] = values.Opacity;
			}
		}

		public void CommitFaces(int count)
		{
			if (count <= 0)
			{
				return;
			}

			// After whatever DrawFace kept, in order.
			FlushWorld();
			long recording = System.Diagnostics.Stopwatch.GetTimestamp();
			fixed (WorldDraw* draws = staged)
			{
				WorldDraws(draws, count);
			}

			recordTicks += System.Diagnostics.Stopwatch.GetTimestamp() - recording;
			frameDraws += count;
			frameFaces += count;
			// A face drawn singly next writes its object block afresh.
			objectDirty = true;
		}
	}
}
