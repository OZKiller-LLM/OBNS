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
	/// <summary>The x87 floating-point unit.</summary>
	/// <remarks>
	/// Registers are held as doubles rather than 80-bit extended values. A plugin that relies on
	/// the extra 11 bits of an intermediate result sees the answer SSE2 code would give, which is
	/// what the same source compiled for any modern target produces; the rounding control, the
	/// status word, comparisons and classification behave as on hardware.
	/// </remarks>
	public sealed partial class Cpu
	{
		private readonly double[] st = new double[8];
		private readonly bool[] stEmpty = new bool[8];
		private int top;
		private ushort fpuControl;
		private bool c0, c1, c2, c3;

		/// <summary>Returns a floating-point result from a host function, in ST(0) as the x86 ABI requires.</summary>
		public void ReturnDouble(double value)
		{
			FPush(value);
		}

		/// <summary>Reads a double argument of a host function at a byte offset into the arguments.</summary>
		public double ArgDouble(int byteOffset)
		{
			return Memory.ReadDouble(R[ESP] + 4 + (uint)byteOffset);
		}

		/// <summary>The value in ST(0), for runtime helpers that take their argument there.</summary>
		public double PeekST0()
		{
			return ST(0);
		}

		/// <summary>Pops ST(0), for runtime helpers that consume their argument.</summary>
		public double PopST0()
		{
			return FPop();
		}

		private void FpuReset()
		{
			top = 0;
			fpuControl = 0x037F;
			c0 = c1 = c2 = c3 = false;
			for (int i = 0; i < 8; i++)
			{
				st[i] = 0.0;
				stEmpty[i] = true;
			}
		}

		private double ST(int i)
		{
			return st[(top + i) & 7];
		}

		private void SetST(int i, double value)
		{
			st[(top + i) & 7] = value;
			stEmpty[(top + i) & 7] = false;
		}

		private void FPush(double value)
		{
			top = (top - 1) & 7;
			st[top] = value;
			stEmpty[top] = false;
		}

		private double FPop()
		{
			double value = st[top];
			stEmpty[top] = true;
			top = (top + 1) & 7;
			return value;
		}

		private ushort FpuStatus =>
			(ushort)((c3 ? 1 << 14 : 0) | (top << 11) | (c2 ? 1 << 10 : 0) | (c1 ? 1 << 9 : 0) | (c0 ? 1 << 8 : 0));

		private ushort FpuTags
		{
			get
			{
				int tags = 0;
				for (int i = 0; i < 8; i++)
				{
					tags |= (stEmpty[i] ? 3 : 0) << (2 * i);
				}

				return (ushort)tags;
			}
		}

		/// <summary>Rounds as the control word's rounding field says, for FIST and FRNDINT.</summary>
		private double RoundFpu(double value)
		{
			switch ((fpuControl >> 10) & 3)
			{
				case 0: return Math.Round(value, MidpointRounding.ToEven);
				case 1: return Math.Floor(value);
				case 2: return Math.Ceiling(value);
				default: return Math.Truncate(value);
			}
		}

		/// <summary>Converts to an integer as FIST does: out of range or NaN gives the "integer indefinite" value.</summary>
		private long FpuToInteger(double value, int bits, bool truncate)
		{
			double rounded = truncate ? Math.Truncate(value) : RoundFpu(value);
			double limit = Math.Pow(2, bits - 1);
			if (double.IsNaN(rounded) || rounded >= limit || rounded < -limit)
			{
				return -(long)limit;
			}

			return (long)rounded;
		}

		private void FpuCompare(double a, double b)
		{
			c1 = false;
			if (double.IsNaN(a) || double.IsNaN(b))
			{
				c3 = c2 = c0 = true;
			}
			else
			{
				c3 = a == b;
				c2 = false;
				c0 = a < b;
			}
		}

		private void FpuCompareFlags(double a, double b)
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

		private static double Arith(int op, double a, double b)
		{
			switch (op)
			{
				case 0: return a + b;
				case 1: return a * b;
				case 4: return a - b;
				case 5: return b - a;
				case 6: return a / b;
				case 7: return b / a;
				default: throw new InvalidOperationException();
			}
		}

		/// <summary>ST(0) = ST(0) op operand, for the memory and register forms of D8/DC/DA/DE.</summary>
		private void ArithST0(int op, double operand)
		{
			switch (op)
			{
				case 2:
					FpuCompare(ST(0), operand);
					return;
				case 3:
					FpuCompare(ST(0), operand);
					FPop();
					return;
				default:
					SetST(0, Arith(op, ST(0), operand));
					return;
			}
		}

		// --- 80-bit extended precision, for FLD/FSTP m80 ---

		private double ReadExtended(uint address)
		{
			ulong mantissa = Memory.Read64(address);
			ushort exponentSign = Memory.Read16(address + 8);
			bool negative = (exponentSign & 0x8000) != 0;
			int exponent = exponentSign & 0x7FFF;
			double value;
			if (exponent == 0 && mantissa == 0)
			{
				value = 0.0;
			}
			else if (exponent == 0x7FFF)
			{
				value = (mantissa << 1) == 0 ? double.PositiveInfinity : double.NaN;
			}
			else
			{
				value = mantissa * Math.Pow(2, exponent - 16383 - 63);
			}

			return negative ? -value : value;
		}

		private void WriteExtended(uint address, double value)
		{
			ulong mantissa;
			int exponent;
			bool negative = value < 0 || (value == 0 && double.IsNegative(value));
			double magnitude = Math.Abs(value);
			if (double.IsNaN(value))
			{
				mantissa = 0xC000000000000000;
				exponent = 0x7FFF;
			}
			else if (double.IsInfinity(value))
			{
				mantissa = 0x8000000000000000;
				exponent = 0x7FFF;
			}
			else if (magnitude == 0)
			{
				mantissa = 0;
				exponent = 0;
			}
			else
			{
				long bits = BitConverter.DoubleToInt64Bits(magnitude);
				int e = (int)((bits >> 52) & 0x7FF);
				ulong fraction = (ulong)bits & 0xFFFFFFFFFFFFF;
				if (e == 0)
				{
					// Subnormal double: normalise it.
					int shift = System.Numerics.BitOperations.LeadingZeroCount(fraction) - 11;
					fraction <<= shift + 1;
					fraction &= 0xFFFFFFFFFFFFF;
					e = 1 - shift - 1;
				}

				exponent = e - 1023 + 16383;
				mantissa = (1UL << 63) | (fraction << 11);
			}

			Memory.Write64(address, mantissa);
			Memory.Write16(address + 8, (ushort)(exponent | (negative ? 0x8000 : 0)));
		}

		private void Fxam()
		{
			double value = ST(0);
			c1 = double.IsNegative(value);
			if (stEmpty[top])
			{
				c3 = true; c2 = false; c0 = true;
			}
			else if (double.IsNaN(value))
			{
				c3 = false; c2 = false; c0 = true;
			}
			else if (double.IsInfinity(value))
			{
				c3 = false; c2 = true; c0 = true;
			}
			else if (value == 0)
			{
				c3 = true; c2 = false; c0 = false;
			}
			else if (double.IsSubnormal(value))
			{
				c3 = true; c2 = true; c0 = false;
			}
			else
			{
				c3 = false; c2 = true; c0 = false;
			}
		}

		private void StoreEnvironment(uint address)
		{
			Memory.Write32(address, fpuControl | 0xFFFF0000u);
			Memory.Write32(address + 4, FpuStatus | 0xFFFF0000u);
			Memory.Write32(address + 8, FpuTags | 0xFFFF0000u);
			for (uint i = 12; i < 28; i += 4)
			{
				Memory.Write32(address + i, 0);
			}
		}

		private void LoadEnvironment(uint address)
		{
			fpuControl = Memory.Read16(address);
			ushort status = Memory.Read16(address + 4);
			top = (status >> 11) & 7;
			c0 = (status & (1 << 8)) != 0;
			c1 = (status & (1 << 9)) != 0;
			c2 = (status & (1 << 10)) != 0;
			c3 = (status & (1 << 14)) != 0;
			ushort tags = Memory.Read16(address + 8);
			for (int i = 0; i < 8; i++)
			{
				stEmpty[i] = ((tags >> (2 * i)) & 3) == 3;
			}
		}

		private void StepFpu(byte op)
		{
			DecodeModRm();
			int reg = modReg;
			if (!ModIsRegister)
			{
				uint a = modAddress;
				switch (op)
				{
					case 0xD8:
						ArithST0(reg, Memory.ReadSingle(a));
						return;
					case 0xDC:
						ArithST0(reg, Memory.ReadDouble(a));
						return;
					case 0xDA:
						ArithST0(reg, (int)Memory.Read32(a));
						return;
					case 0xDE:
						ArithST0(reg, (short)Memory.Read16(a));
						return;
					case 0xD9:
						switch (reg)
						{
							case 0: FPush(Memory.ReadSingle(a)); return;
							case 2: Memory.WriteSingle(a, (float)ST(0)); return;
							case 3: Memory.WriteSingle(a, (float)FPop()); return;
							case 4: LoadEnvironment(a); return;
							case 5: fpuControl = Memory.Read16(a); return;
							case 6: StoreEnvironment(a); return;
							case 7: Memory.Write16(a, fpuControl); return;
						}

						break;
					case 0xDB:
						switch (reg)
						{
							case 0: FPush((int)Memory.Read32(a)); return;
							case 1: Memory.Write32(a, (uint)FpuToInteger(FPop(), 32, true)); return;
							case 2: Memory.Write32(a, (uint)FpuToInteger(ST(0), 32, false)); return;
							case 3: Memory.Write32(a, (uint)FpuToInteger(FPop(), 32, false)); return;
							case 5: FPush(ReadExtended(a)); return;
							case 7: WriteExtended(a, FPop()); return;
						}

						break;
					case 0xDD:
						switch (reg)
						{
							case 0: FPush(Memory.ReadDouble(a)); return;
							case 1: Memory.Write64(a, (ulong)FpuToInteger(FPop(), 64, true)); return;
							case 2: Memory.WriteDouble(a, ST(0)); return;
							case 3: Memory.WriteDouble(a, FPop()); return;
							case 4: // FRSTOR
								LoadEnvironment(a);
								for (int i = 0; i < 8; i++)
								{
									st[(top + i) & 7] = ReadExtended(a + 28 + (uint)(10 * i));
								}

								return;
							case 6: // FNSAVE
								StoreEnvironment(a);
								for (int i = 0; i < 8; i++)
								{
									WriteExtended(a + 28 + (uint)(10 * i), ST(i));
								}

								FpuReset();
								return;
							case 7: Memory.Write16(a, FpuStatus); return;
						}

						break;
					case 0xDF:
						switch (reg)
						{
							case 0: FPush((short)Memory.Read16(a)); return;
							case 1: Memory.Write16(a, (ushort)FpuToInteger(FPop(), 16, true)); return;
							case 2: Memory.Write16(a, (ushort)FpuToInteger(ST(0), 16, false)); return;
							case 3: Memory.Write16(a, (ushort)FpuToInteger(FPop(), 16, false)); return;
							case 5: FPush((long)Memory.Read64(a)); return;
							case 7: Memory.Write64(a, (ulong)FpuToInteger(FPop(), 64, false)); return;
						}

						break;
				}

				throw Unsupported("x87 " + op.ToString("X2") + " /" + reg + " (memory)");
			}

			int i2 = modRm;
			switch (op)
			{
				case 0xD8:
					ArithST0(reg, ST(i2));
					return;
				case 0xDC:
				case 0xDE:
				{
					// ST(i) = ST(i) op ST(0), with the subtract and divide directions reversed in the encoding.
					double sti = ST(i2);
					double st0 = ST(0);
					switch (reg)
					{
						case 0: SetST(i2, sti + st0); break;
						case 1: SetST(i2, sti * st0); break;
						case 2: FpuCompare(st0, sti); break;
						case 3:
							FpuCompare(st0, sti);
							if (op == 0xDE && i2 == 1)
							{
								FPop(); // FCOMPP
							}

							FPop();
							return;
						case 4: SetST(i2, st0 - sti); break;
						case 5: SetST(i2, sti - st0); break;
						case 6: SetST(i2, st0 / sti); break;
						case 7: SetST(i2, sti / st0); break;
					}

					if (op == 0xDE)
					{
						FPop();
					}

					return;
				}
				case 0xD9:
					switch (reg)
					{
						case 0: FPush(ST(i2)); return;
						case 1:
						{
							double t = ST(0);
							SetST(0, ST(i2));
							SetST(i2, t);
							return;
						}
						case 2: return; // FNOP
						case 3: SetST(i2, ST(0)); FPop(); return; // FSTP1 (undocumented alias)
					}

					switch (modRm | (reg << 3))
					{
						case 0x20: SetST(0, -ST(0)); return; // FCHS
						case 0x21: SetST(0, Math.Abs(ST(0))); return; // FABS
						case 0x24: FpuCompare(ST(0), 0.0); return; // FTST
						case 0x25: Fxam(); return;
						case 0x28: FPush(1.0); return;
						case 0x29: FPush(Math.Log2(10.0)); return;
						case 0x2A: FPush(Math.Log2(Math.E)); return;
						case 0x2B: FPush(Math.PI); return;
						case 0x2C: FPush(Math.Log10(2.0)); return;
						case 0x2D: FPush(Math.Log(2.0)); return;
						case 0x2E: FPush(0.0); return;
						case 0x30: SetST(0, Math.Pow(2, ST(0)) - 1); return; // F2XM1
						case 0x31: // FYL2X
						{
							double x = FPop();
							SetST(0, ST(0) * Math.Log2(x));
							return;
						}
						case 0x32: // FPTAN
							SetST(0, Math.Tan(ST(0)));
							FPush(1.0);
							c2 = false;
							return;
						case 0x33: // FPATAN
						{
							double x = FPop();
							SetST(0, Math.Atan2(ST(0), x));
							return;
						}
						case 0x34: // FXTRACT
						{
							double v = ST(0);
							int exponent = v == 0 ? 0 : (int)Math.Floor(Math.Log2(Math.Abs(v)));
							SetST(0, exponent);
							FPush(v / Math.Pow(2, exponent));
							return;
						}
						case 0x35: // FPREM1
							SetST(0, Math.IEEERemainder(ST(0), ST(1)));
							c2 = false;
							return;
						case 0x36: top = (top - 1) & 7; return; // FDECSTP
						case 0x37: top = (top + 1) & 7; return; // FINCSTP
						case 0x38: // FPREM
						{
							double a = ST(0), b = ST(1);
							SetST(0, a - Math.Truncate(a / b) * b);
							c2 = false;
							return;
						}
						case 0x39: // FYL2XP1
						{
							double x = FPop();
							SetST(0, ST(0) * Math.Log2(x + 1));
							return;
						}
						case 0x3A: SetST(0, Math.Sqrt(ST(0))); return;
						case 0x3B: // FSINCOS
						{
							double v = ST(0);
							SetST(0, Math.Sin(v));
							FPush(Math.Cos(v));
							c2 = false;
							return;
						}
						case 0x3C: SetST(0, RoundFpu(ST(0))); return; // FRNDINT
						case 0x3D: SetST(0, ST(0) * Math.Pow(2, Math.Truncate(ST(1)))); return; // FSCALE
						case 0x3E: SetST(0, Math.Sin(ST(0))); c2 = false; return;
						case 0x3F: SetST(0, Math.Cos(ST(0))); c2 = false; return;
					}

					break;
				case 0xDA:
					switch (reg)
					{
						case 0: if (CF) SetST(0, ST(i2)); return; // FCMOVB
						case 1: if (ZF) SetST(0, ST(i2)); return; // FCMOVE
						case 2: if (CF || ZF) SetST(0, ST(i2)); return; // FCMOVBE
						case 3: if (PF) SetST(0, ST(i2)); return; // FCMOVU
						case 5:
							if (i2 == 1) // FUCOMPP
							{
								FpuCompare(ST(0), ST(1));
								FPop();
								FPop();
								return;
							}

							break;
					}

					break;
				case 0xDB:
					switch (reg)
					{
						case 0: if (!CF) SetST(0, ST(i2)); return;
						case 1: if (!ZF) SetST(0, ST(i2)); return;
						case 2: if (!CF && !ZF) SetST(0, ST(i2)); return;
						case 3: if (!PF) SetST(0, ST(i2)); return;
						case 4:
							if (i2 == 2) { c0 = c1 = c2 = c3 = false; return; } // FNCLEX
							if (i2 == 3) { FpuReset(); return; } // FNINIT
							break;
						case 5: FpuCompareFlags(ST(0), ST(i2)); return; // FUCOMI
						case 6: FpuCompareFlags(ST(0), ST(i2)); return; // FCOMI
					}

					break;
				case 0xDD:
					switch (reg)
					{
						case 0: stEmpty[(top + i2) & 7] = true; return; // FFREE
						case 2: SetST(i2, ST(0)); return; // FST ST(i)
						case 3: SetST(i2, ST(0)); FPop(); return; // FSTP ST(i)
						case 4: FpuCompare(ST(0), ST(i2)); return; // FUCOM
						case 5: FpuCompare(ST(0), ST(i2)); FPop(); return; // FUCOMP
					}

					break;
				case 0xDF:
					switch (reg)
					{
						case 4:
							if (i2 == 0) // FNSTSW AX
							{
								SetReg(EAX, 16, FpuStatus);
								return;
							}

							break;
						case 5: FpuCompareFlags(ST(0), ST(i2)); FPop(); return; // FUCOMIP
						case 6: FpuCompareFlags(ST(0), ST(i2)); FPop(); return; // FCOMIP
					}

					break;
			}

			throw Unsupported("x87 " + op.ToString("X2") + " " + (0xC0 | (reg << 3) | i2).ToString("X2"));
		}
	}
}
