// OBNS: a copy of upstream's source/LibRender2/Text/OpenGlString.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1): the 2D frame goes through IFrameDrawing (OBNS/RENDERERS.md).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using OpenBveApi.Colors;
using OpenBveApi.Graphics;
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Textures;
using OpenTK.Graphics.OpenGL;

namespace LibRender2.Text
{
	public class OpenGlString
	{
		private readonly BaseRenderer renderer;

		internal OpenGlString(BaseRenderer renderer)
		{
			this.renderer = renderer;
		}

		public void Update()
		{
			renderer.Frame.ScreenChanged(); // OBNS
		}


		/// <summary>Renders a string to the screen.</summary>
		/// <param name="font">The font to use.</param>
		/// <param name="text">The string to render.</param>
		/// <param name="location">The location.</param>
		/// <param name="alignment">The alignment.</param>
		/// <param name="color">The color.</param>
		/// <remarks>This function sets the OpenGL blend function to glBlendFunc(Gl.GL_SRC_ALPHA, Gl.GL_ONE_MINUS_SRC_ALPHA).</remarks>
		public void Draw(OpenGlFont font, string text, Vector2 location, TextAlignment alignment, Color128 color)
		{
			if (text == null || font == null)
			{
				return;
			}
			/*
			 * Prepare the top-left coordinates for rendering, incorporating the
			 * orientation of the string in relation to the specified location.
			 * */
			double left;

			if ((alignment & TextAlignment.Left) == 0)
			{
				double width = 0;

				for (int i = 0; i < text.Length; i++)
				{
					i += font.GetCharacterData(text, i, out _, out OpenGlFontChar data) - 1;
					width += data.TypographicSize.X;
				}

				if ((alignment & TextAlignment.Right) != 0)
				{
					left = location.X - width;
				}
				else
				{
					left = location.X - width / 2;
				}
			}
			else
			{
				left = location.X;
			}

			double top;

			if ((alignment & TextAlignment.Top) == 0)
			{
				double height = 0;

				for (int i = 0; i < text.Length; i++)
				{
					i += font.GetCharacterData(text, i, out _, out OpenGlFontChar data) - 1;

					if (data.TypographicSize.Y > height)
					{
						height = data.TypographicSize.Y;
					}
				}

				if ((alignment & TextAlignment.Bottom) != 0)
				{
					top = location.Y - height;
				}
				else
				{
					top = location.Y - height / 2;
				}
			}
			else
			{
				top = location.Y;
			}

			if (renderer.currentHost.Application != HostApplication.TrainEditor2)
			{
				DrawWithShader(text, font, left, top, color);
			}
			else
			{
				DrawImmediate(text, font, left, top, color);
			}

		}

		private void DrawImmediate(string text, OpenGlFont font, double left, double top, Color128 color)
		{
			/*
			 * Render the string.
			 * */
			GL.Enable(EnableCap.Texture2D);

			GL.MatrixMode(MatrixMode.Projection);
			GL.PushMatrix();
			unsafe
			{
				fixed (double* matrixPointer = &renderer.CurrentProjectionMatrix.Row0.X)
				{
					GL.LoadMatrix(matrixPointer);
				}
				GL.MatrixMode(MatrixMode.Modelview);
				GL.PushMatrix();
				fixed (double* matrixPointer = &renderer.CurrentViewMatrix.Row0.X)
				{
					GL.LoadMatrix(matrixPointer);
				}
			}
			

			for (int i = 0; i < text.Length; i++)
			{
				i += font.GetCharacterData(text, i, out Texture texture, out OpenGlFontChar data) - 1;

				if (renderer.currentHost.LoadTexture(ref texture, OpenGlTextureWrapMode.ClampClamp))
				{
					GL.BindTexture(TextureTarget.Texture2D, texture.OpenGlTextures[(int)OpenGlTextureWrapMode.ClampClamp].Name);

					double x = left - (data.PhysicalSize.X - data.TypographicSize.X) / 2;
					double y = top - (data.PhysicalSize.Y - data.TypographicSize.Y) / 2;

					/*
					 * In the first pass, mask off the background with pure black.
					 * */
					GL.BlendFunc(BlendingFactor.Zero, BlendingFactor.OneMinusSrcColor);
					GL.Begin(PrimitiveType.Quads);
					GL.Color4(color.A, color.A, color.A, 1.0f);
					GL.TexCoord2(data.TextureCoordinates.X, data.TextureCoordinates.Y);
					GL.Vertex2(x, y);
					GL.TexCoord2(data.TextureCoordinates.X + data.TextureCoordinates.Z, data.TextureCoordinates.Y);
					GL.Vertex2(x + data.PhysicalSize.X, y);
					GL.TexCoord2(data.TextureCoordinates.X + data.TextureCoordinates.Z, data.TextureCoordinates.Y + data.TextureCoordinates.W);
					GL.Vertex2(x + data.PhysicalSize.X, y + data.PhysicalSize.Y);
					GL.TexCoord2(data.TextureCoordinates.X, data.TextureCoordinates.Y + data.TextureCoordinates.W);
					GL.Vertex2(x, y + data.PhysicalSize.Y);
					GL.End();

					/*
					 * In the second pass, add the character onto the background.
					 * */
					GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
					GL.Begin(PrimitiveType.Quads);
					GL.Color4(color.R, color.G, color.B, color.A);
					GL.TexCoord2(data.TextureCoordinates.X, data.TextureCoordinates.Y);
					GL.Vertex2(x, y);
					GL.TexCoord2(data.TextureCoordinates.X + data.TextureCoordinates.Z, data.TextureCoordinates.Y);
					GL.Vertex2(x + data.PhysicalSize.X, y);
					GL.TexCoord2(data.TextureCoordinates.X + data.TextureCoordinates.Z, data.TextureCoordinates.Y + data.TextureCoordinates.W);
					GL.Vertex2(x + data.PhysicalSize.X, y + data.PhysicalSize.Y);
					GL.TexCoord2(data.TextureCoordinates.X, data.TextureCoordinates.Y + data.TextureCoordinates.W);
					GL.Vertex2(x, y + data.PhysicalSize.Y);
					GL.End();
				}

				left += data.TypographicSize.X;
			}

			renderer.RestoreBlendFunc();
			GL.Disable(EnableCap.Texture2D);

			GL.PopMatrix();

			GL.MatrixMode(MatrixMode.Projection);
			GL.PopMatrix();
		}

		private void DrawWithShader(string text, OpenGlFont font, double left, double top, Color128 color)
		{
			// OBNS: the drawing is the API's (IFrameDrawing).
			renderer.Frame.BeginText();

			for (int i = 0; i < text.Length; i++)
			{
				i += font.GetCharacterData(text, i, out Texture texture, out OpenGlFontChar data) - 1;
				if (renderer.currentHost.LoadTexture(ref texture, OpenGlTextureWrapMode.ClampClamp))
				{
					double x = left - (data.PhysicalSize.X - data.TypographicSize.X) / 2;
					double y = top - (data.PhysicalSize.Y - data.TypographicSize.Y) / 2;

					renderer.Frame.Glyph(texture, data.TextureCoordinates, new Vector2(x, y), data.PhysicalSize, color);
				}
				left += data.TypographicSize.X;
			}
			renderer.Frame.EndText();
		}

		/// <summary>Renders a string to the screen.</summary>
		/// <param name="font">The font to use.</param>
		/// <param name="text">The string to render.</param>
		/// <param name="location">The location.</param>
		/// <param name="alignment">The alignment.</param>
		/// <param name="color">The color.</param>
		/// <param name="shadow">Whether to draw a shadow.</param>
		/// <remarks>This function sets the OpenGL blend function to glBlendFunc(Gl.GL_SRC_ALPHA, Gl.GL_ONE_MINUS_SRC_ALPHA).</remarks>
		public void Draw(OpenGlFont font, string text, Vector2 location, TextAlignment alignment, Color128 color, bool shadow)
		{
			if (shadow)
			{
				Draw(font, text, new Vector2(location.X - 1, location.Y + 1), alignment, new Color128(0.0f, 0.0f, 0.0f, 0.5f * color.A));
			}
			Draw(font, text, location, alignment, color);
		}
	}
}
