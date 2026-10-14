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
	/// <summary>SSE and SSE2: the scalar and packed operations compilers emit for float maths and copies.</summary>
	public sealed partial class Cpu
	{
		private readonly ulong[] xmmLo = new ulong[8];
		private readonly ulong[] xmmHi = new ulong[8];
		private uint mxcsr = 0x1F80;

		/// <summary>The low double of an XMM register, for host functions that take arguments there.</summary>
		public double GetXmmLowDouble(int reg) => BitConverter.Int64BitsToDouble((long)xmmLo[reg]);

		/// <summary>Sets the low double of an XMM register (the upper half is left alone, as MOVSD does).</summary>
		public void SetXmmLowDouble(int reg, double value) => xmmLo[reg] = (ulong)BitConverter.DoubleToInt64Bits(value);

		// --- lane access ---

		private float XmmSingle(int reg, int lane)
		{
			ulong half = lane < 2 ? xmmLo[reg] : xmmHi[reg];
			return BitConverter.Int32BitsToSingle((int)(uint)(half >> (32 * (lane & 1))));
		}

		private void SetXmmSingle(int reg, int lane, float value)
		{
			ulong bits = (uint)BitConverter.SingleToInt32Bits(value);
			int shift = 32 * (lane & 1);
			ulong mask = 0xFFFFFFFFUL << shift;
			if (lane < 2)
			{
				xmmLo[reg] = (xmmLo[reg] & ~mask) | (bits << shift);
			}
			else
			{
				xmmHi[reg] = (xmmHi[reg] & ~mask) | (bits << shift);
			}
		}

		private double XmmDouble(int reg, int lane)
		{
			return BitConverter.Int64BitsToDouble((long)(lane == 0 ? xmmLo[reg] : xmmHi[reg]));
		}

		private void SetXmmDouble(int reg, int lane, double value)
		{
			ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
			if (lane == 0)
			{
				xmmLo[reg] = bits;
			}
			else
			{
				xmmHi[reg] = bits;
			}
		}

		private uint XmmDword(int reg, int lane)
		{
			ulong half = lane < 2 ? xmmLo[reg] : xmmHi[reg];
			return (uint)(half >> (32 * (lane & 1)));
		}

		private void SetXmmDword(int reg, int lane, uint value)
		{
			int shift = 32 * (lane & 1);
			ulong mask = 0xFFFFFFFFUL << shift;
			if (lane < 2)
			{
				xmmLo[reg] = (xmmLo[reg] & ~mask) | ((ulong)value << shift);
			}
			else
			{
				xmmHi[reg] = (xmmHi[reg] & ~mask) | ((ulong)value << shift);
			}
		}

		/// <summary>The r/m operand as 128 bits (register or memory).</summary>
		private (ulong Lo, ulong Hi) ReadXmmRm128()
		{
			return ModIsRegister ? (xmmLo[modRm], xmmHi[modRm]) : (Memory.Read64(modAddress), Memory.Read64(modAddress + 8));
		}

		private ulong ReadXmmRm64()
		{
			return ModIsRegister ? xmmLo[modRm] : Memory.Read64(modAddress);
		}

		private uint ReadXmmRm32()
		{
			return ModIsRegister ? (uint)xmmLo[modRm] : Memory.Read32(modAddress);
		}

		private void WriteXmmRm128(ulong lo, ulong hi)
		{
			if (ModIsRegister)
			{
				xmmLo[modRm] = lo;
				xmmHi[modRm] = hi;
			}
			else
			{
				Memory.Write64(modAddress, lo);
				Memory.Write64(modAddress + 8, hi);
			}
		}

		private static float AsSingle(uint bits) => BitConverter.Int32BitsToSingle((int)bits);
		private static double AsDouble(ulong bits) => BitConverter.Int64BitsToDouble((long)bits);
		private static uint Bits(float value) => (uint)BitConverter.SingleToInt32Bits(value);
		private static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

		private static double SseArith(byte op, double a, double b)
		{
			switch (op)
			{
				case 0x58: return a + b;
				case 0x59: return a * b;
				case 0x5C: return a - b;
				case 0x5E: return a / b;
				case 0x5D: return a < b ? a : b; // MIN returns the second operand when either is NaN or both are equal
				case 0x5F: return a > b ? a : b;
				default: throw new InvalidOperationException();
			}
		}

		/// <summary>Rounds as MXCSR says, for CVTSS2SI/CVTSD2SI.</summary>
		private double RoundSse(double value)
		{
			switch ((mxcsr >> 13) & 3)
			{
				case 0: return Math.Round(value, MidpointRounding.ToEven);
				case 1: return Math.Floor(value);
				case 2: return Math.Ceiling(value);
				default: return Math.Truncate(value);
			}
		}

		private static uint ToInt32Indefinite(double value)
		{
			if (double.IsNaN(value) || value >= 2147483648.0 || value < -2147483648.0)
			{
				return 0x80000000;
			}

			return (uint)(int)value;
		}

		private void SseCompareFlags(double a, double b)
		{
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
		}

		/// <summary>Executes an SSE instruction (opcode after 0F). Returns false if it is not one.</summary>
		private bool StepSse(byte op, int osz)
		{
			bool p66 = opSize16, pF3 = repz, pF2 = repnz;
			switch (op)
			{
				case 0x10: // MOVUPS/MOVUPD/MOVSS/MOVSD load
				{
					DecodeModRm();
					int d = modReg;
					if (pF3)
					{
						if (ModIsRegister)
						{
							SetXmmDword(d, 0, (uint)xmmLo[modRm]);
						}
						else
						{
							xmmLo[d] = Memory.Read32(modAddress);
							xmmHi[d] = 0;
						}
					}
					else if (pF2)
					{
						if (ModIsRegister)
						{
							xmmLo[d] = xmmLo[modRm];
						}
						else
						{
							xmmLo[d] = Memory.Read64(modAddress);
							xmmHi[d] = 0;
						}
					}
					else
					{
						(xmmLo[d], xmmHi[d]) = ReadXmmRm128();
					}

					return true;
				}
				case 0x11: // store forms
				{
					DecodeModRm();
					int s = modReg;
					if (pF3)
					{
						if (ModIsRegister) SetXmmDword(modRm, 0, (uint)xmmLo[s]);
						else Memory.Write32(modAddress, (uint)xmmLo[s]);
					}
					else if (pF2)
					{
						if (ModIsRegister) xmmLo[modRm] = xmmLo[s];
						else Memory.Write64(modAddress, xmmLo[s]);
					}
					else
					{
						WriteXmmRm128(xmmLo[s], xmmHi[s]);
					}

					return true;
				}
				case 0x12: // MOVLPS/MOVLPD load (MOVHLPS with a register)
				{
					DecodeModRm();
					if (pF2 || pF3)
					{
						// MOVDDUP / MOVSLDUP (SSE3): not reported by CPUID.
						return false;
					}

					xmmLo[modReg] = ModIsRegister ? xmmHi[modRm] : Memory.Read64(modAddress);
					return true;
				}
				case 0x13:
					DecodeModRm();
					Memory.Write64(modAddress, xmmLo[modReg]);
					return true;
				case 0x16: // MOVHPS/MOVHPD load (MOVLHPS with a register)
					DecodeModRm();
					xmmHi[modReg] = ModIsRegister ? xmmLo[modRm] : Memory.Read64(modAddress);
					return true;
				case 0x17:
					DecodeModRm();
					Memory.Write64(modAddress, xmmHi[modReg]);
					return true;
				case 0x14: // UNPCKLPS / UNPCKLPD
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					if (p66)
					{
						xmmHi[d] = lo;
					}
					else
					{
						uint a0 = XmmDword(d, 0), a1 = XmmDword(d, 1);
						xmmLo[d] = a0 | ((ulong)(uint)lo << 32);
						xmmHi[d] = a1 | ((ulong)(uint)(lo >> 32) << 32);
					}

					return true;
				}
				case 0x15: // UNPCKHPS / UNPCKHPD
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					if (p66)
					{
						xmmLo[d] = xmmHi[d];
						xmmHi[d] = hi;
					}
					else
					{
						uint a2 = XmmDword(d, 2), a3 = XmmDword(d, 3);
						xmmLo[d] = a2 | ((ulong)(uint)hi << 32);
						xmmHi[d] = a3 | ((ulong)(uint)(hi >> 32) << 32);
					}

					return true;
				}
				case 0x28: // MOVAPS / MOVAPD
					DecodeModRm();
					(xmmLo[modReg], xmmHi[modReg]) = ReadXmmRm128();
					return true;
				case 0x29:
				case 0x2B: // MOVNTPS / MOVNTPD
					DecodeModRm();
					WriteXmmRm128(xmmLo[modReg], xmmHi[modReg]);
					return true;
				case 0x2A: // CVTSI2SS / CVTSI2SD
				{
					if (!pF3 && !pF2) return false;
					DecodeModRm();
					int value = (int)ReadRm(32);
					if (pF3) SetXmmSingle(modReg, 0, value);
					else xmmLo[modReg] = Bits((double)value);
					return true;
				}
				case 0x2C: // CVTTSS2SI / CVTTSD2SI
				case 0x2D: // CVTSS2SI / CVTSD2SI
				{
					if (!pF3 && !pF2) return false;
					DecodeModRm();
					double value = pF3 ? AsSingle(ReadXmmRm32()) : AsDouble(ReadXmmRm64());
					value = op == 0x2C ? Math.Truncate(value) : RoundSse(value);
					SetReg(modReg, 32, ToInt32Indefinite(value));
					return true;
				}
				case 0x2E: // UCOMISS / UCOMISD
				case 0x2F: // COMISS / COMISD
				{
					DecodeModRm();
					if (p66) SseCompareFlags(XmmDouble(modReg, 0), AsDouble(ReadXmmRm64()));
					else SseCompareFlags(XmmSingle(modReg, 0), AsSingle(ReadXmmRm32()));
					return true;
				}
				case 0x50: // MOVMSKPS / MOVMSKPD
				{
					DecodeModRm();
					int s = modRm;
					uint mask = p66
						? (uint)((xmmLo[s] >> 63) | ((xmmHi[s] >> 63) << 1))
						: (XmmDword(s, 0) >> 31) | ((XmmDword(s, 1) >> 31) << 1) | ((XmmDword(s, 2) >> 31) << 2) | ((XmmDword(s, 3) >> 31) << 3);
					SetReg(modReg, 32, mask);
					return true;
				}
				case 0x51: // SQRT
					DecodeModRm();
					if (pF3) SetXmmSingle(modReg, 0, MathF.Sqrt(AsSingle(ReadXmmRm32())));
					else if (pF2) xmmLo[modReg] = Bits(Math.Sqrt(AsDouble(ReadXmmRm64())));
					else if (p66)
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						xmmLo[modReg] = Bits(Math.Sqrt(AsDouble(lo)));
						xmmHi[modReg] = Bits(Math.Sqrt(AsDouble(hi)));
					}
					else
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						for (int i = 0; i < 4; i++)
						{
							ulong half = i < 2 ? lo : hi;
							SetXmmSingle(modReg, i, MathF.Sqrt(AsSingle((uint)(half >> (32 * (i & 1))))));
						}
					}

					return true;
				case 0x54: // ANDPS/ANDPD
				case 0x55: // ANDNPS/ANDNPD
				case 0x56: // ORPS/ORPD
				case 0x57: // XORPS/XORPD
				{
					if (pF3 || pF2) return false;
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					switch (op)
					{
						case 0x54: xmmLo[d] &= lo; xmmHi[d] &= hi; break;
						case 0x55: xmmLo[d] = ~xmmLo[d] & lo; xmmHi[d] = ~xmmHi[d] & hi; break;
						case 0x56: xmmLo[d] |= lo; xmmHi[d] |= hi; break;
						default: xmmLo[d] ^= lo; xmmHi[d] ^= hi; break;
					}

					return true;
				}
				case 0x58: case 0x59: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
				{
					DecodeModRm();
					int d = modReg;
					if (pF3)
					{
						SetXmmSingle(d, 0, (float)SseArith(op, XmmSingle(d, 0), AsSingle(ReadXmmRm32())));
					}
					else if (pF2)
					{
						xmmLo[d] = Bits(SseArith(op, XmmDouble(d, 0), AsDouble(ReadXmmRm64())));
					}
					else if (p66)
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						xmmLo[d] = Bits(SseArith(op, XmmDouble(d, 0), AsDouble(lo)));
						xmmHi[d] = Bits(SseArith(op, XmmDouble(d, 1), AsDouble(hi)));
					}
					else
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						for (int i = 0; i < 4; i++)
						{
							ulong half = i < 2 ? lo : hi;
							float b = AsSingle((uint)(half >> (32 * (i & 1))));
							SetXmmSingle(d, i, (float)SseArith(op, XmmSingle(d, i), b));
						}
					}

					return true;
				}
				case 0x5A: // CVTSS2SD / CVTSD2SS / CVTPS2PD / CVTPD2PS
				{
					DecodeModRm();
					int d = modReg;
					if (pF3)
					{
						xmmLo[d] = Bits((double)AsSingle(ReadXmmRm32()));
					}
					else if (pF2)
					{
						SetXmmSingle(d, 0, (float)AsDouble(ReadXmmRm64()));
					}
					else if (p66)
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						xmmLo[d] = Bits((float)AsDouble(lo)) | ((ulong)Bits((float)AsDouble(hi)) << 32);
						xmmHi[d] = 0;
					}
					else
					{
						ulong lo = ReadXmmRm64();
						xmmLo[d] = Bits((double)AsSingle((uint)lo));
						xmmHi[d] = Bits((double)AsSingle((uint)(lo >> 32)));
					}

					return true;
				}
				case 0x5B: // CVTDQ2PS / CVTPS2DQ (66) / CVTTPS2DQ (F3)
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					for (int i = 0; i < 4; i++)
					{
						uint v = (uint)((i < 2 ? lo : hi) >> (32 * (i & 1)));
						if (!p66 && !pF3)
						{
							SetXmmSingle(d, i, (int)v);
						}
						else
						{
							double f = AsSingle(v);
							SetXmmDword(d, i, ToInt32Indefinite(pF3 ? Math.Truncate(f) : RoundSse(f)));
						}
					}

					return true;
				}
				case 0xE6: // CVTTPD2DQ (66) / CVTDQ2PD (F3) / CVTPD2DQ (F2)
				{
					DecodeModRm();
					int d = modReg;
					if (pF3)
					{
						ulong lo = ReadXmmRm64();
						xmmLo[d] = Bits((double)(int)(uint)lo);
						xmmHi[d] = Bits((double)(int)(uint)(lo >> 32));
					}
					else
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						double a = AsDouble(lo), b = AsDouble(hi);
						uint ia = ToInt32Indefinite(p66 ? Math.Truncate(a) : RoundSse(a));
						uint ib = ToInt32Indefinite(p66 ? Math.Truncate(b) : RoundSse(b));
						xmmLo[d] = ia | ((ulong)ib << 32);
						xmmHi[d] = 0;
					}

					return true;
				}
				case 0xC2: // CMPPS / CMPPD / CMPSS / CMPSD
				{
					DecodeModRm();
					int d = modReg;
					if (pF2)
					{
						double a = XmmDouble(d, 0), b = AsDouble(ReadXmmRm64());
						xmmLo[d] = SseCompare(Fetch8(), a, b) ? ulong.MaxValue : 0;
					}
					else if (pF3)
					{
						float a = XmmSingle(d, 0), b = AsSingle(ReadXmmRm32());
						SetXmmDword(d, 0, SseCompare(Fetch8(), a, b) ? 0xFFFFFFFF : 0);
					}
					else if (p66)
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						byte pred = Fetch8();
						xmmLo[d] = SseCompare(pred, XmmDouble(d, 0), AsDouble(lo)) ? ulong.MaxValue : 0;
						xmmHi[d] = SseCompare(pred, XmmDouble(d, 1), AsDouble(hi)) ? ulong.MaxValue : 0;
					}
					else
					{
						(ulong lo, ulong hi) = ReadXmmRm128();
						byte pred = Fetch8();
						for (int i = 0; i < 4; i++)
						{
							float b = AsSingle((uint)((i < 2 ? lo : hi) >> (32 * (i & 1))));
							SetXmmDword(d, i, SseCompare(pred, XmmSingle(d, i), b) ? 0xFFFFFFFF : 0);
						}
					}

					return true;
				}
				case 0xC6: // SHUFPS / SHUFPD
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					byte imm = Fetch8();
					int d = modReg;
					if (p66)
					{
						ulong a = (imm & 1) == 0 ? xmmLo[d] : xmmHi[d];
						ulong b = (imm & 2) == 0 ? lo : hi;
						xmmLo[d] = a;
						xmmHi[d] = b;
					}
					else
					{
						uint[] a = { XmmDword(d, 0), XmmDword(d, 1), XmmDword(d, 2), XmmDword(d, 3) };
						uint[] b = { (uint)lo, (uint)(lo >> 32), (uint)hi, (uint)(hi >> 32) };
						uint r0 = a[imm & 3], r1 = a[(imm >> 2) & 3], r2 = b[(imm >> 4) & 3], r3 = b[(imm >> 6) & 3];
						xmmLo[d] = r0 | ((ulong)r1 << 32);
						xmmHi[d] = r2 | ((ulong)r3 << 32);
					}

					return true;
				}
			}

			// --- SSE2 integer forms (66 prefix), and MOVD/MOVQ ---
			if (op == 0x7E && pF3) // MOVQ xmm, xmm/m64
			{
				DecodeModRm();
				xmmLo[modReg] = ReadXmmRm64();
				xmmHi[modReg] = 0;
				return true;
			}

			if ((op == 0x6F || op == 0x7F) && pF3) // MOVDQU
			{
				DecodeModRm();
				if (op == 0x6F) (xmmLo[modReg], xmmHi[modReg]) = ReadXmmRm128();
				else WriteXmmRm128(xmmLo[modReg], xmmHi[modReg]);
				return true;
			}

			if (!p66)
			{
				return false;
			}

			switch (op)
			{
				case 0x6E: // MOVD xmm, r/m32
					DecodeModRm();
					xmmLo[modReg] = ReadRm(32);
					xmmHi[modReg] = 0;
					return true;
				case 0x7E: // MOVD r/m32, xmm
					DecodeModRm();
					WriteRm(32, (uint)xmmLo[modReg]);
					return true;
				case 0xD6: // MOVQ xmm/m64, xmm
					DecodeModRm();
					if (ModIsRegister)
					{
						xmmLo[modRm] = xmmLo[modReg];
						xmmHi[modRm] = 0;
					}
					else
					{
						Memory.Write64(modAddress, xmmLo[modReg]);
					}

					return true;
				case 0x6F: // MOVDQA
					DecodeModRm();
					(xmmLo[modReg], xmmHi[modReg]) = ReadXmmRm128();
					return true;
				case 0x7F:
				case 0xE7: // MOVNTDQ
					DecodeModRm();
					WriteXmmRm128(xmmLo[modReg], xmmHi[modReg]);
					return true;
				case 0xDB: // PAND
				case 0xDF: // PANDN
				case 0xEB: // POR
				case 0xEF: // PXOR
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					switch (op)
					{
						case 0xDB: xmmLo[d] &= lo; xmmHi[d] &= hi; break;
						case 0xDF: xmmLo[d] = ~xmmLo[d] & lo; xmmHi[d] = ~xmmHi[d] & hi; break;
						case 0xEB: xmmLo[d] |= lo; xmmHi[d] |= hi; break;
						default: xmmLo[d] ^= lo; xmmHi[d] ^= hi; break;
					}

					return true;
				}
				case 0xD4: // PADDQ
				case 0xFB: // PSUBQ
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					if (op == 0xD4) { xmmLo[d] += lo; xmmHi[d] += hi; }
					else { xmmLo[d] -= lo; xmmHi[d] -= hi; }
					return true;
				}
				case 0xFE: // PADDD
				case 0xFA: // PSUBD
				case 0x76: // PCMPEQD
				case 0x66: // PCMPGTD
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					for (int i = 0; i < 4; i++)
					{
						uint a = XmmDword(d, i);
						uint b = (uint)((i < 2 ? lo : hi) >> (32 * (i & 1)));
						uint r = op == 0xFE ? a + b : op == 0xFA ? a - b : op == 0x76 ? (a == b ? 0xFFFFFFFF : 0) : ((int)a > (int)b ? 0xFFFFFFFF : 0);
						SetXmmDword(d, i, r);
					}

					return true;
				}
				case 0x74: // PCMPEQB
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					xmmLo[d] = CompareBytes(xmmLo[d], lo);
					xmmHi[d] = CompareBytes(xmmHi[d], hi);
					return true;
				}
				case 0xD7: // PMOVMSKB
				{
					DecodeModRm();
					int s = modRm;
					uint mask = 0;
					for (int i = 0; i < 8; i++)
					{
						mask |= (uint)((xmmLo[s] >> (8 * i + 7)) & 1) << i;
						mask |= (uint)((xmmHi[s] >> (8 * i + 7)) & 1) << (i + 8);
					}

					SetReg(modReg, 32, mask);
					return true;
				}
				case 0xC5: // PEXTRW r32, xmm, imm8
				{
					DecodeModRm();
					byte imm = Fetch8();
					int lane = imm & 7;
					ulong half = lane < 4 ? xmmLo[modRm] : xmmHi[modRm];
					SetReg(modReg, 32, (ushort)(half >> (16 * (lane & 3))));
					return true;
				}
				case 0xC4: // PINSRW xmm, r32/m16, imm8
				{
					DecodeModRm();
					ushort value = (ushort)ReadRm(16);
					byte imm = Fetch8();
					int lane = imm & 7;
					int shift = 16 * (lane & 3);
					ulong mask = 0xFFFFUL << shift;
					if (lane < 4) xmmLo[modReg] = (xmmLo[modReg] & ~mask) | ((ulong)value << shift);
					else xmmHi[modReg] = (xmmHi[modReg] & ~mask) | ((ulong)value << shift);
					return true;
				}
				case 0x70: // PSHUFD
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					byte imm = Fetch8();
					uint[] s = { (uint)lo, (uint)(lo >> 32), (uint)hi, (uint)(hi >> 32) };
					int d = modReg;
					xmmLo[d] = s[imm & 3] | ((ulong)s[(imm >> 2) & 3] << 32);
					xmmHi[d] = s[(imm >> 4) & 3] | ((ulong)s[(imm >> 6) & 3] << 32);
					return true;
				}
				case 0x60: // PUNPCKLBW
				case 0x61: // PUNPCKLWD
				case 0x62: // PUNPCKLDQ
				case 0x6C: // PUNPCKLQDQ
				case 0x6D: // PUNPCKHQDQ
				{
					DecodeModRm();
					(ulong lo, ulong hi) = ReadXmmRm128();
					int d = modReg;
					if (op == 0x6C)
					{
						xmmHi[d] = lo;
						return true;
					}

					if (op == 0x6D)
					{
						xmmLo[d] = xmmHi[d];
						xmmHi[d] = hi;
						return true;
					}

					// Interleave the low halves element by element: a0, b0, a1, b1, ...
					int width = op == 0x60 ? 8 : op == 0x61 ? 16 : 32;
					ulong a = xmmLo[d];
					ulong m = (1UL << width) - 1;
					System.UInt128 result = System.UInt128.Zero;
					for (int i = 0; i < 64 / width; i++)
					{
						System.UInt128 ea = (a >> (width * i)) & m;
						System.UInt128 eb = (lo >> (width * i)) & m;
						result |= ea << (2 * i * width);
						result |= eb << (2 * i * width + width);
					}

					xmmLo[d] = (ulong)result;
					xmmHi[d] = (ulong)(result >> 64);
					return true;
				}
				// Shifts of every lane by the count in the low quadword of an XMM register or memory.
				case 0xD1: case 0xD2: case 0xD3: // PSRLW / PSRLD / PSRLQ
				case 0xE1: case 0xE2: // PSRAW / PSRAD
				case 0xF1: case 0xF2: case 0xF3: // PSLLW / PSLLD / PSLLQ
				{
					DecodeModRm();
					(ulong countLo, _) = ReadXmmRm128();
					int laneBits = (op & 0x0F) == 1 ? 16 : (op & 0x0F) == 2 ? 32 : 64;
					int kind = op >= 0xF0 ? 2 : op >= 0xE0 ? 1 : 0;
					ShiftLanes(modReg, laneBits, kind, countLo);
					return true;
				}
				case 0x71: // PSRLW / PSRAW / PSLLW imm
				{
					DecodeModRm();
					byte count = Fetch8();
					int kind = modReg switch
					{
						2 => 0,
						4 => 1,
						6 => 2,
						_ => throw Unsupported("66 0F 71 /" + modReg)
					};
					ShiftLanes(modRm, 16, kind, count);
					return true;
				}
				case 0x72: // PSRLD / PSRAD / PSLLD imm
				case 0x73: // PSRLQ / PSRLDQ / PSLLQ / PSLLDQ imm
				{
					DecodeModRm();
					int r = modRm;
					byte count = Fetch8();
					if (op == 0x72)
					{
						for (int i = 0; i < 4; i++)
						{
							uint v = XmmDword(r, i);
							uint result = modReg switch
							{
								2 => count > 31 ? 0 : v >> count,
								4 => (uint)((int)v >> Math.Min((int)count, 31)),
								6 => count > 31 ? 0 : v << count,
								_ => throw Unsupported("66 0F 72 /" + modReg)
							};
							SetXmmDword(r, i, result);
						}

						return true;
					}

					switch (modReg)
					{
						case 2:
							xmmLo[r] = count > 63 ? 0 : xmmLo[r] >> count;
							xmmHi[r] = count > 63 ? 0 : xmmHi[r] >> count;
							return true;
						case 6:
							xmmLo[r] = count > 63 ? 0 : xmmLo[r] << count;
							xmmHi[r] = count > 63 ? 0 : xmmHi[r] << count;
							return true;
						case 3: // PSRLDQ: bytes
						case 7: // PSLLDQ
						{
							System.UInt128 v = new System.UInt128(xmmHi[r], xmmLo[r]);
							int bits = Math.Min(count, (byte)16) * 8;
							v = bits >= 128 ? System.UInt128.Zero : modReg == 3 ? v >> bits : v << bits;
							xmmLo[r] = (ulong)v;
							xmmHi[r] = (ulong)(v >> 64);
							return true;
						}
					}

					throw Unsupported("66 0F 73 /" + modReg);
				}
			}

			return false;
		}

		/// <summary>
		/// Shifts every lane of an XMM register: kind 0 logical right, 1 arithmetic right, 2 left.
		/// A count past the lane width clears the lane (or fills it with its sign, arithmetically).
		/// </summary>
		private void ShiftLanes(int reg, int laneBits, int kind, ulong count)
		{
			ulong laneMask = laneBits == 64 ? ulong.MaxValue : (1UL << laneBits) - 1;
			ulong[] halves = { xmmLo[reg], xmmHi[reg] };
			for (int h = 0; h < 2; h++)
			{
				ulong result = 0;
				for (int shift = 0; shift < 64; shift += laneBits)
				{
					ulong lane = (halves[h] >> shift) & laneMask;
					ulong shifted;
					if (kind == 1)
					{
						long signedLane = (long)(lane << (64 - laneBits)) >> (64 - laneBits);
						shifted = (ulong)(signedLane >> (int)Math.Min(count, (ulong)laneBits - 1)) & laneMask;
					}
					else if (count >= (ulong)laneBits)
					{
						shifted = 0;
					}
					else
					{
						shifted = (kind == 0 ? lane >> (int)count : lane << (int)count) & laneMask;
					}

					result |= shifted << shift;
				}

				halves[h] = result;
			}

			xmmLo[reg] = halves[0];
			xmmHi[reg] = halves[1];
		}

		private static ulong CompareBytes(ulong a, ulong b)
		{
			ulong r = 0;
			for (int i = 0; i < 8; i++)
			{
				if (((a >> (8 * i)) & 0xFF) == ((b >> (8 * i)) & 0xFF))
				{
					r |= 0xFFUL << (8 * i);
				}
			}

			return r;
		}

		private static bool SseCompare(byte predicate, double a, double b)
		{
			bool unordered = double.IsNaN(a) || double.IsNaN(b);
			switch (predicate & 7)
			{
				case 0: return a == b;
				case 1: return a < b;
				case 2: return a <= b;
				case 3: return unordered;
				case 4: return !(a == b);
				case 5: return !(a < b);
				case 6: return !(a <= b);
				default: return !unordered;
			}
		}
	}
}
