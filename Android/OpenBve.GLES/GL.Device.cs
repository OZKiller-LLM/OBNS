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
using System.Text;

namespace OpenTK.Graphics.OpenGL
{
	/// <summary>
	/// The OpenGL ES 3 entry points GL uses, as one interface, so that something other than the
	/// system's GLES driver can stand behind them: the native Vulkan renderer's GL-on-Vulkan layer
	/// (OpenBve.Vulkan.VulkanGL). Same names, arguments and meaning as the C functions.
	/// </summary>
	public interface IGLDevice
	{
		void glEnable(int cap);
		void glDisable(int cap);
		void glClear(int mask);
		void glClearColor(float r, float g, float b, float a);
		void glViewport(int x, int y, int width, int height);
		void glBlendFunc(int src, int dst);
		void glDepthMask(byte flag);
		void glDepthFunc(int func);
		void glCullFace(int mode);
		void glLineWidth(float width);
		void glHint(int target, int mode);
		void glPixelStorei(int pname, int param);
		int glGetError();
		IntPtr glGetString(int name);
		void glGetFloatv(int pname, float[] parameters);
		void glGetIntegerv(int pname, int[] parameters);
		void glReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels);
		void glGenTextures(int n, int[] textures);
		void glDeleteTextures(int n, int[] textures);
		void glBindTexture(int target, int texture);
		void glActiveTexture(int texture);
		void glGenerateMipmap(int target);
		void glTexParameteri(int target, int pname, int param);
		void glTexParameterf(int target, int pname, float param);
		void glTexParameteriv(int target, int pname, int[] parameters);
		void glTexParameterfv(int target, int pname, float[] parameters);
		void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels);
		void glTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels);
		void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border);
		void glGenBuffers(int n, int[] buffers);
		void glDeleteBuffers(int n, int[] buffers);
		void glBindBuffer(int target, int buffer);
		void glBindBufferBase(int target, int index, int buffer);
		void glBufferData(int target, IntPtr size, IntPtr data, int usage);
		void glBufferSubData(int target, IntPtr offset, IntPtr size, IntPtr data);
		void glGenVertexArrays(int n, int[] arrays);
		void glDeleteVertexArrays(int n, int[] arrays);
		void glBindVertexArray(int array);
		void glEnableVertexAttribArray(int index);
		void glDisableVertexAttribArray(int index);
		void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer);
		void glVertexAttribIPointer(int index, int size, int type, int stride, IntPtr pointer);
		void glDrawArrays(int mode, int first, int count);
		void glDrawElements(int mode, int count, int type, IntPtr indices);
		int glCreateShader(int type);
		void glDeleteShader(int shader);
		void glShaderSource(int shader, int count, IntPtr[] str, int[] length);
		void glCompileShader(int shader);
		void glGetShaderiv(int shader, int pname, int[] parameters);
		void glGetShaderInfoLog(int shader, int bufferSize, int[] length, StringBuilder infoLog);
		int glCreateProgram();
		void glDeleteProgram(int program);
		void glAttachShader(int program, int shader);
		void glLinkProgram(int program);
		void glUseProgram(int program);
		void glGetProgramiv(int program, int pname, int[] parameters);
		void glGetProgramInfoLog(int program, int bufferSize, int[] length, StringBuilder infoLog);
		int glGetAttribLocation(int program, string name);
		int glGetUniformLocation(int program, string name);
		int glGetUniformBlockIndex(int program, string name);
		void glUniformBlockBinding(int program, int blockIndex, int binding);
		void glUniform1i(int location, int v0);
		void glUniform1f(int location, float v0);
		void glUniform2f(int location, float v0, float v1);
		void glUniform3f(int location, float v0, float v1, float v2);
		void glUniform4f(int location, float v0, float v1, float v2, float v3);
		void glUniform1fv(int location, int count, float[] value);
		void glUniformMatrix4fv(int location, int count, byte transpose, float[] value);
		void glGenFramebuffers(int n, int[] framebuffers);
		void glDeleteFramebuffers(int n, int[] framebuffers);
		void glBindFramebuffer(int target, int framebuffer);
		void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level);
		void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer);
		int glCheckFramebufferStatus(int target);
		void glGenRenderbuffers(int n, int[] renderbuffers);
		void glDeleteRenderbuffers(int n, int[] renderbuffers);
		void glBindRenderbuffer(int target, int renderbuffer);
		void glRenderbufferStorage(int target, int internalFormat, int width, int height);
		void glDrawBuffers(int n, int[] buffers);
		void glReadBuffer(int mode);
	}

	public static partial class GL
	{
		/// <summary>
		/// When set, every GL call goes to this device instead of the GLES driver: everything in GL
		/// above the native calls - the quad and polygon translation, the 16-bit textures, the state
		/// and uniform caches - applies unchanged. Set on the render thread before the renderer
		/// starts, and never changed while it runs.
		/// </summary>
		public static IGLDevice Device;

		private static void glEnable(int cap)
		{
			if (Device != null)
			{
				Device.glEnable(cap);
			}
			else
			{
				es_glEnable(cap);
			}
		}

		private static void glDisable(int cap)
		{
			if (Device != null)
			{
				Device.glDisable(cap);
			}
			else
			{
				es_glDisable(cap);
			}
		}

		private static void glClear(int mask)
		{
			if (Device != null)
			{
				Device.glClear(mask);
			}
			else
			{
				es_glClear(mask);
			}
		}

		private static void glClearColor(float r, float g, float b, float a)
		{
			if (Device != null)
			{
				Device.glClearColor(r, g, b, a);
			}
			else
			{
				es_glClearColor(r, g, b, a);
			}
		}

		private static void glViewport(int x, int y, int width, int height)
		{
			if (Device != null)
			{
				Device.glViewport(x, y, width, height);
			}
			else
			{
				es_glViewport(x, y, width, height);
			}
		}

		private static void glBlendFunc(int src, int dst)
		{
			if (Device != null)
			{
				Device.glBlendFunc(src, dst);
			}
			else
			{
				es_glBlendFunc(src, dst);
			}
		}

		private static void glDepthMask(byte flag)
		{
			if (Device != null)
			{
				Device.glDepthMask(flag);
			}
			else
			{
				es_glDepthMask(flag);
			}
		}

		private static void glDepthFunc(int func)
		{
			if (Device != null)
			{
				Device.glDepthFunc(func);
			}
			else
			{
				es_glDepthFunc(func);
			}
		}

		private static void glCullFace(int mode)
		{
			if (Device != null)
			{
				Device.glCullFace(mode);
			}
			else
			{
				es_glCullFace(mode);
			}
		}

		private static void glLineWidth(float width)
		{
			if (Device != null)
			{
				Device.glLineWidth(width);
			}
			else
			{
				es_glLineWidth(width);
			}
		}

		private static void glHint(int target, int mode)
		{
			if (Device != null)
			{
				Device.glHint(target, mode);
			}
			else
			{
				es_glHint(target, mode);
			}
		}

		private static void glPixelStorei(int pname, int param)
		{
			if (Device != null)
			{
				Device.glPixelStorei(pname, param);
			}
			else
			{
				es_glPixelStorei(pname, param);
			}
		}

		private static int glGetError() => Device != null ? Device.glGetError() : es_glGetError();

		private static IntPtr glGetString(int name) => Device != null ? Device.glGetString(name) : es_glGetString(name);

		private static void glGetFloatv(int pname, float[] parameters)
		{
			if (Device != null)
			{
				Device.glGetFloatv(pname, parameters);
			}
			else
			{
				es_glGetFloatv(pname, parameters);
			}
		}

		private static void glGetIntegerv(int pname, int[] parameters)
		{
			if (Device != null)
			{
				Device.glGetIntegerv(pname, parameters);
			}
			else
			{
				es_glGetIntegerv(pname, parameters);
			}
		}

		private static void glReadPixels(int x, int y, int width, int height, int format, int type, byte[] pixels)
		{
			if (Device != null)
			{
				Device.glReadPixels(x, y, width, height, format, type, pixels);
			}
			else
			{
				es_glReadPixels(x, y, width, height, format, type, pixels);
			}
		}

		private static void glGenTextures(int n, int[] textures)
		{
			if (Device != null)
			{
				Device.glGenTextures(n, textures);
			}
			else
			{
				es_glGenTextures(n, textures);
			}
		}

		private static void glDeleteTextures(int n, int[] textures)
		{
			if (Device != null)
			{
				Device.glDeleteTextures(n, textures);
			}
			else
			{
				es_glDeleteTextures(n, textures);
			}
		}

		private static void glBindTexture(int target, int texture)
		{
			if (Device != null)
			{
				Device.glBindTexture(target, texture);
			}
			else
			{
				es_glBindTexture(target, texture);
			}
		}

		private static void glActiveTexture(int texture)
		{
			if (Device != null)
			{
				Device.glActiveTexture(texture);
			}
			else
			{
				es_glActiveTexture(texture);
			}
		}

		private static void glGenerateMipmap(int target)
		{
			if (Device != null)
			{
				Device.glGenerateMipmap(target);
			}
			else
			{
				es_glGenerateMipmap(target);
			}
		}

		private static void glTexParameteri(int target, int pname, int param)
		{
			if (Device != null)
			{
				Device.glTexParameteri(target, pname, param);
			}
			else
			{
				es_glTexParameteri(target, pname, param);
			}
		}

		private static void glTexParameterf(int target, int pname, float param)
		{
			if (Device != null)
			{
				Device.glTexParameterf(target, pname, param);
			}
			else
			{
				es_glTexParameterf(target, pname, param);
			}
		}

		private static void glTexParameteriv(int target, int pname, int[] parameters)
		{
			if (Device != null)
			{
				Device.glTexParameteriv(target, pname, parameters);
			}
			else
			{
				es_glTexParameteriv(target, pname, parameters);
			}
		}

		private static void glTexParameterfv(int target, int pname, float[] parameters)
		{
			if (Device != null)
			{
				Device.glTexParameterfv(target, pname, parameters);
			}
			else
			{
				es_glTexParameterfv(target, pname, parameters);
			}
		}

		private static void glTexImage2D(int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels)
		{
			if (Device != null)
			{
				Device.glTexImage2D(target, level, internalFormat, width, height, border, format, type, pixels);
			}
			else
			{
				es_glTexImage2D(target, level, internalFormat, width, height, border, format, type, pixels);
			}
		}

		private static void glTexSubImage2D(int target, int level, int xoffset, int yoffset, int width, int height, int format, int type, IntPtr pixels)
		{
			if (Device != null)
			{
				Device.glTexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, pixels);
			}
			else
			{
				es_glTexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, pixels);
			}
		}

		private static void glCopyTexImage2D(int target, int level, int internalFormat, int x, int y, int width, int height, int border)
		{
			if (Device != null)
			{
				Device.glCopyTexImage2D(target, level, internalFormat, x, y, width, height, border);
			}
			else
			{
				es_glCopyTexImage2D(target, level, internalFormat, x, y, width, height, border);
			}
		}

		private static void glGenBuffers(int n, int[] buffers)
		{
			if (Device != null)
			{
				Device.glGenBuffers(n, buffers);
			}
			else
			{
				es_glGenBuffers(n, buffers);
			}
		}

		private static void glDeleteBuffers(int n, int[] buffers)
		{
			if (Device != null)
			{
				Device.glDeleteBuffers(n, buffers);
			}
			else
			{
				es_glDeleteBuffers(n, buffers);
			}
		}

		private static void glBindBuffer(int target, int buffer)
		{
			if (Device != null)
			{
				Device.glBindBuffer(target, buffer);
			}
			else
			{
				es_glBindBuffer(target, buffer);
			}
		}

		private static void glBindBufferBase(int target, int index, int buffer)
		{
			if (Device != null)
			{
				Device.glBindBufferBase(target, index, buffer);
			}
			else
			{
				es_glBindBufferBase(target, index, buffer);
			}
		}

		private static void glBufferData(int target, IntPtr size, IntPtr data, int usage)
		{
			if (Device != null)
			{
				Device.glBufferData(target, size, data, usage);
			}
			else
			{
				es_glBufferData(target, size, data, usage);
			}
		}

		private static void glBufferSubData(int target, IntPtr offset, IntPtr size, IntPtr data)
		{
			if (Device != null)
			{
				Device.glBufferSubData(target, offset, size, data);
			}
			else
			{
				es_glBufferSubData(target, offset, size, data);
			}
		}

		private static void glGenVertexArrays(int n, int[] arrays)
		{
			if (Device != null)
			{
				Device.glGenVertexArrays(n, arrays);
			}
			else
			{
				es_glGenVertexArrays(n, arrays);
			}
		}

		private static void glDeleteVertexArrays(int n, int[] arrays)
		{
			if (Device != null)
			{
				Device.glDeleteVertexArrays(n, arrays);
			}
			else
			{
				es_glDeleteVertexArrays(n, arrays);
			}
		}

		private static void glBindVertexArray(int array)
		{
			if (Device != null)
			{
				Device.glBindVertexArray(array);
			}
			else
			{
				es_glBindVertexArray(array);
			}
		}

		private static void glEnableVertexAttribArray(int index)
		{
			if (Device != null)
			{
				Device.glEnableVertexAttribArray(index);
			}
			else
			{
				es_glEnableVertexAttribArray(index);
			}
		}

		private static void glDisableVertexAttribArray(int index)
		{
			if (Device != null)
			{
				Device.glDisableVertexAttribArray(index);
			}
			else
			{
				es_glDisableVertexAttribArray(index);
			}
		}

		private static void glVertexAttribPointer(int index, int size, int type, byte normalized, int stride, IntPtr pointer)
		{
			if (Device != null)
			{
				Device.glVertexAttribPointer(index, size, type, normalized, stride, pointer);
			}
			else
			{
				es_glVertexAttribPointer(index, size, type, normalized, stride, pointer);
			}
		}

		private static void glVertexAttribIPointer(int index, int size, int type, int stride, IntPtr pointer)
		{
			if (Device != null)
			{
				Device.glVertexAttribIPointer(index, size, type, stride, pointer);
			}
			else
			{
				es_glVertexAttribIPointer(index, size, type, stride, pointer);
			}
		}

		private static void glDrawArrays(int mode, int first, int count)
		{
			if (Device != null)
			{
				Device.glDrawArrays(mode, first, count);
			}
			else
			{
				es_glDrawArrays(mode, first, count);
			}
		}

		private static void glDrawElements(int mode, int count, int type, IntPtr indices)
		{
			if (Device != null)
			{
				Device.glDrawElements(mode, count, type, indices);
			}
			else
			{
				es_glDrawElements(mode, count, type, indices);
			}
		}

		private static int glCreateShader(int type) => Device != null ? Device.glCreateShader(type) : es_glCreateShader(type);

		private static void glDeleteShader(int shader)
		{
			if (Device != null)
			{
				Device.glDeleteShader(shader);
			}
			else
			{
				es_glDeleteShader(shader);
			}
		}

		private static void glShaderSource(int shader, int count, IntPtr[] str, int[] length)
		{
			if (Device != null)
			{
				Device.glShaderSource(shader, count, str, length);
			}
			else
			{
				es_glShaderSource(shader, count, str, length);
			}
		}

		private static void glCompileShader(int shader)
		{
			if (Device != null)
			{
				Device.glCompileShader(shader);
			}
			else
			{
				es_glCompileShader(shader);
			}
		}

		private static void glGetShaderiv(int shader, int pname, int[] parameters)
		{
			if (Device != null)
			{
				Device.glGetShaderiv(shader, pname, parameters);
			}
			else
			{
				es_glGetShaderiv(shader, pname, parameters);
			}
		}

		private static void glGetShaderInfoLog(int shader, int bufferSize, int[] length, StringBuilder infoLog)
		{
			if (Device != null)
			{
				Device.glGetShaderInfoLog(shader, bufferSize, length, infoLog);
			}
			else
			{
				es_glGetShaderInfoLog(shader, bufferSize, length, infoLog);
			}
		}

		private static int glCreateProgram() => Device != null ? Device.glCreateProgram() : es_glCreateProgram();

		private static void glDeleteProgram(int program)
		{
			if (Device != null)
			{
				Device.glDeleteProgram(program);
			}
			else
			{
				es_glDeleteProgram(program);
			}
		}

		private static void glAttachShader(int program, int shader)
		{
			if (Device != null)
			{
				Device.glAttachShader(program, shader);
			}
			else
			{
				es_glAttachShader(program, shader);
			}
		}

		private static void glLinkProgram(int program)
		{
			if (Device != null)
			{
				Device.glLinkProgram(program);
			}
			else
			{
				es_glLinkProgram(program);
			}
		}

		private static void glUseProgram(int program)
		{
			if (Device != null)
			{
				Device.glUseProgram(program);
			}
			else
			{
				es_glUseProgram(program);
			}
		}

		private static void glGetProgramiv(int program, int pname, int[] parameters)
		{
			if (Device != null)
			{
				Device.glGetProgramiv(program, pname, parameters);
			}
			else
			{
				es_glGetProgramiv(program, pname, parameters);
			}
		}

		private static void glGetProgramInfoLog(int program, int bufferSize, int[] length, StringBuilder infoLog)
		{
			if (Device != null)
			{
				Device.glGetProgramInfoLog(program, bufferSize, length, infoLog);
			}
			else
			{
				es_glGetProgramInfoLog(program, bufferSize, length, infoLog);
			}
		}

		private static int glGetAttribLocation(int program, string name) => Device != null ? Device.glGetAttribLocation(program, name) : es_glGetAttribLocation(program, name);

		private static int glGetUniformLocation(int program, string name) => Device != null ? Device.glGetUniformLocation(program, name) : es_glGetUniformLocation(program, name);

		private static int glGetUniformBlockIndex(int program, string name) => Device != null ? Device.glGetUniformBlockIndex(program, name) : es_glGetUniformBlockIndex(program, name);

		private static void glUniformBlockBinding(int program, int blockIndex, int binding)
		{
			if (Device != null)
			{
				Device.glUniformBlockBinding(program, blockIndex, binding);
			}
			else
			{
				es_glUniformBlockBinding(program, blockIndex, binding);
			}
		}

		private static void glUniform1i(int location, int v0)
		{
			if (Device != null)
			{
				Device.glUniform1i(location, v0);
			}
			else
			{
				es_glUniform1i(location, v0);
			}
		}

		private static void glUniform1f(int location, float v0)
		{
			if (Device != null)
			{
				Device.glUniform1f(location, v0);
			}
			else
			{
				es_glUniform1f(location, v0);
			}
		}

		private static void glUniform2f(int location, float v0, float v1)
		{
			if (Device != null)
			{
				Device.glUniform2f(location, v0, v1);
			}
			else
			{
				es_glUniform2f(location, v0, v1);
			}
		}

		private static void glUniform3f(int location, float v0, float v1, float v2)
		{
			if (Device != null)
			{
				Device.glUniform3f(location, v0, v1, v2);
			}
			else
			{
				es_glUniform3f(location, v0, v1, v2);
			}
		}

		private static void glUniform4f(int location, float v0, float v1, float v2, float v3)
		{
			if (Device != null)
			{
				Device.glUniform4f(location, v0, v1, v2, v3);
			}
			else
			{
				es_glUniform4f(location, v0, v1, v2, v3);
			}
		}

		private static void glUniform1fv(int location, int count, float[] value)
		{
			if (Device != null)
			{
				Device.glUniform1fv(location, count, value);
			}
			else
			{
				es_glUniform1fv(location, count, value);
			}
		}

		private static void glUniformMatrix4fv(int location, int count, byte transpose, float[] value)
		{
			if (Device != null)
			{
				Device.glUniformMatrix4fv(location, count, transpose, value);
			}
			else
			{
				es_glUniformMatrix4fv(location, count, transpose, value);
			}
		}

		private static void glGenFramebuffers(int n, int[] framebuffers)
		{
			if (Device != null)
			{
				Device.glGenFramebuffers(n, framebuffers);
			}
			else
			{
				es_glGenFramebuffers(n, framebuffers);
			}
		}

		private static void glDeleteFramebuffers(int n, int[] framebuffers)
		{
			if (Device != null)
			{
				Device.glDeleteFramebuffers(n, framebuffers);
			}
			else
			{
				es_glDeleteFramebuffers(n, framebuffers);
			}
		}

		private static void glBindFramebuffer(int target, int framebuffer)
		{
			if (Device != null)
			{
				Device.glBindFramebuffer(target, framebuffer);
			}
			else
			{
				es_glBindFramebuffer(target, framebuffer);
			}
		}

		private static void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level)
		{
			if (Device != null)
			{
				Device.glFramebufferTexture2D(target, attachment, textarget, texture, level);
			}
			else
			{
				es_glFramebufferTexture2D(target, attachment, textarget, texture, level);
			}
		}

		private static void glFramebufferRenderbuffer(int target, int attachment, int renderbuffertarget, int renderbuffer)
		{
			if (Device != null)
			{
				Device.glFramebufferRenderbuffer(target, attachment, renderbuffertarget, renderbuffer);
			}
			else
			{
				es_glFramebufferRenderbuffer(target, attachment, renderbuffertarget, renderbuffer);
			}
		}

		private static int glCheckFramebufferStatus(int target) => Device != null ? Device.glCheckFramebufferStatus(target) : es_glCheckFramebufferStatus(target);

		private static void glGenRenderbuffers(int n, int[] renderbuffers)
		{
			if (Device != null)
			{
				Device.glGenRenderbuffers(n, renderbuffers);
			}
			else
			{
				es_glGenRenderbuffers(n, renderbuffers);
			}
		}

		private static void glDeleteRenderbuffers(int n, int[] renderbuffers)
		{
			if (Device != null)
			{
				Device.glDeleteRenderbuffers(n, renderbuffers);
			}
			else
			{
				es_glDeleteRenderbuffers(n, renderbuffers);
			}
		}

		private static void glBindRenderbuffer(int target, int renderbuffer)
		{
			if (Device != null)
			{
				Device.glBindRenderbuffer(target, renderbuffer);
			}
			else
			{
				es_glBindRenderbuffer(target, renderbuffer);
			}
		}

		private static void glRenderbufferStorage(int target, int internalFormat, int width, int height)
		{
			if (Device != null)
			{
				Device.glRenderbufferStorage(target, internalFormat, width, height);
			}
			else
			{
				es_glRenderbufferStorage(target, internalFormat, width, height);
			}
		}

		private static void glDrawBuffers(int n, int[] buffers)
		{
			if (Device != null)
			{
				Device.glDrawBuffers(n, buffers);
			}
			else
			{
				es_glDrawBuffers(n, buffers);
			}
		}

		private static void glReadBuffer(int mode)
		{
			if (Device != null)
			{
				Device.glReadBuffer(mode);
			}
			else
			{
				es_glReadBuffer(mode);
			}
		}
	}
}
