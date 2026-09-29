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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// ReSharper disable once CheckNamespace
namespace OpenTK.Graphics.OpenGL
{
	/// <summary>
	/// The subset of the OpenTK 3 GL API that OpenBVE uses, bound to OpenGL ES 3 on Android.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Android exports the core ES 3 entry points directly from libGLESv3.so, so these are plain
	/// P/Invokes rather than function pointers fetched through eglGetProcAddress.
	/// </para>
	/// <para>
	/// Calls with no ES equivalent — immediate mode, the matrix stack, polygon mode — throw
	/// <see cref="NotSupportedException"/>. Upstream only reaches them when shader creation has
	/// failed, which cannot happen on a device that got this far.
	/// </para>
	/// </remarks>
	public static class GL
	{
		private const string Library = "libGLESv3.so";

		private const int GL_INFO_LOG_LENGTH = 0x8B84;

		// --- native entry points ---

		[DllImport(Library, EntryPoint = "glEnable")] private static extern void glEnable(int cap);
		[DllImport(Library, EntryPoint = "glDisable")] private static extern void glDisable(int cap);
		[DllImport(Library, EntryPoint = "glClear")] private static extern void glClear(int mask);
		[DllImport(Library, EntryPoint = "glClearColor")] private static extern void glClearColor(float r, float g, float b, float a);
		[DllImport(Library, EntryPoint = "glViewport")] private static extern void glViewport(int x, int y, int width, int height);
		[DllImport(Library, EntryPoint = "glBlendFunc")] private static extern void glBlendFunc(int src, int dst);
		[DllImport(Library, EntryPoint = "glDepthMask")] private static extern void glDepthMask(byte flag);
		[DllImport(Library, EntryPoint = "glDepthFunc")] private static extern void glDepthFunc(int func);
		[DllImport(Library, EntryPoint = "glCullFace")] private static extern void glCullFace(int mode);
		[DllImport(Library, EntryPoint = "glLineWidth")] private static extern void glLineWidth(float width);
		[DllImport(Library, EntryPoint = "glHint")] private static extern void glHint(int target, int mode);
		[DllImport(Library, EntryPoint = "glPixelStorei")] private static extern void glPixelStorei(int pname, int param);
		[DllImport(Library, EntryPoint = "glGetError")] private static extern int glGetError();
		[DllImport(Library, EntryPoint = "glGetString")] private static extern IntPtr glGetString(int name);
		[DllImport(Library, EntryPoint = "glGetFloatv")] private static extern void glGetFloatv(int pname, float[] parameters);
		[DllImport(Library, EntryPoint = "glGetIntegerv")] private static extern void glGetIntegerv(int pname, int[] parameters);
		[DllImport(Library, EntryPoint = "glReadPixels")] private static extern void glReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels);

		[DllImport(Library, EntryPoint = "glGenTextures")] private static extern void glGenTextures(int n, int[] textures);
		[DllImport(Library, EntryPoint = "glDeleteTextures")] private static extern void glDeleteTextures(int n, int[] textures);
		[DllImport(Library, EntryPoint = "glBindTexture")] private static extern void glBindTexture(int target, int texture);
		[DllImport(Library, EntryPoint = "glActiveTexture")] private static extern void glActiveTexture(int texture);
		[DllImport(Library, EntryPoint = "glGenerateMipmap")] private static extern void glGenerateMipmap(int target);
		[DllImport(Library, EntryPoint = "glTexParameteri")] private static extern void glTexParameteri(int target, int pname, int param);
		[DllImport(Library, EntryPoint = "glTexParameterf")] private static extern void glTexParameterf(int target, int pname, float param);
		[DllImport(Library, EntryPoint = "glTexParameteriv")] private static extern void glTexParameteriv(int target, int pname, int[] parameters);
		[DllImport(Library, EntryPoint = "glTexParameterfv")] private static extern void glTexParameterfv(int target, int pname, float[] parameters);
		[DllImport(Library, EntryPoint = "glTexImage2D")] private static extern void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels);
		[DllImport(Library, EntryPoint = "glCopyTexImage2D")] private static extern void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border);

		[DllImport(Library, EntryPoint = "glGenBuffers")] private static extern void glGenBuffers(int n, int[] buffers);
		[DllImport(Library, EntryPoint = "glDeleteBuffers")] private static extern void glDeleteBuffers(int n, int[] buffers);
		[DllImport(Library, EntryPoint = "glBindBuffer")] private static extern void glBindBuffer(int target, int buffer);
		[DllImport(Library, EntryPoint = "glBindBufferBase")] private static extern void glBindBufferBase(int target, int index, int buffer);
		[DllImport(Library, EntryPoint = "glBufferData")] private static extern void glBufferData(int target, IntPtr size, IntPtr data, int usage);
		[DllImport(Library, EntryPoint = "glBufferSubData")] private static extern void glBufferSubData(int target, IntPtr offset, IntPtr size, IntPtr data);

		[DllImport(Library, EntryPoint = "glGenVertexArrays")] private static extern void glGenVertexArrays(int n, int[] arrays);
		[DllImport(Library, EntryPoint = "glDeleteVertexArrays")] private static extern void glDeleteVertexArrays(int n, int[] arrays);
		[DllImport(Library, EntryPoint = "glBindVertexArray")] private static extern void glBindVertexArray(int array);
		[DllImport(Library, EntryPoint = "glEnableVertexAttribArray")] private static extern void glEnableVertexAttribArray(int index);
		[DllImport(Library, EntryPoint = "glDisableVertexAttribArray")] private static extern void glDisableVertexAttribArray(int index);
		[DllImport(Library, EntryPoint = "glVertexAttribPointer")] private static extern void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer);
		[DllImport(Library, EntryPoint = "glVertexAttribIPointer")] private static extern void glVertexAttribIPointer(int index, int size, int type, int stride, IntPtr pointer);
		[DllImport(Library, EntryPoint = "glDrawArrays")] private static extern void glDrawArrays(int mode, int first, int count);
		[DllImport(Library, EntryPoint = "glDrawElements")] private static extern void glDrawElements(int mode, int count, int type, IntPtr indices);

		[DllImport(Library, EntryPoint = "glCreateShader")] private static extern int glCreateShader(int type);
		[DllImport(Library, EntryPoint = "glDeleteShader")] private static extern void glDeleteShader(int shader);
		[DllImport(Library, EntryPoint = "glShaderSource")] private static extern void glShaderSource(int shader, int count, IntPtr[] str, int[] length);
		[DllImport(Library, EntryPoint = "glCompileShader")] private static extern void glCompileShader(int shader);
		[DllImport(Library, EntryPoint = "glGetShaderiv")] private static extern void glGetShaderiv(int shader, int pname, int[] parameters);
		[DllImport(Library, EntryPoint = "glGetShaderInfoLog")] private static extern void glGetShaderInfoLog(int shader, int bufferSize, int[] length, StringBuilder infoLog);

		[DllImport(Library, EntryPoint = "glCreateProgram")] private static extern int glCreateProgram();
		[DllImport(Library, EntryPoint = "glDeleteProgram")] private static extern void glDeleteProgram(int program);
		[DllImport(Library, EntryPoint = "glAttachShader")] private static extern void glAttachShader(int program, int shader);
		[DllImport(Library, EntryPoint = "glLinkProgram")] private static extern void glLinkProgram(int program);
		[DllImport(Library, EntryPoint = "glUseProgram")] private static extern void glUseProgram(int program);
		[DllImport(Library, EntryPoint = "glGetProgramiv")] private static extern void glGetProgramiv(int program, int pname, int[] parameters);
		[DllImport(Library, EntryPoint = "glGetProgramInfoLog")] private static extern void glGetProgramInfoLog(int program, int bufferSize, int[] length, StringBuilder infoLog);
		[DllImport(Library, EntryPoint = "glGetAttribLocation")] private static extern int glGetAttribLocation(int program, string name);
		[DllImport(Library, EntryPoint = "glGetUniformLocation")] private static extern int glGetUniformLocation(int program, string name);
		[DllImport(Library, EntryPoint = "glGetUniformBlockIndex")] private static extern int glGetUniformBlockIndex(int program, string name);
		[DllImport(Library, EntryPoint = "glUniformBlockBinding")] private static extern void glUniformBlockBinding(int program, int blockIndex, int binding);

		[DllImport(Library, EntryPoint = "glUniform1i")] private static extern void glUniform1i(int location, int v0);
		[DllImport(Library, EntryPoint = "glUniform1f")] private static extern void glUniform1f(int location, float v0);
		[DllImport(Library, EntryPoint = "glUniform2f")] private static extern void glUniform2f(int location, float v0, float v1);
		[DllImport(Library, EntryPoint = "glUniform3f")] private static extern void glUniform3f(int location, float v0, float v1, float v2);
		[DllImport(Library, EntryPoint = "glUniform4f")] private static extern void glUniform4f(int location, float v0, float v1, float v2, float v3);
		[DllImport(Library, EntryPoint = "glUniform1fv")] private static extern void glUniform1fv(int location, int count, float[] value);
		[DllImport(Library, EntryPoint = "glUniformMatrix4fv")] private static extern void glUniformMatrix4fv(int location, int count, byte transpose, float[] value);

		[DllImport(Library, EntryPoint = "glGenFramebuffers")] private static extern void glGenFramebuffers(int n, int[] framebuffers);
		[DllImport(Library, EntryPoint = "glDeleteFramebuffers")] private static extern void glDeleteFramebuffers(int n, int[] framebuffers);
		[DllImport(Library, EntryPoint = "glBindFramebuffer")] private static extern void glBindFramebuffer(int target, int framebuffer);
		[DllImport(Library, EntryPoint = "glFramebufferTexture2D")] private static extern void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level);
		[DllImport(Library, EntryPoint = "glFramebufferRenderbuffer")] private static extern void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer);
		[DllImport(Library, EntryPoint = "glCheckFramebufferStatus")] private static extern int glCheckFramebufferStatus(int target);
		[DllImport(Library, EntryPoint = "glGenRenderbuffers")] private static extern void glGenRenderbuffers(int n, int[] renderbuffers);
		[DllImport(Library, EntryPoint = "glDeleteRenderbuffers")] private static extern void glDeleteRenderbuffers(int n, int[] renderbuffers);
		[DllImport(Library, EntryPoint = "glBindRenderbuffer")] private static extern void glBindRenderbuffer(int target, int renderbuffer);
		[DllImport(Library, EntryPoint = "glRenderbufferStorage")] private static extern void glRenderbufferStorage(int target, int internalFormat, int width, int height);
		[DllImport(Library, EntryPoint = "glDrawBuffers")] private static extern void glDrawBuffers(int n, int[] buffers);
		[DllImport(Library, EntryPoint = "glReadBuffer")] private static extern void glReadBuffer(int mode);

		// --- state ---

		/// <summary>Capabilities that do not exist in GLES and are silently ignored.</summary>
		private static bool IsDesktopOnly(EnableCap cap)
		{
			switch (cap)
			{
				case EnableCap.Lighting:
				case EnableCap.Fog:
				case EnableCap.Texture2D:
				case EnableCap.DepthClamp:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Enables a capability.</summary>
		public static void Enable(EnableCap cap)
		{
			if (!IsDesktopOnly(cap) && CapabilityChanges(cap, true))
			{
				glEnable((int)cap);
			}
		}

		/// <summary>Disables a capability.</summary>
		public static void Disable(EnableCap cap)
		{
			if (!IsDesktopOnly(cap) && CapabilityChanges(cap, false))
			{
				glDisable((int)cap);
			}
		}

		/// <summary>
		/// Reads RGBA bytes back from the framebuffer. Diagnostic only (a full pipeline stall);
		/// not part of OpenTK's signature set, so named for what it is.
		/// </summary>
		public static void ReadPixelsRgba(int x, int y, int width, int height, byte[] pixels)
		{
			const int GL_RGBA = 0x1908, GL_UNSIGNED_BYTE = 0x1401;
			glReadPixels(x, y, width, height, GL_RGBA, GL_UNSIGNED_BYTE, pixels);
		}

		/// <summary>Clears the given buffers.</summary>
		public static void Clear(ClearBufferMask mask)
		{
			glClear((int)mask);
		}

		/// <summary>Sets the colour the colour buffer is cleared to.</summary>
		public static void ClearColor(float red, float green, float blue, float alpha)
		{
			glClearColor(red, green, blue, alpha);
		}

		/// <summary>Sets the viewport.</summary>
		public static void Viewport(int x, int y, int width, int height)
		{
			glViewport(x, y, width, height);
		}

		/// <summary>Sets the blend function.</summary>
		public static void BlendFunc(BlendingFactor source, BlendingFactor destination)
		{
			if (blendSource == (int)source && blendDestination == (int)destination)
			{
				return;
			}

			blendSource = (int)source;
			blendDestination = (int)destination;
			glBlendFunc((int)source, (int)destination);
		}

		/// <summary>Enables or disables writes to the depth buffer.</summary>
		public static void DepthMask(bool flag)
		{
			int value = flag ? 1 : 0;
			if (depthMask == value)
			{
				return;
			}

			depthMask = value;
			glDepthMask((byte)value);
		}

		/// <summary>Sets the depth comparison function.</summary>
		public static void DepthFunc(DepthFunction func)
		{
			if (depthFunc == (int)func)
			{
				return;
			}

			depthFunc = (int)func;
			glDepthFunc((int)func);
		}

		/// <summary>Sets which faces are culled.</summary>
		public static void CullFace(CullFaceMode mode)
		{
			if (cullFace == (int)mode)
			{
				return;
			}

			cullFace = (int)mode;
			glCullFace((int)mode);
		}

		/// <summary>Sets the width of rasterised lines.</summary>
		public static void LineWidth(float width)
		{
			glLineWidth(width);
		}

		/// <summary>Sets an implementation hint. Only the hints GLES recognises are forwarded.</summary>
		public static void Hint(HintTarget target, HintMode mode)
		{
			if (target == HintTarget.GenerateMipmapHint || target == HintTarget.FragmentShaderDerivativeHint)
			{
				glHint((int)target, (int)mode);
			}
		}

		/// <summary>Sets a pixel storage parameter.</summary>
		public static void PixelStore(PixelStoreParameter pname, int param)
		{
			glPixelStorei((int)pname, param);
		}

		/// <summary>Returns and clears the most recent error.</summary>
		public static ErrorCode GetError()
		{
			return (ErrorCode)glGetError();
		}

		/// <summary>Returns a driver string.</summary>
		public static string GetString(StringName name)
		{
			IntPtr pointer = glGetString((int)name);
			return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(pointer);
		}

		/// <summary>Queries a floating point state value.</summary>
		public static float GetFloat(GetPName pname)
		{
			float[] values = new float[4];
			glGetFloatv((int)pname, values);
			return values[0];
		}

		/// <summary>Queries an integer state value.</summary>
		public static int GetInteger(GetPName pname)
		{
			int[] values = new int[4];
			glGetIntegerv((int)pname, values);
			return values[0];
		}

		// --- textures ---

		/// <summary>Generates a texture name.</summary>
		public static int GenTexture()
		{
			int[] textures = new int[1];
			glGenTextures(1, textures);
			return textures[0];
		}

		/// <summary>Generates texture names.</summary>
		public static void GenTextures(int n, out int textures)
		{
			int[] names = new int[n < 1 ? 1 : n];
			glGenTextures(n, names);
			textures = names[0];
		}

		/// <summary>Generates texture names.</summary>
		public static void GenTextures(int n, int[] textures)
		{
			glGenTextures(n, textures);
		}

		/// <summary>Deletes a texture.</summary>
		public static void DeleteTexture(int texture)
		{
			glDeleteTextures(1, new[] { texture });
		}

		/// <summary>Deletes textures.</summary>
		public static void DeleteTextures(int n, ref int textures)
		{
			glDeleteTextures(n, new[] { textures });
		}

		/// <summary>Deletes textures.</summary>
		public static void DeleteTextures(int n, int[] textures)
		{
			glDeleteTextures(n, textures);
		}

		/// <summary>Binds a texture to a target.</summary>
		public static void BindTexture(TextureTarget target, int texture)
		{
			glBindTexture((int)target, texture);
		}

		/// <summary>Selects the active texture unit.</summary>
		public static void ActiveTexture(TextureUnit texture)
		{
			glActiveTexture((int)texture);
		}

		/// <summary>Generates mipmaps for the bound texture.</summary>
		public static void GenerateMipmap(GenerateMipmapTarget target)
		{
			glGenerateMipmap((int)target);
		}

		/// <summary>Sets an integer texture parameter.</summary>
		public static void TexParameter(TextureTarget target, TextureParameterName pname, int param)
		{
			if (IsUnsupportedTextureParameter(pname))
			{
				return;
			}

			glTexParameteri((int)target, (int)pname, param);
		}

		/// <summary>Sets a floating point texture parameter.</summary>
		public static void TexParameter(TextureTarget target, TextureParameterName pname, float param)
		{
			if (IsUnsupportedTextureParameter(pname))
			{
				return;
			}

			glTexParameterf((int)target, (int)pname, param);
		}

		/// <summary>Sets a texture parameter from an array.</summary>
		public static void TexParameter(TextureTarget target, TextureParameterName pname, int[] parameters)
		{
			if (IsUnsupportedTextureParameter(pname))
			{
				return;
			}

			glTexParameteriv((int)target, (int)pname, parameters);
		}

		/// <summary>Sets a texture parameter from an array.</summary>
		public static void TexParameter(TextureTarget target, TextureParameterName pname, float[] parameters)
		{
			if (IsUnsupportedTextureParameter(pname))
			{
				return;
			}

			glTexParameterfv((int)target, (int)pname, parameters);
		}

		/// <summary>
		/// GLES has no border colour and no combined RGBA swizzle. Setting them would raise
		/// GL_INVALID_ENUM, so they are dropped; the visible effect is that ClampToBorder
		/// behaves as ClampToEdge.
		/// </summary>
		private static bool IsUnsupportedTextureParameter(TextureParameterName pname)
		{
			return pname == TextureParameterName.TextureBorderColor || pname == TextureParameterName.TextureSwizzleRgba;
		}

		/// <summary>Uploads a texture image.</summary>
		public static void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, IntPtr pixels)
		{
			glTexImage2D((int)target, level, (int)internalFormat, width, height, border, (int)format, (int)TranslateDepthType(internalFormat, type, pixels), pixels);
		}

		private const int GL_UNSIGNED_SHORT = 0x1403;
		private const int GL_UNSIGNED_INT = 0x1405;

		/// <summary>
		/// Desktop GL accepts any transfer type for a depth texture, and OpenBVE allocates its
		/// depth textures with FLOAT. GLES 3 ties the type to the sized format — DEPTH_COMPONENT16
		/// takes UNSIGNED_SHORT or UNSIGNED_INT, DEPTH_COMPONENT24 takes UNSIGNED_INT — and raises
		/// GL_INVALID_OPERATION otherwise. When no data is being uploaded the type only describes
		/// a transfer that never happens, so it can be corrected freely.
		/// </summary>
		private static int TranslateDepthType(PixelInternalFormat internalFormat, PixelType type, IntPtr pixels)
		{
			if (pixels != IntPtr.Zero || type != PixelType.Float)
			{
				return (int)type;
			}

			switch (internalFormat)
			{
				case PixelInternalFormat.DepthComponent16:
					return GL_UNSIGNED_SHORT;
				case PixelInternalFormat.DepthComponent24:
					return GL_UNSIGNED_INT;
				default:
					return (int)type;
			}
		}

		/// <summary>Uploads a texture image.</summary>
		public static void TexImage2D<T>(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			if (pixels == null)
			{
				TexImage2D(target, level, internalFormat, width, height, border, format, type, IntPtr.Zero);
				return;
			}

			GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
			try
			{
				TexImage2D(target, level, internalFormat, width, height, border, format, type, handle.AddrOfPinnedObject());
			}
			finally
			{
				handle.Free();
			}
		}

		/// <summary>Copies the framebuffer into the bound texture.</summary>
		public static void CopyTexImage2D(TextureTarget target, int level, InternalFormat internalFormat, int x, int y, int width, int height, int border)
		{
			glCopyTexImage2D((int)target, level, (int)internalFormat, x, y, width, height, border);
		}

		// --- buffers ---

		/// <summary>Generates a buffer name.</summary>
		public static int GenBuffer()
		{
			int[] buffers = new int[1];
			glGenBuffers(1, buffers);
			return buffers[0];
		}

		/// <summary>Generates buffer names.</summary>
		public static void GenBuffers(int n, out int buffers)
		{
			int[] names = new int[n < 1 ? 1 : n];
			glGenBuffers(n, names);
			buffers = names[0];
		}

		/// <summary>Generates buffer names.</summary>
		public static void GenBuffers(int n, int[] buffers)
		{
			glGenBuffers(n, buffers);
		}

		/// <summary>Deletes a buffer.</summary>
		public static void DeleteBuffer(int buffer)
		{
			glDeleteBuffers(1, new[] { buffer });
		}

		/// <summary>Binds a buffer to a target.</summary>
		public static void BindBuffer(BufferTarget target, int buffer)
		{
			glBindBuffer((int)target, buffer);
		}

		/// <summary>Binds a buffer to an indexed target.</summary>
		public static void BindBufferBase(BufferTarget target, int index, int buffer)
		{
			glBindBufferBase((int)target, index, buffer);
		}

		/// <summary>Binds a buffer to an indexed target.</summary>
		public static void BindBufferBase(BufferRangeTarget target, int index, int buffer)
		{
			glBindBufferBase((int)target, index, buffer);
		}

		/// <summary>Creates and fills a buffer's data store.</summary>
		public static void BufferData<T>(BufferTarget target, int size, T[] data, BufferUsageHint usage) where T : struct
		{
			BufferData(target, new IntPtr(size), data, usage);
		}

		/// <summary>Creates and fills a buffer's data store.</summary>
		public static void BufferData<T>(BufferTarget target, IntPtr size, T[] data, BufferUsageHint usage) where T : struct
		{
			if (data == null)
			{
				glBufferData((int)target, size, IntPtr.Zero, (int)usage);
				return;
			}

			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				glBufferData((int)target, size, handle.AddrOfPinnedObject(), (int)usage);
			}
			finally
			{
				handle.Free();
			}
		}

		/// <summary>Creates and fills a buffer's data store.</summary>
		public static void BufferData(BufferTarget target, IntPtr size, IntPtr data, BufferUsageHint usage)
		{
			glBufferData((int)target, size, data, (int)usage);
		}

		/// <summary>Updates part of a buffer's data store.</summary>
		public static void BufferSubData<T>(BufferTarget target, IntPtr offset, int size, T[] data) where T : struct
		{
			GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
			try
			{
				glBufferSubData((int)target, offset, new IntPtr(size), handle.AddrOfPinnedObject());
			}
			finally
			{
				handle.Free();
			}
		}

		/// <summary>Updates part of a buffer's data store.</summary>
		public static void BufferSubData<T>(BufferTarget target, IntPtr offset, IntPtr size, T[] data) where T : struct
		{
			BufferSubData(target, offset, (int)size, data);
		}

		// --- vertex arrays ---

		/// <summary>Generates a vertex array name.</summary>
		public static int GenVertexArray()
		{
			int[] arrays = new int[1];
			glGenVertexArrays(1, arrays);
			return arrays[0];
		}

		/// <summary>Generates vertex array names.</summary>
		public static void GenVertexArrays(int n, out int arrays)
		{
			int[] names = new int[n < 1 ? 1 : n];
			glGenVertexArrays(n, names);
			arrays = names[0];
		}

		/// <summary>Generates vertex array names.</summary>
		public static void GenVertexArrays(int n, int[] arrays)
		{
			glGenVertexArrays(n, arrays);
		}

		/// <summary>Deletes a vertex array.</summary>
		public static void DeleteVertexArray(int array)
		{
			glDeleteVertexArrays(1, new[] { array });
		}

		/// <summary>Binds a vertex array.</summary>
		public static void BindVertexArray(int array)
		{
			glBindVertexArray(array);
		}

		/// <summary>Enables a vertex attribute array.</summary>
		public static void EnableVertexAttribArray(int index)
		{
			glEnableVertexAttribArray(index);
		}

		/// <summary>Disables a vertex attribute array.</summary>
		public static void DisableVertexAttribArray(int index)
		{
			glDisableVertexAttribArray(index);
		}

		/// <summary>Describes a vertex attribute array.</summary>
		public static void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, int offset)
		{
			glVertexAttribPointer(index, size, (int)type, normalized ? (byte)1 : (byte)0, stride, new IntPtr(offset));
		}

		/// <summary>Describes a vertex attribute array.</summary>
		public static void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, IntPtr offset)
		{
			glVertexAttribPointer(index, size, (int)type, normalized ? (byte)1 : (byte)0, stride, offset);
		}

		/// <summary>Describes an integer vertex attribute array.</summary>
		public static void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr offset)
		{
			glVertexAttribIPointer(index, size, (int)type, stride, offset);
		}

		/*
		 * GLES has no GL_QUADS, GL_QUAD_STRIP or GL_POLYGON, and OpenBVE's meshes are full of all
		 * three: a face whose type is not set at all is drawn as a polygon, and the object
		 * optimiser only converts some of them to triangles. They are drawn here as triangles
		 * instead, which is exact rather than approximate for the geometry OpenBVE produces:
		 *
		 *   - GL_POLYGON is a triangle fan over the same vertices, in the same order. Both require
		 *     a convex, planar face, which is what the parsers emit.
		 *   - GL_QUAD_STRIP is a triangle strip over the same vertices: quad k of a quad strip is
		 *     vertices 2k, 2k+1, 2k+3, 2k+2, which is exactly the pair of triangles a strip draws.
		 *   - GL_QUADS is one fan per group of four vertices. A face carrying more than one quad
		 *     is drawn as several calls rather than by rewriting its indices, since the indices
		 *     live in a buffer on the GPU.
		 *
		 * Winding, and so back-face culling, is preserved in each case.
		 */

		/// <summary>Draws primitives from array data.</summary>
		public static void DrawArrays(PrimitiveType mode, int first, int count)
		{
			if (mode == PrimitiveType.Quads)
			{
				for (int quad = 0; quad + 4 <= count; quad += 4)
				{
					glDrawArrays((int)PrimitiveType.TriangleFan, first + quad, 4);
				}

				return;
			}

			glDrawArrays((int)Translate(mode), first, count);
		}

		/// <summary>Draws indexed primitives.</summary>
		public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, int offset)
		{
			DrawElements(mode, count, type, new IntPtr(offset));
		}

		/// <summary>Draws indexed primitives.</summary>
		public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, IntPtr offset)
		{
			if (mode == PrimitiveType.Quads)
			{
				long stride = 4L * IndexSize(type);
				for (int quad = 0; quad + 4 <= count; quad += 4)
				{
					glDrawElements((int)PrimitiveType.TriangleFan, 4, (int)type, new IntPtr(offset.ToInt64() + quad / 4 * stride));
				}

				return;
			}

			glDrawElements((int)Translate(mode), count, (int)type, offset);
		}

		/// <summary>The primitive GLES draws in place of a desktop one.</summary>
		private static PrimitiveType Translate(PrimitiveType mode)
		{
			switch (mode)
			{
				case PrimitiveType.Polygon:
					return PrimitiveType.TriangleFan;
				case PrimitiveType.QuadStrip:
					return PrimitiveType.TriangleStrip;
				case PrimitiveType.Quads:
					// Handled by the callers above, which draw one fan per quad.
					return PrimitiveType.TriangleFan;
				default:
					return mode;
			}
		}

		/// <summary>The size in bytes of one index of the given type.</summary>
		private static int IndexSize(DrawElementsType type)
		{
			return type == DrawElementsType.UnsignedShort ? 2 : 4;
		}

		// --- shaders and programs ---

		/// <summary>Creates a shader object.</summary>
		public static int CreateShader(ShaderType type)
		{
			return glCreateShader((int)type);
		}

		/// <summary>Deletes a shader object.</summary>
		public static void DeleteShader(int shader)
		{
			glDeleteShader(shader);
		}

		/// <summary>
		/// Sets a shader's source, translating OpenBVE's desktop GLSL to GLSL ES 3.00 on the way
		/// through. See <see cref="OpenBve.GLES.ShaderTranslator"/>.
		/// </summary>
		public static void ShaderSource(int shader, string source)
		{
			string translated = OpenBve.GLES.ShaderTranslator.ToOpenGLES(source);
			IntPtr text = Marshal.StringToHGlobalAnsi(translated);
			try
			{
				glShaderSource(shader, 1, new[] { text }, new[] { translated.Length });
			}
			finally
			{
				Marshal.FreeHGlobal(text);
			}
		}

		/// <summary>Compiles a shader.</summary>
		public static void CompileShader(int shader)
		{
			glCompileShader(shader);
		}

		/// <summary>Queries a shader object.</summary>
		public static void GetShader(int shader, ShaderParameter pname, out int parameters)
		{
			int[] values = new int[1];
			glGetShaderiv(shader, (int)pname, values);
			parameters = values[0];
		}

		/// <summary>Returns a shader's info log.</summary>
		public static string GetShaderInfoLog(int shader)
		{
			int[] length = new int[1];
			glGetShaderiv(shader, GL_INFO_LOG_LENGTH, length);
			if (length[0] <= 0)
			{
				return string.Empty;
			}

			StringBuilder log = new StringBuilder(length[0] + 1);
			glGetShaderInfoLog(shader, log.Capacity, null, log);
			return log.ToString();
		}

		/// <summary>Returns a shader's info log.</summary>
		public static void GetShaderInfoLog(int shader, out string log)
		{
			log = GetShaderInfoLog(shader);
		}

		/// <summary>Creates a program object.</summary>
		public static int CreateProgram()
		{
			return glCreateProgram();
		}

		/// <summary>Deletes a program object.</summary>
		public static void DeleteProgram(int program)
		{
			uniformCache.Remove(program);
			glDeleteProgram(program);
		}

		/// <summary>Attaches a shader to a program.</summary>
		public static void AttachShader(int program, int shader)
		{
			glAttachShader(program, shader);
		}

		/// <summary>Links a program.</summary>
		public static void LinkProgram(int program)
		{
			// Linking resets every uniform to its default, and may move locations.
			uniformCache.Remove(program);
			glLinkProgram(program);
		}

		/// <summary>
		/// The program bound on the current context, tracked here so that ProgramUniform emulation
		/// never has to ask the driver. Every bind in OpenBVE goes through <see cref="UseProgram"/>.
		/// </summary>
		private static int currentProgram;

		/// <summary>
		/// Forgets cached driver state. Call whenever a new GL context is made current, since the
		/// cache describes the previous one.
		/// </summary>
		public static void ResetStateCache()
		{
			currentProgram = 0;
			capabilities.Clear();
			blendSource = blendDestination = depthMask = depthFunc = cullFace = -1;
			uniformCache.Clear();
		}

		/// <summary>Makes a program current.</summary>
		public static void UseProgram(int program)
		{
			currentProgram = program;
			glUseProgram(program);
		}

		/// <summary>Queries a program object.</summary>
		public static void GetProgram(int program, GetProgramParameterName pname, out int parameters)
		{
			int[] values = new int[1];
			glGetProgramiv(program, (int)pname, values);
			parameters = values[0];
		}

		/// <summary>Returns a program's info log.</summary>
		public static string GetProgramInfoLog(int program)
		{
			int[] length = new int[1];
			glGetProgramiv(program, GL_INFO_LOG_LENGTH, length);
			if (length[0] <= 0)
			{
				return string.Empty;
			}

			StringBuilder log = new StringBuilder(length[0] + 1);
			glGetProgramInfoLog(program, log.Capacity, null, log);
			return log.ToString();
		}

		/// <summary>Returns a program's info log.</summary>
		public static void GetProgramInfoLog(int program, out string log)
		{
			log = GetProgramInfoLog(program);
		}

		/// <summary>Returns the location of an attribute.</summary>
		public static int GetAttribLocation(int program, string name)
		{
			return glGetAttribLocation(program, name);
		}

		/// <summary>Returns the location of a uniform.</summary>
		public static int GetUniformLocation(int program, string name)
		{
			return glGetUniformLocation(program, name);
		}

		/// <summary>Returns the index of a uniform block.</summary>
		public static int GetUniformBlockIndex(int program, string name)
		{
			return glGetUniformBlockIndex(program, name);
		}

		/// <summary>Assigns a uniform block to a binding point.</summary>
		public static void UniformBlockBinding(int program, int blockIndex, int binding)
		{
			glUniformBlockBinding(program, blockIndex, binding);
		}

		/// <summary>
		/// Fragment output locations are declared in the shader source on GLES, so this is a no-op.
		/// The ported shaders use layout(location = ...) qualifiers instead.
		/// </summary>
		public static void BindFragDataLocation(int program, int colorNumber, string name)
		{
		}

		// --- uniforms ---

		/// <summary>Sets an integer uniform on the current program.</summary>
		public static void Uniform1(int location, int v0)
		{
			if (UniformChanges(currentProgram, location, 1, v0))
			{
				glUniform1i(location, v0);
			}
		}

		/// <summary>Sets a float uniform on the current program.</summary>
		public static void Uniform1(int location, float v0)
		{
			if (UniformChanges(currentProgram, location, 2, Bits(v0)))
			{
				glUniform1f(location, v0);
			}
		}

		/// <summary>Sets a float array uniform on the current program.</summary>
		public static void Uniform1(int location, int count, float[] value)
		{
			// Arrays are not cached; forget any cached elements they overwrite.
			ForgetUniforms(currentProgram, location, count);
			glUniform1fv(location, count, value);
		}

		/// <summary>Sets a two-component uniform on the current program.</summary>
		public static void Uniform2(int location, float v0, float v1)
		{
			if (UniformChanges(currentProgram, location, 3, Bits(v0), Bits(v1)))
			{
				glUniform2f(location, v0, v1);
			}
		}

		/// <summary>Sets a three-component uniform on the current program.</summary>
		public static void Uniform3(int location, float v0, float v1, float v2)
		{
			if (UniformChanges(currentProgram, location, 4, Bits(v0), Bits(v1), Bits(v2)))
			{
				glUniform3f(location, v0, v1, v2);
			}
		}

		/// <summary>Sets a four-component uniform on the current program.</summary>
		public static void Uniform4(int location, float v0, float v1, float v2, float v3)
		{
			if (UniformChanges(currentProgram, location, 5, Bits(v0), Bits(v1), Bits(v2), Bits(v3)))
			{
				glUniform4f(location, v0, v1, v2, v3);
			}
		}

		/// <summary>Sets a matrix uniform on the current program.</summary>
		public static void UniformMatrix4(int location, bool transpose, ref Matrix4 matrix)
		{
			float[] values = ToArray(matrix);
			if (MatrixChanges(currentProgram, location, transpose, values))
			{
				glUniformMatrix4fv(location, 1, transpose ? (byte)1 : (byte)0, values);
			}
		}

		/*
		 * glProgramUniform* is GLES 3.1. Emulating it on 3.0 keeps the minimum device
		 * requirement at ES 3.0 for the cost of two extra glUseProgram calls per set.
		 */

		/// <summary>Sets an integer uniform on the given program.</summary>
		public static void ProgramUniform1(int program, int location, int v0)
		{
			if (!UniformChanges(program, location, 1, v0))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniform1i(location, v0);
			EndProgram(previous, program);
		}

		/// <summary>Sets a float uniform on the given program.</summary>
		public static void ProgramUniform1(int program, int location, float v0)
		{
			if (!UniformChanges(program, location, 2, Bits(v0)))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniform1f(location, v0);
			EndProgram(previous, program);
		}

		/// <summary>Sets a two-component uniform on the given program.</summary>
		public static void ProgramUniform2(int program, int location, float v0, float v1)
		{
			if (!UniformChanges(program, location, 3, Bits(v0), Bits(v1)))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniform2f(location, v0, v1);
			EndProgram(previous, program);
		}

		/// <summary>Sets a three-component uniform on the given program.</summary>
		public static void ProgramUniform3(int program, int location, float v0, float v1, float v2)
		{
			if (!UniformChanges(program, location, 4, Bits(v0), Bits(v1), Bits(v2)))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniform3f(location, v0, v1, v2);
			EndProgram(previous, program);
		}

		/// <summary>Sets a four-component uniform on the given program.</summary>
		public static void ProgramUniform4(int program, int location, float v0, float v1, float v2, float v3)
		{
			if (!UniformChanges(program, location, 5, Bits(v0), Bits(v1), Bits(v2), Bits(v3)))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniform4f(location, v0, v1, v2, v3);
			EndProgram(previous, program);
		}

		/// <summary>Sets a matrix uniform on the given program.</summary>
		public static void ProgramUniformMatrix4(int program, int location, bool transpose, ref Matrix4 matrix)
		{
			float[] values = ToArray(matrix);
			if (!MatrixChanges(program, location, transpose, values))
			{
				return;
			}

			int previous = BeginProgram(program);
			glUniformMatrix4fv(location, 1, transpose ? (byte)1 : (byte)0, values);
			EndProgram(previous, program);
		}

		/*
		 * Binds the target program only if it is not already current, and reports what was bound.
		 * In practice OpenBVE activates a shader before setting its uniforms, so this costs no GL
		 * calls at all. It deliberately does not query GL_CURRENT_PROGRAM: a glGet is a synchronous
		 * round trip that stalls the pipeline, and with around fifteen uniforms set per face, the
		 * first version of this emulation dropped a modest scene to a few frames per second.
		 */
		private static int BeginProgram(int program)
		{
			int previous = currentProgram;
			if (previous != program)
			{
				glUseProgram(program);
			}

			return previous;
		}

		/// <summary>Restores the program that was bound before <see cref="BeginProgram"/>, if it changed it.</summary>
		private static void EndProgram(int previous, int program)
		{
			if (previous != program)
			{
				glUseProgram(previous);
			}
		}

		/// <summary>One reused buffer for matrix uploads: glUniformMatrix4fv copies it before returning.</summary>
		private static readonly float[] matrixBuffer = new float[16];

		private static float[] ToArray(Matrix4 matrix)
		{
			float[] m = matrixBuffer;
			m[0] = matrix.Row0.X; m[1] = matrix.Row0.Y; m[2] = matrix.Row0.Z; m[3] = matrix.Row0.W;
			m[4] = matrix.Row1.X; m[5] = matrix.Row1.Y; m[6] = matrix.Row1.Z; m[7] = matrix.Row1.W;
			m[8] = matrix.Row2.X; m[9] = matrix.Row2.Y; m[10] = matrix.Row2.Z; m[11] = matrix.Row2.W;
			m[12] = matrix.Row3.X; m[13] = matrix.Row3.Y; m[14] = matrix.Row3.Z; m[15] = matrix.Row3.W;
			return m;
		}

		/*
		 * Redundant state elimination.
		 *
		 * OpenBVE's renderer sets most of its state for every face it draws - cull face on or
		 * off, material flags, shininess, opacity - whether or not it changed, and a busy scene
		 * has around nine thousand faces. On a phone each of those is a driver call (through
		 * ANGLE, a validated one), and together they were most of the frame. GL state and uniform
		 * values persist until changed, so skipping a set to the value already in place is
		 * exact, provided every change goes through here - which it does, since this class is the
		 * only GL binding the renderer has. The caches start empty ("unknown") for each context.
		 */

		private static readonly Dictionary<int, bool> capabilities = new Dictionary<int, bool>();
		private static int blendSource = -1;
		private static int blendDestination = -1;
		private static int depthMask = -1;
		private static int depthFunc = -1;
		private static int cullFace = -1;

		/// <summary>Whether enabling or disabling a capability would change it, recording the new state.</summary>
		private static bool CapabilityChanges(EnableCap cap, bool enabled)
		{
			if (capabilities.TryGetValue((int)cap, out bool current) && current == enabled)
			{
				return false;
			}

			capabilities[(int)cap] = enabled;
			return true;
		}

		/// <summary>A uniform's last value: which setter set it, and up to four components as raw bits.</summary>
		private struct UniformValue
		{
			internal int Kind, A, B, C, D;
		}

		/// <summary>Per program: scalar and vector uniforms by location, and matrices by location.</summary>
		private sealed class ProgramUniforms
		{
			internal readonly Dictionary<int, UniformValue> Values = new Dictionary<int, UniformValue>();
			internal readonly Dictionary<int, float[]> Matrices = new Dictionary<int, float[]>();
		}

		private static readonly Dictionary<int, ProgramUniforms> uniformCache = new Dictionary<int, ProgramUniforms>();

		private static int Bits(float value)
		{
			return BitConverter.SingleToInt32Bits(value);
		}

		private static ProgramUniforms UniformsOf(int program)
		{
			if (!uniformCache.TryGetValue(program, out ProgramUniforms uniforms))
			{
				uniforms = new ProgramUniforms();
				uniformCache[program] = uniforms;
			}

			return uniforms;
		}

		/// <summary>Whether setting a uniform would change it, recording the new value.</summary>
		/// <remarks>
		/// Values are compared bit for bit, so -0 and +0, or two NaNs, count as different and are
		/// sent. Location -1 (a uniform the compiler removed) is a no-op in GL, so it is skipped.
		/// </remarks>
		private static bool UniformChanges(int program, int location, int kind, int a, int b = 0, int c = 0, int d = 0)
		{
			if (location < 0)
			{
				return false;
			}

			if (program == 0)
			{
				return true;
			}

			Dictionary<int, UniformValue> values = UniformsOf(program).Values;
			if (values.TryGetValue(location, out UniformValue last) && last.Kind == kind && last.A == a && last.B == b && last.C == c && last.D == d)
			{
				return false;
			}

			values[location] = new UniformValue { Kind = kind, A = a, B = b, C = c, D = d };
			return true;
		}

		/// <summary>Whether setting a matrix uniform would change it, recording the new value.</summary>
		private static bool MatrixChanges(int program, int location, bool transpose, float[] values)
		{
			if (location < 0)
			{
				return false;
			}

			if (program == 0)
			{
				return true;
			}

			Dictionary<int, float[]> matrices = UniformsOf(program).Matrices;
			if (matrices.TryGetValue(location, out float[] last))
			{
				bool same = last[16] == (transpose ? 1.0f : 0.0f);
				for (int i = 0; same && i < 16; i++)
				{
					same = Bits(last[i]) == Bits(values[i]);
				}

				if (same)
				{
					return false;
				}
			}
			else
			{
				last = new float[17];
				matrices[location] = last;
			}

			Array.Copy(values, last, 16);
			last[16] = transpose ? 1.0f : 0.0f;
			return true;
		}

		/// <summary>Forgets cached values that an uncached call (an array upload) overwrites.</summary>
		private static void ForgetUniforms(int program, int location, int count)
		{
			if (!uniformCache.TryGetValue(program, out ProgramUniforms uniforms))
			{
				return;
			}

			for (int i = 0; i < count; i++)
			{
				uniforms.Values.Remove(location + i);
				uniforms.Matrices.Remove(location + i);
			}
		}

		// --- framebuffers ---

		/// <summary>Generates a framebuffer name.</summary>
		public static int GenFramebuffer()
		{
			int[] framebuffers = new int[1];
			glGenFramebuffers(1, framebuffers);
			return framebuffers[0];
		}

		/// <summary>Generates framebuffer names.</summary>
		public static void GenFramebuffers(int n, out int framebuffers)
		{
			int[] names = new int[n < 1 ? 1 : n];
			glGenFramebuffers(n, names);
			framebuffers = names[0];
		}

		/// <summary>Deletes a framebuffer.</summary>
		public static void DeleteFramebuffer(int framebuffer)
		{
			glDeleteFramebuffers(1, new[] { framebuffer });
		}

		/// <summary>Binds a framebuffer.</summary>
		public static void BindFramebuffer(FramebufferTarget target, int framebuffer)
		{
			glBindFramebuffer((int)target, framebuffer);
		}

		/// <summary>Attaches a texture image to a framebuffer.</summary>
		public static void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, int texture, int level)
		{
			glFramebufferTexture2D((int)target, (int)attachment, (int)textarget, texture, level);
		}

		/// <summary>Attaches a renderbuffer to a framebuffer.</summary>
		public static void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbuffertarget, int renderbuffer)
		{
			glFramebufferRenderbuffer((int)target, (int)attachment, (int)renderbuffertarget, renderbuffer);
		}

		/// <summary>Returns the completeness status of the bound framebuffer.</summary>
		public static FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target)
		{
			return (FramebufferErrorCode)glCheckFramebufferStatus((int)target);
		}

		/// <summary>Generates a renderbuffer name.</summary>
		public static int GenRenderbuffer()
		{
			int[] renderbuffers = new int[1];
			glGenRenderbuffers(1, renderbuffers);
			return renderbuffers[0];
		}

		/// <summary>Generates renderbuffer names.</summary>
		public static void GenRenderbuffers(int n, out int renderbuffers)
		{
			int[] names = new int[n < 1 ? 1 : n];
			glGenRenderbuffers(n, names);
			renderbuffers = names[0];
		}

		/// <summary>Deletes a renderbuffer.</summary>
		public static void DeleteRenderbuffer(int renderbuffer)
		{
			glDeleteRenderbuffers(1, new[] { renderbuffer });
		}

		/// <summary>Binds a renderbuffer.</summary>
		public static void BindRenderbuffer(RenderbufferTarget target, int renderbuffer)
		{
			glBindRenderbuffer((int)target, renderbuffer);
		}

		/// <summary>Allocates a renderbuffer's storage.</summary>
		public static void RenderbufferStorage(RenderbufferTarget target, RenderbufferStorage internalFormat, int width, int height)
		{
			glRenderbufferStorage((int)target, (int)internalFormat, width, height);
		}

		/// <summary>Selects the buffers to draw into.</summary>
		public static void DrawBuffers(int n, DrawBuffersEnum[] buffers)
		{
			int[] values = new int[n];
			for (int i = 0; i < n; i++)
			{
				values[i] = (int)buffers[i];
			}

			glDrawBuffers(n, values);
		}

		/// <summary>Selects the buffer to draw into. GLES only has the plural form.</summary>
		public static void DrawBuffer(DrawBufferMode mode)
		{
			glDrawBuffers(1, new[] { (int)mode });
		}

		/// <summary>Selects the buffer to read from.</summary>
		public static void ReadBuffer(ReadBufferMode mode)
		{
			glReadBuffer((int)mode);
		}

		// --- fixed-function: unreachable once shaders are available ---

		private static NotSupportedException Legacy(string name)
		{
			return new NotSupportedException("GL." + name + " is fixed-function OpenGL and has no OpenGL ES equivalent. " +
			                                 "This is a legacy fallback path that should not run once shaders are available.");
		}

		/// <summary>Not available on GLES.</summary>
		public static void Begin(PrimitiveType mode) => throw Legacy("Begin");

		/// <summary>Not available on GLES.</summary>
		public static void End() => throw Legacy("End");

		/// <summary>Not available on GLES.</summary>
		public static void Vertex2(double x, double y) => throw Legacy("Vertex2");

		/// <summary>Not available on GLES.</summary>
		public static void Vertex2(float x, float y) => throw Legacy("Vertex2");

		/// <summary>Not available on GLES.</summary>
		public static void Vertex3(double x, double y, double z) => throw Legacy("Vertex3");

		/// <summary>Not available on GLES.</summary>
		public static void Vertex3(float x, float y, float z) => throw Legacy("Vertex3");

		/// <summary>Not available on GLES.</summary>
		public static void TexCoord2(double s, double t) => throw Legacy("TexCoord2");

		/// <summary>Not available on GLES.</summary>
		public static void TexCoord2(float s, float t) => throw Legacy("TexCoord2");

		/// <summary>Not available on GLES.</summary>
		public static void Color3(double r, double g, double b) => throw Legacy("Color3");

		/// <summary>Not available on GLES.</summary>
		public static void Color3(float r, float g, float b) => throw Legacy("Color3");

		/*
		 * The fixed-function colour. Unlike the other legacy calls this one is ignored rather than
		 * refused: upstream's route map and marker overlays set it to white just before a
		 * Rectangle.Draw, which sets its own colour in the shader, whenever the options do not
		 * claim a forwards-compatible context - and claiming one would also change how greyscale
		 * textures are uploaded.
		 */

		/// <summary>No effect on GLES; the shaders take their colour as a uniform.</summary>
		public static void Color4(double r, double g, double b, double a)
		{
		}

		/// <summary>No effect on GLES; the shaders take their colour as a uniform.</summary>
		public static void Color4(float r, float g, float b, float a)
		{
		}

		/// <summary>Not available on GLES.</summary>
		public static void MatrixMode(MatrixMode mode) => throw Legacy("MatrixMode");

		/// <summary>Not available on GLES.</summary>
		public static void LoadIdentity() => throw Legacy("LoadIdentity");

		/// <summary>Not available on GLES.</summary>
		public static void LoadMatrix(ref Matrix4 matrix) => throw Legacy("LoadMatrix");

		/// <summary>Not available on GLES.</summary>
		public static unsafe void LoadMatrix(double* matrix) => throw Legacy("LoadMatrix");

		/// <summary>Not available on GLES.</summary>
		public static unsafe void LoadMatrix(float* matrix) => throw Legacy("LoadMatrix");

		/// <summary>Not available on GLES.</summary>
		public static void PushMatrix() => throw Legacy("PushMatrix");

		/// <summary>Not available on GLES.</summary>
		public static void PopMatrix() => throw Legacy("PopMatrix");

		/// <summary>Not available on GLES.</summary>
		public static void Ortho(double left, double right, double bottom, double top, double zNear, double zFar) => throw Legacy("Ortho");

		/// <summary>Not available on GLES.</summary>
		public static void PolygonMode(MaterialFace face, PolygonMode mode) => throw Legacy("PolygonMode");
	}

	/// <summary>Renderbuffer storage formats.</summary>
	public enum RenderbufferStorage
	{
		DepthComponent16 = 0x81A5,
		DepthComponent24 = 0x81A6,
		Depth24Stencil8 = 0x88F0,
		Rgba8 = 0x8058,
		StencilIndex8 = 0x8D48
	}
}
