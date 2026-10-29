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

namespace OpenBve.X86
{
	/// <summary>
	/// The part of Microsoft's C++ runtime (msvcp140.dll) that plugins built with Visual C++ 2015 and later import for
	/// file streams: std::ifstream's basic_streambuf, basic_ios and basic_istream, the char-to-char codecvt, locales,
	/// and the odds and ends (call_once, the error maps, the throw helpers).
	/// </summary>
	/// <remarks>
	/// The plugin's own code is the templates' inline part (basic_filebuf, basic_ifstream, use_facet), which reads these
	/// objects' fields directly, so they are laid out as Visual C++ lays them out for x86:
	/// basic_streambuf: vptr, _Gfirst, _Pfirst, _IGfirst, _IPfirst, _Gnext, _Pnext, _IGnext, _IPnext, _Gcount, _Pcount,
	/// _IGcount, _IPcount, _Plocale (56 bytes); ios_base: vptr, two words (_Stdstr's), _Mystate at 12, _Except, _Fmtfl,
	/// then _Prec and _Wide (64-bit, at 24 and 32), _Arr, _Calls, _Ploc (56 bytes; _Mystate and _Mystrbuf where the
	/// plugin's own code reads them); basic_ios adds _Mystrbuf, _Tiestr, _Fillch (72);
	/// basic_istream: vbptr, _Chcount (64-bit, at 8), with basic_ios a virtual base found through the vbptr's table.
	/// Members are thiscall (this in ECX, the callee removes the arguments); a constructor of a class with a virtual
	/// base takes a hidden last argument, nonzero when it is to construct that base. A locale is a pointer's worth of padding (Visual C++ 2019's, for binary compatibility), then its _Locimp pointer;
	/// the _Locimp holds no facets, so use_facet asks the facet's _Getcat for one, made here.
	/// </remarks>
	public sealed partial class Win32Process
	{
		private const uint StreamEof = 0xFFFFFFFF;
		private const uint IosGood = 0, IosEof = 1, IosFail = 2, IosBad = 4;

		private uint globalLocimp, streambufVftable, iosVftable, istreamVbtable, codecvtVftable, systemErrorText;
		private uint localeIdCount;

		private void RegisterMsvcp()
		{
			const string p = "msvcp140";
			const string streambuf = "?$basic_streambuf@DU?$char_traits@D@std@@@std@@";
			const string ios = "?$basic_ios@DU?$char_traits@D@std@@@std@@";
			const string istream = "?$basic_istream@DU?$char_traits@D@std@@@std@@";
			const string codecvt = "?$codecvt@DDU_Mbstatet@@@std@@";

			dataImports[p + "!?id@" + codecvt + "2V0locale@2@A"] = () => AllocateStatic(4);

			// --- basic_streambuf<char> ---
			Export(p, "??0" + streambuf + "IAE@XZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				EnsureCppRuntime();
				Memory.Fill(self, 0, 56);
				Memory.Write32(self, streambufVftable);
				Memory.Write32(self + 52, NewLocale());
				StreambufInit(self, self + 4, self + 20, self + 36, self + 8, self + 24, self + 40);
				return self;
			});
			Export(p, "??1" + streambuf + "UAE@XZ", 0, c => 0);
			Export(p, "?_Init@" + streambuf + "IAEXXZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				StreambufInit(self, self + 4, self + 20, self + 36, self + 8, self + 24, self + 40);
				return 0;
			});
			Export(p, "?_Init@" + streambuf + "IAEXPAPAD0PAH001@Z", 24, c =>
			{
				StreambufInit(c.R[Cpu.ECX], c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5));
				return 0;
			});
			Export(p, "?getloc@" + streambuf + "QBE?AVlocale@2@XZ", 4, c =>
			{
				uint self = c.R[Cpu.ECX], result = c.Arg(0);
				uint locale = Memory.Read32(self + 52);
				Memory.Write32(result, 0);
				Memory.Write32(result + 4, locale != 0 ? Memory.Read32(locale + 4) : globalLocimp);
				return result;
			});
			Export(p, "?sgetc@" + streambuf + "QAEHXZ", 0, c => StreambufSgetc(c.R[Cpu.ECX]));
			Export(p, "?sbumpc@" + streambuf + "QAEHXZ", 0, c => StreambufSbumpc(c.R[Cpu.ECX]));
			Export(p, "?snextc@" + streambuf + "QAEHXZ", 0, c => StreambufSnextc(c.R[Cpu.ECX]));
			Export(p, "?_Pninc@" + streambuf + "IAEPADXZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				uint count = Memory.Read32(self + 48), next = Memory.Read32(self + 32);
				Memory.Write32(count, Memory.Read32(count) - 1);
				uint at = Memory.Read32(next);
				Memory.Write32(next, at + 1);
				return at;
			});

			// Its virtual functions' defaults (the plugin's basic_filebuf overrides the others).
			Export(p, "?_Lock@" + streambuf + "UAEXXZ", 0, c => 0);
			Export(p, "?_Unlock@" + streambuf + "UAEXXZ", 0, c => 0);
			Export(p, "?imbue@" + streambuf + "MAEXABVlocale@2@@Z", 4, c => 0);
			Export(p, "?setbuf@" + streambuf + "MAEPAV12@PAD_J@Z", 12, c => c.R[Cpu.ECX]);
			Export(p, "?showmanyc@" + streambuf + "MAE_JXZ", 0, c => 0);
			Export(p, "?sync@" + streambuf + "MAEHXZ", 0, c => 0);
			Export(p, "?uflow@" + streambuf + "MAEHXZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				return CallVirtual(self, 6) == StreamEof ? StreamEof : Memory.Read8(GetNextAndAdvance(self));
			});
			Export(p, "?xsgetn@" + streambuf + "MAE_JPAD_J@Z", 12, c =>
			{
				uint self = c.R[Cpu.ECX], target = c.Arg(0);
				long wanted = (long)(c.Arg(1) | ((ulong)c.Arg(2) << 32));
				long copied = 0;
				while (copied < wanted)
				{
					uint available = GetAvailable(self);
					if (available > 0)
					{
						uint n = (uint)Math.Min(available, wanted - copied);
						uint next = Memory.Read32(self + 28);
						CopyMemory(target + (uint)copied, Memory.Read32(next), n);
						Memory.Write32(next, Memory.Read32(next) + n);
						uint count = Memory.Read32(self + 44);
						Memory.Write32(count, Memory.Read32(count) - n);
						copied += n;
						continue;
					}

					uint meta = (uint)CallVirtual(self, 7);
					if (meta == StreamEof)
					{
						break;
					}

					Memory.Write8(target + (uint)copied, (byte)meta);
					copied++;
				}

				return (ulong)copied;
			});
			Export(p, "?xsputn@" + streambuf + "MAE_JPBD_J@Z", 12, c =>
			{
				uint self = c.R[Cpu.ECX], source = c.Arg(0);
				long wanted = (long)(c.Arg(1) | ((ulong)c.Arg(2) << 32));
				long written = 0;
				while (written < wanted)
				{
					uint nextPointer = Memory.Read32(self + 32), countPointer = Memory.Read32(self + 48);
					uint available = Memory.Read32(nextPointer) != 0 ? Memory.Read32(countPointer) : 0;
					if (available > 0)
					{
						uint n = (uint)Math.Min(available, wanted - written);
						CopyMemory(Memory.Read32(nextPointer), source + (uint)written, n);
						Memory.Write32(nextPointer, Memory.Read32(nextPointer) + n);
						Memory.Write32(countPointer, Memory.Read32(countPointer) - n);
						written += n;
						continue;
					}

					if ((uint)CallVirtual(self, 3, Memory.Read8(source + (uint)written)) == StreamEof)
					{
						break;
					}

					written++;
				}

				return (ulong)written;
			});

			// --- basic_ios<char> (and ios_base) ---
			Export(p, "??0" + ios + "IAE@XZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				EnsureCppRuntime();
				Memory.Fill(self, 0, 72);
				Memory.Write32(self, iosVftable);
				return self;
			});
			Export(p, "??1" + ios + "UAE@XZ", 0, c => 0);
			Export(p, "?clear@" + ios + "QAEXH_N@Z", 8, c =>
			{
				IosClear(c.R[Cpu.ECX], c.Arg(0));
				return 0;
			});
			Export(p, "?setstate@" + ios + "QAEXH_N@Z", 8, c =>
			{
				uint self = c.R[Cpu.ECX];
				if (c.Arg(0) != IosGood)
				{
					IosClear(self, Memory.Read32(self + 12) | c.Arg(0));
				}

				return 0;
			});
			Export(p, "?widen@" + ios + "QBEDD@Z", 4, c => c.Arg(0) & 0xFF);

			// --- basic_istream<char> ---
			Export(p, "??0" + istream + "QAE@PAV?$basic_streambuf@DU?$char_traits@D@std@@@1@_N@Z", 12, c =>
			{
				uint self = c.R[Cpu.ECX], buffer = c.Arg(0);
				EnsureCppRuntime();
				if (c.Arg(2) != 0)
				{
					// The most derived class: its basic_ios is built here, right after it.
					Memory.Write32(self, istreamVbtable);
					Memory.Fill(self + 16, 0, 72);
					Memory.Write32(self + 16, iosVftable);
				}

				Memory.Write64(self + 8, 0);
				IosInit(IosOf(self), buffer);
				return self;
			});
			Export(p, "??1" + istream + "UAE@XZ", 0, c => 0);
			Export(p, "?_Ipfx@" + istream + "QAE_N_N@Z", 4, c =>
			{
				uint self = c.R[Cpu.ECX];
				bool noSkip = (c.Arg(0) & 0xFF) != 0;
				uint ios = IosOf(self);
				if (Memory.Read32(ios + 12) == IosGood)
				{
					uint buffer = Memory.Read32(ios + 56);
					if (!noSkip && (Memory.Read32(ios + 20) & 1) != 0)
					{
						// skipws: past white space, as ctype<char>::is(space) has it.
						for (uint meta = StreambufSgetc(buffer); ; meta = StreambufSnextc(buffer))
						{
							if (meta == StreamEof)
							{
								IosClear(ios, Memory.Read32(ios + 12) | IosEof);
								break;
							}

							if (!(meta == ' ' || (meta >= 9 && meta <= 13)))
							{
								break;
							}
						}
					}

					if (Memory.Read32(ios + 12) == IosGood)
					{
						return 1;
					}
				}

				IosClear(ios, Memory.Read32(ios + 12) | IosFail);
				return 0;
			});

			// --- locales and the char-to-char codecvt (no conversion) ---
			Export(p, "?_Getgloballocale@locale@std@@CAPAV_Locimp@12@XZ", 0, c =>
			{
				EnsureCppRuntime();
				return globalLocimp;
			});
			Export(p, "??Bid@locale@std@@QAEIXZ", 0, c =>
			{
				uint self = c.R[Cpu.ECX];
				if (Memory.Read32(self) == 0)
				{
					Memory.Write32(self, ++localeIdCount);
				}

				return Memory.Read32(self);
			});
			Export(p, "?_Getcat@" + codecvt + "SAIPAPBVfacet@locale@2@PBV42@@Z", 0, c =>
			{
				EnsureCppRuntime();
				if (c.Arg(0) != 0 && Memory.Read32(c.Arg(0)) == 0)
				{
					uint facet = HeapAllocate(64);
					Memory.Write32(facet, codecvtVftable);
					Memory.Write32(c.Arg(0), facet);
				}

				return 2; // LC_CTYPE
			});
			Export(p, "?always_noconv@codecvt_base@std@@QBE_NXZ", 0, c => 1);
			// in, out: (state, from, from_end, from_next&, to, to_end, to_next&); unshift: (state, to, to_end, to_next&).
			Export(p, "?in@" + codecvt + "QBEHAAU_Mbstatet@@PBD1AAPBDPAD3AAPAD@Z", 28, c => NoConversion(c.Arg(3), c.Arg(1), c.Arg(6), c.Arg(4)));
			Export(p, "?out@" + codecvt + "QBEHAAU_Mbstatet@@PBD1AAPBDPAD3AAPAD@Z", 28, c => NoConversion(c.Arg(3), c.Arg(1), c.Arg(6), c.Arg(4)));
			Export(p, "?unshift@" + codecvt + "QBEHAAU_Mbstatet@@PAD1AAPAD@Z", 16, c => NoConversion(0, 0, c.Arg(3), c.Arg(1)));

			// --- files, locks, once, errors ---
			Export(p, "?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z", 0, c => FileStreamOpen(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(1)));
			Export(p, "?_Fiopen@std@@YAPAU_iobuf@@PB_WHH@Z", 0, c => FileStreamOpen(Memory.ReadWString(c.Arg(0)), c.Arg(1)));
			Export(p, "??0_Lockit@std@@QAE@H@Z", 4, c => c.R[Cpu.ECX]);
			Export(p, "??1_Lockit@std@@QAE@XZ", 0, c => 0);
			Export(p, "?_Execute_once@std@@YAHAAUonce_flag@1@P6GHPAX1PAPAX@Z1@Z", 0, c =>
			{
				uint flag = c.Arg(0), callback = c.Arg(1), parameter = c.Arg(2);
				if (Memory.Read32(flag) != 0)
				{
					return 1;
				}

				uint result = (uint)Cpu.Call(callback, flag, parameter, 0);
				if (result != 0)
				{
					Memory.Write32(flag, 2);
				}

				return result;
			});
			Export(p, "?_Syserror_map@std@@YAPBDH@Z", 0, c => systemErrorText != 0 ? systemErrorText : systemErrorText = StaticCString("unknown error"));
			Export(p, "?_Winerror_map@std@@YAHH@Z", 0, c => 0);
			Export(p, "?_Winerror_message@std@@YAKKPADK@Z", 0, c =>
			{
				if (c.Arg(1) != 0 && c.Arg(2) != 0)
				{
					Memory.Write8(c.Arg(1), 0);
				}

				return 0;
			});
			// The throw helpers: the standard exceptions, thrown as C++ exceptions the plugin may catch (CxxExceptions.cs).
			Export(p, "?_Xlength_error@std@@YAXPBD@Z", 0, c => throw StandardThrow("length_error", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xout_of_range@std@@YAXPBD@Z", 0, c => throw StandardThrow("out_of_range", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xinvalid_argument@std@@YAXPBD@Z", 0, c => throw StandardThrow("invalid_argument", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xbad_alloc@std@@YAXXZ", 0, c => throw StandardThrow("bad_alloc", "bad allocation", "exception"));
		}

		/// <summary>The runtime's own objects, made on first use: the global locale's _Locimp and the virtual tables.</summary>
		private void EnsureCppRuntime()
		{
			if (globalLocimp != 0)
			{
				return;
			}

			// A facet's table: the scalar deleting destructor, _Incref, _Decref (which never hands it back for deleting).
			uint destructor = Cpu.AddHostFunction("msvcp140!facet destructor", c => c.R[Cpu.ECX], 4);
			uint incref = Cpu.AddHostFunction("msvcp140!facet _Incref", c => { Memory.Write32(c.R[Cpu.ECX] + 4, Memory.Read32(c.R[Cpu.ECX] + 4) + 1); return 0; }, 0);
			uint decref = Cpu.AddHostFunction("msvcp140!facet _Decref", c => 0, 0);

			// _Locimp: facet (vptr, refs), _Facetvec, _Facetcount (none), _Catmask, _Xparent, _Name ("C").
			uint locimpVftable = AllocateStatic(12);
			Memory.Write32(locimpVftable, destructor);
			Memory.Write32(locimpVftable + 4, incref);
			Memory.Write32(locimpVftable + 8, decref);
			globalLocimp = AllocateStatic(40);
			Memory.Write32(globalLocimp, locimpVftable);
			Memory.Write32(globalLocimp + 4, 1);
			Memory.Write32(globalLocimp + 16, 0x3F);
			Memory.Write8(globalLocimp + 20, 1);
			Memory.Write32(globalLocimp + 24, StaticCString("C"));

			// codecvt<char, char, _Mbstatet>: always_noconv, max_length 1, encoding 1, and in/out/unshift/length as noconv.
			codecvtVftable = AllocateStatic(40);
			uint[] codecvtSlots =
			{
				destructor, incref, decref,
				Cpu.AddHostFunction("msvcp140!codecvt do_always_noconv", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_max_length", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_encoding", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_in", c => NoConversion(c.Arg(3), c.Arg(1), c.Arg(6), c.Arg(4)), 28),
				Cpu.AddHostFunction("msvcp140!codecvt do_out", c => NoConversion(c.Arg(3), c.Arg(1), c.Arg(6), c.Arg(4)), 28),
				Cpu.AddHostFunction("msvcp140!codecvt do_unshift", c => NoConversion(0, 0, c.Arg(3), c.Arg(1)), 16),
				Cpu.AddHostFunction("msvcp140!codecvt do_length", c => Math.Min(c.Arg(2) - c.Arg(1), c.Arg(3)), 16)
			};
			for (int i = 0; i < codecvtSlots.Length; i++)
			{
				Memory.Write32(codecvtVftable + 4 * (uint)i, codecvtSlots[i]);
			}

			// basic_streambuf<char>'s table, for a buffer the plugin does not derive from: overflow, pbackfail, underflow
			// give eof, seekoff and seekpos -1, the rest as exported.
			string streambuf = "?$basic_streambuf@DU?$char_traits@D@std@@@std@@";
			streambufVftable = AllocateStatic(15 * 4);
			uint[] streambufSlots =
			{
				Cpu.AddHostFunction("msvcp140!basic_streambuf destructor", c => c.R[Cpu.ECX], 4),
				HostExport("msvcp140", "?_Lock@" + streambuf + "UAEXXZ"),
				HostExport("msvcp140", "?_Unlock@" + streambuf + "UAEXXZ"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf overflow", c => StreamEof, 4),
				Cpu.AddHostFunction("msvcp140!basic_streambuf pbackfail", c => StreamEof, 4),
				HostExport("msvcp140", "?showmanyc@" + streambuf + "MAE_JXZ"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf underflow", c => StreamEof, 0),
				HostExport("msvcp140", "?uflow@" + streambuf + "MAEHXZ"),
				HostExport("msvcp140", "?xsgetn@" + streambuf + "MAE_JPAD_J@Z"),
				HostExport("msvcp140", "?xsputn@" + streambuf + "MAE_JPBD_J@Z"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf seekoff", c => { Memory.Write64(c.Arg(0), ulong.MaxValue); return c.Arg(0); }, 20),
				Cpu.AddHostFunction("msvcp140!basic_streambuf seekpos", c => { Memory.Write64(c.Arg(0), ulong.MaxValue); return c.Arg(0); }, 32),
				HostExport("msvcp140", "?setbuf@" + streambuf + "MAEPAV12@PAD_J@Z"),
				HostExport("msvcp140", "?sync@" + streambuf + "MAEHXZ"),
				HostExport("msvcp140", "?imbue@" + streambuf + "MAEXABVlocale@2@@Z")
			};
			for (int i = 0; i < streambufSlots.Length; i++)
			{
				Memory.Write32(streambufVftable + 4 * (uint)i, streambufSlots[i]);
			}

			// basic_ios<char>'s table (its scalar deleting destructor only), and a lone basic_istream's virtual base
			// table: its own place, then basic_ios 16 bytes on.
			iosVftable = AllocateStatic(4);
			Memory.Write32(iosVftable, Cpu.AddHostFunction("msvcp140!basic_ios destructor", c => c.R[Cpu.ECX], 4));
			istreamVbtable = AllocateStatic(8);
			Memory.Write32(istreamVbtable, 0);
			Memory.Write32(istreamVbtable + 4, 16);
		}

		/// <summary>A new std::locale (the global one), in the heap: a pointer's worth of padding, then its _Locimp pointer.</summary>
		private uint NewLocale()
		{
			uint locale = HeapAllocate(8);
			Memory.Write32(locale + 4, globalLocimp);
			return locale;
		}

		/// <summary>basic_streambuf::_Init: the get and put areas' indirect pointers, the areas emptied.</summary>
		private void StreambufInit(uint self, uint getFirst, uint getNext, uint getCount, uint putFirst, uint putNext, uint putCount)
		{
			Memory.Write32(self + 12, getFirst);
			Memory.Write32(self + 16, putFirst);
			Memory.Write32(self + 28, getNext);
			Memory.Write32(self + 32, putNext);
			Memory.Write32(self + 44, getCount);
			Memory.Write32(self + 48, putCount);
			if (getFirst == self + 4)
			{
				// The no-argument form: the buffer's own pointers, empty.
				Memory.Fill(self + 4, 0, 8);
				Memory.Fill(self + 20, 0, 8);
				Memory.Fill(self + 36, 0, 8);
			}
		}

		/// <summary>_Gnavail: characters left in the get area (none without one).</summary>
		private uint GetAvailable(uint self)
		{
			return Memory.Read32(Memory.Read32(self + 28)) != 0 ? Memory.Read32(Memory.Read32(self + 44)) : 0;
		}

		/// <summary>_Gninc: the current character's address, the get area moved past it.</summary>
		private uint GetNextAndAdvance(uint self)
		{
			uint count = Memory.Read32(self + 44), next = Memory.Read32(self + 28);
			Memory.Write32(count, Memory.Read32(count) - 1);
			uint at = Memory.Read32(next);
			Memory.Write32(next, at + 1);
			return at;
		}

		private uint StreambufSgetc(uint self)
		{
			return GetAvailable(self) > 0 ? Memory.Read8(Memory.Read32(Memory.Read32(self + 28))) : (uint)CallVirtual(self, 6);
		}

		private uint StreambufSbumpc(uint self)
		{
			return GetAvailable(self) > 0 ? Memory.Read8(GetNextAndAdvance(self)) : (uint)CallVirtual(self, 7);
		}

		private uint StreambufSnextc(uint self)
		{
			if (GetAvailable(self) > 1)
			{
				// _Gpreinc: past the current character, the next one returned.
				uint next = Memory.Read32(self + 28), count = Memory.Read32(self + 44);
				Memory.Write32(next, Memory.Read32(next) + 1);
				Memory.Write32(count, Memory.Read32(count) - 1);
				return Memory.Read8(Memory.Read32(next));
			}

			return StreambufSbumpc(self) == StreamEof ? StreamEof : StreambufSgetc(self);
		}

		/// <summary>Calls a virtual function (thiscall) by its slot in the object's table.</summary>
		private ulong CallVirtual(uint self, int slot, params uint[] args)
		{
			uint function = Memory.Read32(Memory.Read32(self) + 4 * (uint)slot);
			Cpu.R[Cpu.ECX] = self;
			return Cpu.Call(function, args);
		}

		/// <summary>An istream's basic_ios: through its virtual base table (the second entry is the base's offset).</summary>
		private uint IosOf(uint istream)
		{
			return istream + Memory.Read32(Memory.Read32(istream) + 4);
		}

		/// <summary>basic_ios::init: ios_base::_Init, the stream buffer, no tie, a space as the fill.</summary>
		private void IosInit(uint ios, uint buffer)
		{
			Memory.Write64(ios + 4, 0); // _Stdstr and the field before _Mystate
			Memory.Write32(ios + 16, 0); // _Except
			Memory.Write32(ios + 20, 0x201); // skipws | dec
			Memory.Write64(ios + 24, 6); // _Prec
			Memory.Write64(ios + 32, 0); // _Wide
			Memory.Write32(ios + 40, 0); // _Arr
			Memory.Write32(ios + 44, 0); // _Calls
			Memory.Write32(ios + 48, NewLocale()); // _Ploc
			Memory.Write32(ios + 56, buffer);
			Memory.Write32(ios + 60, 0);
			Memory.Write8(ios + 64, (byte)' ');
			IosClear(ios, IosGood);
		}

		/// <summary>basic_ios::clear: the state (bad without a buffer); exceptions on it are not raised, but logged.</summary>
		private void IosClear(uint ios, uint state)
		{
			state = (state | (Memory.Read32(ios + 56) == 0 ? IosBad : IosGood)) & 0x17;
			Memory.Write32(ios + 12, state);
			if ((state & Memory.Read32(ios + 16)) != 0)
			{
				Log("plugin: a stream's state " + state + " would throw ios_base::failure (not raised)");
			}
		}

		/// <summary>codecvt in/out/unshift for char to char: nothing converted, both sides' next where they start (noconv).</summary>
		private uint NoConversion(uint fromNext, uint from, uint toNext, uint to)
		{
			if (fromNext != 0)
			{
				Memory.Write32(fromNext, from);
			}

			if (toNext != 0)
			{
				Memory.Write32(toNext, to);
			}

			return 3; // codecvt_base::noconv
		}

		/// <summary>std::_Fiopen: a FILE* for a file stream's open mode (in 1, out 2, ate 4, app 8, trunc 16, binary 32).</summary>
		private uint FileStreamOpen(string name, uint mode)
		{
			bool binary = (mode & 0x20) != 0, atEnd = (mode & 0x04) != 0;
			string fopenMode;
			switch (mode & 0x1B)
			{
				case 0x01: fopenMode = "r"; break;
				case 0x02:
				case 0x12: fopenMode = "w"; break;
				case 0x0A:
				case 0x08: fopenMode = "a"; break;
				case 0x03: fopenMode = "r+"; break;
				case 0x13: fopenMode = "w+"; break;
				case 0x0B:
				case 0x09: fopenMode = "a+"; break;
				default: return 0;
			}

			uint file = CrtOpen(name, binary ? fopenMode + "b" : fopenMode);
			if (file != 0 && atEnd && crtFiles.TryGetValue(file, out System.IO.Stream stream))
			{
				stream.Seek(0, System.IO.SeekOrigin.End);
			}

			return file;
		}
	}
}
