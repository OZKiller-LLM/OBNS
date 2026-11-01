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
	/// An x86-64 interpreter for 64-bit Windows plugins: the integer instruction set and the SSE/SSE2 compilers emit
	/// (Cpu64.Sse.cs), under the Windows x64 calling convention.
	/// </summary>
	/// <remarks>
	/// The plugin's image, stack and heap are all placed below 4 GB, which x64 code doesn't mind, so the address space is
	/// the 32-bit layer's <see cref="Memory"/> and a host function can read a pointer argument as a uint. An address above
	/// 4 GB is an access violation. Host functions and their table stay on <see cref="Cpu"/>, which routes their arguments
	/// here. Nothing here executes native code.
	/// </remarks>
	public sealed partial class Cpu64
	{
		public const int RAX = 0, RCX = 1, RDX = 2, RBX = 3, RSP = 4, RBP = 5, RSI = 6, RDI = 7, R8 = 8, R9 = 9, R10 = 10, R11 = 11, R12 = 12, R13 = 13, R14 = 14, R15 = 15;
		private const ulong ReturnTrap = 0xFFFFFFF0;

		public readonly ulong[] R = new ulong[16];
		public ulong Rip;
		public ulong GsBase;
		public bool CF, PF, AF, ZF, SF, OF, DF;

		public readonly Memory Memory;
		private readonly Cpu host;

		public long InstructionCount;
		public long InstructionBudget = 500_000_000;

		/// <summary>Diagnostics (the test harness): called before every emulated instruction. Null in the game.</summary>
		public Action<Cpu64> StepHook;

		private readonly ulong[] recent = new ulong[32];
		private int recentIndex;

		public Cpu64(Memory memory, Cpu hostFunctions)
		{
			Memory = memory;
			host = hostFunctions;
		}

		/// <summary>Finds and enters the handler for a C++ exception a host function raised (Cxx64.cs).</summary>
		public Func<GuestThrow, bool> ThrowDispatcher { get; set; }

		private readonly System.Collections.Generic.List<ulong> callFloors = new System.Collections.Generic.List<ulong>();

		/// <summary>Where the innermost call from the host started on the stack (frames below it are that call's).</summary>
		public ulong CallFloor => callFloors.Count > 0 ? callFloors[callFloors.Count - 1] : ulong.MaxValue;

		// --- calling into emulated code ---

		/// <summary>Calls an emulated function under the x64 convention: RCX, RDX, R8, R9, then the stack above the home space.</summary>
		public ulong Call(uint function, params ulong[] args)
		{
			ulong savedRsp = R[RSP];
			ulong savedRip = Rip;
			int stackArgs = Math.Max(args.Length, 4);
			// At the callee's first instruction RSP + 8 is 16-byte aligned, with the home space above the return address.
			ulong rsp = (savedRsp - 128 - (ulong)(8 * stackArgs)) & ~15UL;
			for (int i = 0; i < args.Length; i++)
			{
				Memory.Write64(Address(rsp + (ulong)(8 * i)), args[i]);
			}

			if (args.Length > 0) R[RCX] = args[0];
			if (args.Length > 1) R[RDX] = args[1];
			if (args.Length > 2) R[R8] = args[2];
			if (args.Length > 3) R[R9] = args[3];
			R[RSP] = rsp - 8;
			Memory.Write64(Address(R[RSP]), ReturnTrap);
			Rip = function;
			callFloors.Add(R[RSP]);
			try
			{
				Run();
			}
			finally
			{
				callFloors.RemoveAt(callFloors.Count - 1);
			}

			Rip = savedRip;
			R[RSP] = savedRsp;
			return R[RAX];
		}

		private void Run()
		{
			long budget = InstructionCount + InstructionBudget;
			while (true)
			{
				if (Rip == ReturnTrap)
				{
					return;
				}

				if (Rip >= Cpu.HostRegion)
				{
					InvokeHost();
					continue;
				}

				if (InstructionCount > budget)
				{
					throw new X86Exception("the plugin ran for too long without returning (at 0x" + Rip.ToString("X") + ")");
				}

				recent[recentIndex++ & 31] = Rip;
				StepHook?.Invoke(this);
				try
				{
					Step();
				}
				catch (X86Exception ex) when (!ex.Message.Contains(" [at "))
				{
					throw new X86Exception(ex.Message + " [at 0x" + lastInstruction.ToString("X") + "]");
				}

				InstructionCount++;
			}
		}

		private void InvokeHost()
		{
			// Home the register arguments, as a variadic callee does, so a host function can read them all from the stack.
			uint rsp = Address(R[RSP]);
			Memory.Write64(rsp + 8, R[RCX]);
			Memory.Write64(rsp + 16, R[RDX]);
			Memory.Write64(rsp + 24, R[R8]);
			Memory.Write64(rsp + 32, R[R9]);
			ulong result;
			try
			{
				result = host.InvokeHostFor64((uint)Rip);
			}
			catch (GuestThrow thrown)
			{
				R[RSP] = rsp; // a call the host function made may have left it deeper
				if (ThrowDispatcher != null && ThrowDispatcher(thrown))
				{
					return;
				}

				if (callFloors.Count > 1)
				{
					throw;
				}

				throw new X86Exception("the plugin threw a C++ exception nothing caught (" + thrown.Message + ")");
			}

			// A host function's -1 is a 32-bit one, but a caller may compare it as a pointer (INVALID_HANDLE_VALUE).
			R[RAX] = result == 0xFFFFFFFF ? ulong.MaxValue : result;
			Rip = Pop();
		}

		/// <summary>The addresses of the most recently executed instructions, oldest first.</summary>
		public System.Collections.Generic.IEnumerable<ulong> RecentInstructions()
		{
			for (int i = 0; i < 32; i++)
			{
				ulong address = recent[(recentIndex + i) & 31];
				if (address != 0)
				{
					yield return address;
				}
			}
		}

		// --- arguments, for host functions ---

		/// <summary>The n-th argument slot of a host function (0-based): an integer register or the stack, as they were at the call.</summary>
		public ulong Arg(int n)
		{
			return Memory.Read64(Address(R[RSP] + 8 + (ulong)(8 * n)));
		}

		/// <summary>The address of the n-th argument slot of a host function: the home space, then the stack.</summary>
		public uint ArgAddress(int n)
		{
			return Address(R[RSP] + 8 + (ulong)(8 * n));
		}

		/// <summary>A floating-point argument in the n-th slot of a host function.</summary>
		public double ArgDouble(int n)
		{
			return n < 4 ? BitConverter.Int64BitsToDouble((long)X[2 * n]) : BitConverter.Int64BitsToDouble((long)Arg(n));
		}

		/// <summary>Sets a host function's floating-point result.</summary>
		public void ReturnDouble(double value)
		{
			X[0] = (ulong)BitConverter.DoubleToInt64Bits(value);
			X[1] = 0;
		}

		// --- memory and the stack ---

		/// <summary>The 32-bit address of a 64-bit one; anything above 4 GB faults, as nothing is mapped there.</summary>
		private static uint Address(ulong address)
		{
			if (address >> 32 != 0)
			{
				throw new X86Exception("access violation at 0x" + address.ToString("X"));
			}

			return (uint)address;
		}

		public void Push(ulong value)
		{
			R[RSP] -= 8;
			Memory.Write64(Address(R[RSP]), value);
		}

		public ulong Pop()
		{
			ulong value = Memory.Read64(Address(R[RSP]));
			R[RSP] += 8;
			return value;
		}

		private ulong ReadMem(ulong address, int size)
		{
			uint a = Address(address);
			switch (size)
			{
				case 8: return Memory.Read8(a);
				case 16: return Memory.Read16(a);
				case 32: return Memory.Read32(a);
				default: return Memory.Read64(a);
			}
		}

		private void WriteMem(ulong address, int size, ulong value)
		{
			uint a = Address(address);
			switch (size)
			{
				case 8: Memory.Write8(a, (byte)value); break;
				case 16: Memory.Write16(a, (ushort)value); break;
				case 32: Memory.Write32(a, (uint)value); break;
				default: Memory.Write64(a, value); break;
			}
		}

		// --- decoding state ---

		private ulong lastInstruction;
		private bool opSize16, repz, repnz, gs;
		private int rex;
		private bool RexW => (rex & 8) != 0;
		private int RexR => (rex & 4) << 1;
		private int RexX => (rex & 2) << 2;
		private int RexB => (rex & 1) << 3;

		private byte Fetch8()
		{
			byte value = Memory.Read8(Address(Rip));
			Rip++;
			return value;
		}

		private ushort Fetch16()
		{
			ushort value = Memory.Read16(Address(Rip));
			Rip += 2;
			return value;
		}

		private uint Fetch32()
		{
			uint value = Memory.Read32(Address(Rip));
			Rip += 4;
			return value;
		}

		private ulong Fetch64()
		{
			ulong value = Memory.Read64(Address(Rip));
			Rip += 8;
			return value;
		}

		/// <summary>An immediate for an operand of the given size, sign-extended to 64 bits (a 64-bit operand takes 32 bits).</summary>
		private ulong FetchSigned(int size)
		{
			switch (size)
			{
				case 8: return (ulong)(sbyte)Fetch8();
				case 16: return (ulong)(short)Fetch16();
				default: return (ulong)(int)Fetch32();
			}
		}

		private int OperandSize => RexW ? 64 : opSize16 ? 16 : 32;

		// --- ModR/M ---

		private int modMod, modReg, modRm;
		private ulong modBase;
		private bool modRipRelative;

		/// <summary>
		/// The operand's address. A RIP-relative one counts from the end of the instruction, past any immediate, so every
		/// immediate is fetched before this is read.
		/// </summary>
		private ulong Ea => modRipRelative ? modBase + Rip : modBase;

		private bool ModIsRegister => modMod == 3;

		private void DecodeModRm()
		{
			byte b = Fetch8();
			modMod = b >> 6;
			modReg = ((b >> 3) & 7) | RexR;
			modRm = b & 7;
			modRipRelative = false;
			if (modMod == 3)
			{
				modRm |= RexB;
				return;
			}

			ulong address;
			if (modRm == 4)
			{
				byte sib = Fetch8();
				int scale = sib >> 6;
				int index = ((sib >> 3) & 7) | RexX;
				int baseReg = sib & 7;
				if (baseReg == 5 && modMod == 0)
				{
					address = (ulong)(int)Fetch32();
				}
				else
				{
					address = R[baseReg | RexB];
				}

				if (index != 4)
				{
					address += R[index] << scale;
				}
			}
			else if (modRm == 5 && modMod == 0)
			{
				address = (ulong)(int)Fetch32();
				modRipRelative = true;
			}
			else
			{
				address = R[modRm | RexB];
			}

			if (modMod == 1)
			{
				address += (ulong)(sbyte)Fetch8();
			}
			else if (modMod == 2)
			{
				address += (ulong)(int)Fetch32();
			}

			if (gs)
			{
				address += GsBase;
			}

			modBase = address;
		}

		// --- registers and operands by size ---

		private ulong GetReg(int reg, int size)
		{
			switch (size)
			{
				case 8:
					return rex == 0 && reg >= 4 && reg < 8 ? (R[reg - 4] >> 8) & 0xFF : R[reg] & 0xFF;
				case 16:
					return R[reg] & 0xFFFF;
				case 32:
					return R[reg] & 0xFFFFFFFF;
				default:
					return R[reg];
			}
		}

		private void SetReg(int reg, int size, ulong value)
		{
			switch (size)
			{
				case 8:
					if (rex == 0 && reg >= 4 && reg < 8)
					{
						R[reg - 4] = (R[reg - 4] & ~0xFF00UL) | ((value & 0xFF) << 8);
					}
					else
					{
						R[reg] = (R[reg] & ~0xFFUL) | (value & 0xFF);
					}

					break;
				case 16:
					R[reg] = (R[reg] & ~0xFFFFUL) | (value & 0xFFFF);
					break;
				case 32:
					R[reg] = value & 0xFFFFFFFF; // a 32-bit write clears the upper half
					break;
				default:
					R[reg] = value;
					break;
			}
		}

		private ulong ReadRm(int size)
		{
			return ModIsRegister ? GetReg(modRm, size) : ReadMem(Ea, size);
		}

		private void WriteRm(int size, ulong value)
		{
			if (ModIsRegister)
			{
				SetReg(modRm, size, value);
			}
			else
			{
				WriteMem(Ea, size, value);
			}
		}

		// --- flags and arithmetic ---

		private static ulong Mask(int size)
		{
			return size == 64 ? ulong.MaxValue : (1UL << size) - 1;
		}

		private static ulong SignBit(int size)
		{
			return 1UL << (size - 1);
		}

		private static bool Parity(ulong value)
		{
			uint b = (uint)value & 0xFF;
			b ^= b >> 4;
			b ^= b >> 2;
			b ^= b >> 1;
			return (b & 1) == 0;
		}

		private void SetResultFlags(ulong result, int size)
		{
			ZF = (result & Mask(size)) == 0;
			SF = (result & SignBit(size)) != 0;
			PF = Parity(result);
		}

		private void SetLogicFlags(ulong result, int size)
		{
			SetResultFlags(result, size);
			CF = OF = AF = false;
		}

		private ulong Add(ulong a, ulong b, int size, bool carryIn = false)
		{
			ulong mask = Mask(size);
			a &= mask;
			b &= mask;
			ulong c = carryIn ? 1UL : 0UL;
			ulong result = (a + b + c) & mask;
			CF = result < a || carryIn && result == a;
			OF = ((a ^ result) & (b ^ result) & SignBit(size)) != 0;
			AF = ((a ^ b ^ result) & 0x10) != 0;
			SetResultFlags(result, size);
			return result;
		}

		private ulong Sub(ulong a, ulong b, int size, bool borrowIn = false)
		{
			ulong mask = Mask(size);
			a &= mask;
			b &= mask;
			ulong c = borrowIn ? 1UL : 0UL;
			ulong result = (a - b - c) & mask;
			CF = a < b || borrowIn && a == b;
			OF = ((a ^ b) & (a ^ result) & SignBit(size)) != 0;
			AF = ((a ^ b ^ result) & 0x10) != 0;
			SetResultFlags(result, size);
			return result;
		}

		private ulong Alu(int op, ulong a, ulong b, int size)
		{
			ulong result;
			switch (op)
			{
				case 0: return Add(a, b, size);
				case 1: result = (a | b) & Mask(size); SetLogicFlags(result, size); return result;
				case 2: return Add(a, b, size, CF);
				case 3: return Sub(a, b, size, CF);
				case 4: result = a & b & Mask(size); SetLogicFlags(result, size); return result;
				case 5: return Sub(a, b, size);
				case 6: result = (a ^ b) & Mask(size); SetLogicFlags(result, size); return result;
				default: return Sub(a, b, size); // CMP: the caller keeps the destination
			}
		}

		private bool Condition(int cc)
		{
			bool value;
			switch (cc >> 1)
			{
				case 0: value = OF; break;
				case 1: value = CF; break;
				case 2: value = ZF; break;
				case 3: value = CF || ZF; break;
				case 4: value = SF; break;
				case 5: value = PF; break;
				case 6: value = SF != OF; break;
				default: value = ZF || SF != OF; break;
			}

			return (cc & 1) != 0 ? !value : value;
		}

		private X86Exception Unsupported(string what)
		{
			return new X86Exception("unsupported x64 instruction " + what + " at 0x" + lastInstruction.ToString("X"));
		}

		private static ulong SignExtend(ulong value, int size)
		{
			switch (size)
			{
				case 8: return (ulong)(sbyte)value;
				case 16: return (ulong)(short)value;
				case 32: return (ulong)(int)value;
				default: return value;
			}
		}

		// --- one instruction ---

		public void Step()
		{
			lastInstruction = Rip;
			opSize16 = repz = repnz = gs = false;
			rex = 0;
			byte op;
			while (true)
			{
				op = Fetch8();
				switch (op)
				{
					case 0x66: opSize16 = true; continue;
					case 0xF2: repnz = true; repz = false; continue;
					case 0xF3: repz = true; repnz = false; continue;
					case 0x65: gs = true; continue;
					case 0x26: case 0x2E: case 0x36: case 0x3E: case 0x64: case 0x67: case 0xF0:
						if (op == 0x67 || op == 0x64) throw Unsupported("prefix 0x" + op.ToString("X2"));
						continue;
				}

				break;
			}

			if (op >= 0x40 && op <= 0x4F)
			{
				rex = op;
				op = Fetch8();
			}

			int size = OperandSize;
			if (op < 0x40 && (op & 7) < 6)
			{
				// ADD OR ADC SBB AND SUB XOR CMP, in their six forms.
				int alu = op >> 3;
				int form = op & 7;
				switch (form)
				{
					case 0: case 1:
					{
						int s = form == 0 ? 8 : size;
						DecodeModRm();
						ulong r = Alu(alu, ReadRm(s), GetReg(modReg, s), s);
						if (alu != 7) WriteRm(s, r);
						break;
					}
					case 2: case 3:
					{
						int s = form == 2 ? 8 : size;
						DecodeModRm();
						ulong r = Alu(alu, GetReg(modReg, s), ReadRm(s), s);
						if (alu != 7) SetReg(modReg, s, r);
						break;
					}
					case 4:
					{
						ulong r = Alu(alu, GetReg(RAX, 8), FetchSigned(8), 8);
						if (alu != 7) SetReg(RAX, 8, r);
						break;
					}
					default:
					{
						ulong r = Alu(alu, GetReg(RAX, size), FetchSigned(size), size);
						if (alu != 7) SetReg(RAX, size, r);
						break;
					}
				}

				return;
			}

			switch (op)
			{
				case 0x0F:
					StepTwoByte(size);
					return;
				case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
					Push(R[(op & 7) | RexB]);
					return;
				case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
					R[(op & 7) | RexB] = Pop();
					return;
				case 0x63: // MOVSXD
					DecodeModRm();
					SetReg(modReg, size, (ulong)(int)ReadRm(32));
					return;
				case 0x68:
					Push(FetchSigned(32));
					return;
				case 0x6A:
					Push(FetchSigned(8));
					return;
				case 0x69: case 0x6B:
				{
					DecodeModRm();
					long imm = (long)(op == 0x6B ? FetchSigned(8) : FetchSigned(size));
					SetReg(modReg, size, Imul((long)SignExtend(ReadRm(size), size), imm, size));
					return;
				}
				case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
				case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F:
				{
					ulong displacement = FetchSigned(8);
					if (Condition(op & 15)) Rip += displacement;
					return;
				}
				case 0x80: case 0x81: case 0x83:
				{
					int s = op == 0x80 ? 8 : size;
					DecodeModRm();
					ulong imm = op == 0x81 ? FetchSigned(s) : FetchSigned(8);
					int alu = modReg & 7;
					ulong r = Alu(alu, ReadRm(s), imm, s);
					if (alu != 7) WriteRm(s, r);
					return;
				}
				case 0x84: case 0x85:
				{
					int s = op == 0x84 ? 8 : size;
					DecodeModRm();
					SetLogicFlags(ReadRm(s) & GetReg(modReg, s), s);
					return;
				}
				case 0x86: case 0x87:
				{
					int s = op == 0x86 ? 8 : size;
					DecodeModRm();
					ulong a = ReadRm(s);
					WriteRm(s, GetReg(modReg, s));
					SetReg(modReg, s, a);
					return;
				}
				case 0x88: DecodeModRm(); WriteRm(8, GetReg(modReg, 8)); return;
				case 0x89: DecodeModRm(); WriteRm(size, GetReg(modReg, size)); return;
				case 0x8A: DecodeModRm(); SetReg(modReg, 8, ReadRm(8)); return;
				case 0x8B: DecodeModRm(); SetReg(modReg, size, ReadRm(size)); return;
				case 0x8D:
					DecodeModRm();
					if (ModIsRegister) throw Unsupported("LEA with a register");
					SetReg(modReg, size, Ea);
					return;
				case 0x8F:
				{
					DecodeModRm();
					ulong value = Pop();
					WriteRm(64, value);
					return;
				}
				case 0x90:
					if (RexB == 0) return; // NOP, PAUSE
					goto case 0x91;
				case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
				{
					int reg = (op & 7) | RexB;
					ulong a = GetReg(RAX, size);
					SetReg(RAX, size, GetReg(reg, size));
					SetReg(reg, size, a);
					return;
				}
				case 0x98: // CBW, CWDE, CDQE
					SetReg(RAX, size, SignExtend(GetReg(RAX, size / 2), size / 2));
					return;
				case 0x99: // CWD, CDQ, CQO
					SetReg(RDX, size, (GetReg(RAX, size) & SignBit(size)) != 0 ? ulong.MaxValue : 0);
					return;
				case 0x9C:
					Push(GetFlags());
					return;
				case 0x9D:
					SetFlags(Pop());
					return;
				case 0x9E:
					SetFlags((GetFlags() & ~0xFFUL) | (R[RAX] >> 8 & 0xFF));
					return;
				case 0x9F:
					SetReg(RAX, 16, (R[RAX] & 0xFF) | ((GetFlags() & 0xFF) << 8));
					return;
				case 0xA4: StringOp(0, 8); return;
				case 0xA5: StringOp(0, size); return;
				case 0xA6: StringOp(1, 8); return;
				case 0xA7: StringOp(1, size); return;
				case 0xA8: SetLogicFlags(GetReg(RAX, 8) & FetchSigned(8), 8); return;
				case 0xA9: SetLogicFlags(GetReg(RAX, size) & FetchSigned(size), size); return;
				case 0xAA: StringOp(2, 8); return;
				case 0xAB: StringOp(2, size); return;
				case 0xAC: StringOp(3, 8); return;
				case 0xAD: StringOp(3, size); return;
				case 0xAE: StringOp(4, 8); return;
				case 0xAF: StringOp(4, size); return;
				case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
					SetReg((op & 7) | RexB, 8, Fetch8());
					return;
				case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
					SetReg((op & 7) | RexB, size, size == 64 ? Fetch64() : size == 16 ? Fetch16() : Fetch32());
					return;
				case 0xC0: case 0xC1:
				{
					int s = op == 0xC0 ? 8 : size;
					DecodeModRm();
					uint count = Fetch8();
					WriteRm(s, Shift(modReg & 7, ReadRm(s), count, s));
					return;
				}
				case 0xD0: case 0xD1:
				{
					int s = op == 0xD0 ? 8 : size;
					DecodeModRm();
					WriteRm(s, Shift(modReg & 7, ReadRm(s), 1, s));
					return;
				}
				case 0xD2: case 0xD3:
				{
					int s = op == 0xD2 ? 8 : size;
					DecodeModRm();
					WriteRm(s, Shift(modReg & 7, ReadRm(s), (uint)R[RCX] & 0xFF, s));
					return;
				}
				case 0xC2:
				{
					uint bytes = Fetch16();
					Rip = Pop();
					R[RSP] += bytes;
					return;
				}
				case 0xC3:
					Rip = Pop();
					return;
				case 0xC6:
					DecodeModRm();
					if ((modReg & 7) != 0) throw Unsupported("C6 /" + (modReg & 7));
					{
						ulong imm = Fetch8();
						WriteRm(8, imm);
					}

					return;
				case 0xC7:
					DecodeModRm();
					if ((modReg & 7) != 0) throw Unsupported("C7 /" + (modReg & 7));
					{
						ulong imm = FetchSigned(size);
						WriteRm(size, imm);
					}

					return;
				case 0xC9:
					R[RSP] = R[RBP];
					R[RBP] = Pop();
					return;
				case 0xCC:
					throw new X86Exception("the plugin hit a breakpoint (INT 3)");
				case 0xD9: case 0xDB: case 0xDD: case 0xDF: case 0xD8: case 0xDA: case 0xDC: case 0xDE:
					StepFpu(op);
					return;
				case 0xE3:
				{
					ulong displacement = FetchSigned(8);
					if (R[RCX] == 0) Rip += displacement;
					return;
				}
				case 0xE8:
				{
					ulong displacement = FetchSigned(32);
					Push(Rip);
					Rip += displacement;
					return;
				}
				case 0xE9:
				{
					ulong displacement = FetchSigned(32);
					Rip += displacement;
					return;
				}
				case 0xEB:
				{
					ulong displacement = FetchSigned(8);
					Rip += displacement;
					return;
				}
				case 0xF4:
					throw new X86Exception("the plugin ran into a HLT");
				case 0xF5: CF = !CF; return;
				case 0xF8: CF = false; return;
				case 0xF9: CF = true; return;
				case 0xFC: DF = false; return;
				case 0xFD: DF = true; return;
				case 0xF6: Group3(8); return;
				case 0xF7: Group3(size); return;
				case 0xFE: case 0xFF:
					Group5(op == 0xFE ? 8 : size);
					return;
				default:
					throw Unsupported("opcode 0x" + op.ToString("X2"));
			}
		}

		private ulong GetFlags()
		{
			return 2UL | (CF ? 1UL : 0) | (PF ? 4UL : 0) | (AF ? 0x10UL : 0) | (ZF ? 0x40UL : 0) | (SF ? 0x80UL : 0) | (DF ? 0x400UL : 0) | (OF ? 0x800UL : 0) | 0x200UL;
		}

		private void SetFlags(ulong value)
		{
			CF = (value & 1) != 0;
			PF = (value & 4) != 0;
			AF = (value & 0x10) != 0;
			ZF = (value & 0x40) != 0;
			SF = (value & 0x80) != 0;
			DF = (value & 0x400) != 0;
			OF = (value & 0x800) != 0;
		}

		private ulong Imul(long a, long b, int size)
		{
			Mul128((ulong)a, (ulong)b, out ulong high, out ulong low);
			// The signed high half: the unsigned product's, less each operand if the other is negative.
			if (a < 0) high -= (ulong)b;
			if (b < 0) high -= (ulong)a;
			bool fits;
			switch (size)
			{
				case 64: fits = (long)high == (long)low >> 63; break;
				default: fits = SignExtend(low & Mask(size), size) == low && (long)high == (long)low >> 63; break;
			}

			CF = OF = !fits;
			return low & Mask(size);
		}

		private static void Mul128(ulong a, ulong b, out ulong high, out ulong low)
		{
			ulong a0 = (uint)a, a1 = a >> 32, b0 = (uint)b, b1 = b >> 32;
			ulong p00 = a0 * b0, p01 = a0 * b1, p10 = a1 * b0, p11 = a1 * b1;
			ulong middle = (p00 >> 32) + (uint)p01 + (uint)p10;
			low = (middle << 32) | (uint)p00;
			high = p11 + (p01 >> 32) + (p10 >> 32) + (middle >> 32);
		}

		/// <summary>The unsigned 128-by-64 division: high must be less than the divisor.</summary>
		private static void Div128(ulong high, ulong low, ulong divisor, out ulong quotient, out ulong remainder)
		{
			quotient = 0;
			ulong r = high;
			for (int i = 63; i >= 0; i--)
			{
				bool top = (r >> 63) != 0;
				r = (r << 1) | ((low >> i) & 1);
				if (top || r >= divisor)
				{
					r -= divisor;
					quotient |= 1UL << i;
				}
			}

			remainder = r;
		}

		private ulong Shift(int op, ulong value, uint count, int size)
		{
			count &= size == 64 ? 63u : 31u;
			ulong mask = Mask(size);
			value &= mask;
			if (count == 0)
			{
				return value;
			}

			ulong result;
			switch (op)
			{
				case 0: // ROL
				{
					int c = (int)(count % (uint)size);
					result = c == 0 ? value : ((value << c) | (value >> (size - c))) & mask;
					CF = (result & 1) != 0;
					OF = ((result & SignBit(size)) != 0) != CF;
					return result;
				}
				case 1: // ROR
				{
					int c = (int)(count % (uint)size);
					result = c == 0 ? value : ((value >> c) | (value << (size - c))) & mask;
					CF = (result & SignBit(size)) != 0;
					OF = ((result & SignBit(size)) != 0) != ((result & (SignBit(size) >> 1)) != 0);
					return result;
				}
				case 2: // RCL
					result = value;
					for (uint i = 0; i < count % (uint)(size + 1); i++)
					{
						bool carry = (result & SignBit(size)) != 0;
						result = ((result << 1) | (CF ? 1UL : 0)) & mask;
						CF = carry;
					}

					OF = ((result & SignBit(size)) != 0) != CF;
					return result;
				case 3: // RCR
					result = value;
					OF = ((value & SignBit(size)) != 0) != CF;
					for (uint i = 0; i < count % (uint)(size + 1); i++)
					{
						bool carry = (result & 1) != 0;
						result = (result >> 1) | (CF ? SignBit(size) : 0);
						CF = carry;
					}

					return result;
				case 4: case 6: // SHL
					if (count > (uint)size)
					{
						result = 0;
						CF = false;
					}
					else
					{
						CF = count == (uint)size ? (value & 1) != 0 : ((value >> (size - (int)count)) & 1) != 0;
						result = count == 64 ? 0 : (value << (int)count) & mask;
					}

					OF = ((result & SignBit(size)) != 0) != CF;
					SetResultFlags(result, size);
					return result;
				case 5: // SHR
					CF = count > (uint)size ? false : ((value >> ((int)count - 1)) & 1) != 0;
					OF = (value & SignBit(size)) != 0;
					result = count >= (uint)size ? 0 : value >> (int)count;
					SetResultFlags(result, size);
					return result;
				default: // SAR
				{
					long signed = (long)SignExtend(value, size);
					int c = Math.Min((int)count, 63);
					CF = ((signed >> (c - 1)) & 1) != 0;
					OF = false;
					result = (ulong)(signed >> c) & mask;
					SetResultFlags(result, size);
					return result;
				}
			}
		}

		private void Group3(int size)
		{
			DecodeModRm();
			int op = modReg & 7;
			switch (op)
			{
				case 0: case 1:
				{
					ulong imm = FetchSigned(size);
					SetLogicFlags(ReadRm(size) & imm, size);
					return;
				}
				case 2:
					WriteRm(size, ~ReadRm(size) & Mask(size));
					return;
				case 3:
				{
					ulong value = ReadRm(size);
					ulong result = Sub(0, value, size);
					CF = (value & Mask(size)) != 0;
					WriteRm(size, result);
					return;
				}
				case 4: // MUL
				{
					ulong a = GetReg(RAX, size), b = ReadRm(size);
					if (size == 64)
					{
						Mul128(a, b, out ulong high, out ulong low);
						R[RAX] = low;
						R[RDX] = high;
						CF = OF = high != 0;
					}
					else
					{
						ulong product = a * b;
						if (size == 8)
						{
							SetReg(RAX, 16, product);
							CF = OF = (product >> 8) != 0;
						}
						else
						{
							SetReg(RAX, size, product);
							SetReg(RDX, size, product >> size);
							CF = OF = (product >> size) != 0;
						}
					}

					return;
				}
				case 5: // IMUL
				{
					long a = (long)SignExtend(GetReg(RAX, size), size), b = (long)SignExtend(ReadRm(size), size);
					if (size == 64)
					{
						Mul128((ulong)a, (ulong)b, out ulong high, out ulong low);
						if (a < 0) high -= (ulong)b;
						if (b < 0) high -= (ulong)a;
						R[RAX] = low;
						R[RDX] = high;
						CF = OF = (long)high != (long)low >> 63;
					}
					else
					{
						long product = a * b;
						if (size == 8)
						{
							SetReg(RAX, 16, (ulong)product);
						}
						else
						{
							SetReg(RAX, size, (ulong)product);
							SetReg(RDX, size, (ulong)product >> size);
						}

						CF = OF = (long)SignExtend((ulong)product & Mask(size), size) != product;
					}

					return;
				}
				case 6: // DIV
				{
					ulong divisor = ReadRm(size);
					if (divisor == 0)
					{
						throw new X86Exception("the plugin divided by zero");
					}

					if (size == 64)
					{
						if (R[RDX] >= divisor)
						{
							throw new X86Exception("the plugin's division overflowed");
						}

						Div128(R[RDX], R[RAX], divisor, out ulong quotient, out ulong remainder);
						R[RAX] = quotient;
						R[RDX] = remainder;
					}
					else if (size == 8)
					{
						ulong dividend = GetReg(RAX, 16);
						SetReg(RAX, 16, (dividend / divisor & 0xFF) | ((dividend % divisor & 0xFF) << 8));
					}
					else
					{
						ulong dividend = GetReg(RAX, size) | (GetReg(RDX, size) << size);
						SetReg(RAX, size, dividend / divisor);
						SetReg(RDX, size, dividend % divisor);
					}

					return;
				}
				default: // IDIV
				{
					long divisor = (long)SignExtend(ReadRm(size), size);
					if (divisor == 0)
					{
						throw new X86Exception("the plugin divided by zero");
					}

					if (size == 64)
					{
						bool negative = (long)R[RDX] < 0;
						ulong high = R[RDX], low = R[RAX];
						if (negative)
						{
							low = ~low + 1;
							high = ~high + (low == 0 ? 1UL : 0);
						}

						ulong magnitude = divisor < 0 ? (ulong)(-divisor) : (ulong)divisor;
						if (high >= magnitude)
						{
							throw new X86Exception("the plugin's division overflowed");
						}

						Div128(high, low, magnitude, out ulong quotient, out ulong remainder);
						R[RAX] = negative != divisor < 0 ? ~quotient + 1 : quotient;
						R[RDX] = negative ? ~remainder + 1 : remainder;
					}
					else if (size == 8)
					{
						long dividend = (short)GetReg(RAX, 16);
						SetReg(RAX, 16, ((ulong)(dividend / divisor) & 0xFF) | (((ulong)(dividend % divisor) & 0xFF) << 8));
					}
					else
					{
						long dividend = (long)SignExtend(GetReg(RAX, size) | (GetReg(RDX, size) << size), size * 2 == 64 ? 64 : 32);
						SetReg(RAX, size, (ulong)(dividend / divisor));
						SetReg(RDX, size, (ulong)(dividend % divisor));
					}

					return;
				}
			}
		}

		private void Group5(int size)
		{
			DecodeModRm();
			int op = modReg & 7;
			switch (op)
			{
				case 0:
				{
					bool carry = CF;
					ulong result = Add(ReadRm(size), 1, size);
					CF = carry;
					WriteRm(size, result);
					return;
				}
				case 1:
				{
					bool carry = CF;
					ulong result = Sub(ReadRm(size), 1, size);
					CF = carry;
					WriteRm(size, result);
					return;
				}
				case 2:
				{
					ulong target = ReadRm(64);
					Push(Rip);
					Rip = target;
					return;
				}
				case 4:
					Rip = ReadRm(64);
					return;
				case 6:
					Push(ReadRm(64));
					return;
				default:
					throw Unsupported("FF /" + op);
			}
		}

		// --- string instructions: 0 MOVS, 1 CMPS, 2 STOS, 3 LODS, 4 SCAS ---

		private void StringOp(int kind, int size)
		{
			ulong step = (ulong)(size / 8);
			if (DF) step = (ulong)-(long)step;
			bool repeat = repz || repnz;
			if (repeat && R[RCX] == 0)
			{
				return;
			}

			while (true)
			{
				switch (kind)
				{
					case 0:
						WriteMem(R[RDI], size, ReadMem(R[RSI], size));
						R[RSI] += step;
						R[RDI] += step;
						break;
					case 1:
						Sub(ReadMem(R[RSI], size), ReadMem(R[RDI], size), size);
						R[RSI] += step;
						R[RDI] += step;
						break;
					case 2:
						WriteMem(R[RDI], size, GetReg(RAX, size));
						R[RDI] += step;
						break;
					case 3:
						SetReg(RAX, size, ReadMem(R[RSI], size));
						R[RSI] += step;
						break;
					default:
						Sub(GetReg(RAX, size), ReadMem(R[RDI], size), size);
						R[RDI] += step;
						break;
				}

				if (!repeat)
				{
					return;
				}

				R[RCX]--;
				if (R[RCX] == 0)
				{
					return;
				}

				if (kind == 1 || kind == 4)
				{
					if (repz && !ZF || repnz && ZF)
					{
						return;
					}
				}
			}
		}

		// --- two-byte opcodes ---

		private void StepTwoByte(int size)
		{
			byte op = Fetch8();
			if (op >= 0x40 && op <= 0x4F) // CMOVcc
			{
				DecodeModRm();
				ulong value = ReadRm(size);
				if (Condition(op & 15))
				{
					SetReg(modReg, size, value);
				}
				else if (size == 32)
				{
					R[modReg] &= 0xFFFFFFFF; // a 32-bit CMOV clears the upper half either way
				}

				return;
			}

			if (op >= 0x80 && op <= 0x8F) // Jcc
			{
				ulong displacement = FetchSigned(32);
				if (Condition(op & 15)) Rip += displacement;
				return;
			}

			if (op >= 0x90 && op <= 0x9F) // SETcc
			{
				DecodeModRm();
				WriteRm(8, Condition(op & 15) ? 1UL : 0);
				return;
			}

			if (op >= 0xC8 && op <= 0xCF) // BSWAP
			{
				int reg = (op & 7) | RexB;
				ulong value = R[reg];
				if (size == 64)
				{
					ulong v = value;
					v = ((v & 0x00FF00FF00FF00FF) << 8) | ((v >> 8) & 0x00FF00FF00FF00FF);
					v = ((v & 0x0000FFFF0000FFFF) << 16) | ((v >> 16) & 0x0000FFFF0000FFFF);
					R[reg] = (v << 32) | (v >> 32);
				}
				else
				{
					uint v = (uint)value;
					R[reg] = (v << 24) | ((v & 0xFF00) << 8) | ((v >> 8) & 0xFF00) | (v >> 24);
				}

				return;
			}

			switch (op)
			{
				case 0x01:
				{
					byte next = Fetch8();
					if (next == 0xD0) // XGETBV: no AVX state, as CPUID says
					{
						R[RAX] = 3;
						R[RDX] = 0;
						return;
					}

					throw Unsupported("0F 01 " + next.ToString("X2"));
				}
				case 0x0B:
					throw new X86Exception("the plugin ran into UD2 (its code says this cannot happen)");
				case 0x0D: case 0x18: case 0x19: case 0x1A: case 0x1B: case 0x1C: case 0x1D: case 0x1E: case 0x1F:
					DecodeModRm(); // prefetches and multi-byte NOPs
					return;
				case 0x31: // RDTSC
				{
					ulong ticks = (ulong)InstructionCount;
					R[RAX] = (uint)ticks;
					R[RDX] = ticks >> 32;
					return;
				}
				case 0xA2:
					Cpuid();
					return;
				case 0xA3:
				{
					DecodeModRm();
					BitOp(0, size, GetReg(modReg, size));
					return;
				}
				case 0xAB:
					DecodeModRm();
					BitOp(1, size, GetReg(modReg, size));
					return;
				case 0xB3:
					DecodeModRm();
					BitOp(2, size, GetReg(modReg, size));
					return;
				case 0xBB:
					DecodeModRm();
					BitOp(3, size, GetReg(modReg, size));
					return;
				case 0xBA:
				{
					DecodeModRm();
					int kind = modReg & 7;
					if (kind < 4) throw Unsupported("0F BA /" + kind);
					ulong bit = Fetch8();
					BitOp(kind - 4, size, bit & (ulong)(size - 1));
					return;
				}
				case 0xA4: case 0xA5:
				{
					DecodeModRm();
					uint count = op == 0xA4 ? Fetch8() : (uint)R[RCX] & 0xFF;
					DoubleShift(true, size, count);
					return;
				}
				case 0xAC: case 0xAD:
				{
					DecodeModRm();
					uint count = op == 0xAC ? Fetch8() : (uint)R[RCX] & 0xFF;
					DoubleShift(false, size, count);
					return;
				}
				case 0xAE:
				{
					byte peek = Memory.Read8(Address(Rip));
					if (peek >= 0xE8) // LFENCE, MFENCE, SFENCE
					{
						Rip++;
						return;
					}

					DecodeModRm();
					int kind = modReg & 7;
					switch (kind)
					{
						case 2: mxcsr = (uint)ReadMem(Ea, 32); return;
						case 3: WriteMem(Ea, 32, mxcsr); return;
						case 7: return; // CLFLUSH
						default: throw Unsupported("0F AE /" + kind);
					}
				}
				case 0xAF:
				{
					DecodeModRm();
					SetReg(modReg, size, Imul((long)SignExtend(GetReg(modReg, size), size), (long)SignExtend(ReadRm(size), size), size));
					return;
				}
				case 0xB0: case 0xB1:
				{
					int s = op == 0xB0 ? 8 : size;
					DecodeModRm();
					ulong destination = ReadRm(s);
					ulong accumulator = GetReg(RAX, s);
					Sub(accumulator, destination, s);
					if (ZF)
					{
						WriteRm(s, GetReg(modReg, s));
					}
					else
					{
						SetReg(RAX, s, destination);
					}

					return;
				}
				case 0xB6: DecodeModRm(); SetReg(modReg, size, ReadRm(8)); return;
				case 0xB7: DecodeModRm(); SetReg(modReg, size, ReadRm(16)); return;
				case 0xBE: DecodeModRm(); SetReg(modReg, size, SignExtend(ReadRm(8), 8)); return;
				case 0xBF: DecodeModRm(); SetReg(modReg, size, SignExtend(ReadRm(16), 16)); return;
				case 0xBC: case 0xBD:
				{
					DecodeModRm();
					ulong value = ReadRm(size);
					ZF = value == 0;
					if (value != 0)
					{
						int index = 0;
						if (op == 0xBC)
						{
							while (((value >> index) & 1) == 0) index++;
						}
						else
						{
							index = size - 1;
							while (((value >> index) & 1) == 0) index--;
						}

						SetReg(modReg, size, (ulong)index);
					}

					return;
				}
				case 0xC0: case 0xC1:
				{
					int s = op == 0xC0 ? 8 : size;
					DecodeModRm();
					ulong destination = ReadRm(s);
					ulong source = GetReg(modReg, s);
					ulong sum = Add(destination, source, s);
					SetReg(modReg, s, destination);
					WriteRm(s, sum);
					return;
				}
				case 0xC7:
				{
					DecodeModRm();
					if ((modReg & 7) != 1 || ModIsRegister) throw Unsupported("0F C7 /" + (modReg & 7));
					ulong address = Ea;
					if (RexW)
					{
						ulong low = ReadMem(address, 64), high = ReadMem(address + 8, 64);
						ZF = low == R[RAX] && high == R[RDX];
						if (ZF)
						{
							WriteMem(address, 64, R[RBX]);
							WriteMem(address + 8, 64, R[RCX]);
						}
						else
						{
							R[RAX] = low;
							R[RDX] = high;
						}
					}
					else
					{
						ulong value = ReadMem(address, 64);
						ZF = value == (((R[RDX] & 0xFFFFFFFF) << 32) | (R[RAX] & 0xFFFFFFFF));
						if (ZF)
						{
							WriteMem(address, 64, ((R[RCX] & 0xFFFFFFFF) << 32) | (R[RBX] & 0xFFFFFFFF));
						}
						else
						{
							R[RAX] = (uint)value;
							R[RDX] = value >> 32;
						}
					}

					return;
				}
				default:
					StepSse(op);
					return;
			}
		}

		/// <summary>BT, BTS, BTR, BTC. In memory a register's bit number can reach past the operand, as on the processor.</summary>
		private void BitOp(int kind, int size, ulong bit)
		{
			long index = (long)SignExtend(bit, size);
			if (ModIsRegister)
			{
				int b = (int)(index & (size - 1));
				ulong value = GetReg(modRm, size);
				CF = ((value >> b) & 1) != 0;
				if (kind == 1) SetReg(modRm, size, value | (1UL << b));
				else if (kind == 2) SetReg(modRm, size, value & ~(1UL << b));
				else if (kind == 3) SetReg(modRm, size, value ^ (1UL << b));
				return;
			}

			ulong address = Ea + (ulong)((index >> 3) & ~(long)(size / 8 - 1));
			int offset = (int)(index & (size - 1));
			ulong memory = ReadMem(address, size);
			CF = ((memory >> offset) & 1) != 0;
			if (kind == 1) WriteMem(address, size, memory | (1UL << offset));
			else if (kind == 2) WriteMem(address, size, memory & ~(1UL << offset));
			else if (kind == 3) WriteMem(address, size, memory ^ (1UL << offset));
		}

		private void DoubleShift(bool left, int size, uint count)
		{
			count &= size == 64 ? 63u : 31u;
			if (count == 0)
			{
				return;
			}

			ulong destination = ReadRm(size), source = GetReg(modReg, size), result;
			if (left)
			{
				CF = ((destination >> (size - (int)count)) & 1) != 0;
				result = ((destination << (int)count) | (source >> (size - (int)count))) & Mask(size);
			}
			else
			{
				CF = ((destination >> ((int)count - 1)) & 1) != 0;
				result = ((destination >> (int)count) | (source << (size - (int)count))) & Mask(size);
			}

			OF = ((result ^ destination) & SignBit(size)) != 0;
			SetResultFlags(result, size);
			WriteRm(size, result);
		}

		/// <summary>An x86-64 processor with SSE2 and nothing newer, so run-time checks in the plugin choose its plain paths.</summary>
		private void Cpuid()
		{
			uint leaf = (uint)R[RAX];
			switch (leaf)
			{
				case 0:
					R[RAX] = 1;
					R[RBX] = 0x756E6547; // "GenuineIntel"
					R[RDX] = 0x49656E69;
					R[RCX] = 0x6C65746E;
					break;
				case 1:
					R[RAX] = 0x00000F41;
					R[RBX] = 0;
					R[RCX] = 0;
					// FPU, TSC, CX8, CMOV, FXSR, MMX, SSE, SSE2.
					R[RDX] = 1u | (1u << 4) | (1u << 8) | (1u << 15) | (1u << 23) | (1u << 24) | (1u << 25) | (1u << 26);
					break;
				default:
					R[RAX] = R[RBX] = R[RCX] = R[RDX] = 0;
					break;
			}
		}

		/// <summary>x87: only what a C runtime's start-up and floating-point resets do; x64 code does its sums in SSE.</summary>
		private void StepFpu(byte op)
		{
			DecodeModRm();
			int kind = modReg & 7;
			if (op == 0xDB && modMod == 3 && kind == 4) return; // FNCLEX, FNINIT
			if (op == 0xD9 && modMod != 3 && kind == 7) { WriteMem(Ea, 16, 0x27F); return; } // FNSTCW
			if (op == 0xD9 && modMod != 3 && kind == 5) return; // FLDCW
			if (op == 0xDD && modMod != 3 && kind == 7) { WriteMem(Ea, 16, 0); return; } // FNSTSW
			if (op == 0xDF && modMod == 3 && kind == 4) { SetReg(RAX, 16, 0); return; } // FNSTSW AX
			throw Unsupported("x87 0x" + op.ToString("X2") + " /" + kind);
		}
	}
}
