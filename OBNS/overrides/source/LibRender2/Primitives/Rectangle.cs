// OBNS: a copy of upstream's source/LibRender2/Primitives/Rectangle.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1): the 2D frame goes through IFrameDrawing (OBNS/RENDERERS.md).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2022, The OpenBVE Project
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

using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace LibRender2.Primitives
{
	public class Rectangle
	{
		/// <summary>Holds a reference to the base renderer</summary>
		private readonly BaseRenderer renderer;

		internal Rectangle(BaseRenderer renderer)
		{
			this.renderer = renderer;
		}

		public void Update()
		{
			renderer.Frame.ScreenChanged(); // OBNS
		}

		/// <summary>Draws a simple 2D rectangle using two-pass alpha blending.</summary>
		/// <param name="texture">The texture, or a null reference.</param>
		/// <param name="point">The top-left coordinates in pixels.</param>
		/// <param name="size">The size in pixels.</param>
		/// <param name="color">The color, or a null reference.</param>
		/// <param name="textureCoordinates">The texture coordinates to be applied</param>
		/// <param name="wrapMode">A wrap mode if overriding that of the texture</param>
		public void DrawAlpha(Texture texture, Vector2 point, Vector2 size, Color128? color = null, Vector2? textureCoordinates = null, OpenGlTextureWrapMode? wrapMode = null)
		{
			renderer.UnsetBlendFunc();
			renderer.SetAlphaFunc(AlphaFunction.Equal, 1.0f);
			renderer.Frame.SetDepthWrite(true); // OBNS
			Draw(texture, point, size, color, textureCoordinates, wrapMode);
			renderer.SetBlendFunc();
			renderer.SetAlphaFunc(AlphaFunction.Less, 1.0f);
			renderer.Frame.SetDepthWrite(false); // OBNS
			Draw(texture, point, size, color, textureCoordinates, wrapMode);
			renderer.SetAlphaFunc(AlphaFunction.Equal, 1.0f);
		}

		/// <summary>Draws a simple 2D rectangle using two-pass alpha blending, using the texture size.</summary>
		/// <param name="texture">The texture, or a null reference.</param>
		/// <param name="point">The top-left coordinates in pixels.</param>
		/// <param name="color">The color, or a null reference.</param>
		/// <param name="textureCoordinates">The texture coordinates to be applied</param>
		public void DrawAlpha(Texture texture, Vector2 point, Color128? color = null, Vector2? textureCoordinates = null)
		{
			if (texture == null)
			{
				return;
			}
			renderer.UnsetBlendFunc();
			renderer.SetAlphaFunc(AlphaFunction.Equal, 1.0f);
			renderer.Frame.SetDepthWrite(true); // OBNS
			Draw(texture, point, texture.Size, color, textureCoordinates);
			renderer.SetBlendFunc();
			renderer.SetAlphaFunc(AlphaFunction.Less, 1.0f);
			renderer.Frame.SetDepthWrite(false); // OBNS
			Draw(texture, point, texture.Size, color, textureCoordinates);
			renderer.SetAlphaFunc(AlphaFunction.Equal, 1.0f);
		}

		/// <summary>Draws a simple 2D rectangle.</summary>
		/// <param name="texture">The texture, or a null reference.</param>
		/// <param name="point">The top-left coordinates in pixels.</param>
		/// <param name="size">The size in pixels.</param>
		/// <param name="color">The color, or a null reference.</param>
		/// <param name="textureCoordinates">The texture coordinates to be applied</param>
		/// <param name="wrapMode">A wrap mode if overriding that of the texture</param>
		public void Draw(Texture texture, Vector2 point, Vector2 size, Color128? color = null, Vector2? textureCoordinates = null, OpenGlTextureWrapMode? wrapMode = null)
		{
			if (textureCoordinates == null)
			{
				DrawWithShader(texture, point, size, color, Vector2.One, wrapMode);
			}
			else
			{
				DrawWithShader(texture, point, size, color, (Vector2)textureCoordinates, wrapMode);
			}
		}

		/// <summary>Draws a simple 2D rectangle, using the texture size.</summary>
		/// <param name="texture">The texture, or a null reference.</param>
		/// <param name="point">The top-left coordinates in pixels.</param>
		/// <param name="color">The color, or a null reference.</param>
		/// <param name="textureCoordinates">The texture coordinates to be applied</param>
		public void Draw(Texture texture, Vector2 point, Color128? color = null, Vector2? textureCoordinates = null)
		{
			if (texture == null)
			{
				return;
			}
			if (textureCoordinates == null)
			{
				DrawWithShader(texture, point, texture.Size, color, Vector2.One);
			}
			else
			{
				DrawWithShader(texture, point, texture.Size, color, (Vector2)textureCoordinates);
			}
		}

		private void DrawWithShader(Texture texture, Vector2 point, Vector2 size, Color128? color, Vector2 coordinates, OpenGlTextureWrapMode? wrapMode = null)
		{
			if (wrapMode == null)
			{
				wrapMode = OpenGlTextureWrapMode.ClampClamp;
			}

			// OBNS: the drawing is the API's (IFrameDrawing).
			if (texture != null && !renderer.currentHost.LoadTexture(ref texture, (OpenGlTextureWrapMode)wrapMode))
			{
				texture = null;
			}

			renderer.Frame.Rectangle(texture, (OpenGlTextureWrapMode)wrapMode, point, size, color ?? Color128.White, coordinates);
		}
	}
}
