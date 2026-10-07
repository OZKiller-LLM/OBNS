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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBve.Vulkan
{
	/*
	 * Programs. OpenBVE builds three from upstream's shader sources: default (the world and the
	 * cab), rectangle and text (the overlays). Each is recognised by its source and drawn with
	 * the matching SPIR-V from Shaders/. Their uniforms are one std140 block per program; a GL
	 * uniform location is the uniform's byte offset in that block, divided by four, so setting
	 * one is a write into the program's copy of the block, which goes into the frame's uniform
	 * ring when the program next draws. Uniforms upstream's shaders declare but this renderer does
	 * not use (the shadow cascades) answer -1, as an unused uniform does in GL.
	 */
	public sealed unsafe partial class VulkanGL
	{
		private const int GL_VERTEX_SHADER = 0x8B31, GL_FRAGMENT_SHADER = 0x8B30;
		private const int GL_COMPILE_STATUS = 0x8B81, GL_LINK_STATUS = 0x8B82;

		/// <summary>The sampler uniform's location: set by upstream, nothing to store.</summary>
		private const int SamplerLocation = 1000;

		private enum ProgramKind
		{
			Unsupported,
			Default,
			Rectangle,
			Text
		}

		private sealed class GLShader
		{
			internal int Type;
			internal string Source = string.Empty;
		}

		private sealed class GLProgram
		{
			internal readonly List<GLShader> Shaders = new List<GLShader>();
			internal ProgramKind Kind;
			internal byte[] Data = Array.Empty<byte>();
			internal bool Dirty = true;
			internal bool NormalDirty = true;
			/// <summary>The uniform buffer binding its animation matrices come from (glUniformBlockBinding).</summary>
			internal int BlockBinding;
			internal ulong RingSerial;
			internal BufferHeap.Block RingBlock;
			internal uint RingOffset;
		}

		// Byte offsets in default.vert's DrawBlock.
		private const int DefaultBlockSize = 464, ModelViewOffset = 64, NormalMatrixOffset = 192;

		private static readonly Dictionary<string, int> DefaultUniforms = new Dictionary<string, int>
		{
			{ "uCurrentProjectionMatrix", 0 },
			{ "uCurrentModelViewMatrix", ModelViewOffset },
			{ "uCurrentTextureMatrix", 128 },
			{ "uLight.position", 256 },
			{ "uLight.ambient", 272 },
			{ "uLight.diffuse", 288 },
			{ "uLight.specular", 304 },
			{ "uLight.lightModel", 320 },
			{ "uMaterial.ambient", 336 },
			{ "uMaterial.diffuse", 352 },
			{ "uMaterial.specular", 368 },
			{ "uMaterial.emission", 384 },
			{ "uMaterial.shininess", 396 },
			{ "uMaterialFlags", 400 },
			{ "uIsLight", 404 },
			{ "uIsFog", 408 },
			{ "uFogIsLinear", 412 },
			{ "uBrightness", 416 },
			{ "uOpacity", 420 },
			{ "uAlphaTest", 424 },
			{ "uFogStart", 432 },
			{ "uFogEnd", 436 },
			{ "uFogDensity", 440 },
			{ "uFogColor", 448 }
		};

		// Byte offsets in rectangle.vert's Block2D (rectangle and text).
		private const int Block2DSize = 208;

		private static readonly Dictionary<string, int> Uniforms2D = new Dictionary<string, int>
		{
			{ "uCurrentProjectionMatrix", 0 },
			{ "uCurrentModelViewMatrix", 64 },
			{ "uPoint", 128 },
			{ "uSize", 136 },
			{ "uCoordinates", 144 },
			{ "uAtlasLocation", 160 },
			{ "uColor", 176 },
			{ "uAlphaTest", 192 }
		};

		private static readonly string[] DefaultAttributes = { "iPosition", "iNormal", "iUv", "iColor", "iMatrixChain" };

		private readonly Dictionary<int, GLShader> glShaders = new Dictionary<int, GLShader>();
		private readonly Dictionary<int, GLProgram> glPrograms = new Dictionary<int, GLProgram>();
		private GLProgram currentProgram;

		public int glCreateShader(int type)
		{
			int name = nextName++;
			glShaders[name] = new GLShader { Type = type };
			return name;
		}

		public void glDeleteShader(int shader) => glShaders.Remove(shader);

		public void glShaderSource(int shader, int count, IntPtr[] str, int[] length)
		{
			if (!glShaders.TryGetValue(shader, out GLShader target))
			{
				return;
			}

			StringBuilder source = new StringBuilder();
			for (int i = 0; i < count; i++)
			{
				source.Append(length != null && length[i] >= 0 ? Marshal.PtrToStringAnsi(str[i], length[i]) : Marshal.PtrToStringAnsi(str[i]));
			}

			target.Source = source.ToString();
		}

		public void glCompileShader(int shader)
		{
		}

		public void glGetShaderiv(int shader, int pname, int[] parameters) => parameters[0] = pname == GL_COMPILE_STATUS ? 1 : 0;

		public void glGetShaderInfoLog(int shader, int bufferSize, int[] length, StringBuilder infoLog)
		{
			infoLog?.Clear();
		}

		public int glCreateProgram()
		{
			int name = nextName++;
			glPrograms[name] = new GLProgram();
			return name;
		}

		public void glDeleteProgram(int program)
		{
			if (glPrograms.TryGetValue(program, out GLProgram deleted) && currentProgram == deleted)
			{
				currentProgram = null;
			}

			glPrograms.Remove(program);
		}

		public void glAttachShader(int program, int shader)
		{
			if (glPrograms.TryGetValue(program, out GLProgram target) && glShaders.TryGetValue(shader, out GLShader attached))
			{
				target.Shaders.Add(attached);
			}
		}

		public void glLinkProgram(int program)
		{
			if (!glPrograms.TryGetValue(program, out GLProgram target))
			{
				return;
			}

			string vertex = string.Empty, fragment = string.Empty;
			foreach (GLShader shader in target.Shaders)
			{
				if (shader.Type == GL_VERTEX_SHADER)
				{
					vertex = shader.Source;
				}
				else if (shader.Type == GL_FRAGMENT_SHADER)
				{
					fragment = shader.Source;
				}
			}

			// Upstream's programs, by what their sources declare.
			if (fragment.Contains("uObjectIndex"))
			{
				target.Kind = ProgramKind.Unsupported;
				ReportOnce("the picking shader is not supported by the Vulkan renderer");
			}
			else if (vertex.Contains("uAnimationMatricies") && fragment.Contains("uTexture"))
			{
				target.Kind = ProgramKind.Default;
			}
			else if (vertex.Contains("uAtlasLocation"))
			{
				target.Kind = ProgramKind.Text;
			}
			else if (vertex.Contains("uCoordinates"))
			{
				target.Kind = ProgramKind.Rectangle;
			}
			else
			{
				target.Kind = ProgramKind.Unsupported;
				ReportOnce("a shader program the Vulkan renderer does not know is not drawn");
			}

			target.Data = new byte[target.Kind == ProgramKind.Default ? DefaultBlockSize : Block2DSize];
			target.Dirty = true;
			target.NormalDirty = true;
		}

		public void glUseProgram(int program)
		{
			currentProgram = program != 0 && glPrograms.TryGetValue(program, out GLProgram used) ? used : null;
		}

		public void glGetProgramiv(int program, int pname, int[] parameters) => parameters[0] = pname == GL_LINK_STATUS ? 1 : 0;

		public void glGetProgramInfoLog(int program, int bufferSize, int[] length, StringBuilder infoLog)
		{
			infoLog?.Clear();
		}

		public int glGetAttribLocation(int program, string name)
		{
			if (glPrograms.TryGetValue(program, out GLProgram target) && target.Kind == ProgramKind.Default)
			{
				return Array.IndexOf(DefaultAttributes, name);
			}

			return -1;
		}

		public int glGetUniformLocation(int program, string name)
		{
			if (!glPrograms.TryGetValue(program, out GLProgram target) || target.Kind == ProgramKind.Unsupported)
			{
				return -1;
			}

			if (name == "uTexture")
			{
				return SamplerLocation;
			}

			Dictionary<string, int> table = target.Kind == ProgramKind.Default ? DefaultUniforms : Uniforms2D;
			return table.TryGetValue(name, out int offset) ? offset / 4 : -1;
		}

		public int glGetUniformBlockIndex(int program, string name)
		{
			return glPrograms.TryGetValue(program, out GLProgram target) && target.Kind == ProgramKind.Default && name == "uAnimationMatricies" ? 0 : -1;
		}

		public void glUniformBlockBinding(int program, int blockIndex, int binding)
		{
			if (blockIndex == 0 && glPrograms.TryGetValue(program, out GLProgram target))
			{
				target.BlockBinding = binding;
			}
		}

		/// <summary>Where a uniform write goes in the current program's block, or -1 if nowhere.</summary>
		private int UniformTarget(int location, int bytes)
		{
			GLProgram program = currentProgram;
			if (program == null || location < 0 || location == SamplerLocation)
			{
				return -1;
			}

			int offset = location * 4;
			if (offset + bytes > program.Data.Length)
			{
				return -1;
			}

			program.Dirty = true;
			if (program.Kind == ProgramKind.Default && offset < ModelViewOffset + 64 && offset + bytes > ModelViewOffset)
			{
				program.NormalDirty = true;
			}

			return offset;
		}

		private void WriteUniform<T>(int offset, int index, T value) where T : unmanaged
		{
			Unsafe.WriteUnaligned(ref currentProgram.Data[offset + index * 4], value);
		}

		public void glUniform1i(int location, int v0)
		{
			int offset = UniformTarget(location, 4);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
			}
		}

		public void glUniform1f(int location, float v0)
		{
			int offset = UniformTarget(location, 4);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
			}
		}

		public void glUniform2f(int location, float v0, float v1)
		{
			int offset = UniformTarget(location, 8);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
			}
		}

		public void glUniform3f(int location, float v0, float v1, float v2)
		{
			int offset = UniformTarget(location, 12);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
				WriteUniform(offset, 2, v2);
			}
		}

		public void glUniform4f(int location, float v0, float v1, float v2, float v3)
		{
			int offset = UniformTarget(location, 16);
			if (offset >= 0)
			{
				WriteUniform(offset, 0, v0);
				WriteUniform(offset, 1, v1);
				WriteUniform(offset, 2, v2);
				WriteUniform(offset, 3, v3);
			}
		}

		public void glUniform1fv(int location, int count, float[] value)
		{
			// Arrays of floats have a 16 byte stride in std140; none of the blocks here has one.
			if (count == 1)
			{
				glUniform1f(location, value[0]);
			}
		}

		public void glUniformMatrix4fv(int location, int count, byte transpose, float[] value)
		{
			int offset = UniformTarget(location, 64);
			if (offset < 0)
			{
				return;
			}

			for (int column = 0; column < 4; column++)
			{
				for (int row = 0; row < 4; row++)
				{
					WriteUniform(offset, column * 4 + row, transpose != 0 ? value[row * 4 + column] : value[column * 4 + row]);
				}
			}
		}

		/// <summary>The default program's normal matrix, transpose(inverse(mat3(modelview))), into its block.</summary>
		private static void UpdateNormalMatrix(GLProgram program)
		{
			fixed (byte* data = program.Data)
			{
				float* m = (float*)(data + ModelViewOffset);
				float* n = (float*)(data + NormalMatrixOffset);
				// a[row, column] = m[column * 4 + row]
				float a00 = m[0], a10 = m[1], a20 = m[2];
				float a01 = m[4], a11 = m[5], a21 = m[6];
				float a02 = m[8], a12 = m[9], a22 = m[10];
				float c00 = a11 * a22 - a12 * a21, c01 = a12 * a20 - a10 * a22, c02 = a10 * a21 - a11 * a20;
				float c10 = a02 * a21 - a01 * a22, c11 = a00 * a22 - a02 * a20, c12 = a01 * a20 - a00 * a21;
				float c20 = a01 * a12 - a02 * a11, c21 = a02 * a10 - a00 * a12, c22 = a00 * a11 - a01 * a10;
				float determinant = a00 * c00 + a01 * c01 + a02 * c02;
				float scale = Math.Abs(determinant) > 1e-20f ? 1.0f / determinant : 1.0f;
				// transpose(inverse(A)) = cofactor(A) / det(A); n[column * 4 + row] = c[row, column]
				n[0] = c00 * scale;
				n[1] = c10 * scale;
				n[2] = c20 * scale;
				n[3] = 0.0f;
				n[4] = c01 * scale;
				n[5] = c11 * scale;
				n[6] = c21 * scale;
				n[7] = 0.0f;
				n[8] = c02 * scale;
				n[9] = c12 * scale;
				n[10] = c22 * scale;
				n[11] = 0.0f;
				n[12] = n[13] = n[14] = 0.0f;
				n[15] = 1.0f;
			}

			program.NormalDirty = false;
		}
	}
}
