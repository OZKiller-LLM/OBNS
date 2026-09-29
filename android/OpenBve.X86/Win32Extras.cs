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
using System.Security.Cryptography;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>
	/// The less common imports found by surveying a library of real train plugins
	/// (OpenBve.X86.Tests survey): pointer checks, locale and date formatting, INI writing, the
	/// Visual Studio 2015+ runtime (UCRT API sets, vcruntime140, msvcp140) and some C library
	/// odds and ends.
	/// </summary>
	public sealed partial class Win32Process
	{
		private readonly Dictionary<uint, List<uint>> onexitTables = new Dictionary<uint, List<uint>>();
		private uint currentExceptionAddress, localeConvAddress, localeNameAddress;

		private void RegisterExtras()
		{
			const string k = "kernel32";
			const string m = "msvcrt";

			// --- pointer probes: true (1) when the range is NOT fully readable/writable ---
			Export(k, "IsBadReadPtr", 8, c => RangeMapped(c.Arg(0), c.Arg(1)) ? 0u : 1u);
			Export(k, "IsBadWritePtr", 8, c => RangeMapped(c.Arg(0), c.Arg(1)) ? 0u : 1u);
			Export(k, "IsBadCodePtr", 4, c => RangeMapped(c.Arg(0), 1) ? 0u : 1u);
			Export(k, "IsBadStringPtrA", 8, c => RangeMapped(c.Arg(0), 1) ? 0u : 1u);
			Export(k, "IsBadStringPtrW", 8, c => RangeMapped(c.Arg(0), 2) ? 0u : 1u);

			// --- process odds and ends ---
			Export(k, "SetConsoleCtrlHandler", 8, c => 1);
			Export(k, "ReadConsoleW", 20, c => 0);
			Export(k, "ReadConsoleA", 20, c => 0);
			Export(k, "DebugBreak", 0, c => throw new X86Exception("the plugin hit a breakpoint (DebugBreak)"));
			Export(k, "FatalAppExitA", 8, c => throw new X86Exception("the plugin ended with a fatal error: " + Memory.ReadCString(c.Arg(1), AnsiEncoding)));
			Export(k, "FatalAppExitW", 8, c => throw new X86Exception("the plugin ended with a fatal error: " + Memory.ReadWString(c.Arg(1))));
			Export(k, "HeapQueryInformation", 20, c => 0);
			Export(k, "GetSystemInfo", 4, c => { SystemInfo(c.Arg(0)); return 0; });
			Export(k, "GetNativeSystemInfo", 4, c => { SystemInfo(c.Arg(0)); return 0; });
			Export(k, "InterlockedPushEntrySList", 8, c =>
			{
				uint head = c.Arg(0), entry = c.Arg(1);
				uint first = Memory.Read32(head);
				Memory.Write32(entry, first);
				Memory.Write32(head, entry);
				Memory.Write16(head + 4, (ushort)(Memory.Read16(head + 4) + 1));
				return first;
			});
			Export(k, "InterlockedPopEntrySList", 4, c =>
			{
				uint head = c.Arg(0);
				uint first = Memory.Read32(head);
				if (first != 0)
				{
					Memory.Write32(head, Memory.Read32(first));
					Memory.Write16(head + 4, (ushort)(Memory.Read16(head + 4) - 1));
				}

				return first;
			});

			// --- locales and dates: one locale, English (United States) ---
			Export(k, "EnumSystemLocalesA", 8, c => { EnumLocales(c.Arg(0), false); return 1; });
			Export(k, "EnumSystemLocalesW", 8, c => { EnumLocales(c.Arg(0), true); return 1; });
			Export(k, "GetLocaleInfoA", 16, c => LocaleInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false));
			Export(k, "GetLocaleInfoW", 16, c => LocaleInfo(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), true));
			Export(k, "GetSystemDefaultLCID", 0, c => 0x0409);
			Export(k, "GetUserDefaultUILanguage", 0, c => 0x0409);
			Export(k, "GetSystemDefaultLangID", 0, c => 0x0409);
			Export(k, "GetDateFormatA", 24, c => FormatDateTime(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), false, true, c.Arg(1)));
			Export(k, "GetDateFormatW", 24, c => FormatDateTime(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), true, true, c.Arg(1)));
			Export(k, "GetTimeFormatA", 24, c => FormatDateTime(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), false, false, c.Arg(1)));
			Export(k, "GetTimeFormatW", 24, c => FormatDateTime(c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), true, false, c.Arg(1)));

			// --- INI files: wide reads, and writes (confined to the root folder like every file) ---
			Export(k, "GetPrivateProfileStringW", 24, c =>
			{
				string fallback = Memory.ReadWString(c.Arg(2)) ?? string.Empty;
				string value = ReadIni(Memory.ReadWString(c.Arg(5)), Memory.ReadWString(c.Arg(0)), Memory.ReadWString(c.Arg(1))) ?? fallback;
				return WriteString(c.Arg(3), c.Arg(4), value, true);
			});
			Export(k, "GetPrivateProfileIntW", 16, c =>
			{
				string value = ReadIni(Memory.ReadWString(c.Arg(3)), Memory.ReadWString(c.Arg(0)), Memory.ReadWString(c.Arg(1)));
				return value != null && int.TryParse(value.Trim(), out int number) ? (uint)number : c.Arg(2);
			});
			Export(k, "WritePrivateProfileStringA", 16, c => WriteIni(Memory.ReadCString(c.Arg(3), AnsiEncoding), Memory.ReadCString(c.Arg(0), AnsiEncoding),
				c.Arg(1) == 0 ? null : Memory.ReadCString(c.Arg(1), AnsiEncoding), c.Arg(2) == 0 ? null : Memory.ReadCString(c.Arg(2), AnsiEncoding)) ? 1u : 0u);
			Export(k, "WritePrivateProfileStringW", 16, c => WriteIni(Memory.ReadWString(c.Arg(3)), Memory.ReadWString(c.Arg(0)),
				c.Arg(1) == 0 ? null : Memory.ReadWString(c.Arg(1)), c.Arg(2) == 0 ? null : Memory.ReadWString(c.Arg(2))) ? 1u : 0u);

			// --- other system DLLs ---
			Export("advapi32", "SystemFunction036", 8, c => // RtlGenRandom
			{
				byte[] bytes = new byte[c.Arg(1)];
				RandomNumberGenerator.Fill(bytes);
				Memory.WriteBytes(c.Arg(0), bytes);
				return 1;
			});
			Export("winmm", "joyGetPos", 8, c => 167); // JOYERR_UNPLUGGED: no joystick
			Export("winmm", "joyGetPosEx", 8, c => 167);
			Export("winmm", "joyGetNumDevs", 0, c => 0);

			// --- Visual Studio 2015+ runtime start-up and shut-down (UCRT, vcruntime140) ---
			Export(m, "_initialize_onexit_table", 0, c => { Memory.Fill(c.Arg(0), 0, 12); onexitTables[c.Arg(0)] = new List<uint>(); return 0; });
			Export(m, "_register_onexit_function", 0, c =>
			{
				if (!onexitTables.TryGetValue(c.Arg(0), out List<uint> table))
				{
					onexitTables[c.Arg(0)] = table = new List<uint>();
				}

				table.Add(c.Arg(1));
				return 0;
			});
			Export(m, "_execute_onexit_table", 0, c =>
			{
				if (onexitTables.TryGetValue(c.Arg(0), out List<uint> table))
				{
					onexitTables.Remove(c.Arg(0));
					for (int i = table.Count - 1; i >= 0; i--)
					{
						Cpu.Call(table[i]);
					}
				}

				return 0;
			});
			Export(m, "_crt_atexit", 0, c => 0);
			Export(m, "_crt_at_quick_exit", 0, c => 0);
			Export(m, "_cexit", 0, c => 0);
			Export(m, "_c_exit", 0, c => 0);
			Export(m, "_configure_narrow_argv", 0, c => 0);
			Export(m, "_configure_wide_argv", 0, c => 0);
			Export(m, "_initialize_narrow_environment", 0, c => 0);
			Export(m, "_initialize_wide_environment", 0, c => 0);
			Export(m, "_seh_filter_dll", 0, c => 0); // EXCEPTION_CONTINUE_SEARCH
			Export(m, "_seh_filter_exe", 0, c => 0);
			Export(m, "_callnewh", 0, c => 0);
			Export(m, "_invalid_parameter_noinfo", 0, c => 0);
			Export(m, "_invalid_parameter_noinfo_noreturn", 0, c => throw new X86Exception("the plugin's C runtime reported an invalid parameter"));
			Export(m, "__std_type_info_destroy_list", 0, c => 0);
			Export(m, "__current_exception", 0, c => CurrentExceptionSlot());
			Export(m, "__current_exception_context", 0, c => CurrentExceptionSlot() + 4);
			Export(m, "__CxxFrameHandler3", 0, c => 1); // ExceptionContinueSearch: only reached if an exception is raised
			Export(m, "__CxxFrameHandler4", 0, c => 1);
			Export(m, "__std_terminate", 0, c => throw new X86Exception("the plugin called std::terminate"));
			Export(m, "__std_exception_copy", 0, c =>
			{
				// struct __std_exception_data { const char* _What; bool _DoFree; }
				uint what = Memory.Read32(c.Arg(0));
				bool free = Memory.Read8(c.Arg(0) + 4) != 0;
				if (free && what != 0)
				{
					uint length = CLength(what);
					uint copy = HeapAllocate(length + 1);
					CopyMemory(copy, what, length + 1);
					what = copy;
				}

				Memory.Write32(c.Arg(1), what);
				Memory.Write8(c.Arg(1) + 4, (byte)(free ? 1 : 0));
				return 0;
			});
			Export(m, "__std_exception_destroy", 0, c =>
			{
				if (Memory.Read8(c.Arg(0) + 4) != 0) HeapFree(Memory.Read32(c.Arg(0)));
				Memory.Write32(c.Arg(0), 0);
				Memory.Write8(c.Arg(0) + 4, 0);
				return 0;
			});
			foreach (string thrower in new[] { "?_Xbad_function_call@std@@YAXXZ", "?_Xinvalid_argument@std@@YAXPBD@Z", "?_Xlength_error@std@@YAXPBD@Z", "?_Xout_of_range@std@@YAXPBD@Z", "?_Xbad_alloc@std@@YAXXZ", "?_Xruntime_error@std@@YAXPBD@Z" })
			{
				string name = thrower;
				Export("msvcp140", name, 0, c => throw new X86Exception("the plugin threw a C++ standard library exception (" + name.Substring(2, name.IndexOf('@') - 2) + ")"));
			}

			// --- maths: the SSE2 "precise" helpers take and return their values in XMM registers ---
			Export(m, "_libm_sse2_pow_precise", 0, c => { c.SetXmmLowDouble(0, Math.Pow(c.GetXmmLowDouble(0), c.GetXmmLowDouble(1))); return 0; });
			Export(m, "_libm_sse2_sqrt_precise", 0, c => { c.SetXmmLowDouble(0, Math.Sqrt(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_log_precise", 0, c => { c.SetXmmLowDouble(0, Math.Log(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_log10_precise", 0, c => { c.SetXmmLowDouble(0, Math.Log10(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_exp_precise", 0, c => { c.SetXmmLowDouble(0, Math.Exp(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_sin_precise", 0, c => { c.SetXmmLowDouble(0, Math.Sin(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_cos_precise", 0, c => { c.SetXmmLowDouble(0, Math.Cos(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_tan_precise", 0, c => { c.SetXmmLowDouble(0, Math.Tan(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_libm_sse2_atan_precise", 0, c => { c.SetXmmLowDouble(0, Math.Atan(c.GetXmmLowDouble(0))); return 0; });
			Export(m, "_dclass", 0, c =>
			{
				double x = c.ArgDouble(0);
				// FP_ZERO 0, FP_INFINITE 1, FP_NAN 2, FP_NORMAL -1, FP_SUBNORMAL -2
				return double.IsNaN(x) ? 2u : double.IsInfinity(x) ? 1u : x == 0 ? 0u : double.IsSubnormal(x) ? unchecked((uint)-2) : unchecked((uint)-1);
			});
			Export(m, "round", 0, c => { c.ReturnDouble(Math.Round(c.ArgDouble(0), MidpointRounding.AwayFromZero)); return 0; });
			Export(m, "roundf", 0, c => { c.ReturnDouble(MathF.Round(BitConverter.Int32BitsToSingle((int)c.Arg(0)), MidpointRounding.AwayFromZero)); return 0; });
			Export(m, "nextafterf", 0, c =>
			{
				float x = BitConverter.Int32BitsToSingle((int)c.Arg(0)), y = BitConverter.Int32BitsToSingle((int)c.Arg(1));
				c.ReturnDouble(x == y ? y : x < y ? MathF.BitIncrement(x) : MathF.BitDecrement(x));
				return 0;
			});
			Export(m, "nextafter", 0, c =>
			{
				double x = c.ArgDouble(0), y = c.ArgDouble(8);
				c.ReturnDouble(x == y ? y : x < y ? Math.BitIncrement(x) : Math.BitDecrement(x));
				return 0;
			});

			// --- wide strings ---
			Export(m, "wcstod", 0, c =>
			{
				string text = Memory.ReadWString(c.Arg(0)) ?? string.Empty;
				int used = NumberPrefix(text, true);
				double.TryParse(text.Substring(0, used).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
				if (c.Arg(1) != 0) Memory.Write32(c.Arg(1), c.Arg(0) + (uint)(2 * used));
				c.ReturnDouble(value);
				return 0;
			});
			Export(m, "wcstol", 0, c =>
			{
				string text = Memory.ReadWString(c.Arg(0)) ?? string.Empty;
				int used = NumberPrefix(text, false);
				long.TryParse(text.Substring(0, used).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value);
				if (c.Arg(1) != 0) Memory.Write32(c.Arg(1), c.Arg(0) + (uint)(2 * used));
				return (uint)(int)Math.Clamp(value, int.MinValue, int.MaxValue);
			});
			Export(m, "iswblank", 0, c => c.Arg(0) == ' ' || c.Arg(0) == '\t' ? 0x40u : 0u);
			Export(m, "iswdigit", 0, c => c.Arg(0) >= '0' && c.Arg(0) <= '9' ? 4u : 0u);
			Export(m, "iswspace", 0, c => char.IsWhiteSpace((char)c.Arg(0)) ? 8u : 0u);
			Export(m, "iswalpha", 0, c => char.IsLetter((char)c.Arg(0)) ? 0x103u : 0u);

			// --- C library odds and ends ---
			dataImports[m + "!_ctype"] = () => CtypeTable();
			Export(m, "_isctype", 0, c => c.Arg(0) < 256 ? Memory.Read16(CtypeTable() + 2 + 2 * c.Arg(0)) & c.Arg(1) : 0u);
			Export(m, "getc", 0, c => crtFiles.TryGetValue(c.Arg(0), out FileStream stream) ? (uint)stream.ReadByte() : 0xFFFFFFFF);
			Export(m, "putc", 0, c => { WriteStream(c.Arg(1), new[] { (byte)c.Arg(0) }); return c.Arg(0); });
			Export(m, "ungetc", 0, c =>
			{
				if (c.Arg(0) == 0xFFFFFFFF || !crtFiles.TryGetValue(c.Arg(1), out FileStream stream) || stream.Position == 0) return 0xFFFFFFFF;
				stream.Position--;
				return c.Arg(0);
			});
			Export(m, "fgetpos", 0, c =>
			{
				if (!crtFiles.TryGetValue(c.Arg(0), out FileStream stream)) return 0xFFFFFFFF;
				Memory.Write64(c.Arg(1), (ulong)stream.Position);
				return 0;
			});
			Export(m, "fsetpos", 0, c =>
			{
				if (!crtFiles.TryGetValue(c.Arg(0), out FileStream stream)) return 0xFFFFFFFF;
				stream.Position = (long)Memory.Read64(c.Arg(1));
				return 0;
			});
			Export(m, "setvbuf", 0, c => 0);
			Export(m, "setlocale", 0, c => localeNameAddress != 0 ? localeNameAddress : localeNameAddress = StaticCString("C"));
			Export(m, "localeconv", 0, c =>
			{
				if (localeConvAddress == 0)
				{
					// struct lconv: decimal_point, thousands_sep, grouping, then currency fields.
					localeConvAddress = AllocateStatic(96);
					Memory.Write32(localeConvAddress, StaticCString("."));
					for (uint i = 4; i < 40; i += 4) Memory.Write32(localeConvAddress + i, StaticCString(string.Empty));
				}

				return localeConvAddress;
			});
			Export(m, "strcoll", 0, c => (uint)CCompare(c.Arg(0), c.Arg(1), uint.MaxValue, false));
			Export(m, "strxfrm", 0, c =>
			{
				uint length = CLength(c.Arg(1));
				if (c.Arg(2) > length) CopyMemory(c.Arg(0), c.Arg(1), length + 1);
				return length;
			});
			Export(m, "strftime", 0, c => StrFormatTime(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
			// Low-level file descriptors: plugins seen only reach these through stdio, which never
			// hands out a descriptor here, so they report failure as an unopened descriptor would.
			Export(m, "_read", 0, c => 0xFFFFFFFF);
			Export(m, "_write", 0, c => c.Arg(0) == 1 || c.Arg(0) == 2 ? c.Arg(2) : 0xFFFFFFFF);
			Export(m, "_lseeki64", 0, c => ulong.MaxValue);
			Export(m, "_filelengthi64", 0, c => ulong.MaxValue);
			Export(m, "_fstati64", 0, c => 0xFFFFFFFF);
			Export(m, "_fdopen", 0, c => 0);
		}

		/*
		 * The locales the emulated Windows knows: the ones BVE content is written for. Static C
		 * runtimes' setlocale("japanese") and the like enumerate the system's locales and match
		 * the English language and country names, so each needs its names and code pages.
		 * Columns: LCID, ANSI code page, OEM code page, English language, English country,
		 * Windows language abbreviation, Windows country abbreviation, ISO language, ISO country.
		 */
		private static readonly (uint Lcid, int Ansi, int Oem, string Language, string Country, string Lang3, string Country3, string Iso639, string Iso3166)[] Locales =
		{
			(0x0409, 1252, 437, "English", "United States", "ENU", "USA", "en", "US"),
			(0x0809, 1252, 850, "English", "United Kingdom", "ENG", "GBR", "en", "GB"),
			(0x0411, 932, 932, "Japanese", "Japan", "JPN", "JPN", "ja", "JP"),
			(0x0404, 950, 950, "Chinese", "Taiwan", "CHT", "TWN", "zh", "TW"),
			(0x0C04, 950, 950, "Chinese", "Hong Kong SAR", "ZHH", "HKG", "zh", "HK"),
			(0x0804, 936, 936, "Chinese", "People's Republic of China", "CHS", "CHN", "zh", "CN"),
			(0x0412, 949, 949, "Korean", "Korea", "KOR", "KOR", "ko", "KR"),
			(0x0421, 1252, 850, "Indonesian", "Indonesia", "IND", "IDN", "id", "ID"),
			(0x0407, 1252, 850, "German", "Germany", "DEU", "DEU", "de", "DE"),
			(0x040C, 1252, 850, "French", "France", "FRA", "FRA", "fr", "FR"),
			(0x0415, 1250, 852, "Polish", "Poland", "PLK", "POL", "pl", "PL")
		};

		private void EnumLocales(uint callback, bool wide)
		{
			foreach (var locale in Locales)
			{
				string id = locale.Lcid.ToString("X8");
				if ((uint)Cpu.Call(callback, wide ? StaticWString(id) : StaticCString(id)) == 0) break;
			}
		}

		private uint LocaleInfo(uint lcid, uint type, uint buffer, uint size, bool wide)
		{
			// The user and system defaults (0x400, 0x800, 0) mean English (United States).
			lcid &= 0xFFFF;
			if (lcid == 0x400 || lcid == 0x800 || lcid == 0) lcid = 0x0409;
			int index = Array.FindIndex(Locales, l => l.Lcid == lcid);
			if (index < 0)
			{
				lastError = 87; // ERROR_INVALID_PARAMETER
				return 0;
			}

			var locale = Locales[index];
			bool returnNumber = (type & 0x20000000) != 0; // LOCALE_RETURN_NUMBER
			uint what = type & 0x0FFFFFFF & ~0x80000000u;
			string text = what switch
			{
				0x0001 => locale.Lcid.ToString("X4"), // LOCALE_ILANGUAGE
				0x0002 or 0x1001 or 0x0004 => locale.Language, // SLANGUAGE, SENGLANGUAGE, SNATIVELANGNAME
				0x0003 => locale.Lang3, // SABBREVLANGNAME
				0x0005 => "1", // ICOUNTRY
				0x0006 or 0x1002 or 0x0008 => locale.Country, // SCOUNTRY, SENGCOUNTRY, SNATIVECTRYNAME
				0x0007 => locale.Country3, // SABBREVCTRYNAME
				0x000B => locale.Oem.ToString(), // IDEFAULTCODEPAGE
				0x1004 => locale.Ansi.ToString(), // IDEFAULTANSICODEPAGE
				0x0059 => locale.Iso639, // SISO639LANGNAME
				0x005A => locale.Iso3166, // SISO3166CTRYNAME
				0x005C => locale.Iso639 + "-" + locale.Iso3166, // SNAME
				0x000C => ",", // SLIST
				0x000E => ".", // SDECIMAL
				0x000F => ",", // STHOUSAND
				0x0010 => "3;0", // SGROUPING
				0x001D => "/", // SDATE
				0x001E => ":", // STIME
				0x001F => "M/d/yyyy", // SSHORTDATE
				0x0020 => "dddd, MMMM d, yyyy", // SLONGDATE
				0x1003 => "h:mm:ss tt", // STIMEFORMAT
				0x0014 => "$", // SCURRENCY
				0x0015 => "USD", // SINTLSYMBOL
				0x0016 => ".", // SMONDECIMALSEP
				0x0017 => ",", // SMONTHOUSANDSEP
				0x0018 => "3;0", // SMONGROUPING
				0x0019 or 0x001A => "2", // ICURRDIGITS, IINTLCURRDIGITS
				0x001B or 0x001C => "0", // ICURRENCY, INEGCURR
				0x000D => "1", // IMEASURE
				0x0011 => "2", // IDIGITS
				0x0012 => "1", // ILZERO
				0x0013 => "0123456789", // SNATIVEDIGITS
				0x0021 or 0x0022 or 0x0023 or 0x0024 or 0x0026 or 0x0027 => "0", // IDATE, ILDATE, ITIME, ITLZERO, IDAYLZERO, IMONLZERO
				0x0025 => "1", // ICENTURY
				0x0028 => "AM", // S1159
				0x0029 => "PM", // S2359
				>= 0x002A and <= 0x0030 => CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(what - 0x002A + 1) % 7], // SDAYNAME1 (Monday)..7
				>= 0x0031 and <= 0x0037 => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(what - 0x0031 + 1) % 7],
				>= 0x0038 and <= 0x0043 => CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[what - 0x0038], // SMONTHNAME1..12
				>= 0x0044 and <= 0x004F => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames[what - 0x0044],
				0x0050 => string.Empty, // SPOSITIVESIGN
				0x0051 => "-", // SNEGATIVESIGN
				0x0052 => "3", // IPOSSIGNPOSN
				0x0053 => "0", // INEGSIGNPOSN
				0x0054 or 0x0056 => "1", // IPOSSYMPRECEDES, INEGSYMPRECEDES
				0x0055 or 0x0057 => "0", // IPOSSEPBYSPACE, INEGSEPBYSPACE
				0x1005 => "0", // ITIMEMARKPOSN
				0x1009 => "1", // ICALENDARTYPE (Gregorian)
				0x100B => "6", // IFIRSTDAYOFWEEK (Sunday)
				0x100C => "0", // IFIRSTWEEKOFYEAR
				0x1010 => "1", // INEGNUMBER
				// Anything else: an empty value, which the C runtimes that ask treat as "not set",
				// rather than a failure that would abandon setlocale altogether.
				_ => string.Empty
			};

			if (returnNumber)
			{
				// The number itself, in a DWORD; the size is counted in characters (two for a DWORD).
				if (size == 0) return wide ? 2u : 4u;
				uint number = what == 0x0001 ? locale.Lcid : uint.TryParse(text, out uint n) ? n : 0;
				Memory.Write32(buffer, number);
				return wide ? 2u : 4u;
			}

			if (size == 0) return (uint)(wide ? text.Length : AnsiEncoding.GetByteCount(text)) + 1;
			return WriteString(buffer, size, text, wide) + 1;
		}

		private bool RangeMapped(uint address, uint size)
		{
			if (address == 0) return false;
			if (size == 0) return true;
			for (ulong page = address & ~0xFFFUL; page < (ulong)address + size; page += 0x1000)
			{
				if (page > uint.MaxValue || !Memory.IsMapped((uint)page)) return false;
			}

			return true;
		}

		private void SystemInfo(uint info)
		{
			Memory.Fill(info, 0, 36);
			Memory.Write16(info, 0); // PROCESSOR_ARCHITECTURE_INTEL
			Memory.Write32(info + 4, 0x1000); // page size
			Memory.Write32(info + 8, 0x00010000);
			Memory.Write32(info + 12, 0x7FFEFFFF);
			Memory.Write32(info + 16, 1); // active processor mask
			Memory.Write32(info + 20, 1); // processors
			Memory.Write32(info + 24, 586);
			Memory.Write32(info + 28, 0x10000); // allocation granularity
			Memory.Write16(info + 32, 6);
		}

		private uint CurrentExceptionSlot()
		{
			return currentExceptionAddress != 0 ? currentExceptionAddress : currentExceptionAddress = AllocateStatic(8);
		}

		/// <summary>GetDateFormat / GetTimeFormat, in the one locale the process has (en-US).</summary>
		private uint FormatDateTime(uint systemTime, uint format, uint buffer, uint size, bool wide, bool date, uint flags)
		{
			DateTime time = systemTime == 0 ? DateTime.Now : new DateTime(Memory.Read16(systemTime), Math.Max((int)Memory.Read16(systemTime + 2), 1), Math.Max((int)Memory.Read16(systemTime + 6), 1),
				Memory.Read16(systemTime + 8), Memory.Read16(systemTime + 10), Memory.Read16(systemTime + 12));
			string picture = format == 0 ? null : wide ? Memory.ReadWString(format) : Memory.ReadCString(format, AnsiEncoding);
			string text;
			if (picture == null)
			{
				// DATE_LONGDATE is 2; TIME_NOSECONDS 2, TIME_FORCE24HOURFORMAT 8.
				text = date ? time.ToString((flags & 2) != 0 ? "dddd, MMMM d, yyyy" : "M/d/yyyy", CultureInfo.InvariantCulture)
					: time.ToString((flags & 8) != 0 ? ((flags & 2) != 0 ? "HH:mm" : "HH:mm:ss") : ((flags & 2) != 0 ? "h:mm tt" : "h:mm:ss tt"), CultureInfo.InvariantCulture);
			}
			else
			{
				// Windows pictures (yyyy, MM, dd, HH, hh, mm, ss, tt) match .NET's except for quoting.
				text = time.ToString(picture.Replace("'", "\\'"), CultureInfo.InvariantCulture);
			}

			if (size == 0) return (uint)text.Length + 1;
			return WriteString(buffer, size, text, wide) + 1;
		}

		private bool WriteIni(string file, string section, string key, string value)
		{
			string host = ToHostPath(file);
			if (host == null || section == null)
			{
				lastError = ErrorFileNotFound;
				return false;
			}

			List<string> lines = File.Exists(host) ? new List<string>(File.ReadAllLines(host, AnsiEncoding)) : new List<string>();
			int start = lines.FindIndex(l => l.Trim().StartsWith("[", StringComparison.Ordinal) && string.Equals(l.Trim().Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase));
			if (start < 0)
			{
				if (key == null) return true;
				lines.Add("[" + section + "]");
				start = lines.Count - 1;
			}

			int end = start + 1;
			while (end < lines.Count && !lines[end].Trim().StartsWith("[", StringComparison.Ordinal)) end++;
			if (key == null)
			{
				lines.RemoveRange(start, end - start);
			}
			else
			{
				int existing = -1;
				for (int i = start + 1; i < end; i++)
				{
					int equals = lines[i].IndexOf('=');
					if (equals > 0 && string.Equals(lines[i].Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) existing = i;
				}

				if (value == null)
				{
					if (existing >= 0) lines.RemoveAt(existing);
				}
				else if (existing >= 0)
				{
					lines[existing] = key + "=" + value;
				}
				else
				{
					lines.Insert(end, key + "=" + value);
				}
			}

			File.WriteAllLines(host, lines, AnsiEncoding);
			return true;
		}

		private static int NumberPrefix(string text, bool allowFraction)
		{
			int i = 0;
			while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
			if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
			while (i < text.Length && (char.IsDigit(text[i]) || allowFraction && (text[i] == '.' || text[i] == 'e' || text[i] == 'E' ||
			                                                                      (text[i] == '-' || text[i] == '+') && i > 0 && (text[i - 1] == 'e' || text[i - 1] == 'E'))))
			{
				i++;
			}

			return i;
		}

		private uint StrFormatTime(uint buffer, uint size, uint format, uint tm)
		{
			// struct tm: sec, min, hour, mday, mon, year (since 1900), wday, yday, isdst.
			DateTime time = new DateTime(1900 + (int)Memory.Read32(tm + 20), 1 + (int)Memory.Read32(tm + 16), Math.Max(1, (int)Memory.Read32(tm + 12)),
				(int)Memory.Read32(tm + 8), (int)Memory.Read32(tm + 4), Math.Min(59, (int)Memory.Read32(tm)));
			string picture = Memory.ReadCString(format, AnsiEncoding) ?? string.Empty;
			StringBuilder text = new StringBuilder();
			for (int i = 0; i < picture.Length; i++)
			{
				if (picture[i] != '%' || i + 1 >= picture.Length)
				{
					text.Append(picture[i]);
					continue;
				}

				char code = picture[++i];
				if (code == '#' && i + 1 < picture.Length) code = picture[++i];
				text.Append(code switch
				{
					'Y' => time.ToString("yyyy", CultureInfo.InvariantCulture),
					'y' => time.ToString("yy", CultureInfo.InvariantCulture),
					'm' => time.ToString("MM", CultureInfo.InvariantCulture),
					'd' => time.ToString("dd", CultureInfo.InvariantCulture),
					'H' => time.ToString("HH", CultureInfo.InvariantCulture),
					'I' => time.ToString("hh", CultureInfo.InvariantCulture),
					'M' => time.ToString("mm", CultureInfo.InvariantCulture),
					'S' => time.ToString("ss", CultureInfo.InvariantCulture),
					'p' => time.ToString("tt", CultureInfo.InvariantCulture),
					'a' => time.ToString("ddd", CultureInfo.InvariantCulture),
					'A' => time.ToString("dddd", CultureInfo.InvariantCulture),
					'b' => time.ToString("MMM", CultureInfo.InvariantCulture),
					'B' => time.ToString("MMMM", CultureInfo.InvariantCulture),
					'j' => time.DayOfYear.ToString("000", CultureInfo.InvariantCulture),
					'c' => time.ToString("MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture),
					'x' => time.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
					'X' => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
					'%' => "%",
					_ => "%" + code
				});
			}

			byte[] bytes = AnsiEncoding.GetBytes(text.ToString());
			if (bytes.Length + 1 > size) return 0;
			Memory.WriteBytes(buffer, bytes);
			Memory.Write8(buffer + (uint)bytes.Length, 0);
			return (uint)bytes.Length;
		}
	}
}
