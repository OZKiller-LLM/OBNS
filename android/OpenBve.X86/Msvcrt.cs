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
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>The Microsoft C runtime (msvcrt.dll) functions plugins import.</summary>
	/// <remarks>All cdecl: the caller removes the arguments, so each is registered with 0 argument bytes.</remarks>
	public sealed partial class Win32Process
	{
		private uint errnoAddress;
		private uint iobAddress;
		private uint randomState = 1;
		private readonly Dictionary<uint, FileStream> crtFiles = new Dictionary<uint, FileStream>();

		private void RegisterMsvcrt()
		{
			const string m = "msvcrt";

			// --- data imports ---
			dataImports[m + "!_iob"] = () => Iob();
			dataImports[m + "!_adjust_fdiv"] = () => AllocateStatic(4);
			dataImports[m + "!__initenv"] = () => AllocateStatic(4);
			dataImports[m + "!_acmdln"] = () => { uint p = AllocateStatic(4); Memory.Write32(p, StaticCString("OpenBve.exe")); return p; };
			dataImports[m + "!__mb_cur_max"] = () => { uint p = AllocateStatic(4); Memory.Write32(p, 1); return p; };
			dataImports[m + "!_pctype"] = () => { uint p = AllocateStatic(4); Memory.Write32(p, CtypeTable() + 2); return p; };
			dataImports[m + "!_environ"] = () => AllocateStatic(8);

			// --- startup and shutdown ---
			Export(m, "__dllonexit", 0, c => c.Arg(0)); // registered handlers run at process exit, which never comes
			Export(m, "_onexit", 0, c => c.Arg(0));
			Export(m, "atexit", 0, c => 0);
			Export(m, "_initterm", 0, c =>
			{
				for (uint p = c.Arg(0); p < c.Arg(1); p += 4)
				{
					uint function = Memory.Read32(p);
					if (function != 0)
					{
						Cpu.Call(function);
					}
				}

				return 0;
			});
			Export(m, "_initterm_e", 0, c =>
			{
				for (uint p = c.Arg(0); p < c.Arg(1); p += 4)
				{
					uint function = Memory.Read32(p);
					if (function != 0)
					{
						uint result = (uint)Cpu.Call(function);
						if (result != 0) return result;
					}
				}

				return 0;
			});
			Export(m, "_amsg_exit", 0, c => throw new X86Exception("the plugin's C runtime reported fatal error " + c.Arg(0)));
			Export(m, "abort", 0, c => throw new X86Exception("the plugin called abort()"));
			Export(m, "exit", 0, c => throw new X86Exception("the plugin called exit(" + c.Arg(0) + ")"));
			Export(m, "_exit", 0, c => throw new X86Exception("the plugin called _exit(" + c.Arg(0) + ")"));
			Export(m, "_assert", 0, c => throw new X86Exception("assertion failed in the plugin: " + Memory.ReadCString(c.Arg(0)) + " (" + Memory.ReadCString(c.Arg(1)) + ":" + c.Arg(2) + ")"));
			Export(m, "_wassert", 0, c => throw new X86Exception("assertion failed in the plugin: " + Memory.ReadWString(c.Arg(0))));
			Export(m, "_errno", 0, c => errnoAddress != 0 ? errnoAddress : errnoAddress = AllocateStatic(4));
			Export(m, "__p__iob", 0, c => Iob());
			Export(m, "__iob_func", 0, c => Iob());
			Export(m, "__set_app_type", 0, c => 0);
			Export(m, "_lock", 0, c => 0);
			Export(m, "_unlock", 0, c => 0);
			Export(m, "_except_handler3", 0, c => 1); // ExceptionContinueSearch: only reached if an exception is raised
			Export(m, "_except_handler4_common", 0, c => 1);
			Export(m, "_XcptFilter", 0, c => 0);
			Export(m, "_CxxThrowException", 0, c => throw new X86Exception("the plugin threw a C++ exception"));
			Export(m, "_purecall", 0, c => throw new X86Exception("the plugin called a pure virtual function"));
			Export(m, "terminate", 0, c => throw new X86Exception("the plugin called terminate()"));
			Export(m, "_controlfp", 0, c => 0x0009001F);
			Export(m, "_control87", 0, c => 0x0009001F);
			Export(m, "_clearfp", 0, c => 0);
			Export(m, "__getmainargs", 0, c => 0);

			// --- memory ---
			Export(m, "malloc", 0, c => HeapAllocate(c.Arg(0), false));
			Export(m, "calloc", 0, c => HeapAllocate(c.Arg(0) * c.Arg(1)));
			Export(m, "realloc", 0, c => HeapReallocate(c.Arg(0), c.Arg(1)));
			Export(m, "free", 0, c => { HeapFree(c.Arg(0)); return 0; });
			Export(m, "_msize", 0, c => HeapBlockSize(c.Arg(0)));
			Export(m, "??2@YAPAXI@Z", 0, c => HeapAllocate(c.Arg(0), false)); // operator new
			Export(m, "??3@YAXPAX@Z", 0, c => { HeapFree(c.Arg(0)); return 0; }); // operator delete
			Export(m, "??_U@YAPAXI@Z", 0, c => HeapAllocate(c.Arg(0), false)); // operator new[]
			Export(m, "??_V@YAXPAX@Z", 0, c => { HeapFree(c.Arg(0)); return 0; }); // operator delete[]
			Export(m, "memset", 0, c => { Memory.Fill(c.Arg(0), (byte)c.Arg(1), c.Arg(2)); return c.Arg(0); });
			Export(m, "memcpy", 0, c => { CopyMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return c.Arg(0); });
			Export(m, "memmove", 0, c => { CopyMemory(c.Arg(0), c.Arg(1), c.Arg(2)); return c.Arg(0); });
			Export(m, "memcmp", 0, c =>
			{
				for (uint i = 0; i < c.Arg(2); i++)
				{
					int d = Memory.Read8(c.Arg(0) + i) - Memory.Read8(c.Arg(1) + i);
					if (d != 0) return (uint)d;
				}

				return 0;
			});
			Export(m, "memchr", 0, c =>
			{
				for (uint i = 0; i < c.Arg(2); i++)
				{
					if (Memory.Read8(c.Arg(0) + i) == (byte)c.Arg(1)) return c.Arg(0) + i;
				}

				return 0;
			});

			// --- strings (byte strings, compared bytewise as the C runtime does) ---
			Export(m, "strlen", 0, c => CLength(c.Arg(0)));
			Export(m, "strcmp", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), uint.MaxValue, false));
			Export(m, "strncmp", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), c.Arg(2), false));
			Export(m, "_stricmp", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), uint.MaxValue, true));
			Export(m, "_strcmpi", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), uint.MaxValue, true));
			Export(m, "_strnicmp", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), c.Arg(2), true));
			Export(m, "strcpy", 0, c => { CopyMemory(c.Arg(0), c.Arg(1), CLength(c.Arg(1)) + 1); return c.Arg(0); });
			Export(m, "strncpy", 0, c =>
			{
				uint n = c.Arg(2), length = Math.Min(CLength(c.Arg(1)), n);
				CopyMemory(c.Arg(0), c.Arg(1), length);
				Memory.Fill(c.Arg(0) + length, 0, n - length);
				return c.Arg(0);
			});
			Export(m, "strcat", 0, c => { CopyMemory(c.Arg(0) + CLength(c.Arg(0)), c.Arg(1), CLength(c.Arg(1)) + 1); return c.Arg(0); });
			Export(m, "strncat", 0, c =>
			{
				uint end = c.Arg(0) + CLength(c.Arg(0));
				uint n = Math.Min(CLength(c.Arg(1)), c.Arg(2));
				CopyMemory(end, c.Arg(1), n);
				Memory.Write8(end + n, 0);
				return c.Arg(0);
			});
			Export(m, "strchr", 0, c =>
			{
				for (uint p = c.Arg(0); ; p++)
				{
					byte b = Memory.Read8(p);
					if (b == (byte)c.Arg(1)) return p;
					if (b == 0) return 0;
				}
			});
			Export(m, "strrchr", 0, c =>
			{
				uint found = 0;
				for (uint p = c.Arg(0); ; p++)
				{
					byte b = Memory.Read8(p);
					if (b == (byte)c.Arg(1)) found = p;
					if (b == 0) return found;
				}
			});
			Export(m, "strstr", 0, c =>
			{
				string haystack = Memory.ReadCString(c.Arg(0));
				int index = haystack.IndexOf(Memory.ReadCString(c.Arg(1)), StringComparison.Ordinal);
				return index < 0 ? 0 : c.Arg(0) + (uint)index;
			});
			Export(m, "_strdup", 0, c =>
			{
				uint length = CLength(c.Arg(0));
				uint copy = HeapAllocate(length + 1);
				CopyMemory(copy, c.Arg(0), length + 1);
				return copy;
			});
			Export(m, "strtol", 0, c => (uint)ParseInteger(c.Arg(0), c.Arg(1), (int)c.Arg(2), true));
			Export(m, "strtoul", 0, c => (uint)ParseInteger(c.Arg(0), c.Arg(1), (int)c.Arg(2), false));
			Export(m, "atoi", 0, c => (uint)ParseInteger(c.Arg(0), 0, 10, true));
			Export(m, "atol", 0, c => (uint)ParseInteger(c.Arg(0), 0, 10, true));
			Export(m, "strtod", 0, c => { c.ReturnDouble(ParseDouble(c.Arg(0), c.Arg(1))); return 0; });
			Export(m, "atof", 0, c => { c.ReturnDouble(ParseDouble(c.Arg(0), 0)); return 0; });
			Export(m, "toupper", 0, c => c.Arg(0) >= 'a' && c.Arg(0) <= 'z' ? c.Arg(0) - 32 : c.Arg(0));
			Export(m, "tolower", 0, c => c.Arg(0) >= 'A' && c.Arg(0) <= 'Z' ? c.Arg(0) + 32 : c.Arg(0));
			Export(m, "isdigit", 0, c => c.Arg(0) >= '0' && c.Arg(0) <= '9' ? 4u : 0u);
			Export(m, "isspace", 0, c => c.Arg(0) == ' ' || (c.Arg(0) >= 9 && c.Arg(0) <= 13) ? 8u : 0u);
			Export(m, "isalpha", 0, c => (c.Arg(0) | 32) >= 'a' && (c.Arg(0) | 32) <= 'z' ? 0x100u : 0u);
			Export(m, "isalnum", 0, c => ((c.Arg(0) | 32) >= 'a' && (c.Arg(0) | 32) <= 'z') || (c.Arg(0) >= '0' && c.Arg(0) <= '9') ? 0x104u : 0u);
			Export(m, "wcslen", 0, c => (uint)(Memory.ReadWString(c.Arg(0))?.Length ?? 0));

			// --- formatted output ---
			Export(m, "sprintf", 0, c => (uint)Memory.WriteCString(c.Arg(0), Format(c.Arg(1), c.R[Cpu.ESP] + 12), AnsiEncoding));
			Export(m, "vsprintf", 0, c => (uint)Memory.WriteCString(c.Arg(0), Format(c.Arg(1), c.Arg(2)), AnsiEncoding));
			Export(m, "_snprintf", 0, c => WriteLimited(c.Arg(0), c.Arg(1), Format(c.Arg(2), c.R[Cpu.ESP] + 16)));
			Export(m, "_vsnprintf", 0, c => WriteLimited(c.Arg(0), c.Arg(1), Format(c.Arg(2), c.Arg(3))));
			Export(m, "printf", 0, c => { string s = Format(c.Arg(0), c.R[Cpu.ESP] + 8); Log("plugin: " + s.TrimEnd()); return (uint)s.Length; });
			Export(m, "vprintf", 0, c => { string s = Format(c.Arg(0), c.Arg(1)); Log("plugin: " + s.TrimEnd()); return (uint)s.Length; });
			Export(m, "fprintf", 0, c => { string s = Format(c.Arg(1), c.R[Cpu.ESP] + 12); WriteStream(c.Arg(0), AnsiEncoding.GetBytes(s)); return (uint)s.Length; });
			Export(m, "vfprintf", 0, c => { string s = Format(c.Arg(1), c.Arg(2)); WriteStream(c.Arg(0), AnsiEncoding.GetBytes(s)); return (uint)s.Length; });
			Export(m, "puts", 0, c => { Log("plugin: " + Memory.ReadCString(c.Arg(0), AnsiEncoding)); return 1; });
			Export(m, "fputs", 0, c => { WriteStream(c.Arg(1), AnsiEncoding.GetBytes(Memory.ReadCString(c.Arg(0), AnsiEncoding))); return 1; });
			Export(m, "fputc", 0, c => { WriteStream(c.Arg(1), new[] { (byte)c.Arg(0) }); return c.Arg(0); });
			Export(m, "fwrite", 0, c =>
			{
				byte[] bytes = new byte[c.Arg(1) * c.Arg(2)];
				Memory.ReadBytes(c.Arg(0), bytes);
				WriteStream(c.Arg(3), bytes);
				return c.Arg(2);
			});
			Export(m, "fflush", 0, c => 0);

			// --- files ---
			Export(m, "fopen", 0, c => CrtOpen(Memory.ReadCString(c.Arg(0), AnsiEncoding), Memory.ReadCString(c.Arg(1))));
			Export(m, "_wfopen", 0, c => CrtOpen(Memory.ReadWString(c.Arg(0)), Memory.ReadWString(c.Arg(1))));
			Export(m, "fclose", 0, c =>
			{
				if (crtFiles.TryGetValue(c.Arg(0), out FileStream stream))
				{
					stream.Dispose();
					crtFiles.Remove(c.Arg(0));
				}

				return 0;
			});
			Export(m, "fread", 0, c =>
			{
				if (!crtFiles.TryGetValue(c.Arg(3), out FileStream stream)) return 0;
				uint size = c.Arg(1), count = c.Arg(2);
				byte[] bytes = new byte[size * count];
				int total = 0;
				while (total < bytes.Length)
				{
					int n = stream.Read(bytes, total, bytes.Length - total);
					if (n <= 0) break;
					total += n;
				}

				Memory.WriteBytes(c.Arg(0), bytes.AsSpan(0, total));
				return size == 0 ? 0 : (uint)total / size;
			});
			Export(m, "fgets", 0, c =>
			{
				if (!crtFiles.TryGetValue(c.Arg(2), out FileStream stream)) return 0;
				int limit = (int)c.Arg(1) - 1;
				List<byte> line = new List<byte>();
				while (line.Count < limit)
				{
					int b = stream.ReadByte();
					if (b < 0) break;
					line.Add((byte)b);
					if (b == '\n') break;
				}

				if (line.Count == 0) return 0;
				Memory.WriteBytes(c.Arg(0), line.ToArray());
				Memory.Write8(c.Arg(0) + (uint)line.Count, 0);
				return c.Arg(0);
			});
			Export(m, "fgetc", 0, c => crtFiles.TryGetValue(c.Arg(0), out FileStream stream) ? (uint)stream.ReadByte() : 0xFFFFFFFF);
			Export(m, "fseek", 0, c =>
			{
				if (!crtFiles.TryGetValue(c.Arg(0), out FileStream stream)) return 0xFFFFFFFF;
				stream.Seek((int)c.Arg(1), (SeekOrigin)c.Arg(2));
				return 0;
			});
			Export(m, "ftell", 0, c => crtFiles.TryGetValue(c.Arg(0), out FileStream stream) ? (uint)stream.Position : 0xFFFFFFFF);
			Export(m, "feof", 0, c => crtFiles.TryGetValue(c.Arg(0), out FileStream stream) && stream.Position >= stream.Length ? 1u : 0u);
			Export(m, "rewind", 0, c => { if (crtFiles.TryGetValue(c.Arg(0), out FileStream stream)) stream.Position = 0; return 0; });

			// --- maths: doubles on the stack, results in ST(0) ---
			Export(m, "pow", 0, c => { c.ReturnDouble(Math.Pow(c.ArgDouble(0), c.ArgDouble(8))); return 0; });
			Export(m, "sqrt", 0, c => { c.ReturnDouble(Math.Sqrt(c.ArgDouble(0))); return 0; });
			Export(m, "sin", 0, c => { c.ReturnDouble(Math.Sin(c.ArgDouble(0))); return 0; });
			Export(m, "cos", 0, c => { c.ReturnDouble(Math.Cos(c.ArgDouble(0))); return 0; });
			Export(m, "tan", 0, c => { c.ReturnDouble(Math.Tan(c.ArgDouble(0))); return 0; });
			Export(m, "asin", 0, c => { c.ReturnDouble(Math.Asin(c.ArgDouble(0))); return 0; });
			Export(m, "acos", 0, c => { c.ReturnDouble(Math.Acos(c.ArgDouble(0))); return 0; });
			Export(m, "atan", 0, c => { c.ReturnDouble(Math.Atan(c.ArgDouble(0))); return 0; });
			Export(m, "atan2", 0, c => { c.ReturnDouble(Math.Atan2(c.ArgDouble(0), c.ArgDouble(8))); return 0; });
			Export(m, "exp", 0, c => { c.ReturnDouble(Math.Exp(c.ArgDouble(0))); return 0; });
			Export(m, "log", 0, c => { c.ReturnDouble(Math.Log(c.ArgDouble(0))); return 0; });
			Export(m, "log10", 0, c => { c.ReturnDouble(Math.Log10(c.ArgDouble(0))); return 0; });
			Export(m, "floor", 0, c => { c.ReturnDouble(Math.Floor(c.ArgDouble(0))); return 0; });
			Export(m, "ceil", 0, c => { c.ReturnDouble(Math.Ceiling(c.ArgDouble(0))); return 0; });
			Export(m, "fabs", 0, c => { c.ReturnDouble(Math.Abs(c.ArgDouble(0))); return 0; });
			Export(m, "fmod", 0, c => { c.ReturnDouble(c.ArgDouble(0) % c.ArgDouble(8)); return 0; });
			Export(m, "ldexp", 0, c => { c.ReturnDouble(c.ArgDouble(0) * Math.Pow(2, (int)Memory.Read32(c.R[Cpu.ESP] + 12))); return 0; });
			Export(m, "abs", 0, c => (uint)Math.Abs((int)c.Arg(0)));
			Export(m, "labs", 0, c => (uint)Math.Abs((int)c.Arg(0)));
			// The _CI* helpers take their operands on the x87 stack instead.
			Export(m, "_CIpow", 0, c => { double y = c.PopST0(); double x = c.PopST0(); c.ReturnDouble(Math.Pow(x, y)); return 0; });
			Export(m, "_CIsqrt", 0, c => { c.ReturnDouble(Math.Sqrt(c.PopST0())); return 0; });
			Export(m, "_CIsin", 0, c => { c.ReturnDouble(Math.Sin(c.PopST0())); return 0; });
			Export(m, "_CIcos", 0, c => { c.ReturnDouble(Math.Cos(c.PopST0())); return 0; });
			Export(m, "_CItan", 0, c => { c.ReturnDouble(Math.Tan(c.PopST0())); return 0; });
			Export(m, "_CIatan", 0, c => { c.ReturnDouble(Math.Atan(c.PopST0())); return 0; });
			Export(m, "_CIatan2", 0, c => { double x = c.PopST0(); double y = c.PopST0(); c.ReturnDouble(Math.Atan2(y, x)); return 0; });
			Export(m, "_CIexp", 0, c => { c.ReturnDouble(Math.Exp(c.PopST0())); return 0; });
			Export(m, "_CIlog", 0, c => { c.ReturnDouble(Math.Log(c.PopST0())); return 0; });
			Export(m, "_CIlog10", 0, c => { c.ReturnDouble(Math.Log10(c.PopST0())); return 0; });
			Export(m, "_CIfmod", 0, c => { double y = c.PopST0(); double x = c.PopST0(); c.ReturnDouble(x % y); return 0; });
			Export(m, "_CIasin", 0, c => { c.ReturnDouble(Math.Asin(c.PopST0())); return 0; });
			Export(m, "_CIacos", 0, c => { c.ReturnDouble(Math.Acos(c.PopST0())); return 0; });
			Export(m, "_ftol", 0, c =>
			{
				long value = (long)Math.Truncate(c.PopST0());
				return (ulong)value;
			});

			// --- other ---
			Export(m, "rand", 0, c =>
			{
				randomState = randomState * 214013 + 2531011;
				return (randomState >> 16) & 0x7FFF;
			});
			Export(m, "srand", 0, c => { randomState = c.Arg(0); return 0; });
			Export(m, "time", 0, c =>
			{
				uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
				if (c.Arg(0) != 0) Memory.Write32(c.Arg(0), now);
				return now;
			});
			Export(m, "clock", 0, c => (uint)clock.ElapsedMilliseconds);
			Export(m, "qsort", 0, c =>
			{
				// Insertion sort through the plugin's comparer: small arrays, and order must match a stable-enough sort.
				uint base_ = c.Arg(0), count = c.Arg(1), width = c.Arg(2), compare = c.Arg(3);
				byte[] temp = new byte[width];
				for (uint i = 1; i < count; i++)
				{
					for (uint j = i; j > 0; j--)
					{
						uint a = base_ + (j - 1) * width, b = base_ + j * width;
						if ((int)(uint)Cpu.Call(compare, a, b) <= 0) break;
						Memory.ReadBytes(a, temp);
						CopyMemory(a, b, width);
						Memory.WriteBytes(b, temp);
					}
				}

				return 0;
			});
		}

		// --- helpers ---

		private uint Iob()
		{
			if (iobAddress == 0)
			{
				// stdin, stdout, stderr: FILE is 32 bytes in msvcrt; nothing reads their contents.
				iobAddress = AllocateStatic(32 * 20);
			}

			return iobAddress;
		}

		private uint ctypeTable;

		private uint CtypeTable()
		{
			if (ctypeTable != 0)
			{
				return ctypeTable;
			}

			ctypeTable = AllocateStatic(2 * 257);
			for (int i = 0; i < 256; i++)
			{
				char ch = (char)i;
				ushort t = 0;
				if (ch >= 'A' && ch <= 'Z') t |= 0x001;
				if (ch >= 'a' && ch <= 'z') t |= 0x002;
				if (ch >= '0' && ch <= '9') t |= 0x004;
				if (ch == ' ' || (ch >= 9 && ch <= 13)) t |= 0x008;
				if (i >= 33 && i < 127 && !char.IsLetterOrDigit(ch)) t |= 0x010;
				if (i < 32 || i == 127) t |= 0x020;
				if (ch == ' ') t |= 0x040;
				if (Uri.IsHexDigit(ch)) t |= 0x080;
				if (char.IsLetter(ch) && i < 128) t |= 0x100;
				Memory.Write16(ctypeTable + 2 + (uint)(2 * i), t);
			}

			return ctypeTable;
		}

		private void CopyMemory(uint destination, uint source, uint count)
		{
			if (count == 0)
			{
				return;
			}

			byte[] bytes = new byte[count];
			Memory.ReadBytes(source, bytes);
			Memory.WriteBytes(destination, bytes);
		}

		private uint CLength(uint address)
		{
			uint n = 0;
			while (Memory.Read8(address + n) != 0) n++;
			return n;
		}

		private int CCompare(uint a, uint b, uint limit, bool ignoreCase)
		{
			for (uint i = 0; i < limit; i++)
			{
				int x = Memory.Read8(a + i), y = Memory.Read8(b + i);
				if (ignoreCase)
				{
					if (x >= 'A' && x <= 'Z') x += 32;
					if (y >= 'A' && y <= 'Z') y += 32;
				}

				if (x != y) return x - y;
				if (x == 0) return 0;
			}

			return 0;
		}

		private long ParseInteger(uint text, uint end, int radix, bool signed)
		{
			uint p = text;
			while (Memory.Read8(p) == ' ' || (Memory.Read8(p) >= 9 && Memory.Read8(p) <= 13)) p++;
			bool negative = false;
			if (Memory.Read8(p) == '-' || Memory.Read8(p) == '+')
			{
				negative = Memory.Read8(p) == '-';
				p++;
			}

			if ((radix == 0 || radix == 16) && Memory.Read8(p) == '0' && (Memory.Read8(p + 1) | 32) == 'x')
			{
				radix = 16;
				p += 2;
			}
			else if (radix == 0)
			{
				radix = Memory.Read8(p) == '0' ? 8 : 10;
			}

			long value = 0;
			uint start = p;
			while (true)
			{
				int ch = Memory.Read8(p);
				int digit = ch >= '0' && ch <= '9' ? ch - '0' : (ch | 32) >= 'a' && (ch | 32) <= 'z' ? (ch | 32) - 'a' + 10 : 99;
				if (digit >= radix) break;
				value = value * radix + digit;
				if (value > 0xFFFFFFFFL) value = 0xFFFFFFFFL + 1;
				p++;
			}

			if (end != 0) Memory.Write32(end, p == start ? text : p);
			if (negative) value = -value;
			if (signed)
			{
				return Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
			}

			return (long)(uint)value;
		}

		private double ParseDouble(uint text, uint end)
		{
			string s = Memory.ReadCString(text, Encoding.Latin1, 4096);
			int i = 0;
			while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
			int start = i;
			if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
			while (i < s.Length && char.IsDigit(s[i])) i++;
			if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
			if (i < s.Length && (s[i] | 32) == 'e')
			{
				int e = i + 1;
				if (e < s.Length && (s[e] == '+' || s[e] == '-')) e++;
				if (e < s.Length && char.IsDigit(s[e])) { i = e; while (i < s.Length && char.IsDigit(s[i])) i++; }
			}

			double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
			if (end != 0) Memory.Write32(end, i == start ? text : text + (uint)i);
			return value;
		}

		private uint WriteLimited(uint buffer, uint size, string text)
		{
			byte[] bytes = AnsiEncoding.GetBytes(text);
			if (bytes.Length < size)
			{
				Memory.WriteBytes(buffer, bytes);
				Memory.Write8(buffer + (uint)bytes.Length, 0);
				return (uint)bytes.Length;
			}

			Memory.WriteBytes(buffer, bytes.AsSpan(0, (int)size));
			return 0xFFFFFFFF;
		}

		private void WriteStream(uint file, byte[] bytes)
		{
			if (crtFiles.TryGetValue(file, out FileStream stream) && stream.CanWrite)
			{
				stream.Write(bytes, 0, bytes.Length);
			}
			else
			{
				Log("plugin: " + AnsiEncoding.GetString(bytes).TrimEnd());
			}
		}

		private uint CrtOpen(string name, string mode)
		{
			string host = ToHostPath(name);
			if (host == null)
			{
				return 0;
			}

			bool write = mode.Contains('w') || mode.Contains('a') || mode.Contains('+');
			if (!write && !File.Exists(host))
			{
				return 0;
			}

			try
			{
				FileStream stream = new FileStream(host, mode.Contains('w') ? FileMode.Create : mode.Contains('a') ? FileMode.Append : FileMode.Open,
					write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
				uint file = AllocateStatic(32);
				crtFiles[file] = stream;
				return file;
			}
			catch (Exception)
			{
				return 0;
			}
		}

		/// <summary>printf formatting, reading arguments from emulated memory in order.</summary>
		private string Format(uint format, uint args)
		{
			string f = Memory.ReadCString(format, AnsiEncoding);
			StringBuilder output = new StringBuilder();
			uint next = args;
			for (int i = 0; i < f.Length; i++)
			{
				char ch = f[i];
				if (ch != '%')
				{
					output.Append(ch);
					continue;
				}

				if (++i >= f.Length) break;
				if (f[i] == '%')
				{
					output.Append('%');
					continue;
				}

				// Flags, width, precision, length.
				bool left = false, plus = false, space = false, zero = false, alternate = false;
				for (; i < f.Length; i++)
				{
					if (f[i] == '-') left = true;
					else if (f[i] == '+') plus = true;
					else if (f[i] == ' ') space = true;
					else if (f[i] == '0') zero = true;
					else if (f[i] == '#') alternate = true;
					else break;
				}

				int width = 0;
				if (i < f.Length && f[i] == '*')
				{
					width = (int)Memory.Read32(next);
					next += 4;
					i++;
				}
				else
				{
					while (i < f.Length && char.IsDigit(f[i])) width = width * 10 + (f[i++] - '0');
				}

				int precision = -1;
				if (i < f.Length && f[i] == '.')
				{
					i++;
					precision = 0;
					if (i < f.Length && f[i] == '*')
					{
						precision = (int)Memory.Read32(next);
						next += 4;
						i++;
					}
					else
					{
						while (i < f.Length && char.IsDigit(f[i])) precision = precision * 10 + (f[i++] - '0');
					}
				}

				bool wide64 = false;
				while (i < f.Length && "hlLIqjzt".IndexOf(f[i]) >= 0)
				{
					if (f[i] == 'I' && i + 2 < f.Length && f[i + 1] == '6' && f[i + 2] == '4')
					{
						wide64 = true;
						i += 3;
						continue;
					}

					if (f[i] == 'l' && i + 1 < f.Length && f[i + 1] == 'l')
					{
						wide64 = true;
						i++;
					}

					i++;
				}

				if (i >= f.Length) break;
				char conversion = f[i];
				string text;
				bool numeric = true;
				switch (conversion)
				{
					case 'd':
					case 'i':
					{
						long v = wide64 ? (long)Memory.Read64(next) : (int)Memory.Read32(next);
						next += wide64 ? 8u : 4u;
						text = Math.Abs(v).ToString(CultureInfo.InvariantCulture);
						if (precision >= 0) text = text.PadLeft(precision, '0');
						text = (v < 0 ? "-" : plus ? "+" : space ? " " : string.Empty) + text;
						break;
					}
					case 'u':
					case 'x':
					case 'X':
					case 'o':
					{
						ulong v = wide64 ? Memory.Read64(next) : Memory.Read32(next);
						next += wide64 ? 8u : 4u;
						text = conversion == 'u' ? v.ToString(CultureInfo.InvariantCulture)
							: conversion == 'o' ? Convert.ToString((long)v, 8)
							: v.ToString(conversion == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
						if (precision >= 0) text = text.PadLeft(precision, '0');
						if (alternate && v != 0 && conversion != 'u') text = (conversion == 'o' ? "0" : conversion == 'x' ? "0x" : "0X") + text;
						break;
					}
					case 'f':
					case 'F':
					case 'e':
					case 'E':
					case 'g':
					case 'G':
					{
						double v = Memory.ReadDouble(next);
						next += 8;
						text = FormatDouble(v, conversion, precision < 0 ? 6 : precision, alternate);
						if (v >= 0 && !double.IsNaN(v)) text = (plus ? "+" : space ? " " : string.Empty) + text;
						break;
					}
					case 'c':
						text = ((char)(byte)Memory.Read32(next)).ToString();
						next += 4;
						numeric = false;
						break;
					case 's':
					{
						uint p = Memory.Read32(next);
						next += 4;
						text = p == 0 ? "(null)" : Memory.ReadCString(p, AnsiEncoding);
						if (precision >= 0 && text.Length > precision) text = text.Substring(0, precision);
						numeric = false;
						break;
					}
					case 'S':
					{
						uint p = Memory.Read32(next);
						next += 4;
						text = p == 0 ? "(null)" : Memory.ReadWString(p);
						numeric = false;
						break;
					}
					case 'p':
						text = Memory.Read32(next).ToString("X8");
						next += 4;
						break;
					case 'n':
						Memory.Write32(Memory.Read32(next), (uint)output.Length);
						next += 4;
						continue;
					default:
						text = "%" + conversion;
						numeric = false;
						break;
				}

				if (text.Length < width)
				{
					if (left)
					{
						text = text.PadRight(width);
					}
					else if (zero && numeric && precision < 0)
					{
						int signLength = text.Length > 0 && (text[0] == '-' || text[0] == '+' || text[0] == ' ') ? 1 : 0;
						text = text.Substring(0, signLength) + text.Substring(signLength).PadLeft(width - signLength, '0');
					}
					else
					{
						text = text.PadLeft(width);
					}
				}

				output.Append(text);
			}

			return output.ToString();
		}

		private static string FormatDouble(double value, char conversion, int precision, bool alternate)
		{
			if (double.IsNaN(value)) return "-1.#IND00";
			if (double.IsPositiveInfinity(value)) return "1.#INF00";
			if (double.IsNegativeInfinity(value)) return "-1.#INF00";
			switch (conversion)
			{
				case 'f':
				case 'F':
					return value.ToString("F" + precision, CultureInfo.InvariantCulture);
				case 'e':
				case 'E':
				{
					// msvcrt prints three exponent digits.
					string s = value.ToString((conversion == 'e' ? "e" : "E") + precision, CultureInfo.InvariantCulture);
					return s;
				}
				default:
				{
					int p = precision == 0 ? 1 : precision;
					if (value == 0) return "0";
					int exponent = (int)Math.Floor(Math.Log10(Math.Abs(value)));
					string s = exponent < -4 || exponent >= p
						? value.ToString((conversion == 'g' ? "e" : "E") + (p - 1), CultureInfo.InvariantCulture)
						: value.ToString("F" + Math.Max(0, p - 1 - exponent), CultureInfo.InvariantCulture);
					if (!alternate && s.Contains('.'))
					{
						int e = s.IndexOfAny(new[] { 'e', 'E' });
						string mantissa = e >= 0 ? s.Substring(0, e) : s;
						string rest = e >= 0 ? s.Substring(e) : string.Empty;
						mantissa = mantissa.TrimEnd('0').TrimEnd('.');
						s = mantissa + rest;
					}

					return s;
				}
			}
		}
	}
}
