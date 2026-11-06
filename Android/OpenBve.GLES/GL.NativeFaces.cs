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

namespace OpenTK.Graphics.OpenGL
{
	/// <summary>
	/// A GL device that takes the world's and the cab's faces directly (AndroidRenderer.DrawFace): each face's transforms,
	/// material and drawing values handed over at once instead of as some fifteen uniform calls and a GL draw. Android's
	/// native Vulkan renderer (OpenBve.Vulkan.VulkanGL) is one; the Switch's deko3d renderer (DekoGL) another, which keeps
	/// the faces as records and draws them in batches, the alone way.
	/// </summary>
	public interface INativeFaceDevice
	{
		/// <summary>Whether faces can be drawn this way now (the default program current, the screen the target).</summary>
		bool CanDrawFaces { get; }

		/// <summary>The face's model view and texture matrices: 16 floats each, row by row of OpenBVE's matrix.</summary>
		void SetFaceTransforms(ReadOnlySpan<float> modelView, ReadOnlySpan<float> texture);

		/// <summary>The face's material, as upstream's RenderFace sets it (colours 0 to 1).</summary>
		void SetFaceMaterial(int flags, System.Numerics.Vector4 ambient, System.Numerics.Vector4 diffuse, System.Numerics.Vector4 specular, System.Numerics.Vector3 emission, float shininess);

		/// <summary>The face's brightness and opacity; no fog (additive faces); alpha test "greater than 0" (nighttime textures).</summary>
		void SetFaceDrawing(float brightness, float opacity, bool noFog, bool alphaAboveZero);

		/// <summary>Draws a range of a vertex array's indices as a face with the values above and GL's current state; false
		/// if it can't be drawn this way (the caller then draws it through GL).</summary>
		bool DrawFace(int vertexArrayName, int textureName, int mode, bool intIndices, int firstIndex, int count);
	}
}
