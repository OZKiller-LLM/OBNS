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

using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace System.Drawing
{
	/// <summary>
	/// The runner's native image and text functions (NX/runner/source/obns_drawing.c), linked into the NRO: P/Invoke
	/// to "__Internal", compiled by Mono's AOT as direct calls.
	/// </summary>
	internal static unsafe class Native
	{
		/// <summary>Decodes a PNG, JPEG or GIF (first frame) to unpremultiplied BGRA, in memory from malloc.</summary>
		/// <returns>0 on success; otherwise the image's format wasn't recognised (1) or it couldn't be decoded (2).</returns>
		[DllImport("__Internal", EntryPoint = "obns_image_decode")]
		internal static extern int ImageDecode(byte* data, nint length, out int width, out int height, out IntPtr pixels);

		/// <summary>Frees pixels from <see cref="ImageDecode"/>.</summary>
		[DllImport("__Internal", EntryPoint = "obns_image_free")]
		internal static extern void ImageFree(IntPtr pixels);

		/// <summary>Encodes BGRA pixels as a PNG (format 0) or JPEG (format 1) to a file.</summary>
		[DllImport("__Internal", EntryPoint = "obns_image_save")]
		internal static extern int ImageSave([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int format, IntPtr pixels, int width, int height, int stride);

		/// <summary>Opens a font of the console's (by family name, bold and italic; any name falls back to the console's standard font) at a pixel size.</summary>
		[DllImport("__Internal", EntryPoint = "obns_font_open")]
		internal static extern IntPtr FontOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string family, int bold, int italic, float pixels);

		/// <summary>The font's ascent (negative, above the baseline) and descent (positive), in pixels.</summary>
		[DllImport("__Internal", EntryPoint = "obns_font_metrics")]
		internal static extern void FontMetrics(IntPtr font, out float ascent, out float descent);

		/// <summary>The advance width of UTF-16 text, the console's other fonts filling characters this one lacks.</summary>
		[DllImport("__Internal", EntryPoint = "obns_font_measure")]
		internal static extern float FontMeasure(IntPtr font, char* text, int length);

		/// <summary>
		/// Draws UTF-16 text, antialiased, onto unpremultiplied BGRA pixels: the baseline's start at (x, y) in text
		/// space, mapped to the pixels by the affine matrix (m11, m12, m21, m22, dx, dy), clipped to a rectangle.
		/// </summary>
		[DllImport("__Internal", EntryPoint = "obns_font_draw")]
		internal static extern void FontDraw(IntPtr font, char* text, int length, float x, float y, uint argb, float* matrix,
			IntPtr pixels, int width, int height, int stride, int clipLeft, int clipTop, int clipRight, int clipBottom);

		/// <summary>Closes a font from <see cref="FontOpen"/>.</summary>
		[DllImport("__Internal", EntryPoint = "obns_font_close")]
		internal static extern void FontClose(IntPtr font);

		/// <summary>The console's font family names.</summary>
		internal static readonly string[] Families = { "Nintendo Standard", "Nintendo Chinese Simplified", "Nintendo Chinese Traditional", "Nintendo Korean" };
	}
}
