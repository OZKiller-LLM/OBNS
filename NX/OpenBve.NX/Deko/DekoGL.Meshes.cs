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

namespace OpenBve.NX.Deko
{
	/*
	 * #11 stage 1 (OBNS/SWITCH.md): the meshes without GL calls. NX/LibRender2's VertexArrayObject, VertexBufferObject and
	 * IndexBufferObject hand their data here (INativeMeshes) instead of generating GL names and replaying GL's buffer
	 * and attribute calls: an array is the same record DekoGL's GL side keeps (GLVertexArray, its GLBuffers in the buffer
	 * heap), held in a list under a handle of its own - NativeArray plus its place - so the faces find it by index, not
	 * through a dictionary. GL calls that still name an array (glBindVertexArray for the background and the overlays'
	 * draws, glDeleteVertexArrays from the finalizers' queue) take these handles too (ArrayNamed).
	 */
	public sealed unsafe partial class DekoGL : INativeMeshes
	{
		/// <summary>Set in the handles of arrays made here, apart from GL's names (which count up from 1).</summary>
		private const int NativeArray = 0x40000000;

		private readonly List<GLVertexArray> nativeArrays = new List<GLVertexArray>();
		private readonly Stack<int> freeNativeArrays = new Stack<int>();

		/// <summary>The vertex buffer an array made here was given (its attributes point at it).</summary>
		private readonly Dictionary<GLVertexArray, GLBuffer> nativeVertices = new Dictionary<GLVertexArray, GLBuffer>();

		/// <summary>Arrays made here and alive now, for the log.</summary>
		public int NativeArrayCount => nativeArrays.Count - freeNativeArrays.Count;

		/// <summary>An array by GL name or by a handle made here; null if there is none.</summary>
		private GLVertexArray ArrayNamed(int name)
		{
			if ((name & NativeArray) != 0)
			{
				int index = name & ~NativeArray;
				return index < nativeArrays.Count ? nativeArrays[index] : null;
			}

			return vertexArrays.TryGetValue(name, out GLVertexArray array) ? array : null;
		}

		public int CreateArray()
		{
			GLVertexArray array = new GLVertexArray();
			int index;
			if (freeNativeArrays.Count != 0)
			{
				index = freeNativeArrays.Pop();
				nativeArrays[index] = array;
			}
			else
			{
				index = nativeArrays.Count;
				nativeArrays.Add(array);
			}

			return NativeArray | index;
		}

		public void DeleteArray(int handle)
		{
			int index = handle & ~NativeArray;
			if ((handle & NativeArray) == 0 || index >= nativeArrays.Count || nativeArrays[index] == null)
			{
				return;
			}

			GLVertexArray array = nativeArrays[index];
			nativeArrays[index] = null;
			freeNativeArrays.Push(index);
			if (vertexArray == array)
			{
				vertexArray = null;
			}

			if (lastDrawArray == array)
			{
				lastDrawArrayName = 0;
				lastDrawArray = null;
			}

			if (nativeVertices.TryGetValue(array, out GLBuffer vertices))
			{
				nativeVertices.Remove(array);
				ReleaseMemory(vertices);
			}

			if (array.Elements != null)
			{
				ReleaseMemory(array.Elements);
				array.Elements = null;
			}
		}

		public void Vertices(int handle, ReadOnlySpan<byte> data, int byteOffset, bool whole)
		{
			GLVertexArray array = ArrayNamed(handle);
			if (array == null || (handle & NativeArray) == 0)
			{
				return;
			}

			if (!nativeVertices.TryGetValue(array, out GLBuffer buffer))
			{
				buffer = new GLBuffer();
				nativeVertices[array] = buffer;
			}

			fixed (byte* source = data)
			{
				if (whole)
				{
					WriteWhole(buffer, source, data.Length);
					// The attributes keep the buffer, not its memory: new memory needs no change to them, but the world
					// layer's view of the array is worked out again.
					array.Version++;
				}
				else
				{
					WriteRange(buffer, source, byteOffset, data.Length);
				}
			}
		}

		public void Indices(int handle, ReadOnlySpan<byte> data, bool intIndices)
		{
			GLVertexArray array = ArrayNamed(handle);
			if (array == null || (handle & NativeArray) == 0)
			{
				return;
			}

			array.Elements ??= new GLBuffer();
			fixed (byte* source = data)
			{
				WriteWhole(array.Elements, source, data.Length);
			}
		}

		public void Layout(int handle, int position, int normal, int uv, int color, int matrixChain, int stride)
		{
			GLVertexArray array = ArrayNamed(handle);
			if (array == null || (handle & NativeArray) == 0 || !nativeVertices.TryGetValue(array, out GLBuffer buffer))
			{
				return;
			}

			// As upstream's VertexBufferObject.SetAttribute: LibRenderVertex's fields in order, those the shader takes.
			int offset = 0;
			SetNativeAttribute(array, buffer, position, 3, GL_FLOAT, false, stride, offset);
			offset += 12;
			SetNativeAttribute(array, buffer, normal, 3, GL_FLOAT, false, stride, offset);
			offset += 12;
			SetNativeAttribute(array, buffer, uv, 2, GL_FLOAT, false, stride, offset);
			offset += 8;
			SetNativeAttribute(array, buffer, color, 4, GL_FLOAT, false, stride, offset);
			offset += 16;
			SetNativeAttribute(array, buffer, matrixChain, 3, GL_INT, true, stride, offset);
			array.Version++;
		}

		private static void SetNativeAttribute(GLVertexArray array, GLBuffer buffer, int index, int size, int type, bool integer, int stride, int offset)
		{
			if (index < 0 || index >= Attributes)
			{
				return;
			}

			ref Attribute attribute = ref array.Attributes[index];
			attribute.Enabled = true;
			attribute.Buffer = buffer;
			attribute.Size = size;
			attribute.Type = type;
			attribute.Normalized = false;
			attribute.Integer = integer;
			attribute.Stride = stride;
			attribute.Offset = offset;
		}

		/// <summary>A buffer's whole contents, as glBufferData: the same memory if it is big enough and no frame in flight
		/// drew with it, new memory otherwise.</summary>
		private void WriteWhole(GLBuffer buffer, byte* data, int length)
		{
			if (buffer.Memory.IsNull || buffer.Memory.Size < (uint)length || buffer.LastUse > completedSerial)
			{
				ReleaseMemory(buffer);
				buffer.Memory = bufferHeap.Allocate((ulong)Math.Max(length, 4));
				buffer.LastUse = 0;
			}

			buffer.Size = length;
			if (data != null && length > 0)
			{
				System.Buffer.MemoryCopy(data, buffer.Memory.Pointer, length, length);
			}
		}

		/// <summary>Part of a buffer, as glBufferSubData: copied first if a frame in flight drew with it.</summary>
		private void WriteRange(GLBuffer buffer, byte* data, long start, int length)
		{
			if (buffer.Memory.IsNull || data == null || length <= 0 || start < 0 || start + length > buffer.Size)
			{
				return;
			}

			if (buffer.LastUse > completedSerial)
			{
				DekoHeap.Allocation copy = bufferHeap.Allocate(buffer.Memory.Size);
				System.Buffer.MemoryCopy(buffer.Memory.Pointer, copy.Pointer, buffer.Size, buffer.Size);
				ReleaseMemory(buffer);
				buffer.Memory = copy;
				buffer.LastUse = 0;
			}

			System.Buffer.MemoryCopy(data, buffer.Memory.Pointer + start, length, length);
		}
	}
}
