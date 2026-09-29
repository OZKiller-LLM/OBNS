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

// ReSharper disable once CheckNamespace
namespace System.Drawing.Imaging
{
	/// <summary>Pixel format identifiers, mirroring the values used by System.Drawing.Common.</summary>
	public enum PixelFormat
	{
		Undefined = 0,
		DontCare = 0,
		Format1bppIndexed = 196865,
		Format4bppIndexed = 197634,
		Format8bppIndexed = 198659,
		Format16bppRgb555 = 135173,
		Format16bppRgb565 = 135174,
		Format16bppArgb1555 = 397319,
		Format24bppRgb = 137224,
		Format32bppRgb = 139273,
		Format32bppArgb = 2498570,
		Format32bppPArgb = 925707,
		Canonical = 2097152
	}

	/// <summary>Access mode used when locking bitmap bits.</summary>
	public enum ImageLockMode
	{
		ReadOnly = 1,
		WriteOnly = 2,
		ReadWrite = 3,
		UserInputBuffer = 4
	}

	/// <summary>Describes a locked bitmap's raw pixel memory.</summary>
	public sealed class BitmapData
	{
		/// <summary>Pointer to the first pixel of the first row.</summary>
		public IntPtr Scan0 { get; internal set; }

		/// <summary>Byte offset between the start of one row and the next.</summary>
		public int Stride { get; internal set; }

		/// <summary>Width in pixels.</summary>
		public int Width { get; internal set; }

		/// <summary>Height in pixels.</summary>
		public int Height { get; internal set; }

		/// <summary>The pixel format of the locked data.</summary>
		public PixelFormat PixelFormat { get; internal set; }
	}

	/// <summary>Identifies an image file format.</summary>
	public sealed class ImageFormat
	{
		private readonly string name;

		private ImageFormat(string name)
		{
			this.name = name;
		}

		/// <summary>The PNG format.</summary>
		public static ImageFormat Png { get; } = new ImageFormat("Png");

		/// <summary>The JPEG format.</summary>
		public static ImageFormat Jpeg { get; } = new ImageFormat("Jpeg");

		/// <summary>The BMP format.</summary>
		public static ImageFormat Bmp { get; } = new ImageFormat("Bmp");

		/// <summary>The GIF format.</summary>
		public static ImageFormat Gif { get; } = new ImageFormat("Gif");

		/// <inheritdoc />
		public override string ToString()
		{
			return name;
		}
	}
}
