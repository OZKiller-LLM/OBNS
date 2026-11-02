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

// OBNS: not an upstream file. What the 2D part of a frame asks of the API drawing it (OBNS/RENDERERS.md): the HUD, text,
// the in-game menu and the loading screen all come down to these.

using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace LibRender2.Frame
{
	/// <summary>The API drawing the 2D part of a frame.</summary>
	public interface IFrameDrawing
	{
		/// <summary>Makes what the drawing needs: the API is ready.</summary>
		void Initialize();

		/// <summary>The screen's size has changed.</summary>
		void ScreenChanged();

		/// <summary>Clears the frame's colour and depth.</summary>
		void Clear();

		/// <summary>The blending for what is drawn next.</summary>
		void SetBlend(bool enabled, BlendingFactor source, BlendingFactor destination);

		/// <summary>The alpha test for what is drawn next.</summary>
		void SetAlphaTest(bool enabled, AlphaFunction function, float value);

		/// <summary>Whether what is drawn next writes depth.</summary>
		void SetDepthWrite(bool enabled);

		/// <summary>A rectangle, in pixels from the screen's top left.</summary>
		/// <param name="texture">The texture, already loaded, or null for plain colour.</param>
		/// <param name="wrapMode">The wrap mode the texture was loaded with.</param>
		/// <param name="point">The top left.</param>
		/// <param name="size">The size.</param>
		/// <param name="color">The colour.</param>
		/// <param name="coordinates">The texture coordinates at the bottom right.</param>
		void Rectangle(Texture texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, Vector2 coordinates);

		/// <summary>Glyphs follow.</summary>
		void BeginText();

		/// <summary>A glyph: masks the background, then adds the character (upstream's two passes, which keep text sharp on any background).</summary>
		/// <param name="texture">The font's texture, already loaded.</param>
		/// <param name="atlas">The glyph's place in the texture: left, top, width, height.</param>
		/// <param name="point">The top left, in pixels.</param>
		/// <param name="size">The size, in pixels.</param>
		/// <param name="color">The colour.</param>
		void Glyph(Texture texture, Vector4 atlas, Vector2 point, Vector2 size, Color128 color);

		/// <summary>The glyphs are done: the blending goes back to what it was.</summary>
		void EndText();
	}
}
