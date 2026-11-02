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

// OBNS: not an upstream file. OpenGL with nothing behind it, for a game drawn wholly by another API (OBNS/RENDERERS.md).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics.OpenGL;

namespace OpenBve.Graphics
{
	/// <summary>
	/// Upstream's renderer calls OpenGL in hundreds of places that no longer draw anything once another API draws the whole
	/// frame. OpenTK calls each function through a table (GL.EntryPoints), so the table is pointed at machine code that does
	/// nothing: a caller on x64 cleans up after itself, so one stub serves every signature. The few whose answers upstream
	/// depends on (a new object's name, a shader's compile status) have stubs of their own.
	/// </summary>
	internal static class GLStub
	{
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protect, out uint old);

		/// <summary>Whether the stubs can be used here: x64 Windows so far (the second and third arguments' registers are the system's).</summary>
		internal static bool Available => Environment.Is64BitProcess && Environment.OSVersion.Platform == PlatformID.Win32NT && RuntimeInformation.ProcessArchitecture == Architecture.X64;

		/// <summary>Points OpenGL's functions at the stubs. Upstream's calls do nothing from now.</summary>
		internal static void Install()
		{
			const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
			IntPtr[] entryPoints = (IntPtr[])typeof(GL).GetField("EntryPoints", flags).GetValue(null);
			byte[] names = (byte[])typeof(GL).GetField("EntryPointNames", flags).GetValue(null);
			int[] offsets = (int[])typeof(GL).GetField("EntryPointNameOffsets", flags).GetValue(null);

			// What upstream reads as OpenGL's version and extensions.
			IntPtr text = Marshal.StringToHGlobalAnsi("4.6.0 GL_EXT_texture_filter_anisotropic");

			byte[] nothing = { 0x31, 0xC0, 0xC3 }; // xor eax,eax; ret
			byte[] one = { 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3 }; // mov eax,1; ret
			byte[] framebufferComplete = { 0xB8, 0xD5, 0x8C, 0x00, 0x00, 0xC3 }; // mov eax,GL_FRAMEBUFFER_COMPLETE; ret
			byte[] secondIsOne = { 0xC7, 0x02, 0x01, 0x00, 0x00, 0x00, 0x31, 0xC0, 0xC3 }; // mov dword [rdx],1; xor eax,eax; ret
			byte[] thirdIsOne = { 0x41, 0xC7, 0x00, 0x01, 0x00, 0x00, 0x00, 0x31, 0xC0, 0xC3 }; // mov dword [r8],1; xor eax,eax; ret
			// The only float upstream asks for is the anisotropic filtering limit, which the options are saved with: 16, as on any GPU now.
			byte[] secondIs16 = { 0xC7, 0x02, 0x00, 0x00, 0x80, 0x41, 0x31, 0xC0, 0xC3 }; // mov dword [rdx],16.0f; xor eax,eax; ret
			byte[] returnsText = new byte[13]; // mov rax,text; ret
			returnsText[0] = 0x48;
			returnsText[1] = 0xB8;
			BitConverter.GetBytes(text.ToInt64()).CopyTo(returnsText, 2);
			returnsText[10] = 0xC3;

			byte[][] stubs = { nothing, one, framebufferComplete, secondIsOne, thirdIsOne, returnsText, secondIs16 };
			int size = 0;
			foreach (byte[] stub in stubs)
			{
				size += (stub.Length + 15) & ~15;
			}

			const uint commitReserve = 0x3000, readWrite = 0x04, executeRead = 0x20;
			IntPtr memory = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)size, commitReserve, readWrite);
			if (memory == IntPtr.Zero)
			{
				throw new InvalidOperationException("no memory for OpenGL's stubs");
			}

			IntPtr[] addresses = new IntPtr[stubs.Length];
			int offset = 0;
			for (int i = 0; i < stubs.Length; i++)
			{
				addresses[i] = memory + offset;
				Marshal.Copy(stubs[i], 0, addresses[i], stubs[i].Length);
				offset += (stubs[i].Length + 15) & ~15;
			}

			VirtualProtect(memory, (UIntPtr)(uint)size, executeRead, out uint _);

			Dictionary<string, IntPtr> special = new Dictionary<string, IntPtr>
			{
				{ "glCreateProgram", addresses[1] },
				{ "glCreateShader", addresses[1] },
				{ "glCheckFramebufferStatus", addresses[2] },
				{ "glGenVertexArrays", addresses[3] },
				{ "glGenBuffers", addresses[3] },
				{ "glGenTextures", addresses[3] },
				{ "glGenFramebuffers", addresses[3] },
				{ "glGenRenderbuffers", addresses[3] },
				{ "glGenQueries", addresses[3] },
				{ "glGenSamplers", addresses[3] },
				{ "glGetShaderiv", addresses[4] },
				{ "glGetProgramiv", addresses[4] },
				{ "glGetString", addresses[5] },
				{ "glGetStringi", addresses[5] },
				{ "glGetFloatv", addresses[6] }
			};

			for (int i = 0; i < entryPoints.Length && i < offsets.Length; i++)
			{
				int end = offsets[i];
				while (end < names.Length && names[end] != 0)
				{
					end++;
				}

				string name = Encoding.ASCII.GetString(names, offsets[i], end - offsets[i]);
				entryPoints[i] = special.TryGetValue(name, out IntPtr address) ? address : addresses[0];
			}
		}
	}
}
