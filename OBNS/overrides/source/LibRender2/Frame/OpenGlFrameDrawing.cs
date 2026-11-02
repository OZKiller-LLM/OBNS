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

// OBNS: not an upstream file. The 2D part of a frame drawn by OpenGL (OBNS/RENDERERS.md): upstream's calls, from its
// Rectangle and OpenGlString.

using LibRender2.Shaders;
using OpenBveApi.Colors;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace LibRender2.Frame
{
	internal class OpenGlFrameDrawing : IFrameDrawing
	{
		private readonly BaseRenderer renderer;
		/// <summary>The shader to draw rectangles with</summary>
		private Shader rectangleShader;
		/// <summary>The shader to draw text with</summary>
		private Shader textShader;

		internal OpenGlFrameDrawing(BaseRenderer renderer)
		{
			this.renderer = renderer;
		}

		public void Initialize()
		{
			textShader = MakeShader("text"); //text shader shares the rectangle fragment shader
			rectangleShader = MakeShader("rectangle");
		}

		private Shader MakeShader(string vertexShader)
		{
			Shader shader = null;
			try
			{
				shader = new Shader(renderer, vertexShader, "rectangle", true);
			}
			catch
			{
				//
			}

			Matrix4D.CreateOrthographicOffCenter(0.0f, renderer.Screen.Width, renderer.Screen.Height, 0.0f, -1.0f, 1.0f, out Matrix4D ProjectionMatrix);
			shader.Activate();
			shader.SetCurrentProjectionMatrix(ProjectionMatrix);
			shader.SetCurrentModelViewMatrix(Matrix4D.Identity);
			return shader;
		}

		public void ScreenChanged()
		{
			Matrix4D.CreateOrthographicOffCenter(0.0f, renderer.Screen.Width, renderer.Screen.Height, 0.0f, -1.0f, 1.0f, out Matrix4D ProjectionMatrix);
			textShader.Activate();
			textShader.SetCurrentProjectionMatrix(ProjectionMatrix);
			rectangleShader.Activate();
			rectangleShader.SetCurrentProjectionMatrix(ProjectionMatrix);
		}

		public void Clear()
		{
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
		}

		public void SetBlend(bool enabled, BlendingFactor source, BlendingFactor destination)
		{
			if (enabled)
			{
				GL.Enable(EnableCap.Blend);
				GL.BlendFunc(source, destination);
			}
			else
			{
				GL.Disable(EnableCap.Blend);
			}
		}

		public void SetAlphaTest(bool enabled, AlphaFunction function, float value)
		{
			renderer.CurrentShader.SetAlphaTest(enabled);
			if (enabled)
			{
				renderer.CurrentShader.SetAlphaFunction(function, value);
			}
		}

		public void SetDepthWrite(bool enabled)
		{
			GL.DepthMask(enabled);
		}

		public void Rectangle(Texture texture, OpenGlTextureWrapMode wrapMode, Vector2 point, Vector2 size, Color128 color, Vector2 coordinates)
		{
			rectangleShader.Activate();
			if (texture != null)
			{
				if (renderer.LastBoundTexture != texture.OpenGlTextures[(int)wrapMode])
				{
					GL.BindTexture(TextureTarget.Texture2D, texture.OpenGlTextures[(int)wrapMode].Name);
					renderer.LastBoundTexture = texture.OpenGlTextures[(int)wrapMode];
				}
			}
			else
			{
				rectangleShader.DisableTexturing();
			}

			rectangleShader.SetColor(color);
			rectangleShader.SetPoint(point);
			rectangleShader.SetSize(size);
			rectangleShader.SetCoordinates(coordinates);
			/*
			 * In order to call GL.DrawArrays with procedural data within the shader,
			 * we first need to bind a dummy VAO
			 * If this is not done, it will generate an InvalidOperation error code
			 */
			renderer.dummyVao.Bind();
			GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 6);
		}

		public void BeginText()
		{
			textShader.Activate();
			renderer.CurrentShader = textShader;

			/*
			 * In order to call GL.DrawArrays with procedural data within the shader,
			 * we first need to bind a dummy VAO
			 * If this is not done, it will generate an InvalidOperation error code
			 */
			renderer.dummyVao.Bind();
		}

		public void Glyph(Texture texture, Vector4 atlas, Vector2 point, Vector2 size, Color128 color)
		{
			if (renderer.LastBoundTexture != texture.OpenGlTextures[(int)OpenGlTextureWrapMode.ClampClamp])
			{
				GL.BindTexture(TextureTarget.Texture2D, texture.OpenGlTextures[(int)OpenGlTextureWrapMode.ClampClamp].Name);
				renderer.LastBoundTexture = texture.OpenGlTextures[(int)OpenGlTextureWrapMode.ClampClamp];
			}

			textShader.SetAtlasLocation(atlas);

			/*
			 * In the first pass, mask off the background with pure black.
			 */
			GL.BlendFunc(BlendingFactor.Zero, BlendingFactor.OneMinusSrcColor);
			textShader.SetColor(new Color128(color.A, color.A, color.A, 1.0f));
			textShader.SetPoint(point);
			textShader.SetSize(size);

			GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 6);
			GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
			textShader.SetColor(color);
			GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 6);
		}

		public void EndText()
		{
			renderer.RestoreBlendFunc();
		}
	}
}
