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
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.NX.Deko
{
	/// <summary>One face for the native side (obns_dk3d.inc's WorldDraw: the same fields, in the same order, 192 bytes).</summary>
	[StructLayout(LayoutKind.Sequential)]
	internal unsafe struct WorldDraw
	{
		public ulong VertexAddress;
		public uint VertexSize;
		public uint Stride;
		public ulong IndexAddress;
		public uint IndexFormat;
		public uint Primitive;
		public uint First;
		public uint Count;
		public ulong FrameAddress;
		public ulong ObjectAddress;
		public ulong MatricesAddress;
		public uint Layout;
		public uint Texture;
		public uint Sampler;
		public uint State;
		public fixed int Viewport[4];
		public fixed float Draw[24];
	}

	/*
	 * The world and the cab the alone way (OBNS/SWITCH.md, step 3; the user, 2026-11-06: "for less rendering cost"). The
	 * renderer's faces don't pass through GL calls: AndroidRenderer.DrawFace works out each face's state as upstream's
	 * RenderFace does (the native faces Android's Vulkan renderer takes) and hands it over through INativeFaceDevice;
	 * here it becomes a 192-byte record, kept with the others and recorded by the native side in batches (obns_dkw_draws)
	 * with the world program (shaders/world.*), whose uniforms are split by how often they change:
	 *
	 *   - the frame's (projection, lights, fog: the default program's values as the renderer set them), written when
	 *     they change, a few times a frame;
	 *   - the object's (model view, texture and normal matrices), once for all of an object's faces;
	 *   - the draw's (material, brightness, opacity, alpha test), pushed inline with the draw.
	 *
	 * The meshes and textures are DekoGL's own (the renderer made them through GL calls once, as it loaded); the state
	 * (depth, blending, culling, viewport) is GL's as the renderer set it, captured in each record. Anything else drawn
	 * through GL - the background, particles, upstream's overlays - first sends the faces kept so far, so the order stays.
	 */
	public sealed unsafe partial class DekoGL : INativeFaceDevice
	{
		[DllImport("__Internal", EntryPoint = "obns_dkw_draws")]
		private static extern int WorldDraws(WorldDraw* draws, int count);

		[DllImport("__Internal", EntryPoint = "obns_dkw_layout")]
		private static extern int WorldLayout(int id, uint* offsets, uint* formats, uint* buffers);

		private const int WorldBatch = 512, FrameBlockSize = 192, ObjectBlockSize = 192;

		// Byte offsets in the default program's block (DefaultUniforms).
		private const int LightOffset = 256, IsLightOffset = 404, IsFogOffset = 408, FogLinearOffset = 412, AlphaFunctionOffset = 424, FogOffset = 432, FogColorOffset = 448;

		private readonly WorldDraw[] world = new WorldDraw[WorldBatch];
		private int worldCount;

		/// <summary>Faces drawn this way in the last finished frame, for the log.</summary>
		public int LastFrameFaces { get; private set; }

		private int frameFaces;

		// The face's values, as set before DrawFace.
		private readonly byte[] objectBlock = new byte[ObjectBlockSize];
		private readonly GLProgram normalScratch = new GLProgram { Kind = ProgramKind.Default, Data = new byte[DefaultBlockSize] };
		private bool objectDirty = true;
		private ulong objectAddress, objectSerial;
		private int faceFlags;
		private Vector4 faceAmbient, faceDiffuse, faceSpecular;
		private Vector3 faceEmission;
		private float faceShininess = 1.0f, faceBrightness = 1.0f, faceOpacity = 1.0f;
		private bool faceNoFog, faceAlphaAboveZero;

		// The frame block, made from the default program's values whenever they change.
		private bool worldFrameDirty = true;
		private ulong frameAddress, frameSerial;
		private readonly byte[] frameBlock = new byte[FrameBlockSize];

		// Vertex layouts registered with the native side, by their attributes.
		private readonly Dictionary<(uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint), int> layouts =
			new Dictionary<(uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint), int>();

		public bool CanDrawFaces => Drawing && currentProgram != null && currentProgram.Kind == ProgramKind.Default && boundFramebuffer == 0;

		public void SetFaceTransforms(ReadOnlySpan<float> modelView, ReadOnlySpan<float> texture)
		{
			fixed (byte* scratch = normalScratch.Data)
			fixed (byte* block = objectBlock)
			{
				float* m = (float*)(scratch + ModelViewOffset);
				float* o = (float*)block;
				for (int i = 0; i < 16; i++)
				{
					m[i] = modelView[i];
					o[i] = modelView[i];
					o[16 + i] = texture[i];
				}

				// The normal matrix, from the model view, by the default program's own code.
				UpdateNormalMatrix(normalScratch);
				float* n = (float*)(scratch + NormalMatrixOffset);
				for (int i = 0; i < 16; i++)
				{
					o[32 + i] = n[i];
				}
			}

			objectDirty = true;
		}

		public void SetFaceMaterial(int flags, System.Numerics.Vector4 ambient, System.Numerics.Vector4 diffuse, System.Numerics.Vector4 specular, System.Numerics.Vector3 emission, float shininess)
		{
			faceFlags = flags;
			faceAmbient = ambient;
			faceDiffuse = diffuse;
			faceSpecular = specular;
			faceEmission = emission;
			faceShininess = shininess;
		}

		public void SetFaceDrawing(float brightness, float opacity, bool noFog, bool alphaAboveZero)
		{
			faceBrightness = brightness;
			faceOpacity = opacity;
			faceNoFog = noFog;
			faceAlphaAboveZero = alphaAboveZero;
		}

		public bool DrawFace(int vertexArrayName, int textureName, int mode, bool intIndices, int firstIndex, int count)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				GLProgram program = currentProgram;
				if (!CanDrawFaces || count <= 0 || mode == GL_POINTS || !vertexArrays.TryGetValue(vertexArrayName, out GLVertexArray array))
				{
					return false;
				}

				GLBuffer elements = array.Elements;
				if (elements == null || elements.Memory.IsNull || !VertexSource(array, out GLBuffer vertices, out int layout, out uint stride))
				{
					return false;
				}

				if (!EnsureFrame())
				{
					return true;
				}

				ref WorldDraw draw = ref world[worldCount];
				draw = default;

				// The texture first: making it records its upload, which runs before the frame's drawing.
				GLTexture texture = null;
				if (textureName != 0)
				{
					textures.TryGetValue(textureName, out texture);
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
				draw.Sampler = (uint)SamplerSlot(SamplerKey(texture));

				// The frame's block, when the renderer's values changed (a few times a frame).
				if (worldFrameDirty || frameSerial != serial || frameAddress == 0)
				{
					BuildFrameBlock(program);
					frameAddress = RingWrite(frameBlock, FrameBlockSize, 256);
					frameSerial = serial;
					worldFrameDirty = false;
				}

				// The object's block, once for all of its faces.
				if (objectDirty || objectSerial != serial || objectAddress == 0)
				{
					objectAddress = RingWrite(objectBlock, ObjectBlockSize, 256);
					objectSerial = serial;
					objectDirty = false;
				}

				if (frameAddress == 0 || objectAddress == 0)
				{
					return true;
				}

				draw.FrameAddress = frameAddress;
				draw.ObjectAddress = objectAddress;

				// The animation matrices, as the GL path binds them.
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

				// The draw's block: material, flags, brightness, opacity, alpha test.
				fixed (float* d = draw.Draw)
				{
				d[0] = faceAmbient.X;
				d[1] = faceAmbient.Y;
				d[2] = faceAmbient.Z;
				d[3] = faceAmbient.W;
				d[4] = faceDiffuse.X;
				d[5] = faceDiffuse.Y;
				d[6] = faceDiffuse.Z;
				d[7] = faceDiffuse.W;
				d[8] = faceSpecular.X;
				d[9] = faceSpecular.Y;
				d[10] = faceSpecular.Z;
				d[11] = faceSpecular.W;
				d[12] = faceEmission.X;
				d[13] = faceEmission.Y;
				d[14] = faceEmission.Z;
				d[15] = faceShininess;
				((int*)d)[16] = faceFlags;
				((int*)d)[17] = faceNoFog ? 1 : 0;
				d[20] = faceBrightness;
				d[21] = faceOpacity;
				fixed (byte* data = program.Data)
				{
					d[22] = faceAlphaAboveZero ? 516.0f : *(float*)(data + AlphaFunctionOffset);
					d[23] = faceAlphaAboveZero ? 0.0f : *(float*)(data + AlphaFunctionOffset + 4);
				}
				}

				// The vertices and indices.
				vertices.LastUse = serial;
				elements.LastUse = serial;
				draw.VertexAddress = vertices.Memory.Gpu;
				draw.VertexSize = (uint)vertices.Size;
				draw.Stride = stride;
				draw.Layout = (uint)layout;
				draw.IndexAddress = elements.Memory.Gpu;
				draw.IndexFormat = intIndices ? 3u : 2u;
				draw.Primitive = (uint)mode; // GL's modes are deko3d's, quads and polygons included
				draw.First = (uint)firstIndex;
				draw.Count = (uint)count;

				// GL's state as the renderer set it.
				uint compare = depthTest ? DekoNative.Compare(depthFunc) : 8u;
				uint source = DekoNative.BlendFactor(blend ? blendSource : GL_ONE), destination = DekoNative.BlendFactor(blend ? blendDestination : GL_ZERO);
				uint cull = !cullFace ? DekoNative.FaceNone : cullMode == GL_FRONT ? DekoNative.FaceFront : cullMode == GL_BACK ? DekoNative.FaceBack : DekoNative.FaceFrontAndBack;
				draw.State = (depthTest ? 1u : 0u) | (depthTest && depthMask ? 2u : 0u) | compare << 2 | (blend ? 1u : 0u) << 6 | source << 7 | destination << 11 | cull << 15;
				draw.Viewport[0] = viewportX;
				draw.Viewport[1] = framebufferHeight - (viewportY + Math.Max(1, viewportHeight));
				draw.Viewport[2] = Math.Max(1, viewportWidth);
				draw.Viewport[3] = Math.Max(1, viewportHeight);

				worldCount++;
				frameFaces++;
				if (worldCount == WorldBatch)
				{
					FlushWorld();
				}

				return true;
			}
			finally
			{
				frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
			}
		}

		/// <summary>Sends the faces kept so far to the native side, to be recorded in order.</summary>
		private void FlushWorld()
		{
			if (worldCount == 0)
			{
				return;
			}

			long recording = System.Diagnostics.Stopwatch.GetTimestamp();
			fixed (WorldDraw* draws = world)
			{
				WorldDraws(draws, worldCount);
			}

			recordTicks += System.Diagnostics.Stopwatch.GetTimestamp() - recording;

			frameDraws += worldCount;
			worldCount = 0;
		}

		/// <summary>The frame block from the default program's values: projection, lights, fog.</summary>
		private void BuildFrameBlock(GLProgram program)
		{
			fixed (byte* source = program.Data)
			fixed (byte* target = frameBlock)
			{
				// Projection (0..63), then the light's position, ambient, diffuse, specular and model (5 vec4s).
				System.Buffer.MemoryCopy(source, target, 64, 64);
				System.Buffer.MemoryCopy(source + LightOffset, target + 64, 80, 80);
				int* flags = (int*)(target + 144);
				flags[0] = *(int*)(source + IsLightOffset);
				flags[1] = *(int*)(source + IsFogOffset);
				flags[2] = *(int*)(source + FogLinearOffset);
				flags[3] = 0;
				System.Buffer.MemoryCopy(source + FogOffset, target + 160, 16, 16);
				System.Buffer.MemoryCopy(source + FogColorOffset, target + 176, 16, 16);
			}
		}

		/// <summary>
		/// Where a vertex array's five attributes come from: one interleaved buffer (the renderer's meshes), each attribute
		/// at its offset in it, or the constant for one without an array; the layout registered once. False if the
		/// attributes are spread over buffers, which the world program doesn't take (the face then goes through GL).
		/// </summary>
		private bool VertexSource(GLVertexArray array, out GLBuffer vertices, out int layout, out uint stride)
		{
			// Per draw, so worked out once per change of the array's attributes (the key's hashing was a good part of a draw).
			if (array.SourceVersion != array.Version)
			{
				array.SourceFound = FindVertexSource(array, out array.SourceVertices, out array.SourceLayout, out array.SourceStride);
				array.SourceVersion = array.Version;
			}

			vertices = array.SourceVertices;
			layout = array.SourceLayout;
			stride = array.SourceStride;
			return array.SourceFound && vertices != null && !vertices.Memory.IsNull;
		}

		private bool FindVertexSource(GLVertexArray array, out GLBuffer vertices, out int layout, out uint stride)
		{
			vertices = null;
			layout = 0;
			stride = 0;
			uint* offsets = stackalloc uint[DefaultLocations];
			uint* formats = stackalloc uint[DefaultLocations];
			uint* buffers = stackalloc uint[DefaultLocations];
			for (int i = 0; i < DefaultLocations; i++)
			{
				Attribute attribute = array.Attributes[i];
				uint format = attribute.Enabled && attribute.Buffer != null ? AttributeFormat(attribute.Type, attribute.Size, attribute.Normalized, attribute.Integer) : 0;
				if (format == 0)
				{
					offsets[i] = i == 4 ? 16u : 0u;
					formats[i] = i == 4 ? DekoNative.Size32[2] | DekoNative.AttribSint << 8 : DekoNative.Size32[3] | DekoNative.AttribFloat << 8;
					buffers[i] = 1;
					continue;
				}

				if (vertices == null)
				{
					vertices = attribute.Buffer;
					stride = (uint)attribute.Stride;
				}
				else if (attribute.Buffer != vertices || (uint)attribute.Stride != stride)
				{
					return false;
				}

				if (attribute.Offset < 0 || attribute.Offset > 16383)
				{
					return false;
				}

				offsets[i] = (uint)attribute.Offset;
				formats[i] = format;
				buffers[i] = 0;
			}

			if (vertices == null)
			{
				return false;
			}

			var key = (offsets[0], offsets[1], offsets[2], offsets[3], offsets[4], formats[0], formats[1], formats[2], formats[3], formats[4],
				buffers[0] | buffers[1] << 1 | buffers[2] << 2 | buffers[3] << 3 | buffers[4] << 4);
			if (!layouts.TryGetValue(key, out layout))
			{
				if (layouts.Count >= 64)
				{
					ReportOnce("deko3d: more than 64 vertex layouts; faces with others go through GL");
					return false;
				}

				layout = layouts.Count;
				WorldLayout(layout, offsets, formats, buffers);
				layouts[key] = layout;
			}

			return true;
		}

		/// <summary>A new frame: the frame and object blocks are written again into its ring (EndFrame's caller).</summary>
		private void WorldFrameEnded()
		{
			LastFrameFaces = frameFaces;
			frameFaces = 0;
			frameAddress = 0;
			objectAddress = 0;
		}
	}
}
