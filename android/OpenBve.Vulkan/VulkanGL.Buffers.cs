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
using Silk.NET.Vulkan;

namespace OpenBve.Vulkan
{
	/*
	 * Buffers and vertex arrays. A buffer's data lives in host-visible memory the CPU writes
	 * straight into. GL lets a buffer change between two draws that use it; the GPU may then
	 * still be reading the old contents (this frame's, or the last), so such a change goes to a
	 * fresh copy and the old one is freed once the GPU is done (copy on write). Uniform buffers -
	 * the animation matrices, rewritten for every animated object - stay on the CPU and are copied
	 * into the frame's uniform ring when drawn with.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int Attributes = 8;
		private const int GL_FLOAT = 0x1406, GL_INT = 0x1404, GL_UNSIGNED_INT = 0x1405, GL_SHORT = 0x1402, GL_UNSIGNED_SHORT = 0x1403, GL_BYTE = 0x1400;

		private sealed class GLBuffer
		{
			internal BufferHeap.Allocation Memory;
			internal int Size;
			/// <summary>The serial of the last submission that drew with it.</summary>
			internal ulong LastUse;

			// A uniform buffer: kept on the CPU, copied into the ring when drawn with.
			internal bool Uniform;
			internal byte[] Shadow;
			internal int Version;
			internal int RingVersion = -1;
			internal ulong RingSerial;
			internal BufferHeap.Block RingBlock;
			internal uint RingOffset;
		}

		private struct Attribute
		{
			internal bool Enabled;
			internal GLBuffer Buffer;
			internal int Size;
			internal int Type;
			internal bool Normalized;
			internal bool Integer;
			internal int Stride;
			internal long Offset;
		}

		private sealed class GLVertexArray
		{
			internal readonly Attribute[] Attributes = new Attribute[VulkanGL.Attributes];
			internal GLBuffer Elements;
			/// <summary>The vertex layout's number (VertexLayoutId), or -1 when the attributes changed.</summary>
			internal int LayoutId = -1;
		}

		private readonly Dictionary<int, GLBuffer> glBuffers = new Dictionary<int, GLBuffer>();
		private readonly Dictionary<int, GLVertexArray> vertexArrays = new Dictionary<int, GLVertexArray>();
		private readonly GLVertexArray defaultVertexArray = new GLVertexArray();
		private GLVertexArray vertexArray;
		private GLBuffer arrayBuffer;
		private GLBuffer uniformBuffer;
		private readonly GLBuffer[] uniformBindings = new GLBuffer[8];

		public void glGenBuffers(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				glBuffers[names[i]] = new GLBuffer();
			}
		}

		public void glDeleteBuffers(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (names[i] == 0 || !glBuffers.TryGetValue(names[i], out GLBuffer buffer))
				{
					continue;
				}

				glBuffers.Remove(names[i]);
				if (arrayBuffer == buffer)
				{
					arrayBuffer = null;
				}

				if (uniformBuffer == buffer)
				{
					uniformBuffer = null;
				}

				for (int binding = 0; binding < uniformBindings.Length; binding++)
				{
					if (uniformBindings[binding] == buffer)
					{
						uniformBindings[binding] = null;
					}
				}

				if (vertexArray != null && vertexArray.Elements == buffer)
				{
					vertexArray.Elements = null;
				}

				ReleaseMemory(buffer);
				buffer.Shadow = null;
			}
		}

		/// <summary>Frees a buffer's memory once the GPU is done with it.</summary>
		private void ReleaseMemory(GLBuffer buffer)
		{
			if (buffer.Memory.IsNull)
			{
				return;
			}

			BufferHeap.Allocation memory = buffer.Memory;
			if (buffer.LastUse <= context.CompletedSerial)
			{
				bufferHeap.Free(memory);
			}
			else
			{
				context.Defer(() => bufferHeap.Free(memory));
			}

			buffer.Memory = default;
		}

		private GLBuffer Named(int name)
		{
			if (name == 0)
			{
				return null;
			}

			if (!glBuffers.TryGetValue(name, out GLBuffer buffer))
			{
				buffer = new GLBuffer();
				glBuffers[name] = buffer;
			}

			return buffer;
		}

		public void glBindBuffer(int target, int buffer)
		{
			switch (target)
			{
				case GL_ARRAY_BUFFER:
					arrayBuffer = Named(buffer);
					break;
				case GL_ELEMENT_ARRAY_BUFFER:
					// Part of the vertex array's state, as in GL.
					CurrentVertexArray.Elements = Named(buffer);
					break;
				case GL_UNIFORM_BUFFER:
					uniformBuffer = Named(buffer);
					break;
			}
		}

		public void glBindBufferBase(int target, int index, int buffer)
		{
			if (target != GL_UNIFORM_BUFFER || index < 0 || index >= uniformBindings.Length)
			{
				return;
			}

			uniformBindings[index] = Named(buffer);
			uniformBuffer = uniformBindings[index];
		}

		private GLBuffer Target(int target)
		{
			switch (target)
			{
				case GL_ARRAY_BUFFER:
					return arrayBuffer;
				case GL_ELEMENT_ARRAY_BUFFER:
					return CurrentVertexArray.Elements;
				case GL_UNIFORM_BUFFER:
					return uniformBuffer;
				default:
					return null;
			}
		}

		public void glBufferData(int target, IntPtr size, IntPtr data, int usage)
		{
			GLBuffer buffer = Target(target);
			int length = (int)size;
			if (buffer == null || length < 0)
			{
				return;
			}

			if (target == GL_UNIFORM_BUFFER || buffer.Uniform)
			{
				buffer.Uniform = true;
				if (buffer.Shadow == null || buffer.Shadow.Length != length)
				{
					buffer.Shadow = new byte[length];
				}

				if (data != IntPtr.Zero)
				{
					new ReadOnlySpan<byte>((void*)data, length).CopyTo(buffer.Shadow);
				}

				buffer.Size = length;
				buffer.Version++;
				return;
			}

			// The same memory again if it is big enough and the GPU is not reading it; otherwise new.
			if (buffer.Memory.IsNull || buffer.Memory.Size < (ulong)length || buffer.LastUse > context.CompletedSerial)
			{
				ReleaseMemory(buffer);
				buffer.Memory = bufferHeap.Allocate((ulong)Math.Max(length, 4));
				buffer.LastUse = 0;
			}

			buffer.Size = length;
			if (data != IntPtr.Zero && length > 0)
			{
				System.Buffer.MemoryCopy((void*)data, buffer.Memory.Pointer, length, length);
			}
		}

		public void glBufferSubData(int target, IntPtr offset, IntPtr size, IntPtr data)
		{
			GLBuffer buffer = Target(target);
			long start = (long)offset;
			int length = (int)size;
			if (buffer == null || data == IntPtr.Zero || length <= 0 || start < 0 || start + length > buffer.Size)
			{
				return;
			}

			if (buffer.Uniform)
			{
				new ReadOnlySpan<byte>((void*)data, length).CopyTo(new Span<byte>(buffer.Shadow, (int)start, length));
				buffer.Version++;
				return;
			}

			if (buffer.Memory.IsNull)
			{
				return;
			}

			if (buffer.LastUse > context.CompletedSerial)
			{
				// Copy on write: the GPU may still read what is there.
				BufferHeap.Allocation copy = bufferHeap.Allocate(buffer.Memory.Size);
				System.Buffer.MemoryCopy(buffer.Memory.Pointer, copy.Pointer, buffer.Size, buffer.Size);
				ReleaseMemory(buffer);
				buffer.Memory = copy;
				buffer.LastUse = 0;
			}

			System.Buffer.MemoryCopy((void*)data, buffer.Memory.Pointer + start, length, length);
		}

		// --- vertex arrays ---

		private GLVertexArray CurrentVertexArray => vertexArray ?? defaultVertexArray;

		public void glGenVertexArrays(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				names[i] = nextName++;
				vertexArrays[names[i]] = new GLVertexArray();
			}
		}

		public void glDeleteVertexArrays(int n, int[] names)
		{
			for (int i = 0; i < n && i < names.Length; i++)
			{
				if (vertexArrays.TryGetValue(names[i], out GLVertexArray array))
				{
					vertexArrays.Remove(names[i]);
					if (vertexArray == array)
					{
						vertexArray = null;
					}
				}
			}
		}

		public void glBindVertexArray(int array)
		{
			if (array == 0)
			{
				vertexArray = null;
				return;
			}

			if (!vertexArrays.TryGetValue(array, out vertexArray))
			{
				vertexArray = new GLVertexArray();
				vertexArrays[array] = vertexArray;
			}
		}

		public void glEnableVertexAttribArray(int index)
		{
			if (index >= 0 && index < Attributes)
			{
				CurrentVertexArray.Attributes[index].Enabled = true;
				CurrentVertexArray.LayoutId = -1;
			}
		}

		public void glDisableVertexAttribArray(int index)
		{
			if (index >= 0 && index < Attributes)
			{
				CurrentVertexArray.Attributes[index].Enabled = false;
				CurrentVertexArray.LayoutId = -1;
			}
		}

		public void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer) => SetAttribute(index, size, type, normalized != 0, false, stride, pointer);

		public void glVertexAttribIPointer(int index, int size, int type, int stride, IntPtr pointer) => SetAttribute(index, size, type, false, true, stride, pointer);

		private void SetAttribute(int index, int size, int type, bool normalized, bool integer, int stride, IntPtr pointer)
		{
			if (index < 0 || index >= Attributes)
			{
				return;
			}

			GLVertexArray array = CurrentVertexArray;
			ref Attribute attribute = ref array.Attributes[index];
			attribute.Buffer = arrayBuffer;
			attribute.Size = size;
			attribute.Type = type;
			attribute.Normalized = normalized;
			attribute.Integer = integer;
			// Stride 0 is tightly packed in GL.
			attribute.Stride = stride != 0 ? stride : size * TypeBytes(type);
			attribute.Offset = (long)pointer;
			array.LayoutId = -1;
		}

		private static int TypeBytes(int type)
		{
			switch (type)
			{
				case GL_BYTE:
				case GL_UNSIGNED_BYTE:
					return 1;
				case GL_SHORT:
				case GL_UNSIGNED_SHORT:
					return 2;
				default:
					return 4;
			}
		}

		/// <summary>The Vulkan format a GL vertex attribute is read in, or Undefined if not supported.</summary>
		private static Format AttributeFormat(int type, int size, bool normalized, bool integer)
		{
			if (size < 1 || size > 4)
			{
				return Format.Undefined;
			}

			int n = size - 1;
			switch (type)
			{
				case GL_FLOAT:
					return new[] { Format.R32Sfloat, Format.R32G32Sfloat, Format.R32G32B32Sfloat, Format.R32G32B32A32Sfloat }[n];
				case GL_INT:
					return integer ? new[] { Format.R32Sint, Format.R32G32Sint, Format.R32G32B32Sint, Format.R32G32B32A32Sint }[n] : Format.Undefined;
				case GL_UNSIGNED_INT:
					return integer ? new[] { Format.R32Uint, Format.R32G32Uint, Format.R32G32B32Uint, Format.R32G32B32A32Uint }[n] : Format.Undefined;
				case GL_UNSIGNED_BYTE:
					return normalized ? new[] { Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8Unorm, Format.R8G8B8A8Unorm }[n] :
						integer ? new[] { Format.R8Uint, Format.R8G8Uint, Format.R8G8B8Uint, Format.R8G8B8A8Uint }[n] : Format.Undefined;
				case GL_BYTE:
					return normalized ? new[] { Format.R8SNorm, Format.R8G8SNorm, Format.R8G8B8SNorm, Format.R8G8B8A8SNorm }[n] : Format.Undefined;
				case GL_UNSIGNED_SHORT:
					return normalized ? new[] { Format.R16Unorm, Format.R16G16Unorm, Format.R16G16B16Unorm, Format.R16G16B16A16Unorm }[n] : Format.Undefined;
				case GL_SHORT:
					return normalized ? new[] { Format.R16SNorm, Format.R16G16SNorm, Format.R16G16B16SNorm, Format.R16G16B16A16SNorm }[n] : Format.Undefined;
				default:
					return Format.Undefined;
			}
		}
	}
}
