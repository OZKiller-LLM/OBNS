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
	/// msvcp140.dll's file stream parts for 64-bit plugins: Msvcp.cs's functions, under x64 names and layouts, for the
	/// plugin's own inline code (basic_filebuf, basic_ifstream, use_facet), which reads these objects' fields directly.
	/// </summary>
	/// <remarks>
	/// basic_streambuf: vptr, _Gfirst 8, _Pfirst 16, _IGfirst 24, _IPfirst 32, _Gnext 40, _Pnext 48, _IGnext 56, _IPnext 64,
	/// _Gcount 72 and _Pcount 76 (ints), _IGcount 80, _IPcount 88, _Plocale 96 (104 bytes). ios_base: vptr, _Stdstr 8, _Mystate 16,
	/// _Except 20, _Fmtfl 24, _Prec 32, _Wide 40, _Arr 48, _Calls 56, _Ploc 64 (72); basic_ios adds _Mystrbuf 72, _Tiestr 80,
	/// _Fillch 88 (96). basic_istream: vbptr, _Chcount 8, with basic_ios at 16. Members take this as the first argument. A
	/// locale is a pointer's worth of padding, then its _Locimp pointer. _Locimp: vptr, refs 8, _Facetvec 16, _Facetcount 24,
	/// _Catmask 32, _Xparent 36, _Name 40.
	/// </remarks>
	public sealed partial class Win32Process
	{
		private void RegisterMsvcp64()
		{
			const string p = "msvcp140";
			const string streambuf = "?$basic_streambuf@DU?$char_traits@D@std@@@std@@";
			const string ios = "?$basic_ios@DU?$char_traits@D@std@@@std@@";
			const string istream = "?$basic_istream@DU?$char_traits@D@std@@@std@@";
			const string codecvt = "?$codecvt@DDU_Mbstatet@@@std@@";

			dataImports[p + "!?id@" + codecvt + "2V0locale@2@A"] = () => AllocateStatic(8);

			// --- basic_streambuf<char> ---
			Export(p, "??0" + streambuf + "IEAA@XZ", 0, c =>
			{
				uint self = c.Arg(0);
				EnsureCppRuntime64();
				Memory.Fill(self, 0, 104);
				Memory.Write64(self, streambufVftable);
				Memory.Write64(self + 96, NewLocale64());
				StreambufInit64(self, self + 8, self + 40, self + 72, self + 16, self + 48, self + 76);
				return self;
			});
			Export(p, "??1" + streambuf + "UEAA@XZ", 0, c => 0);
			Export(p, "?_Init@" + streambuf + "IEAAXXZ", 0, c =>
			{
				uint self = c.Arg(0);
				StreambufInit64(self, self + 8, self + 40, self + 72, self + 16, self + 48, self + 76);
				return 0;
			});
			Export(p, "?_Init@" + streambuf + "IEAAXPEAPEAD0PEAH001@Z", 0, c =>
			{
				StreambufInit64(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5), c.Arg(6));
				return 0;
			});
			Export(p, "?getloc@" + streambuf + "QEBA?AVlocale@2@XZ", 0, c =>
			{
				uint self = c.Arg(0), result = c.Arg(1);
				uint locale = Memory.Read32(self + 96);
				Memory.Write64(result, 0);
				Memory.Write64(result + 8, locale != 0 ? Memory.Read32(locale + 8) : globalLocimp);
				return result;
			});
			Export(p, "?sgetc@" + streambuf + "QEAAHXZ", 0, c => Sgetc64(c.Arg(0)));
			Export(p, "?sbumpc@" + streambuf + "QEAAHXZ", 0, c => Sbumpc64(c.Arg(0)));
			Export(p, "?snextc@" + streambuf + "QEAAHXZ", 0, c => Snextc64(c.Arg(0)));
			Export(p, "?_Pninc@" + streambuf + "IEAAPEADXZ", 0, c =>
			{
				uint self = c.Arg(0);
				uint count = Memory.Read32(self + 88), next = Memory.Read32(self + 64);
				Memory.Write32(count, Memory.Read32(count) - 1);
				uint at = Memory.Read32(next);
				Memory.Write64(next, at + 1);
				return at;
			});

			// Its virtual functions' defaults (the plugin's basic_filebuf overrides the others).
			Export(p, "?_Lock@" + streambuf + "UEAAXXZ", 0, c => 0);
			Export(p, "?_Unlock@" + streambuf + "UEAAXXZ", 0, c => 0);
			Export(p, "?imbue@" + streambuf + "MEAAXAEBVlocale@2@@Z", 0, c => 0);
			Export(p, "?setbuf@" + streambuf + "MEAAPEAV12@PEAD_J@Z", 0, c => c.Arg(0));
			Export(p, "?showmanyc@" + streambuf + "MEAA_JXZ", 0, c => 0);
			Export(p, "?sync@" + streambuf + "MEAAHXZ", 0, c => 0);
			Export(p, "?uflow@" + streambuf + "MEAAHXZ", 0, c =>
			{
				uint self = c.Arg(0);
				return (uint)CallVirtual64(self, 6) == StreamEof ? StreamEof : Memory.Read8(GetNextAndAdvance64(self));
			});
			Export(p, "?xsgetn@" + streambuf + "MEAA_JPEAD_J@Z", 0, c =>
			{
				uint self = c.Arg(0), target = c.Arg(1);
				long wanted = (long)c.Long.Arg(2), copied = 0;
				while (copied < wanted)
				{
					uint available = GetAvailable64(self);
					if (available > 0)
					{
						uint n = (uint)Math.Min(available, wanted - copied);
						uint next = Memory.Read32(self + 56);
						CopyMemory(target + (uint)copied, Memory.Read32(next), n);
						Memory.Write64(next, Memory.Read32(next) + n);
						uint count = Memory.Read32(self + 80);
						Memory.Write32(count, Memory.Read32(count) - n);
						copied += n;
						continue;
					}

					uint meta = (uint)CallVirtual64(self, 7);
					if (meta == StreamEof)
					{
						break;
					}

					Memory.Write8(target + (uint)copied, (byte)meta);
					copied++;
				}

				return (ulong)copied;
			});
			Export(p, "?xsputn@" + streambuf + "MEAA_JPEBD_J@Z", 0, c =>
			{
				uint self = c.Arg(0), source = c.Arg(1);
				long wanted = (long)c.Long.Arg(2), written = 0;
				while (written < wanted)
				{
					uint nextPointer = Memory.Read32(self + 64), countPointer = Memory.Read32(self + 88);
					uint available = Memory.Read32(nextPointer) != 0 ? Memory.Read32(countPointer) : 0;
					if (available > 0)
					{
						uint n = (uint)Math.Min(available, wanted - written);
						CopyMemory(Memory.Read32(nextPointer), source + (uint)written, n);
						Memory.Write64(nextPointer, Memory.Read32(nextPointer) + n);
						Memory.Write32(countPointer, Memory.Read32(countPointer) - n);
						written += n;
						continue;
					}

					if ((uint)CallVirtual64(self, 3, Memory.Read8(source + (uint)written)) == StreamEof)
					{
						break;
					}

					written++;
				}

				return (ulong)written;
			});

			// --- basic_ios<char> (and ios_base) ---
			Export(p, "??0" + ios + "IEAA@XZ", 0, c =>
			{
				uint self = c.Arg(0);
				EnsureCppRuntime64();
				Memory.Fill(self, 0, 96);
				Memory.Write64(self, iosVftable);
				return self;
			});
			Export(p, "??1" + ios + "UEAA@XZ", 0, c => 0);
			Export(p, "?clear@" + ios + "QEAAXH_N@Z", 0, c =>
			{
				IosClear64(c.Arg(0), c.Arg(1));
				return 0;
			});
			Export(p, "?setstate@" + ios + "QEAAXH_N@Z", 0, c =>
			{
				uint self = c.Arg(0);
				if (c.Arg(1) != IosGood)
				{
					IosClear64(self, Memory.Read32(self + 16) | c.Arg(1));
				}

				return 0;
			});
			Export(p, "?widen@" + ios + "QEBADD@Z", 0, c => c.Arg(1) & 0xFF);

			// --- basic_istream<char> ---
			Export(p, "??0" + istream + "QEAA@PEAV?$basic_streambuf@DU?$char_traits@D@std@@@1@_N@Z", 0, c =>
			{
				// (this, the buffer, whether it is the system's, and whether this is the most derived class).
				uint self = c.Arg(0), buffer = c.Arg(1);
				EnsureCppRuntime64();
				if (c.Arg(3) != 0)
				{
					Memory.Write64(self, istreamVbtable);
					Memory.Fill(self + 16, 0, 96);
					Memory.Write64(self + 16, iosVftable);
				}

				Memory.Write64(self + 8, 0);
				IosInit64(IosOf64(self), buffer);
				return self;
			});
			Export(p, "??1" + istream + "UEAA@XZ", 0, c => 0);
			Export(p, "?_Ipfx@" + istream + "QEAA_N_N@Z", 0, c =>
			{
				uint self = c.Arg(0);
				bool noSkip = (c.Arg(1) & 0xFF) != 0;
				uint ios64 = IosOf64(self);
				if (Memory.Read32(ios64 + 16) == IosGood)
				{
					uint buffer = Memory.Read32(ios64 + 72);
					if (!noSkip && (Memory.Read32(ios64 + 24) & 1) != 0)
					{
						for (uint meta = Sgetc64(buffer); ; meta = Snextc64(buffer))
						{
							if (meta == StreamEof)
							{
								IosClear64(ios64, Memory.Read32(ios64 + 16) | IosEof);
								break;
							}

							if (!(meta == ' ' || (meta >= 9 && meta <= 13)))
							{
								break;
							}
						}
					}

					if (Memory.Read32(ios64 + 16) == IosGood)
					{
						return 1;
					}
				}

				IosClear64(ios64, Memory.Read32(ios64 + 16) | IosFail);
				return 0;
			});

			// --- locales and the char-to-char codecvt (no conversion) ---
			Export(p, "?_Getgloballocale@locale@std@@CAPEAV_Locimp@12@XZ", 0, c =>
			{
				EnsureCppRuntime64();
				return globalLocimp;
			});
			Export(p, "??Bid@locale@std@@QEAA_KXZ", 0, c =>
			{
				uint self = c.Arg(0);
				if (Memory.Read32(self) == 0)
				{
					Memory.Write64(self, ++localeIdCount);
				}

				return Memory.Read32(self);
			});
			Export(p, "?_Getcat@" + codecvt + "SA_KPEAPEBVfacet@locale@2@PEBV42@@Z", 0, c =>
			{
				EnsureCppRuntime64();
				if (c.Arg(0) != 0 && Memory.Read32(c.Arg(0)) == 0)
				{
					uint facet = HeapAllocate(64);
					Memory.Write64(facet, codecvtVftable);
					Memory.Write64(c.Arg(0), facet);
				}

				return 2; // LC_CTYPE
			});
			Export(p, "?always_noconv@codecvt_base@std@@QEBA_NXZ", 0, c => 1);
			// in, out: (this, state, from, from_end, from_next&, to, to_end, to_next&); unshift: (this, state, to, to_end, to_next&).
			Export(p, "?in@" + codecvt + "QEBAHAEAU_Mbstatet@@PEBD1AEAPEBDPEAD3AEAPEAD@Z", 0, c => NoConversion64(c.Arg(4), c.Arg(2), c.Arg(7), c.Arg(5)));
			Export(p, "?out@" + codecvt + "QEBAHAEAU_Mbstatet@@PEBD1AEAPEBDPEAD3AEAPEAD@Z", 0, c => NoConversion64(c.Arg(4), c.Arg(2), c.Arg(7), c.Arg(5)));
			Export(p, "?unshift@" + codecvt + "QEBAHAEAU_Mbstatet@@PEAD1AEAPEAD@Z", 0, c => NoConversion64(0, 0, c.Arg(4), c.Arg(2)));

			// --- files, locks, once, errors ---
			Export(p, "?_Fiopen@std@@YAPEAU_iobuf@@PEBDHH@Z", 0, c => FileStreamOpen(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(1)));
			Export(p, "?_Fiopen@std@@YAPEAU_iobuf@@PEB_WHH@Z", 0, c => FileStreamOpen(Memory.ReadWString(c.Arg(0)), c.Arg(1)));
			Export(p, "??0_Lockit@std@@QEAA@H@Z", 0, c => c.Arg(0));
			Export(p, "??1_Lockit@std@@QEAA@XZ", 0, c => 0);
			Export(p, "?_Execute_once@std@@YAHAEAUonce_flag@1@P6AHPEAX1PEAPEAX@Z1@Z", 0, c =>
			{
				uint flag = c.Arg(0), callback = c.Arg(1), parameter = c.Arg(2);
				if (Memory.Read32(flag) != 0)
				{
					return 1;
				}

				uint result = (uint)c.Long.Call(callback, flag, parameter, 0);
				if (result != 0)
				{
					Memory.Write64(flag, 2);
				}

				return result;
			});
			Export(p, "?_Syserror_map@std@@YAPEBDH@Z", 0, c => systemErrorText != 0 ? systemErrorText : systemErrorText = StaticCString("unknown error"));
			Export(p, "?_Winerror_map@std@@YAHH@Z", 0, c => 0);
			Export(p, "?_Winerror_message@std@@YAKKPEADK@Z", 0, c =>
			{
				if (c.Arg(1) != 0 && c.Arg(2) != 0)
				{
					Memory.Write8(c.Arg(1), 0);
				}

				return 0;
			});
			Export(p, "?_Xlength_error@std@@YAXPEBD@Z", 0, c => throw StandardThrow("length_error", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xout_of_range@std@@YAXPEBD@Z", 0, c => throw StandardThrow("out_of_range", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xinvalid_argument@std@@YAXPEBD@Z", 0, c => throw StandardThrow("invalid_argument", Memory.ReadCString(c.Arg(0)), "logic_error", "exception"));
			Export(p, "?_Xbad_alloc@std@@YAXXZ", 0, c => throw StandardThrow("bad_alloc", "bad allocation", "exception"));
		}

		private void EnsureCppRuntime64()
		{
			if (globalLocimp != 0)
			{
				return;
			}

			uint destructor = Cpu.AddHostFunction("msvcp140!facet destructor", c => c.Arg(0), 0);
			uint incref = Cpu.AddHostFunction("msvcp140!facet _Incref", c => { Memory.Write32(c.Arg(0) + 8, Memory.Read32(c.Arg(0) + 8) + 1); return 0; }, 0);
			uint decref = Cpu.AddHostFunction("msvcp140!facet _Decref", c => 0, 0);

			uint locimpVftable = AllocateStatic(24);
			Memory.Write64(locimpVftable, destructor);
			Memory.Write64(locimpVftable + 8, incref);
			Memory.Write64(locimpVftable + 16, decref);
			globalLocimp = AllocateStatic(64);
			Memory.Write64(globalLocimp, locimpVftable);
			Memory.Write32(globalLocimp + 8, 1);
			Memory.Write32(globalLocimp + 32, 0x3F);
			Memory.Write8(globalLocimp + 36, 1);
			Memory.Write64(globalLocimp + 40, StaticCString("C"));

			uint[] codecvtSlots =
			{
				destructor, incref, decref,
				Cpu.AddHostFunction("msvcp140!codecvt do_always_noconv", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_max_length", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_encoding", c => 1, 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_in", c => NoConversion64(c.Arg(4), c.Arg(2), c.Arg(7), c.Arg(5)), 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_out", c => NoConversion64(c.Arg(4), c.Arg(2), c.Arg(7), c.Arg(5)), 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_unshift", c => NoConversion64(0, 0, c.Arg(4), c.Arg(2)), 0),
				Cpu.AddHostFunction("msvcp140!codecvt do_length", c => Math.Min(c.Arg(3) - c.Arg(2), c.Arg(4)), 0)
			};
			codecvtVftable = Table64(codecvtSlots);

			string streambuf = "?$basic_streambuf@DU?$char_traits@D@std@@@std@@";
			streambufVftable = Table64(new[]
			{
				Cpu.AddHostFunction("msvcp140!basic_streambuf destructor", c => c.Arg(0), 0),
				HostExport("msvcp140", "?_Lock@" + streambuf + "UEAAXXZ"),
				HostExport("msvcp140", "?_Unlock@" + streambuf + "UEAAXXZ"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf overflow", c => StreamEof, 0),
				Cpu.AddHostFunction("msvcp140!basic_streambuf pbackfail", c => StreamEof, 0),
				HostExport("msvcp140", "?showmanyc@" + streambuf + "MEAA_JXZ"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf underflow", c => StreamEof, 0),
				HostExport("msvcp140", "?uflow@" + streambuf + "MEAAHXZ"),
				HostExport("msvcp140", "?xsgetn@" + streambuf + "MEAA_JPEAD_J@Z"),
				HostExport("msvcp140", "?xsputn@" + streambuf + "MEAA_JPEBD_J@Z"),
				Cpu.AddHostFunction("msvcp140!basic_streambuf seekoff", c => { Memory.Write64(c.Arg(1), ulong.MaxValue); return c.Arg(1); }, 0),
				Cpu.AddHostFunction("msvcp140!basic_streambuf seekpos", c => { Memory.Write64(c.Arg(1), ulong.MaxValue); return c.Arg(1); }, 0),
				HostExport("msvcp140", "?setbuf@" + streambuf + "MEAAPEAV12@PEAD_J@Z"),
				HostExport("msvcp140", "?sync@" + streambuf + "MEAAHXZ"),
				HostExport("msvcp140", "?imbue@" + streambuf + "MEAAXAEBVlocale@2@@Z")
			});

			iosVftable = Table64(new[] { Cpu.AddHostFunction("msvcp140!basic_ios destructor", c => c.Arg(0), 0) });
			// A lone basic_istream's virtual base table: 32-bit offsets, its own place, then basic_ios 16 bytes on.
			istreamVbtable = AllocateStatic(8);
			Memory.Write32(istreamVbtable, 0);
			Memory.Write32(istreamVbtable + 4, 16);
		}

		private uint Table64(uint[] slots)
		{
			uint table = AllocateStatic(8 * (uint)slots.Length);
			for (int i = 0; i < slots.Length; i++)
			{
				Memory.Write64(table + 8 * (uint)i, slots[i]);
			}

			return table;
		}

		private uint NewLocale64()
		{
			uint locale = HeapAllocate(16);
			Memory.Write64(locale + 8, globalLocimp);
			return locale;
		}

		private void StreambufInit64(uint self, uint getFirst, uint getNext, uint getCount, uint putFirst, uint putNext, uint putCount)
		{
			Memory.Write64(self + 24, getFirst);
			Memory.Write64(self + 32, putFirst);
			Memory.Write64(self + 56, getNext);
			Memory.Write64(self + 64, putNext);
			Memory.Write64(self + 80, getCount);
			Memory.Write64(self + 88, putCount);
			if (getFirst == self + 8)
			{
				Memory.Fill(self + 8, 0, 16);
				Memory.Fill(self + 40, 0, 16);
				Memory.Fill(self + 72, 0, 8);
			}
		}

		private uint GetAvailable64(uint self)
		{
			return Memory.Read32(Memory.Read32(self + 56)) != 0 ? Memory.Read32(Memory.Read32(self + 80)) : 0;
		}

		private uint GetNextAndAdvance64(uint self)
		{
			uint count = Memory.Read32(self + 80), next = Memory.Read32(self + 56);
			Memory.Write32(count, Memory.Read32(count) - 1);
			uint at = Memory.Read32(next);
			Memory.Write64(next, at + 1);
			return at;
		}

		private uint Sgetc64(uint self)
		{
			return GetAvailable64(self) > 0 ? Memory.Read8(Memory.Read32(Memory.Read32(self + 56))) : (uint)CallVirtual64(self, 6);
		}

		private uint Sbumpc64(uint self)
		{
			return GetAvailable64(self) > 0 ? Memory.Read8(GetNextAndAdvance64(self)) : (uint)CallVirtual64(self, 7);
		}

		private uint Snextc64(uint self)
		{
			if (GetAvailable64(self) > 1)
			{
				uint next = Memory.Read32(self + 56), count = Memory.Read32(self + 80);
				Memory.Write64(next, Memory.Read32(next) + 1);
				Memory.Write32(count, Memory.Read32(count) - 1);
				return Memory.Read8(Memory.Read32(next));
			}

			return Sbumpc64(self) == StreamEof ? StreamEof : Sgetc64(self);
		}

		/// <summary>Calls a virtual function by its slot in the object's table, with the object as the first argument.</summary>
		private ulong CallVirtual64(uint self, int slot, params uint[] args)
		{
			uint function = Memory.Read32(Memory.Read32(self) + 8 * (uint)slot);
			ulong[] all = new ulong[args.Length + 1];
			all[0] = self;
			for (int i = 0; i < args.Length; i++)
			{
				all[i + 1] = args[i];
			}

			return Cpu.Long.Call(function, all);
		}

		private uint IosOf64(uint istream)
		{
			return istream + Memory.Read32(Memory.Read32(istream) + 4);
		}

		private void IosInit64(uint ios, uint buffer)
		{
			Memory.Write64(ios + 8, 0); // _Stdstr
			Memory.Write32(ios + 20, 0); // _Except
			Memory.Write32(ios + 24, 0x201); // skipws | dec
			Memory.Write64(ios + 32, 6); // _Prec
			Memory.Write64(ios + 40, 0); // _Wide
			Memory.Write64(ios + 48, 0); // _Arr
			Memory.Write64(ios + 56, 0); // _Calls
			Memory.Write64(ios + 64, NewLocale64()); // _Ploc
			Memory.Write64(ios + 72, buffer);
			Memory.Write64(ios + 80, 0);
			Memory.Write8(ios + 88, (byte)' ');
			IosClear64(ios, IosGood);
		}

		private void IosClear64(uint ios, uint state)
		{
			state = (state | (Memory.Read32(ios + 72) == 0 ? IosBad : IosGood)) & 0x17;
			Memory.Write32(ios + 16, state);
			if ((state & Memory.Read32(ios + 20)) != 0)
			{
				Log("plugin: a stream's state " + state + " would throw ios_base::failure (not raised)");
			}
		}

		private uint NoConversion64(uint fromNext, uint from, uint toNext, uint to)
		{
			if (fromNext != 0)
			{
				Memory.Write64(fromNext, from);
			}

			if (toNext != 0)
			{
				Memory.Write64(toNext, to);
			}

			return 3; // codecvt_base::noconv
		}
	}
}
