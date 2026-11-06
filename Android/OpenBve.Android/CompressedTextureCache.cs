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

using OpenBveApi.Textures;
using ApiTexture = OpenBveApi.Textures.Texture;

namespace OpenBve.Android
{
	/// <summary>A texture as the GPU keeps it compressed: its format (the device's), size, mipmap levels and their bytes.</summary>
	public sealed class CompressedTexture
	{
		public int Format, Width, Height, Levels;
		public byte[] Data;
		public TextureTransparencyType Transparency;
	}

	/// <summary>
	/// Streamed textures kept block-compressed on the storage (OBNS/SWITCH.md, step 4, item E): a texture decoded once is
	/// compressed in the background and kept, and from then on read back instead of decoded - no image decoding, and about
	/// a quarter of the GPU memory. A platform with a device that samples compressed formats sets
	/// <see cref="AndroidHost.CompressedTextures"/> (the Switch does); off otherwise.
	/// </summary>
	public interface ICompressedTextureCache
	{
		/// <summary>On a decoding thread: the kept compressed texture for the file and parameters, if there is one.</summary>
		bool TryRead(PathOrigin origin, out CompressedTexture texture);

		/// <summary>On a decoding thread, after a decode: compress and keep it (in the background; it may be skipped).</summary>
		void Store(PathOrigin origin, ApiTexture decoded);

		/// <summary>On the render thread: the compressed image for the bound texture; false if the device can't.</summary>
		bool Upload(CompressedTexture texture);
	}
}
