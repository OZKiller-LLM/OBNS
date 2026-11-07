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

namespace OpenBve.NX.Deko
{
	/// <summary>One draw for the native side (NX/runner/source/obns_dk3d.inc's Draw3D: the same fields, in the same order).</summary>
	[StructLayout(LayoutKind.Sequential)]
	internal unsafe struct Draw3D
	{
		public fixed ulong VertexAddress[5];
		public fixed uint VertexSize[5];
		public fixed uint VertexStride[5];
		/// <summary>DkVtxAttribSize | DkVtxAttribType &lt;&lt; 8; 0: not read.</summary>
		public fixed uint Attribute[5];
		public uint AttributeCount;
		public ulong IndexAddress;
		/// <summary>DkIdxFormat + 1; 0: not indexed.</summary>
		public uint IndexFormat;
		public uint Primitive;
		public uint First;
		public uint Count;
		public int VertexOffset;
		public ulong BlockAddress;
		public uint BlockSize;
		public ulong MatricesAddress;
		public uint MatricesSize;
		/// <summary>0 default, 1 rectangle, 2 text.</summary>
		public uint Program;
		public uint Texture;
		public uint Sampler;
		public uint DepthTest, DepthWrite, DepthCompare;
		public uint Blend, BlendSource, BlendDestination;
		public uint Cull;
		public fixed int Viewport[4];
	}

	/// <summary>The native side of deko3d's 3D drawing (obns_dk3d.inc), and deko3d's values DekoGL hands it.</summary>
	internal static unsafe class DekoNative
	{
		[DllImport("__Internal", EntryPoint = "obns_dk3d_memory")]
		public static extern IntPtr Memory(uint size, int image, out IntPtr cpu, out ulong gpu);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_memory_free")]
		public static extern void MemoryFree(IntPtr block);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_image_size")]
		public static extern int ImageSize(int width, int height, int levels, int format, out ulong size, out uint alignment);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_image_init")]
		public static extern int ImageInit(int slot, int width, int height, int levels, int format, IntPtr block, uint offset, byte* swizzle);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_image_upload")]
		public static extern void ImageUpload(int slot, ulong source, int width, int height);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_image_upload_level")]
		public static extern void ImageUploadLevel(int slot, ulong source, int level, int width, int height);

		// Block compression (NX/runner/source/obns_bc.c): the formats are deko3d's.
		public const int FormatBC1 = 56, FormatBC1Alpha = 57, FormatBC3 = 59;
		/// <summary>With FormatBC3, for BcCompress: a cut-out (alpha 0 or 255), its hidden colours spread in and its mipmaps' alpha kept to 0 or 255.</summary>
		public const int BcCutout = 0x100;

		[DllImport("__Internal", EntryPoint = "obns_bc_levels")]
		public static extern int BcLevels(int width, int height);

		[DllImport("__Internal", EntryPoint = "obns_bc_size")]
		public static extern long BcSize(int width, int height, int format, int levels);

		[DllImport("__Internal", EntryPoint = "obns_bc_compress")]
		public static extern unsafe int BcCompress(byte* rgba, int width, int height, int format, int levels, byte* output);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_image_mipmaps")]
		public static extern void ImageMipmaps(int slot, int width, int height, int levels);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_sampler")]
		public static extern void Sampler(int slot, int minFilter, int magFilter, int mipFilter, int wrapS, int wrapT, float anisotropy);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_flush_uploads")]
		public static extern void FlushUploads();

		[DllImport("__Internal", EntryPoint = "obns_dk3d_frames_in_flight")]
		public static extern void SetFramesInFlight(int enabled);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_begin")]
		public static extern int Begin();

		[DllImport("__Internal", EntryPoint = "obns_dk3d_clear")]
		public static extern void Clear(int mask, float red, float green, float blue, float alpha);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_draw")]
		public static extern void Draw(Draw3D* draw);

		[DllImport("__Internal", EntryPoint = "obns_dk3d_end")]
		public static extern int End();

		[DllImport("__Internal", EntryPoint = "obns_dk3d_set_clip")]
		public static extern void SetClip(float x, float y, float z, float w);

		// DkImageFormat
		public const int FormatR8 = 1, FormatRG8 = 13, FormatRGBA8 = 28, FormatRGBA4 = 48, FormatRGB5A1 = 50, FormatRGB565 = 51;

		// DkImageSwizzle
		public const byte SwizzleZero = 0, SwizzleOne = 1, SwizzleRed = 2, SwizzleGreen = 3, SwizzleBlue = 4, SwizzleAlpha = 5;

		// DkFilter, DkMipFilter, DkWrapMode
		public const int FilterNearest = 1, FilterLinear = 2;
		public const int MipNone = 1, MipNearest = 2, MipLinear = 3;
		public const int WrapRepeat = 0, WrapMirroredRepeat = 1, WrapClampToEdge = 2;

		// DkVtxAttribType
		public const uint AttribSnorm = 1, AttribUnorm = 2, AttribSint = 3, AttribUint = 4, AttribFloat = 7;

		// DkVtxAttribSize, by component count (1 to 4) and component bytes.
		public static readonly uint[] Size32 = { 0x12, 0x04, 0x02, 0x01 };
		public static readonly uint[] Size16 = { 0x1b, 0x0f, 0x05, 0x03 };
		public static readonly uint[] Size8 = { 0x1d, 0x18, 0x13, 0x0a };

		// DkPrimitive
		public const uint Points = 0, Lines = 1, LineLoop = 2, LineStrip = 3, Triangles = 4, TriangleStrip = 5, TriangleFan = 6;

		// DkCompareOp: GL's (0x0200 + n) is deko3d's n + 1.
		public static uint Compare(int glFunction) => (uint)Math.Clamp(glFunction - 0x0200 + 1, 1, 8);

		// DkFace
		public const uint FaceNone = 0, FaceFront = 1, FaceBack = 2, FaceFrontAndBack = 3;

		/// <summary>GL's blend factor as deko3d's DkBlendFactor (the ones OpenBVE uses).</summary>
		public static uint BlendFactor(int gl)
		{
			switch (gl)
			{
				case 0: return 1; // zero
				case 1: return 2; // one
				case 0x0300: return 3; // src colour
				case 0x0301: return 4; // one minus src colour
				case 0x0302: return 5; // src alpha
				case 0x0303: return 6; // one minus src alpha
				case 0x0304: return 7; // dst alpha
				case 0x0305: return 8; // one minus dst alpha
				case 0x0306: return 9; // dst colour
				case 0x0307: return 10; // one minus dst colour
				default: return 2;
			}
		}
	}
}
