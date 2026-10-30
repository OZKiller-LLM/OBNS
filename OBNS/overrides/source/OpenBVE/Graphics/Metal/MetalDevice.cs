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

// OBNS: not an upstream file. The Metal world layer's device (OBNS/METAL.md): the system's Metal device (the GPU OpenGL
// draws on: a Mac with Apple silicon has one) and its queue, and the images both APIs see: IOSurfaces, a Metal texture
// and an OpenGL rectangle texture each (CGLTexImageIOSurface2D).

using System;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics.Metal
{
	internal sealed unsafe class MetalDevice : IDisposable
	{
		internal IntPtr Device { get; private set; }
		internal IntPtr Queue { get; private set; }
		internal string Description { get; private set; }

		private static readonly IntPtr selName = Mtl.Sel("name"), selNewCommandQueue = Mtl.Sel("newCommandQueue"),
			selTexture2D = Mtl.Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"), selSetUsage = Mtl.Sel("setUsage:"),
			selSetStorageMode = Mtl.Sel("setStorageMode:"), selNewTextureIOSurface = Mtl.Sel("newTextureWithDescriptor:iosurface:plane:"),
			selCommandBuffer = Mtl.Sel("commandBuffer"), selRenderPassDescriptor = Mtl.Sel("renderPassDescriptor"),
			selColorAttachments = Mtl.Sel("colorAttachments"), selObjectAtIndex = Mtl.Sel("objectAtIndexedSubscript:"),
			selSetTexture = Mtl.Sel("setTexture:"), selSetLoadAction = Mtl.Sel("setLoadAction:"), selSetStoreAction = Mtl.Sel("setStoreAction:"),
			selSetClearColor = Mtl.Sel("setClearColor:"), selRenderEncoder = Mtl.Sel("renderCommandEncoderWithDescriptor:"),
			selEndEncoding = Mtl.Sel("endEncoding"), selCommit = Mtl.Sel("commit"), selWaitUntilCompleted = Mtl.Sel("waitUntilCompleted"),
			selStatus = Mtl.Sel("status"), selError = Mtl.Sel("error");

		private MetalDevice()
		{
		}

		/// <summary>The device, or null with the reason.</summary>
		internal static MetalDevice Create(out string reason)
		{
			MetalDevice device = new MetalDevice();
			try
			{
				reason = device.Initialize();
			}
			catch (DllNotFoundException)
			{
				reason = "no Metal (Metal.framework)";
			}
			catch (EntryPointNotFoundException ex)
			{
				reason = "a framework lacks " + ex.Message;
			}
			catch (Exception ex)
			{
				reason = ex.Message;
			}

			if (reason != null)
			{
				device.Dispose();
				return null;
			}

			return device;
		}

		private string Initialize()
		{
			if (Program.CurrentHost.Platform != OpenBveApi.Hosts.HostPlatform.AppleOSX)
			{
				return "Metal is macOS's only";
			}

			Mtl.LoadFrameworks();
			if (Mtl.CGLGetCurrentContext() == IntPtr.Zero)
			{
				return "OpenGL's context is not a CGL one";
			}

			Device = Mtl.MTLCreateSystemDefaultDevice();
			if (Device == IntPtr.Zero)
			{
				return "no Metal device";
			}

			Description = Mtl.Text(Mtl.Id(Device, selName));
			Queue = Mtl.Id(Device, selNewCommandQueue);
			return null;
		}

		/// <summary>An image both APIs see: its IOSurface, Metal texture (a render target, read too) and OpenGL rectangle texture.</summary>
		internal sealed class SharedImage : IDisposable
		{
			internal IntPtr Surface, Texture;
			internal int GLTexture;

			public void Dispose()
			{
				if (GLTexture != 0)
				{
					GL.DeleteTexture(GLTexture);
					GLTexture = 0;
				}

				Mtl.Release(Texture);
				if (Surface != IntPtr.Zero)
				{
					Mtl.CFRelease(Surface);
				}

				Texture = Surface = IntPtr.Zero;
			}
		}

		/// <summary>A shared image, BGRA (colour) or one 32-bit float (the depth as a colour).</summary>
		internal SharedImage CreateSharedImage(uint width, uint height, bool floatDepth)
		{
			SharedImage image = new SharedImage();
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				IntPtr properties = Mtl.Dictionary(new[] { "IOSurfaceWidth", "IOSurfaceHeight", "IOSurfaceBytesPerElement", "IOSurfacePixelFormat" },
					new[] { (int)width, (int)height, 4, floatDepth ? Mtl.SurfaceR32Float : Mtl.SurfaceBGRA });
				image.Surface = Mtl.IOSurfaceCreate(properties);
				if (image.Surface == IntPtr.Zero)
				{
					throw new InvalidOperationException("IOSurfaceCreate failed (" + width + "x" + height + ")");
				}

				IntPtr desc = Mtl.Id(Mtl.Class("MTLTextureDescriptor"), selTexture2D, floatDepth ? Mtl.PixelR32Float : Mtl.PixelBGRA8Unorm, width, height, (byte)0);
				Mtl.Void(desc, selSetUsage, Mtl.UsageRenderTarget | Mtl.UsageShaderRead);
				Mtl.Void(desc, selSetStorageMode, Mtl.StorageShared);
				image.Texture = Mtl.Id(Device, selNewTextureIOSurface, desc, image.Surface, 0UL);
				if (image.Texture == IntPtr.Zero)
				{
					throw new InvalidOperationException("Metal could not make a texture of an IOSurface");
				}

				image.GLTexture = GL.GenTexture();
				GL.BindTexture((TextureTarget)Mtl.GLTextureRectangle, image.GLTexture);
				int error = floatDepth
					? Mtl.CGLTexImageIOSurface2D(Mtl.CGLGetCurrentContext(), Mtl.GLTextureRectangle, Mtl.GLR32F, (int)width, (int)height, Mtl.GLRed, Mtl.GLFloat, image.Surface, 0)
					: Mtl.CGLTexImageIOSurface2D(Mtl.CGLGetCurrentContext(), Mtl.GLTextureRectangle, Mtl.GLRgba8, (int)width, (int)height, Mtl.GLBgra, Mtl.GLUnsignedInt8888Rev, image.Surface, 0);
				GL.TexParameter((TextureTarget)Mtl.GLTextureRectangle, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
				GL.TexParameter((TextureTarget)Mtl.GLTextureRectangle, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
				GL.BindTexture((TextureTarget)Mtl.GLTextureRectangle, 0);
				if (error != 0)
				{
					throw new InvalidOperationException("CGLTexImageIOSurface2D failed (" + error + ") for the " + (floatDepth ? "R32F" : "BGRA") + " image");
				}

				return image;
			}
			catch
			{
				image.Dispose();
				throw;
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
			}
		}

		/// <summary>Waits for a committed command buffer; its error, or null.</summary>
		internal static string Wait(IntPtr commandBuffer)
		{
			Mtl.Void(commandBuffer, selWaitUntilCompleted);
			return Mtl.Number(commandBuffer, selStatus) == Mtl.CommandBufferError ? Mtl.ErrorText(Mtl.Id(commandBuffer, selError)) : null;
		}

		/// <summary>
		/// Checks the whole path once: Metal clears a shared colour and depth image, OpenGL reads them back. Null if the
		/// values arrived, else what went wrong.
		/// </summary>
		internal string SelfTest()
		{
			const uint size = 16;
			SharedImage color = null, depth = null;
			IntPtr pool = Mtl.objc_autoreleasePoolPush();
			try
			{
				color = CreateSharedImage(size, size, false);
				depth = CreateSharedImage(size, size, true);
				IntPtr pass = Mtl.Id(Mtl.Class("MTLRenderPassDescriptor"), selRenderPassDescriptor);
				IntPtr attachments = Mtl.Id(pass, selColorAttachments);
				SharedImage[] images = { color, depth };
				Mtl.ClearColor[] clears = { new Mtl.ClearColor { R = 0.25, G = 0.5, B = 0.75, A = 1.0 }, new Mtl.ClearColor { R = 1.0, G = 1.0, B = 1.0, A = 1.0 } };
				for (int i = 0; i < 2; i++)
				{
					IntPtr attachment = Mtl.Id(attachments, selObjectAtIndex, (ulong)i);
					Mtl.Void(attachment, selSetTexture, images[i].Texture);
					Mtl.Void(attachment, selSetLoadAction, Mtl.LoadClear);
					Mtl.Void(attachment, selSetStoreAction, Mtl.StoreStore);
					Mtl.Void(attachment, selSetClearColor, clears[i]);
				}

				IntPtr commands = Mtl.Id(Queue, selCommandBuffer);
				Mtl.Void(Mtl.Id(commands, selRenderEncoder, pass), selEndEncoding);
				Mtl.Void(commands, selCommit);
				string failed = Wait(commands);
				if (failed != null)
				{
					return "Metal's clear failed: " + failed;
				}

				byte[] pixels = new byte[size * size * 4];
				float[] depths = new float[size * size];
				GL.BindTexture((TextureTarget)Mtl.GLTextureRectangle, color.GLTexture);
				GL.GetTexImage((TextureTarget)Mtl.GLTextureRectangle, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.BindTexture((TextureTarget)Mtl.GLTextureRectangle, depth.GLTexture);
				GL.GetTexImage((TextureTarget)Mtl.GLTextureRectangle, 0, PixelFormat.Red, PixelType.Float, depths);
				GL.BindTexture((TextureTarget)Mtl.GLTextureRectangle, 0);
				ErrorCode error = GL.GetError();
				bool right = Math.Abs(pixels[0] - 64) <= 1 && Math.Abs(pixels[1] - 128) <= 1 && Math.Abs(pixels[2] - 191) <= 1 && pixels[3] == 255 && Math.Abs(depths[0] - 1.0f) < 1e-6f;
				return right && error == ErrorCode.NoError ? null : "Metal's clears read back as " + pixels[0] + ", " + pixels[1] + ", " + pixels[2] + ", " + pixels[3] + " and " + depths[0] + " (OpenGL error " + error + ")";
			}
			catch (Exception ex)
			{
				return ex.Message;
			}
			finally
			{
				Mtl.objc_autoreleasePoolPop(pool);
				color?.Dispose();
				depth?.Dispose();
			}
		}

		public void Dispose()
		{
			Mtl.Release(Queue);
			Mtl.Release(Device);
			Queue = Device = IntPtr.Zero;
		}
	}
}
