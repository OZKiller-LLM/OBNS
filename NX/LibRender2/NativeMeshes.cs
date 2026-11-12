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

namespace LibRender2
{
	/*
	 * OBNS 1.32 (OBNS/SWITCH.md, #11 stage 1): meshes without GL calls. The Switch's VertexArrayObject, VertexBufferObject
	 * and IndexBufferObject (NX/LibRender2, in place of upstream's) hand their data to this device when one is set: it
	 * keeps the vertices and indices in its own GPU memory under a handle of its own, which the renderer's faces then use
	 * as they used the GL name. Called on the render thread only, as upstream's GL calls are.
	 */

	/// <summary>A device that keeps meshes in its own memory (the Switch's deko3d renderer).</summary>
	public interface INativeMeshes
	{
		/// <summary>A new, empty vertex array; its handle (never 0).</summary>
		int CreateArray();

		/// <summary>The array, its vertices and its indices freed (once the GPU is done with them).</summary>
		void DeleteArray(int array);

		/// <summary>
		/// Vertices for the array: <paramref name="whole"/>, the buffer's whole contents (new memory as needed); otherwise
		/// written over what is there from <paramref name="byteOffset"/> (copied first if a frame in flight draws with it).
		/// </summary>
		void Vertices(int array, ReadOnlySpan<byte> data, int byteOffset, bool whole);

		/// <summary>The array's indices, 16 or 32 bits each.</summary>
		void Indices(int array, ReadOnlySpan<byte> data, bool intIndices);

		/// <summary>
		/// The array's vertex layout: LibRenderVertex's fields (position, normal, UV, colour, matrix chain) at the shader's
		/// attribute locations given, -1 for one the shader doesn't take.
		/// </summary>
		void Layout(int array, int position, int normal, int uv, int color, int matrixChain, int stride);
	}

	/// <summary>Where the Switch's mesh classes find their device.</summary>
	public static class NativeMeshes
	{
		/// <summary>The device, or null: upstream's GL calls.</summary>
		public static INativeMeshes Device;
	}
}
