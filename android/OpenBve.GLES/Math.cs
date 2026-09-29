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
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace OpenTK
{
	/// <summary>A two-component vector, matching the layout and API surface OpenTK 3 exposes.</summary>
	/// <remarks>
	/// LibRender2 aliases most vector work to OpenBveApi.Math; these types exist for the places
	/// that talk to GL directly, and for the vertex layout sizes in VertexBufferObject.
	/// </remarks>
	[StructLayout(LayoutKind.Sequential)]
	public struct Vector2 : IEquatable<Vector2>
	{
		/// <summary>The X component.</summary>
		public float X;

		/// <summary>The Y component.</summary>
		public float Y;

		/// <summary>The size of this struct in bytes.</summary>
		public static readonly int SizeInBytes = 8;

		/// <summary>A vector with both components zero.</summary>
		public static readonly Vector2 Zero = new Vector2(0.0f, 0.0f);

		/// <summary>A vector with both components one.</summary>
		public static readonly Vector2 One = new Vector2(1.0f, 1.0f);

		/// <summary>Creates a new vector.</summary>
		public Vector2(float x, float y)
		{
			X = x;
			Y = y;
		}

		/// <inheritdoc />
		public bool Equals(Vector2 other)
		{
			return X == other.X && Y == other.Y;
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			return obj is Vector2 other && Equals(other);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return X.GetHashCode() ^ (Y.GetHashCode() << 2);
		}
	}

	/// <summary>A three-component vector, matching the layout and API surface OpenTK 3 exposes.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct Vector3 : IEquatable<Vector3>
	{
		/// <summary>The X component.</summary>
		public float X;

		/// <summary>The Y component.</summary>
		public float Y;

		/// <summary>The Z component.</summary>
		public float Z;

		/// <summary>The size of this struct in bytes.</summary>
		public static readonly int SizeInBytes = 12;

		/// <summary>A vector with all components zero.</summary>
		public static readonly Vector3 Zero = new Vector3(0.0f, 0.0f, 0.0f);

		/// <summary>A vector with all components one.</summary>
		public static readonly Vector3 One = new Vector3(1.0f, 1.0f, 1.0f);

		/// <summary>Creates a new vector.</summary>
		public Vector3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}

		/// <inheritdoc />
		public bool Equals(Vector3 other)
		{
			return X == other.X && Y == other.Y && Z == other.Z;
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			return obj is Vector3 other && Equals(other);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2);
		}
	}

	/// <summary>A four-component vector, matching the layout and API surface OpenTK 3 exposes.</summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct Vector4 : IEquatable<Vector4>
	{
		/// <summary>The X component.</summary>
		public float X;

		/// <summary>The Y component.</summary>
		public float Y;

		/// <summary>The Z component.</summary>
		public float Z;

		/// <summary>The W component.</summary>
		public float W;

		/// <summary>The size of this struct in bytes.</summary>
		public static readonly int SizeInBytes = 16;

		/// <summary>A vector with all components zero.</summary>
		public static readonly Vector4 Zero = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

		/// <summary>A vector with all components one.</summary>
		public static readonly Vector4 One = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

		/// <summary>Creates a new vector.</summary>
		public Vector4(float x, float y, float z, float w)
		{
			X = x;
			Y = y;
			Z = z;
			W = w;
		}

		/// <inheritdoc />
		public bool Equals(Vector4 other)
		{
			return X == other.X && Y == other.Y && Z == other.Z && W == other.W;
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			return obj is Vector4 other && Equals(other);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return X.GetHashCode() ^ (Y.GetHashCode() << 2) ^ (Z.GetHashCode() >> 2) ^ (W.GetHashCode() >> 1);
		}
	}

	/// <summary>
	/// A 4x4 single-precision matrix, laid out row by row exactly as OpenTK 3 lays it out,
	/// so that it can be handed straight to glUniformMatrix4fv and glBufferData.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct Matrix4 : IEquatable<Matrix4>
	{
		/// <summary>The first row.</summary>
		public Vector4 Row0;

		/// <summary>The second row.</summary>
		public Vector4 Row1;

		/// <summary>The third row.</summary>
		public Vector4 Row2;

		/// <summary>The fourth row.</summary>
		public Vector4 Row3;

		/// <summary>The identity matrix.</summary>
		public static readonly Matrix4 Identity = new Matrix4(
			1.0f, 0.0f, 0.0f, 0.0f,
			0.0f, 1.0f, 0.0f, 0.0f,
			0.0f, 0.0f, 1.0f, 0.0f,
			0.0f, 0.0f, 0.0f, 1.0f);

		/// <summary>Creates a matrix from four rows.</summary>
		public Matrix4(Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3)
		{
			Row0 = row0;
			Row1 = row1;
			Row2 = row2;
			Row3 = row3;
		}

		/// <summary>Creates a matrix from sixteen values, given row by row.</summary>
		public Matrix4(
			float m00, float m01, float m02, float m03,
			float m10, float m11, float m12, float m13,
			float m20, float m21, float m22, float m23,
			float m30, float m31, float m32, float m33)
		{
			Row0 = new Vector4(m00, m01, m02, m03);
			Row1 = new Vector4(m10, m11, m12, m13);
			Row2 = new Vector4(m20, m21, m22, m23);
			Row3 = new Vector4(m30, m31, m32, m33);
		}

		/// <inheritdoc />
		public bool Equals(Matrix4 other)
		{
			return Row0.Equals(other.Row0) && Row1.Equals(other.Row1) && Row2.Equals(other.Row2) && Row3.Equals(other.Row3);
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			return obj is Matrix4 other && Equals(other);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return Row0.GetHashCode() ^ Row1.GetHashCode() ^ Row2.GetHashCode() ^ Row3.GetHashCode();
		}
	}
}
