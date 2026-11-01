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
	/// <summary>SSE and SSE2: all x64 floating-point, and the block moves and vector code compilers emit.</summary>
	public sealed partial class Cpu64
	{
		/// <summary>XMM0 to XMM15, two 64-bit halves each: X[2 * n] is the low half.</summary>
		public readonly ulong[] X = new ulong[32];

		private uint mxcsr = 0x1F80;

		private static float F(ulong bits) => Compat.Int32BitsToSingle((int)(uint)bits);
		private static ulong B(float value) => (uint)Compat.SingleToInt32Bits(value);
		private static double D(ulong bits) => BitConverter.Int64BitsToDouble((long)bits);
		private static ulong B(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

		/// <summary>The instruction's r/m operand as 128 bits: an XMM register or 16 bytes of memory.</summary>
		private void ReadRm128(out ulong low, out ulong high)
		{
			if (ModIsRegister)
			{
				low = X[2 * modRm];
				high = X[2 * modRm + 1];
			}
			else
			{
				ulong address = Ea;
				low = ReadMem(address, 64);
				high = ReadMem(address + 8, 64);
			}
		}

		private ulong ReadRmX(int size)
		{
			return ModIsRegister ? (size == 32 ? (uint)X[2 * modRm] : X[2 * modRm]) : ReadMem(Ea, size);
		}

		private void WriteRm128(ulong low, ulong high)
		{
			if (ModIsRegister)
			{
				X[2 * modRm] = low;
				X[2 * modRm + 1] = high;
			}
			else
			{
				ulong address = Ea;
				WriteMem(address, 64, low);
				WriteMem(address + 8, 64, high);
			}
		}

		private static float ScalarF(int op, float a, float b)
		{
			switch (op)
			{
				case 0x51: return (float)Math.Sqrt(b);
				case 0x58: return a + b;
				case 0x59: return a * b;
				case 0x5C: return a - b;
				case 0x5D: return a < b ? a : b;
				case 0x5E: return a / b;
				default: return a > b ? a : b; // 0x5F
			}
		}

		private static double ScalarD(int op, double a, double b)
		{
			switch (op)
			{
				case 0x51: return Math.Sqrt(b);
				case 0x58: return a + b;
				case 0x59: return a * b;
				case 0x5C: return a - b;
				case 0x5D: return a < b ? a : b;
				case 0x5E: return a / b;
				default: return a > b ? a : b;
			}
		}

		private static bool Compare(int predicate, double a, double b)
		{
			switch (predicate & 7)
			{
				case 0: return a == b;
				case 1: return a < b;
				case 2: return a <= b;
				case 3: return double.IsNaN(a) || double.IsNaN(b);
				case 4: return a != b;
				case 5: return !(a < b);
				case 6: return !(a <= b);
				default: return !double.IsNaN(a) && !double.IsNaN(b);
			}
		}

		/// <summary>Rounds by MXCSR's rounding mode (or truncates), as CVTSS2SI and the rest do; out of range gives the "indefinite" value.</summary>
		private long ToInteger(double value, bool truncate, int bits)
		{
			double rounded;
			if (truncate)
			{
				rounded = Math.Truncate(value);
			}
			else
			{
				switch ((mxcsr >> 13) & 3)
				{
					case 0: rounded = Math.Round(value, MidpointRounding.ToEven); break;
					case 1: rounded = Math.Floor(value); break;
					case 2: rounded = Math.Ceiling(value); break;
					default: rounded = Math.Truncate(value); break;
				}
			}

			if (bits == 64)
			{
				return double.IsNaN(rounded) || rounded >= 9.2233720368547758E18 || rounded < -9.2233720368547758E18 ? long.MinValue : (long)rounded;
			}

			return double.IsNaN(rounded) || rounded >= 2147483648.0 || rounded < -2147483648.0 ? int.MinValue : (int)rounded;
		}

		private static ulong Saturate(long value, long low, long high)
		{
			return (ulong)(value < low ? low : value > high ? high : value);
		}

		/// <summary>Applies a function to each element of two 128-bit values.</summary>
		private static void Lanes(int bits, ulong aLow, ulong aHigh, ulong bLow, ulong bHigh, Func<ulong, ulong, ulong> function, out ulong low, out ulong high)
		{
			low = LanesOf(bits, aLow, bLow, function);
			high = LanesOf(bits, aHigh, bHigh, function);
		}

		private static ulong LanesOf(int bits, ulong a, ulong b, Func<ulong, ulong, ulong> function)
		{
			ulong mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1, result = 0;
			for (int shift = 0; shift < 64; shift += bits)
			{
				result |= (function((a >> shift) & mask, (b >> shift) & mask) & mask) << shift;
			}

			return result;
		}

		private static byte ByteOf(ulong low, ulong high, int index)
		{
			return (byte)((index < 8 ? low >> (8 * index) : high >> (8 * (index - 8))) & 0xFF);
		}

		private void SetByte(ref ulong low, ref ulong high, int index, byte value)
		{
			if (index < 8)
			{
				low = (low & ~(0xFFUL << (8 * index))) | ((ulong)value << (8 * index));
			}
			else
			{
				high = (high & ~(0xFFUL << (8 * (index - 8)))) | ((ulong)value << (8 * (index - 8)));
			}
		}

		private void StepSse(byte op)
		{
			// The mandatory prefix decides the form: none, 66, F3 or F2.
			int form = repz ? 2 : repnz ? 3 : opSize16 ? 1 : 0;
			DecodeModRm();
			int reg = 2 * modReg;
			switch (op)
			{
				case 0x10: // MOVUPS, MOVUPD, MOVSS, MOVSD
					if (form < 2)
					{
						ReadRm128(out ulong low, out ulong high);
						X[reg] = low;
						X[reg + 1] = high;
					}
					else if (form == 2)
					{
						ulong value = ReadRmX(32);
						X[reg] = ModIsRegister ? (X[reg] & 0xFFFFFFFF00000000) | value : value;
						if (!ModIsRegister) X[reg + 1] = 0;
					}
					else
					{
						X[reg] = ReadRmX(64);
						if (!ModIsRegister) X[reg + 1] = 0;
					}

					return;
				case 0x11:
					if (form < 2)
					{
						WriteRm128(X[reg], X[reg + 1]);
					}
					else if (form == 2)
					{
						if (ModIsRegister) X[2 * modRm] = (X[2 * modRm] & 0xFFFFFFFF00000000) | (uint)X[reg];
						else WriteMem(Ea, 32, (uint)X[reg]);
					}
					else
					{
						if (ModIsRegister) X[2 * modRm] = X[reg];
						else WriteMem(Ea, 64, X[reg]);
					}

					return;
				case 0x12: // MOVLPS, MOVLPD, MOVHLPS, MOVDDUP (not: SSE3)
					if (form == 0 && ModIsRegister) X[reg] = X[2 * modRm + 1];
					else if (form < 2) X[reg] = ReadMem(Ea, 64);
					else throw Unsupported("SSE3 0F 12");
					return;
				case 0x13:
					WriteMem(Ea, 64, X[reg]);
					return;
				case 0x14: // UNPCKLPS, UNPCKLPD
				{
					ReadRm128(out ulong low, out ulong _);
					if (form == 1)
					{
						X[reg + 1] = low;
					}
					else
					{
						ulong a = X[reg];
						X[reg] = (uint)a | (low << 32);
						X[reg + 1] = (a >> 32) | (low & 0xFFFFFFFF00000000);
					}

					return;
				}
				case 0x15: // UNPCKHPS, UNPCKHPD
				{
					ReadRm128(out ulong _, out ulong high);
					if (form == 1)
					{
						X[reg] = X[reg + 1];
						X[reg + 1] = high;
					}
					else
					{
						ulong a = X[reg + 1];
						X[reg] = (uint)a | (high << 32);
						X[reg + 1] = (a >> 32) | (high & 0xFFFFFFFF00000000);
					}

					return;
				}
				case 0x16: // MOVHPS, MOVHPD, MOVLHPS
					X[reg + 1] = form == 0 && ModIsRegister ? X[2 * modRm] : ReadMem(Ea, 64);
					return;
				case 0x17:
					WriteMem(Ea, 64, X[reg + 1]);
					return;
				case 0x28: // MOVAPS, MOVAPD
				{
					ReadRm128(out ulong low, out ulong high);
					X[reg] = low;
					X[reg + 1] = high;
					return;
				}
				case 0x29: case 0x2B:
					WriteRm128(X[reg], X[reg + 1]);
					return;
				case 0x2A: // CVTSI2SS, CVTSI2SD
				{
					if (form < 2) throw Unsupported("MMX 0F 2A");
					long value = RexW ? (long)ReadRm(64) : (int)ReadRm(32);
					if (form == 2) X[reg] = (X[reg] & 0xFFFFFFFF00000000) | B((float)value);
					else X[reg] = B((double)value);
					return;
				}
				case 0x2C: case 0x2D: // CVTT..2SI, CVT..2SI
				{
					if (form < 2) throw Unsupported("MMX 0F " + op.ToString("X2"));
					double value = form == 2 ? F(ReadRmX(32)) : D(ReadRmX(64));
					int bits = RexW ? 64 : 32;
					SetReg(modReg, bits, (ulong)ToInteger(value, op == 0x2C, bits));
					return;
				}
				case 0x2E: case 0x2F: // UCOMIS, COMIS
				{
					double a = form == 1 ? D(X[reg]) : F(X[reg]);
					double b = form == 1 ? D(ReadRmX(64)) : F(ReadRmX(32));
					OF = SF = AF = false;
					if (double.IsNaN(a) || double.IsNaN(b))
					{
						ZF = PF = CF = true;
					}
					else
					{
						ZF = a == b;
						PF = false;
						CF = a < b;
					}

					return;
				}
				case 0x50: // MOVMSKPS, MOVMSKPD
				{
					ulong low = X[2 * modRm], high = X[2 * modRm + 1];
					ulong mask = form == 1
						? (low >> 63) | ((high >> 63) << 1)
						: ((low >> 31) & 1) | ((low >> 63) << 1) | (((high >> 31) & 1) << 2) | ((high >> 63) << 3);
					SetReg(modReg, 32, mask);
					return;
				}
				case 0x51: case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
					if (form == 2)
					{
						X[reg] = (X[reg] & 0xFFFFFFFF00000000) | B(ScalarF(op, F(X[reg]), F(ReadRmX(32))));
					}
					else if (form == 3)
					{
						X[reg] = B(ScalarD(op, D(X[reg]), D(ReadRmX(64))));
					}
					else
					{
						ReadRm128(out ulong low, out ulong high);
						if (form == 1)
						{
							X[reg] = B(ScalarD(op, D(X[reg]), D(low)));
							X[reg + 1] = B(ScalarD(op, D(X[reg + 1]), D(high)));
						}
						else
						{
							X[reg] = B(ScalarF(op, F(X[reg]), F(low))) | (B(ScalarF(op, F(X[reg] >> 32), F(low >> 32))) << 32);
							X[reg + 1] = B(ScalarF(op, F(X[reg + 1]), F(high))) | (B(ScalarF(op, F(X[reg + 1] >> 32), F(high >> 32))) << 32);
						}
					}

					return;
				case 0x54: case 0x55: case 0x56: case 0x57: case 0xDB: case 0xDF: case 0xEB: case 0xEF:
				{
					ReadRm128(out ulong low, out ulong high);
					switch (op)
					{
						case 0x54: case 0xDB: X[reg] &= low; X[reg + 1] &= high; break;
						case 0x55: case 0xDF: X[reg] = ~X[reg] & low; X[reg + 1] = ~X[reg + 1] & high; break;
						case 0x56: case 0xEB: X[reg] |= low; X[reg + 1] |= high; break;
						default: X[reg] ^= low; X[reg + 1] ^= high; break;
					}

					return;
				}
				case 0x5A: // CVTPS2PD, CVTPD2PS, CVTSS2SD, CVTSD2SS
					if (form == 2)
					{
						X[reg] = B((double)F(ReadRmX(32)));
					}
					else if (form == 3)
					{
						X[reg] = (X[reg] & 0xFFFFFFFF00000000) | B((float)D(ReadRmX(64)));
					}
					else if (form == 1)
					{
						ReadRm128(out ulong low, out ulong high);
						X[reg] = B((float)D(low)) | (B((float)D(high)) << 32);
						X[reg + 1] = 0;
					}
					else
					{
						ulong low = ReadRmX(64);
						X[reg] = B((double)F(low));
						X[reg + 1] = B((double)F(low >> 32));
					}

					return;
				case 0x5B: // CVTDQ2PS, CVTPS2DQ, CVTTPS2DQ
				{
					ReadRm128(out ulong low, out ulong high);
					if (form == 0)
					{
						X[reg] = B((float)(int)low) | (B((float)(int)(low >> 32)) << 32);
						X[reg + 1] = B((float)(int)high) | (B((float)(int)(high >> 32)) << 32);
					}
					else
					{
						bool truncate = form == 2;
						X[reg] = (uint)ToInteger(F(low), truncate, 32) | ((ulong)(uint)ToInteger(F(low >> 32), truncate, 32) << 32);
						X[reg + 1] = (uint)ToInteger(F(high), truncate, 32) | ((ulong)(uint)ToInteger(F(high >> 32), truncate, 32) << 32);
					}

					return;
				}
				case 0xE6: // CVTDQ2PD, CVTTPD2DQ, CVTPD2DQ
				{
					ReadRm128(out ulong low, out ulong high);
					if (form == 2)
					{
						X[reg] = B((double)(int)low);
						X[reg + 1] = B((double)(int)(low >> 32));
					}
					else
					{
						bool truncate = form == 1;
						X[reg] = (uint)ToInteger(D(low), truncate, 32) | ((ulong)(uint)ToInteger(D(high), truncate, 32) << 32);
						X[reg + 1] = 0;
					}

					return;
				}
				case 0xC2: // CMPPS, CMPPD, CMPSS, CMPSD
				{
					if (form == 2)
					{
						ulong operand = ReadRmXAfterImm(32, out int predicate);
						X[reg] = (X[reg] & 0xFFFFFFFF00000000) | (Compare(predicate, F(X[reg]), F(operand)) ? 0xFFFFFFFFUL : 0);
					}
					else if (form == 3)
					{
						ulong operand = ReadRmXAfterImm(64, out int predicate);
						X[reg] = Compare(predicate, D(X[reg]), D(operand)) ? ulong.MaxValue : 0;
					}
					else
					{
						int predicate = Fetch8();
						ReadRm128(out ulong low, out ulong high);
						if (form == 1)
						{
							X[reg] = Compare(predicate, D(X[reg]), D(low)) ? ulong.MaxValue : 0;
							X[reg + 1] = Compare(predicate, D(X[reg + 1]), D(high)) ? ulong.MaxValue : 0;
						}
						else
						{
							X[reg] = (Compare(predicate, F(X[reg]), F(low)) ? 0xFFFFFFFFUL : 0) | (Compare(predicate, F(X[reg] >> 32), F(low >> 32)) ? 0xFFFFFFFF00000000UL : 0);
							X[reg + 1] = (Compare(predicate, F(X[reg + 1]), F(high)) ? 0xFFFFFFFFUL : 0) | (Compare(predicate, F(X[reg + 1] >> 32), F(high >> 32)) ? 0xFFFFFFFF00000000UL : 0);
						}
					}

					return;
				}
				case 0xC6: // SHUFPS, SHUFPD
				{
					int selector = Fetch8();
					ReadRm128(out ulong low, out ulong high);
					if (form == 1)
					{
						ulong a = (selector & 1) != 0 ? X[reg + 1] : X[reg];
						ulong b = (selector & 2) != 0 ? high : low;
						X[reg] = a;
						X[reg + 1] = b;
					}
					else
					{
						uint[] a = { (uint)X[reg], (uint)(X[reg] >> 32), (uint)X[reg + 1], (uint)(X[reg + 1] >> 32) };
						uint[] b = { (uint)low, (uint)(low >> 32), (uint)high, (uint)(high >> 32) };
						X[reg] = a[selector & 3] | ((ulong)a[(selector >> 2) & 3] << 32);
						X[reg + 1] = b[(selector >> 4) & 3] | ((ulong)b[(selector >> 6) & 3] << 32);
					}

					return;
				}
				case 0x6E: // MOVD, MOVQ to XMM
					if (form != 1) throw Unsupported("MMX 0F 6E");
					X[reg] = RexW ? ReadRm(64) : ReadRm(32);
					X[reg + 1] = 0;
					return;
				case 0x7E:
					if (form == 2) // MOVQ xmm, xmm/m64
					{
						X[reg] = ReadRmX(64);
						X[reg + 1] = 0;
					}
					else if (form == 1) // MOVD, MOVQ from XMM
					{
						if (RexW) WriteRm(64, X[reg]);
						else WriteRm(32, (uint)X[reg]);
					}
					else
					{
						throw Unsupported("MMX 0F 7E");
					}

					return;
				case 0x6F: // MOVDQA, MOVDQU
				{
					if (form == 0 || form == 3) throw Unsupported("MMX 0F 6F");
					ReadRm128(out ulong low, out ulong high);
					X[reg] = low;
					X[reg + 1] = high;
					return;
				}
				case 0x7F: case 0xE7: // MOVDQA, MOVDQU, MOVNTDQ
					if (form == 0 || form == 3) throw Unsupported("MMX 0F " + op.ToString("X2"));
					WriteRm128(X[reg], X[reg + 1]);
					return;
				case 0xD6: // MOVQ xmm/m64, xmm
					if (ModIsRegister)
					{
						X[2 * modRm] = X[reg];
						X[2 * modRm + 1] = 0;
					}
					else
					{
						WriteMem(Ea, 64, X[reg]);
					}

					return;
				case 0xC3: // MOVNTI
					WriteMem(Ea, RexW ? 64 : 32, GetReg(modReg, RexW ? 64 : 32));
					return;
				case 0x70: // PSHUFD, PSHUFHW, PSHUFLW
				{
					int selector = Fetch8();
					ReadRm128(out ulong low, out ulong high);
					if (form == 1)
					{
						uint[] s = { (uint)low, (uint)(low >> 32), (uint)high, (uint)(high >> 32) };
						X[reg] = s[selector & 3] | ((ulong)s[(selector >> 2) & 3] << 32);
						X[reg + 1] = s[(selector >> 4) & 3] | ((ulong)s[(selector >> 6) & 3] << 32);
					}
					else if (form == 2 || form == 3)
					{
						ulong words = form == 2 ? high : low;
						ulong shuffled = 0;
						for (int i = 0; i < 4; i++)
						{
							shuffled |= ((words >> (16 * ((selector >> (2 * i)) & 3))) & 0xFFFF) << (16 * i);
						}

						X[reg] = form == 2 ? low : shuffled;
						X[reg + 1] = form == 2 ? shuffled : high;
					}
					else
					{
						throw Unsupported("MMX 0F 70");
					}

					return;
				}
				case 0xC4: // PINSRW
				{
					int index = Fetch8() & 7;
					ulong value = ModIsRegister ? GetReg(modRm, 32) & 0xFFFF : ReadMem(Ea, 16);
					int half = reg + (index >> 2);
					int shift = 16 * (index & 3);
					X[half] = (X[half] & ~(0xFFFFUL << shift)) | (value << shift);
					return;
				}
				case 0xC5: // PEXTRW
				{
					int index = Fetch8() & 7;
					SetReg(modReg, 32, (X[2 * modRm + (index >> 2)] >> (16 * (index & 3))) & 0xFFFF);
					return;
				}
				case 0xD7: // PMOVMSKB
				{
					ulong low = X[2 * modRm], high = X[2 * modRm + 1];
					ulong mask = 0;
					for (int i = 0; i < 16; i++)
					{
						if ((ByteOf(low, high, i) & 0x80) != 0)
						{
							mask |= 1UL << i;
						}
					}

					SetReg(modReg, 32, mask);
					return;
				}
				case 0x71: case 0x72: case 0x73: // shifts by an immediate
				{
					int kind = modReg & 7;
					int count = Fetch8();
					int target = 2 * modRm;
					int bits = op == 0x71 ? 16 : op == 0x72 ? 32 : 64;
					if (op == 0x73 && (kind == 3 || kind == 7))
					{
						ShiftBytes(target, kind == 7, count);
						return;
					}

					ShiftLanes(target, target, bits, kind == 2 ? 0 : kind == 4 ? 1 : 2, (ulong)count);
					return;
				}
				case 0xD1: case 0xD2: case 0xD3: case 0xE1: case 0xE2: case 0xF1: case 0xF2: case 0xF3:
				{
					ReadRm128(out ulong count, out ulong _);
					int bits = (op & 3) == 1 ? 16 : (op & 3) == 2 ? 32 : 64;
					ShiftLanes(reg, reg, bits, (op & 0xF0) == 0xD0 ? 0 : (op & 0xF0) == 0xE0 ? 1 : 2, count);
					return;
				}
				default:
					Vector(op, reg);
					return;
			}
		}

		private ulong ReadRmXAfterImm(int size, out int predicate)
		{
			predicate = Fetch8();
			return ReadRmX(size);
		}

		/// <summary>The element-wise shifts: kind 0 logical right, 1 arithmetic right, 2 left.</summary>
		private void ShiftLanes(int source, int target, int bits, int kind, ulong count)
		{
			ulong Apply(ulong value, ulong _)
			{
				if (kind == 1)
				{
					int c = count >= (ulong)bits ? bits - 1 : (int)count;
					return (ulong)((long)SignExtend(value, bits) >> c);
				}

				if (count >= (ulong)bits) return 0;
				return kind == 0 ? value >> (int)count : value << (int)count;
			}

			X[target] = LanesOf(bits, X[source], 0, Apply);
			X[target + 1] = LanesOf(bits, X[source + 1], 0, Apply);
		}

		private void ShiftBytes(int target, bool left, int count)
		{
			ulong low = X[target], high = X[target + 1], newLow = 0, newHigh = 0;
			if (count > 15)
			{
				X[target] = X[target + 1] = 0;
				return;
			}

			for (int i = 0; i < 16; i++)
			{
				int from = left ? i - count : i + count;
				byte value = from >= 0 && from < 16 ? ByteOf(low, high, from) : (byte)0;
				SetByte(ref newLow, ref newHigh, i, value);
			}

			X[target] = newLow;
			X[target + 1] = newHigh;
		}

		/// <summary>The integer vector instructions that take two 128-bit operands.</summary>
		private void Vector(byte op, int reg)
		{
			if (!opSize16)
			{
				throw Unsupported("MMX or SSE 0F " + op.ToString("X2"));
			}

			ReadRm128(out ulong bLow, out ulong bHigh);
			ulong aLow = X[reg], aHigh = X[reg + 1], low, high;
			switch (op)
			{
				case 0x60: low = Interleave(aLow, bLow, 8, false); high = Interleave(aLow, bLow, 8, true); break;
				case 0x61: low = Interleave(aLow, bLow, 16, false); high = Interleave(aLow, bLow, 16, true); break;
				case 0x62: low = Interleave(aLow, bLow, 32, false); high = Interleave(aLow, bLow, 32, true); break;
				case 0x6C: low = aLow; high = bLow; break;
				case 0x68: low = Interleave(aHigh, bHigh, 8, false); high = Interleave(aHigh, bHigh, 8, true); break;
				case 0x69: low = Interleave(aHigh, bHigh, 16, false); high = Interleave(aHigh, bHigh, 16, true); break;
				case 0x6A: low = Interleave(aHigh, bHigh, 32, false); high = Interleave(aHigh, bHigh, 32, true); break;
				case 0x6D: low = aHigh; high = bHigh; break;
				case 0x63: // PACKSSWB
					low = Pack(aLow, aHigh, 16, -128, 127);
					high = Pack(bLow, bHigh, 16, -128, 127);
					break;
				case 0x67: // PACKUSWB
					low = Pack(aLow, aHigh, 16, 0, 255);
					high = Pack(bLow, bHigh, 16, 0, 255);
					break;
				case 0x6B: // PACKSSDW
					low = Pack(aLow, aHigh, 32, -32768, 32767);
					high = Pack(bLow, bHigh, 32, -32768, 32767);
					break;
				case 0x64: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => (sbyte)a > (sbyte)b ? ulong.MaxValue : 0, out low, out high); break;
				case 0x65: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (short)a > (short)b ? ulong.MaxValue : 0, out low, out high); break;
				case 0x66: Lanes(32, aLow, aHigh, bLow, bHigh, (a, b) => (int)a > (int)b ? ulong.MaxValue : 0, out low, out high); break;
				case 0x74: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => a == b ? ulong.MaxValue : 0, out low, out high); break;
				case 0x75: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => a == b ? ulong.MaxValue : 0, out low, out high); break;
				case 0x76: Lanes(32, aLow, aHigh, bLow, bHigh, (a, b) => a == b ? ulong.MaxValue : 0, out low, out high); break;
				case 0xD4: low = aLow + bLow; high = aHigh + bHigh; break;
				case 0xFB: low = aLow - bLow; high = aHigh - bHigh; break;
				case 0xFC: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => a + b, out low, out high); break;
				case 0xFD: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => a + b, out low, out high); break;
				case 0xFE: Lanes(32, aLow, aHigh, bLow, bHigh, (a, b) => a + b, out low, out high); break;
				case 0xF8: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => a - b, out low, out high); break;
				case 0xF9: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => a - b, out low, out high); break;
				case 0xFA: Lanes(32, aLow, aHigh, bLow, bHigh, (a, b) => a - b, out low, out high); break;
				case 0xD8: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => a > b ? a - b : 0, out low, out high); break;
				case 0xD9: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => a > b ? a - b : 0, out low, out high); break;
				case 0xDC: Lanes(8, aLow, aHigh, bLow, bHigh, (a, b) => Math.Min(a + b, 255), out low, out high); break;
				case 0xDD: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => Math.Min(a + b, 65535), out low, out high); break;
				case 0xDA: Lanes(8, aLow, aHigh, bLow, bHigh, Math.Min, out low, out high); break;
				case 0xDE: Lanes(8, aLow, aHigh, bLow, bHigh, Math.Max, out low, out high); break;
				case 0xEA: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (short)a < (short)b ? a : b, out low, out high); break;
				case 0xEE: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (short)a > (short)b ? a : b, out low, out high); break;
				case 0xD5: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (ulong)((short)a * (short)b), out low, out high); break;
				case 0xE5: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (ulong)(((short)a * (short)b) >> 16), out low, out high); break;
				case 0xE4: Lanes(16, aLow, aHigh, bLow, bHigh, (a, b) => (a * b) >> 16, out low, out high); break;
				case 0xF4: low = (uint)aLow * (ulong)(uint)bLow; high = (uint)aHigh * (ulong)(uint)bHigh; break;
				default:
					throw Unsupported("SSE 66 0F " + op.ToString("X2"));
			}

			X[reg] = low;
			X[reg + 1] = high;
		}

		/// <summary>One 64-bit half of the interleaving of two 64-bit values, elements of a first.</summary>
		private static ulong Interleave(ulong a, ulong b, int bits, bool upper)
		{
			ulong mask = (1UL << bits) - 1, result = 0;
			int count = 32 / bits;
			int first = upper ? count : 0;
			for (int i = 0; i < count; i++)
			{
				result |= ((a >> (bits * (first + i))) & mask) << (2 * bits * i);
				result |= ((b >> (bits * (first + i))) & mask) << (2 * bits * i + bits);
			}

			return result;
		}

		/// <summary>Packs two 64-bit halves of signed elements into half as many saturated ones.</summary>
		private static ulong Pack(ulong low, ulong high, int bits, long minimum, long maximum)
		{
			ulong result = 0;
			int width = bits / 2;
			int count = 64 / bits;
			ulong mask = (1UL << width) - 1, elementMask = (1UL << bits) - 1;
			for (int i = 0; i < count; i++)
			{
				long l = (long)SignExtend((low >> (bits * i)) & elementMask, bits);
				long h = (long)SignExtend((high >> (bits * i)) & elementMask, bits);
				result |= (Saturate(l, minimum, maximum) & mask) << (width * i);
				result |= (Saturate(h, minimum, maximum) & mask) << (width * (i + count));
			}

			return result;
		}
	}
}
