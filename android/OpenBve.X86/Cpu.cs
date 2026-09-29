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

namespace OpenBve.X86
{
	/// <summary>A function the host provides in place of a Windows DLL export.</summary>
	/// <param name="cpu">The processor, positioned at the call: arguments start at [ESP + 4].</param>
	/// <returns>The value for EAX (EDX takes the high half for 64-bit results).</returns>
	public delegate ulong HostFunction(Cpu cpu);

	/// <summary>
	/// An IA-32 interpreter: the integer instruction set, x87 (Cpu.Fpu.cs) and the SSE/SSE2
	/// subset compilers emit for scalar maths and memory moves (Cpu.Sse.cs).
	/// </summary>
	/// <remarks>
	/// Flat 32-bit protected mode only, as a Windows DLL sees it: no segmentation apart from FS
	/// (the thread information block), no privileged instructions. Anything outside that raises
	/// <see cref="X86Exception"/> naming the instruction and its address.
	/// </remarks>
	public sealed partial class Cpu
	{
		// --- registers ---
		public const int EAX = 0, ECX = 1, EDX = 2, EBX = 3, ESP = 4, EBP = 5, ESI = 6, EDI = 7;

		public readonly uint[] R = new uint[8];
		public uint Eip;
		public uint FsBase;
		public bool CF, PF, AF, ZF, SF, OF, DF;
		private bool IF = true;

		public readonly Memory Memory;

		// --- host functions ---

		/*
		 * Each host function has an address in a reserved region. Emulated code calls it like any
		 * other function; reaching that address runs the host function, which reads its arguments
		 * from the stack, and then "returns" by popping the return address and, for stdcall, the
		 * arguments.
		 */
		public const uint HostRegion = 0xFFF00000;
		private const uint ReturnTrap = 0xFFFFFFF0;
		private readonly List<(HostFunction Function, int ArgBytes, string Name)> hostFunctions = new List<(HostFunction, int, string)>();

		/// <summary>Instructions executed, for diagnostics and runaway protection.</summary>
		public long InstructionCount;

		/// <summary>The most instructions one call into emulated code may take before it is abandoned.</summary>
		public long InstructionBudget = 500_000_000;

		/// <summary>Optional trace of the last few instruction addresses, for fault reports.</summary>
		private readonly uint[] recent = new uint[32];
		private int recentIndex;

		public Cpu(Memory memory)
		{
			Memory = memory;
			Memory.Map(HostRegion, 0x100000);
			FpuReset();
		}

		/// <summary>Registers a host function and returns the address emulated code calls it at.</summary>
		/// <param name="argBytes">Bytes of arguments the function removes on return (stdcall), or 0 (cdecl).</param>
		public uint AddHostFunction(string name, HostFunction function, int argBytes)
		{
			hostFunctions.Add((function, argBytes, name));
			uint address = HostRegion + (uint)(hostFunctions.Count - 1) * 4;
			// A HLT there, in case anything tries to decode it.
			Memory.Write8(address, 0xF4);
			return address;
		}

		/// <summary>
		/// Diagnostics: when set, called after every host (Windows API) function with its name, its
		/// first six stack arguments and its result.
		/// </summary>
		public Action<string, uint[], ulong> HostCallTrace { get; set; }

		/// <summary>The name of the host function at an address, for diagnostics.</summary>
		public string HostFunctionName(uint address)
		{
			int index = (int)((address - HostRegion) / 4);
			return address >= HostRegion && index < hostFunctions.Count ? hostFunctions[index].Name : null;
		}

		// --- calling into emulated code ---

		/// <summary>Calls an emulated function with 32-bit arguments, returning EAX (and EDX in the high half).</summary>
		public ulong Call(uint function, params uint[] args)
		{
			// Restored afterwards whatever the callee pops, so stdcall and cdecl callees both leave the stack balanced.
			uint savedEsp = R[ESP];
			for (int i = args.Length - 1; i >= 0; i--)
			{
				Push(args[i]);
			}

			ulong result = CallPrepared(function);
			R[ESP] = savedEsp;
			return result;
		}

		/// <summary>Calls an emulated function whose arguments are already on the stack.</summary>
		public ulong CallPrepared(uint function)
		{
			Push(ReturnTrap);
			uint savedEip = Eip;
			Eip = function;
			Run();
			Eip = savedEip;
			return R[EAX] | ((ulong)R[EDX] << 32);
		}

		/// <summary>Runs until the current call returns to the trap address.</summary>
		private void Run()
		{
			long budget = InstructionCount + InstructionBudget;
			while (true)
			{
				if (Eip == ReturnTrap)
				{
					return;
				}

				if (Eip >= HostRegion)
				{
					InvokeHost();
					continue;
				}

				if (InstructionCount > budget)
				{
					throw new X86Exception("the plugin ran for too long without returning (at 0x" + Eip.ToString("X8") + ")");
				}

				recent[recentIndex++ & 31] = Eip;
				try
				{
					Step();
				}
				catch (X86Exception ex) when (!ex.Message.Contains(" [at "))
				{
					throw new X86Exception(ex.Message + " [at 0x" + lastInstruction.ToString("X8") + "]");
				}

				InstructionCount++;
			}
		}

		private void InvokeHost()
		{
			int index = (int)((Eip - HostRegion) / 4);
			if (index >= hostFunctions.Count)
			{
				throw new X86Exception("call to an unknown host address 0x" + Eip.ToString("X8"));
			}

			(HostFunction function, int argBytes, string name) = hostFunctions[index];
			uint[] traceArgs = HostCallTrace != null ? new[] { Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5) } : null;
			ulong result = function(this);
			HostCallTrace?.Invoke(name, traceArgs, result);
			R[EAX] = (uint)result;
			R[EDX] = (uint)(result >> 32);
			Eip = Pop();
			R[ESP] += (uint)argBytes;
		}

		/// <summary>The addresses of the most recently executed instructions, oldest first.</summary>
		public IEnumerable<uint> RecentInstructions()
		{
			for (int i = 0; i < 32; i++)
			{
				uint address = recent[(recentIndex + i) & 31];
				if (address != 0)
				{
					yield return address;
				}
			}
		}

		// --- stack ---

		public void Push(uint value)
		{
			R[ESP] -= 4;
			Memory.Write32(R[ESP], value);
		}

		public uint Pop()
		{
			uint value = Memory.Read32(R[ESP]);
			R[ESP] += 4;
			return value;
		}

		/// <summary>Reads the n-th 32-bit argument of a host function (0-based).</summary>
		public uint Arg(int n)
		{
			return Memory.Read32(R[ESP] + 4 + (uint)(4 * n));
		}

		// --- decoding state for the current instruction ---

		private uint lastInstruction;
		private bool opSize16;
		private bool repz, repnz;
		private int segment; // -1 none, 4 = FS, 5 = GS
		private bool addressSize16;

		private byte Fetch8()
		{
			byte value = Memory.Read8(Eip);
			Eip++;
			return value;
		}

		private ushort Fetch16()
		{
			ushort value = Memory.Read16(Eip);
			Eip += 2;
			return value;
		}

		private uint Fetch32()
		{
			uint value = Memory.Read32(Eip);
			Eip += 4;
			return value;
		}

		private uint FetchImm(int size)
		{
			return size == 8 ? Fetch8() : size == 16 ? Fetch16() : Fetch32();
		}

		/// <summary>An immediate of the given size, sign-extended to 32 bits.</summary>
		private uint FetchSigned(int size)
		{
			return size == 8 ? (uint)(sbyte)Fetch8() : size == 16 ? (uint)(short)Fetch16() : Fetch32();
		}

		// --- ModR/M ---

		private int modMod, modReg, modRm;
		private uint modAddress;

		private bool ModIsRegister => modMod == 3;

		private void DecodeModRm()
		{
			byte b = Fetch8();
			modMod = b >> 6;
			modReg = (b >> 3) & 7;
			modRm = b & 7;
			if (modMod == 3)
			{
				return;
			}

			uint address;
			if (addressSize16)
			{
				// The 16-bit forms (67 prefix): MSVC's SEH prologue uses [disp16] to reach fs:[0].
				if (modMod == 0 && modRm == 6)
				{
					address = Fetch16();
				}
				else
				{
					uint bx = R[EBX] & 0xFFFF, bp = R[EBP] & 0xFFFF, si = R[ESI] & 0xFFFF, di = R[EDI] & 0xFFFF;
					address = modRm switch
					{
						0 => bx + si,
						1 => bx + di,
						2 => bp + si,
						3 => bp + di,
						4 => si,
						5 => di,
						6 => bp,
						_ => bx
					};
					if (modMod == 1)
					{
						address += (uint)(sbyte)Fetch8();
					}
					else if (modMod == 2)
					{
						address += Fetch16();
					}
				}

				address &= 0xFFFF;
				if (segment == 4)
				{
					address += FsBase;
				}

				modAddress = address;
				return;
			}

			if (modRm == 4)
			{
				byte sib = Fetch8();
				int scale = sib >> 6;
				int index = (sib >> 3) & 7;
				int baseReg = sib & 7;
				if (baseReg == 5 && modMod == 0)
				{
					address = Fetch32();
				}
				else
				{
					address = R[baseReg];
				}

				if (index != 4)
				{
					address += R[index] << scale;
				}
			}
			else if (modRm == 5 && modMod == 0)
			{
				address = Fetch32();
			}
			else
			{
				address = R[modRm];
			}

			if (modMod == 1)
			{
				address += (uint)(sbyte)Fetch8();
			}
			else if (modMod == 2)
			{
				address += Fetch32();
			}

			if (segment == 4)
			{
				address += FsBase;
			}

			modAddress = address;
		}

		// --- register and operand access by size ---

		private uint GetReg(int reg, int size)
		{
			switch (size)
			{
				case 8:
					return reg < 4 ? R[reg] & 0xFF : (R[reg - 4] >> 8) & 0xFF;
				case 16:
					return R[reg] & 0xFFFF;
				default:
					return R[reg];
			}
		}

		private void SetReg(int reg, int size, uint value)
		{
			switch (size)
			{
				case 8:
					if (reg < 4)
					{
						R[reg] = (R[reg] & 0xFFFFFF00) | (value & 0xFF);
					}
					else
					{
						R[reg - 4] = (R[reg - 4] & 0xFFFF00FF) | ((value & 0xFF) << 8);
					}

					break;
				case 16:
					R[reg] = (R[reg] & 0xFFFF0000) | (value & 0xFFFF);
					break;
				default:
					R[reg] = value;
					break;
			}
		}

		private uint ReadMem(uint address, int size)
		{
			return size == 8 ? Memory.Read8(address) : size == 16 ? Memory.Read16(address) : Memory.Read32(address);
		}

		private void WriteMem(uint address, int size, uint value)
		{
			switch (size)
			{
				case 8:
					Memory.Write8(address, (byte)value);
					break;
				case 16:
					Memory.Write16(address, (ushort)value);
					break;
				default:
					Memory.Write32(address, value);
					break;
			}
		}

		private uint ReadRm(int size)
		{
			return ModIsRegister ? GetReg(modRm, size) : ReadMem(modAddress, size);
		}

		private void WriteRm(int size, uint value)
		{
			if (ModIsRegister)
			{
				SetReg(modRm, size, value);
			}
			else
			{
				WriteMem(modAddress, size, value);
			}
		}

		// --- flags ---

		private static uint Mask(int size)
		{
			return size == 32 ? 0xFFFFFFFF : (1u << size) - 1;
		}

		private static uint SignBit(int size)
		{
			return 1u << (size - 1);
		}

		private static readonly bool[] parity = BuildParity();

		private static bool[] BuildParity()
		{
			bool[] table = new bool[256];
			for (int i = 0; i < 256; i++)
			{
				int bits = 0;
				for (int b = i; b != 0; b >>= 1)
				{
					bits += b & 1;
				}

				table[i] = (bits & 1) == 0;
			}

			return table;
		}

		private void SetLogicFlags(uint result, int size)
		{
			result &= Mask(size);
			CF = false;
			OF = false;
			AF = false;
			ZF = result == 0;
			SF = (result & SignBit(size)) != 0;
			PF = parity[result & 0xFF];
		}

		private void SetResultFlags(uint result, int size)
		{
			result &= Mask(size);
			ZF = result == 0;
			SF = (result & SignBit(size)) != 0;
			PF = parity[result & 0xFF];
		}

		private uint Add(uint a, uint b, int size, bool carryIn = false)
		{
			uint mask = Mask(size);
			a &= mask;
			b &= mask;
			ulong wide = (ulong)a + b + (carryIn ? 1u : 0u);
			uint result = (uint)wide & mask;
			CF = wide > mask;
			OF = ((a ^ result) & (b ^ result) & SignBit(size)) != 0;
			AF = ((a ^ b ^ result) & 0x10) != 0;
			SetResultFlags(result, size);
			return result;
		}

		private uint Sub(uint a, uint b, int size, bool borrowIn = false)
		{
			uint mask = Mask(size);
			a &= mask;
			b &= mask;
			uint borrow = borrowIn ? 1u : 0u;
			uint result = (a - b - borrow) & mask;
			CF = (ulong)a < (ulong)b + borrow;
			OF = ((a ^ b) & (a ^ result) & SignBit(size)) != 0;
			AF = ((a ^ b ^ result) & 0x10) != 0;
			SetResultFlags(result, size);
			return result;
		}

		/// <summary>The eight ALU operations of opcodes 00-3F and groups 80-83, by their /reg number.</summary>
		private uint Alu(int op, uint a, uint b, int size)
		{
			switch (op)
			{
				case 0:
					return Add(a, b, size);
				case 1:
				{
					uint r = (a | b) & Mask(size);
					SetLogicFlags(r, size);
					return r;
				}
				case 2:
					return Add(a, b, size, CF);
				case 3:
					return Sub(a, b, size, CF);
				case 4:
				{
					uint r = a & b & Mask(size);
					SetLogicFlags(r, size);
					return r;
				}
				case 5:
				case 7:
					return Sub(a, b, size);
				default:
				{
					uint r = (a ^ b) & Mask(size);
					SetLogicFlags(r, size);
					return r;
				}
			}
		}

		public uint Eflags
		{
			get
			{
				uint f = 0x2;
				if (CF) f |= 1u << 0;
				if (PF) f |= 1u << 2;
				if (AF) f |= 1u << 4;
				if (ZF) f |= 1u << 6;
				if (SF) f |= 1u << 7;
				if (IF) f |= 1u << 9;
				if (DF) f |= 1u << 10;
				if (OF) f |= 1u << 11;
				return f | idFlag;
			}
			set
			{
				CF = (value & (1u << 0)) != 0;
				PF = (value & (1u << 2)) != 0;
				AF = (value & (1u << 4)) != 0;
				ZF = (value & (1u << 6)) != 0;
				SF = (value & (1u << 7)) != 0;
				IF = (value & (1u << 9)) != 0;
				DF = (value & (1u << 10)) != 0;
				OF = (value & (1u << 11)) != 0;
				idFlag = value & (1u << 21);
			}
		}

		// The ID flag: toggling it is how old code detects CPUID, so it must stick.
		private uint idFlag;

		private bool Condition(int cc)
		{
			bool result;
			switch (cc >> 1)
			{
				case 0: result = OF; break;
				case 1: result = CF; break;
				case 2: result = ZF; break;
				case 3: result = CF || ZF; break;
				case 4: result = SF; break;
				case 5: result = PF; break;
				case 6: result = SF != OF; break;
				default: result = ZF || SF != OF; break;
			}

			return (cc & 1) != 0 ? !result : result;
		}

		// --- execution ---

		private X86Exception Unsupported(string what)
		{
			return new X86Exception("unsupported instruction: " + what);
		}

		/// <summary>Executes one instruction.</summary>
		public void Step()
		{
			lastInstruction = Eip;
			opSize16 = false;
			addressSize16 = false;
			repz = false;
			repnz = false;
			segment = -1;

			byte op;
			while (true)
			{
				op = Fetch8();
				switch (op)
				{
					case 0x66: opSize16 = true; continue;
					case 0xF3: repz = true; continue;
					case 0xF2: repnz = true; continue;
					case 0xF0: continue; // LOCK: single-threaded, so a no-op
					case 0x26: case 0x2E: case 0x36: case 0x3E: continue; // flat segments
					case 0x64: segment = 4; continue;
					case 0x65: segment = 5; continue;
					case 0x67: addressSize16 = true; continue;
				}

				break;
			}

			int osz = opSize16 ? 16 : 32;

			if (op < 0x40 && (op & 7) < 6)
			{
				// ALU r/m,r ; r,r/m ; AL/eAX,imm
				int aluOp = op >> 3;
				int form = op & 7;
				switch (form)
				{
					case 0:
					case 1:
					{
						int size = form == 0 ? 8 : osz;
						DecodeModRm();
						uint result = Alu(aluOp, ReadRm(size), GetReg(modReg, size), size);
						if (aluOp != 7)
						{
							WriteRm(size, result);
						}

						return;
					}
					case 2:
					case 3:
					{
						int size = form == 2 ? 8 : osz;
						DecodeModRm();
						uint result = Alu(aluOp, GetReg(modReg, size), ReadRm(size), size);
						if (aluOp != 7)
						{
							SetReg(modReg, size, result);
						}

						return;
					}
					case 4:
					{
						uint result = Alu(aluOp, GetReg(EAX, 8), Fetch8(), 8);
						if (aluOp != 7)
						{
							SetReg(EAX, 8, result);
						}

						return;
					}
					default:
					{
						uint result = Alu(aluOp, GetReg(EAX, osz), FetchImm(osz), osz);
						if (aluOp != 7)
						{
							SetReg(EAX, osz, result);
						}

						return;
					}
				}
			}

			switch (op)
			{
				case 0x0F:
					StepTwoByte(osz);
					return;

				// INC / DEC r
				case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
				{
					bool carry = CF;
					SetReg(op & 7, osz, Add(GetReg(op & 7, osz), 1, osz));
					CF = carry;
					return;
				}
				case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F:
				{
					bool carry = CF;
					SetReg(op & 7, osz, Sub(GetReg(op & 7, osz), 1, osz));
					CF = carry;
					return;
				}

				// PUSH / POP r
				case 0x50: case 0x51: case 0x52: case 0x53: case 0x54: case 0x55: case 0x56: case 0x57:
					Push(R[op & 7]);
					return;
				case 0x58: case 0x59: case 0x5A: case 0x5B: case 0x5C: case 0x5D: case 0x5E: case 0x5F:
					R[op & 7] = Pop();
					return;

				case 0x60: // PUSHAD
				{
					uint esp = R[ESP];
					Push(R[EAX]); Push(R[ECX]); Push(R[EDX]); Push(R[EBX]);
					Push(esp); Push(R[EBP]); Push(R[ESI]); Push(R[EDI]);
					return;
				}
				case 0x61: // POPAD
					R[EDI] = Pop(); R[ESI] = Pop(); R[EBP] = Pop(); Pop();
					R[EBX] = Pop(); R[EDX] = Pop(); R[ECX] = Pop(); R[EAX] = Pop();
					return;

				case 0x68:
					Push(FetchSigned(osz));
					return;
				case 0x6A:
					Push((uint)(sbyte)Fetch8());
					return;

				case 0x69: // IMUL r, r/m, imm
				case 0x6B:
				{
					DecodeModRm();
					int a = (int)SignExtend(ReadRm(osz), osz);
					int b = (int)(op == 0x6B ? (uint)(sbyte)Fetch8() : SignExtend(FetchImm(osz), osz));
					SetReg(modReg, osz, ImulFlags(a, b, osz));
					return;
				}

				// Jcc rel8
				case 0x70: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: case 0x76: case 0x77:
				case 0x78: case 0x79: case 0x7A: case 0x7B: case 0x7C: case 0x7D: case 0x7E: case 0x7F:
				{
					uint rel = (uint)(sbyte)Fetch8();
					if (Condition(op & 0xF))
					{
						Eip += rel;
					}

					return;
				}

				case 0x80: // group 1: r/m8, imm8
				case 0x82:
				{
					DecodeModRm();
					uint result = Alu(modReg, ReadRm(8), Fetch8(), 8);
					if (modReg != 7)
					{
						WriteRm(8, result);
					}

					return;
				}
				case 0x81:
				case 0x83:
				{
					DecodeModRm();
					uint imm = op == 0x83 ? (uint)(sbyte)Fetch8() : FetchImm(osz);
					uint result = Alu(modReg, ReadRm(osz), imm, osz);
					if (modReg != 7)
					{
						WriteRm(osz, result);
					}

					return;
				}

				case 0x84: // TEST r/m, r
				case 0x85:
				{
					int size = op == 0x84 ? 8 : osz;
					DecodeModRm();
					SetLogicFlags(ReadRm(size) & GetReg(modReg, size), size);
					return;
				}
				case 0x86: // XCHG r/m, r
				case 0x87:
				{
					int size = op == 0x86 ? 8 : osz;
					DecodeModRm();
					uint a = ReadRm(size);
					WriteRm(size, GetReg(modReg, size));
					SetReg(modReg, size, a);
					return;
				}

				// MOV
				case 0x88:
					DecodeModRm();
					WriteRm(8, GetReg(modReg, 8));
					return;
				case 0x89:
					DecodeModRm();
					WriteRm(osz, GetReg(modReg, osz));
					return;
				case 0x8A:
					DecodeModRm();
					SetReg(modReg, 8, ReadRm(8));
					return;
				case 0x8B:
					DecodeModRm();
					SetReg(modReg, osz, ReadRm(osz));
					return;
				case 0x8C: // MOV r/m, Sreg: flat model, report 0x23 for data segments, 0x3B for FS
					DecodeModRm();
					WriteRm(16, modReg == 4 ? 0x3Bu : modReg == 1 ? 0x1Bu : 0x23u);
					return;
				case 0x8D: // LEA
				{
					int saved = segment;
					segment = -1;
					DecodeModRm();
					segment = saved;
					if (ModIsRegister)
					{
						throw Unsupported("LEA with a register operand");
					}

					SetReg(modReg, osz, modAddress);
					return;
				}
				case 0x8E: // MOV Sreg, r/m: ignored in a flat model
					DecodeModRm();
					ReadRm(16);
					return;
				case 0x8F: // POP r/m
				{
					uint value = Pop();
					DecodeModRm();
					WriteRm(osz, value);
					return;
				}

				case 0x90:
					return; // NOP (and PAUSE with F3)
				case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
				{
					uint a = GetReg(EAX, osz);
					SetReg(EAX, osz, GetReg(op & 7, osz));
					SetReg(op & 7, osz, a);
					return;
				}
				case 0x98: // CWDE / CBW
					if (opSize16)
					{
						SetReg(EAX, 16, (uint)(sbyte)R[EAX]);
					}
					else
					{
						R[EAX] = (uint)(short)R[EAX];
					}

					return;
				case 0x99: // CDQ / CWD
					if (opSize16)
					{
						SetReg(EDX, 16, (R[EAX] & 0x8000) != 0 ? 0xFFFFu : 0u);
					}
					else
					{
						R[EDX] = (R[EAX] & 0x80000000) != 0 ? 0xFFFFFFFF : 0;
					}

					return;
				case 0x9B: // FWAIT
					return;
				case 0x9C: // PUSHFD
					Push(Eflags);
					return;
				case 0x9D: // POPFD
					Eflags = Pop();
					return;
				case 0x9E: // SAHF
				{
					uint ah = (R[EAX] >> 8) & 0xFF;
					CF = (ah & 1) != 0; PF = (ah & 4) != 0; AF = (ah & 0x10) != 0; ZF = (ah & 0x40) != 0; SF = (ah & 0x80) != 0;
					return;
				}
				case 0x9F: // LAHF
					SetReg(4, 8, Eflags & 0xFF);
					return;

				// MOV AL/eAX <-> moffs
				case 0xA0:
					SetReg(EAX, 8, Memory.Read8(Moffs()));
					return;
				case 0xA1:
					SetReg(EAX, osz, ReadMem(Moffs(), osz));
					return;
				case 0xA2:
					Memory.Write8(Moffs(), (byte)R[EAX]);
					return;
				case 0xA3:
					WriteMem(Moffs(), osz, R[EAX]);
					return;

				// string instructions
				case 0xA4: StringOp(Movs, 8); return;
				case 0xA5: StringOp(Movs, osz); return;
				case 0xA6: StringOp(Cmps, 8); return;
				case 0xA7: StringOp(Cmps, osz); return;
				case 0xAA: StringOp(Stos, 8); return;
				case 0xAB: StringOp(Stos, osz); return;
				case 0xAC: StringOp(Lods, 8); return;
				case 0xAD: StringOp(Lods, osz); return;
				case 0xAE: StringOp(Scas, 8); return;
				case 0xAF: StringOp(Scas, osz); return;

				case 0xA8: // TEST AL/eAX, imm
					SetLogicFlags(R[EAX] & Fetch8(), 8);
					return;
				case 0xA9:
					SetLogicFlags(R[EAX] & FetchImm(osz), osz);
					return;

				// MOV r, imm
				case 0xB0: case 0xB1: case 0xB2: case 0xB3: case 0xB4: case 0xB5: case 0xB6: case 0xB7:
					SetReg(op & 7, 8, Fetch8());
					return;
				case 0xB8: case 0xB9: case 0xBA: case 0xBB: case 0xBC: case 0xBD: case 0xBE: case 0xBF:
					SetReg(op & 7, osz, FetchImm(osz));
					return;

				case 0xC0: // shift group, imm8
				case 0xC1:
				{
					int size = op == 0xC0 ? 8 : osz;
					DecodeModRm();
					uint value = ReadRm(size);
					WriteRm(size, Shift(modReg, value, Fetch8(), size));
					return;
				}
				case 0xD0:
				case 0xD1:
				case 0xD2:
				case 0xD3:
				{
					int size = (op & 1) == 0 ? 8 : osz;
					DecodeModRm();
					uint count = op < 0xD2 ? 1u : R[ECX] & 0xFF;
					WriteRm(size, Shift(modReg, ReadRm(size), count, size));
					return;
				}

				case 0xC2: // RET imm16
				{
					ushort bytes = Fetch16();
					Eip = Pop();
					R[ESP] += bytes;
					return;
				}
				case 0xC3:
					Eip = Pop();
					return;

				case 0xC6: // MOV r/m, imm
					DecodeModRm();
					WriteRm(8, Fetch8());
					return;
				case 0xC7:
					DecodeModRm();
					WriteRm(osz, FetchImm(osz));
					return;

				case 0xC8: // ENTER
				{
					ushort size = Fetch16();
					byte level = Fetch8();
					if (level != 0)
					{
						throw Unsupported("ENTER with a nesting level");
					}

					Push(R[EBP]);
					R[EBP] = R[ESP];
					R[ESP] -= size;
					return;
				}
				case 0xC9: // LEAVE
					R[ESP] = R[EBP];
					R[EBP] = Pop();
					return;

				case 0xCC: // INT3
					throw new X86Exception("breakpoint (INT3) reached");
				case 0xCD:
					throw new X86Exception("software interrupt INT " + Fetch8().ToString("X2"));

				case 0xD8: case 0xD9: case 0xDA: case 0xDB: case 0xDC: case 0xDD: case 0xDE: case 0xDF:
					StepFpu(op);
					return;

				case 0xE0: // LOOPNE / LOOPE / LOOP
				case 0xE1:
				case 0xE2:
				{
					uint rel = (uint)(sbyte)Fetch8();
					R[ECX]--;
					bool take = R[ECX] != 0 && (op == 0xE2 || (op == 0xE1 ? ZF : !ZF));
					if (take)
					{
						Eip += rel;
					}

					return;
				}
				case 0xE3: // JECXZ
				{
					uint rel = (uint)(sbyte)Fetch8();
					if (R[ECX] == 0)
					{
						Eip += rel;
					}

					return;
				}

				case 0xE8: // CALL rel32
				{
					uint rel = Fetch32();
					Push(Eip);
					Eip += rel;
					return;
				}
				case 0xE9:
				{
					uint rel = Fetch32();
					Eip += rel;
					return;
				}
				case 0xEB:
				{
					uint rel = (uint)(sbyte)Fetch8();
					Eip += rel;
					return;
				}

				case 0xF4:
					throw new X86Exception("HLT reached");
				case 0xF5: CF = !CF; return;
				case 0xF6:
				case 0xF7:
					Group3(op == 0xF6 ? 8 : osz);
					return;
				case 0xF8: CF = false; return;
				case 0xF9: CF = true; return;
				case 0xFA: IF = false; return;
				case 0xFB: IF = true; return;
				case 0xFC: DF = false; return;
				case 0xFD: DF = true; return;
				case 0xFE:
				{
					DecodeModRm();
					bool carry = CF;
					if (modReg == 0)
					{
						WriteRm(8, Add(ReadRm(8), 1, 8));
					}
					else if (modReg == 1)
					{
						WriteRm(8, Sub(ReadRm(8), 1, 8));
					}
					else
					{
						throw Unsupported("FE /" + modReg);
					}

					CF = carry;
					return;
				}
				case 0xFF:
					Group5(osz);
					return;
			}

			throw Unsupported("opcode " + op.ToString("X2"));
		}

		private uint Moffs()
		{
			uint address = addressSize16 ? Fetch16() : Fetch32();
			return segment == 4 ? address + FsBase : address;
		}

		private static uint SignExtend(uint value, int size)
		{
			return size == 8 ? (uint)(sbyte)value : size == 16 ? (uint)(short)value : value;
		}

		private uint ImulFlags(int a, int b, int size)
		{
			long wide = (long)a * b;
			uint result = (uint)wide & Mask(size);
			long truncated = (long)(int)SignExtend(result, size);
			CF = OF = truncated != wide;
			SetResultFlags(result, size);
			return result;
		}

		// --- group 2: shifts and rotates ---

		private uint Shift(int op, uint value, uint count, int size)
		{
			uint mask = Mask(size);
			value &= mask;
			count &= 0x1F;
			if (count == 0)
			{
				return value;
			}

			uint sign = SignBit(size);
			uint result;
			switch (op)
			{
				case 0: // ROL
				{
					int n = (int)(count % (uint)size);
					result = n == 0 ? value : ((value << n) | (value >> (size - n))) & mask;
					CF = (result & 1) != 0;
					OF = ((result & sign) != 0) != CF;
					return result;
				}
				case 1: // ROR
				{
					int n = (int)(count % (uint)size);
					result = n == 0 ? value : ((value >> n) | (value << (size - n))) & mask;
					CF = (result & sign) != 0;
					OF = ((result & sign) != 0) != ((result & (sign >> 1)) != 0);
					return result;
				}
				case 2: // RCL
				{
					result = value;
					for (uint i = 0; i < count % (uint)(size + 1); i++)
					{
						bool top = (result & sign) != 0;
						result = ((result << 1) | (CF ? 1u : 0u)) & mask;
						CF = top;
					}

					OF = ((result & sign) != 0) != CF;
					return result;
				}
				case 3: // RCR
				{
					result = value;
					OF = ((result & sign) != 0) != CF;
					for (uint i = 0; i < count % (uint)(size + 1); i++)
					{
						bool bottom = (result & 1) != 0;
						result = (result >> 1) | (CF ? sign : 0u);
						CF = bottom;
					}

					return result;
				}
				case 4: // SHL
				case 6:
					result = count >= 32 ? 0 : (uint)(((ulong)value << (int)count) & mask);
					CF = count <= (uint)size && ((value >> (size - (int)count)) & 1) != 0;
					OF = ((result & sign) != 0) != CF;
					SetResultFlags(result, size);
					return result;
				case 5: // SHR
					result = value >> (int)count;
					CF = ((value >> ((int)count - 1)) & 1) != 0;
					OF = (value & sign) != 0;
					SetResultFlags(result, size);
					return result;
				default: // SAR
				{
					int signed = (int)SignExtend(value, size);
					result = (uint)(signed >> (int)Math.Min(count, 31u)) & mask;
					CF = ((signed >> (int)Math.Min(count - 1, 31u)) & 1) != 0;
					OF = false;
					SetResultFlags(result, size);
					return result;
				}
			}
		}

		// --- group 3: TEST, NOT, NEG, MUL, IMUL, DIV, IDIV ---

		private void Group3(int size)
		{
			DecodeModRm();
			uint value = ReadRm(size);
			uint mask = Mask(size);
			switch (modReg)
			{
				case 0:
				case 1:
					SetLogicFlags(value & FetchImm(size), size);
					return;
				case 2:
					WriteRm(size, ~value & mask);
					return;
				case 3:
					WriteRm(size, Sub(0, value, size));
					CF = (value & mask) != 0;
					return;
				case 4: // MUL
					if (size == 8)
					{
						uint r = (R[EAX] & 0xFF) * value;
						SetReg(EAX, 16, r);
						CF = OF = (r & 0xFF00) != 0;
					}
					else if (size == 16)
					{
						uint r = (R[EAX] & 0xFFFF) * value;
						SetReg(EAX, 16, r);
						SetReg(EDX, 16, r >> 16);
						CF = OF = (r >> 16) != 0;
					}
					else
					{
						ulong r = (ulong)R[EAX] * value;
						R[EAX] = (uint)r;
						R[EDX] = (uint)(r >> 32);
						CF = OF = R[EDX] != 0;
					}

					return;
				case 5: // IMUL
					if (size == 8)
					{
						int r = (sbyte)R[EAX] * (sbyte)value;
						SetReg(EAX, 16, (uint)r);
						CF = OF = r != (sbyte)r;
					}
					else if (size == 16)
					{
						int r = (short)R[EAX] * (short)value;
						SetReg(EAX, 16, (uint)r);
						SetReg(EDX, 16, (uint)r >> 16);
						CF = OF = r != (short)r;
					}
					else
					{
						long r = (long)(int)R[EAX] * (int)value;
						R[EAX] = (uint)r;
						R[EDX] = (uint)((ulong)r >> 32);
						CF = OF = r != (int)r;
					}

					return;
				case 6: // DIV
				{
					if ((value & mask) == 0)
					{
						throw new X86Exception("integer division by zero");
					}

					if (size == 8)
					{
						uint dividend = R[EAX] & 0xFFFF;
						uint q = dividend / value;
						if (q > 0xFF) throw new X86Exception("integer division overflow");
						SetReg(EAX, 8, q);
						SetReg(4, 8, dividend % value);
					}
					else if (size == 16)
					{
						uint dividend = (R[EAX] & 0xFFFF) | ((R[EDX] & 0xFFFF) << 16);
						uint q = dividend / value;
						if (q > 0xFFFF) throw new X86Exception("integer division overflow");
						SetReg(EAX, 16, q);
						SetReg(EDX, 16, dividend % value);
					}
					else
					{
						ulong dividend = R[EAX] | ((ulong)R[EDX] << 32);
						ulong q = dividend / value;
						if (q > 0xFFFFFFFF) throw new X86Exception("integer division overflow");
						R[EAX] = (uint)q;
						R[EDX] = (uint)(dividend % value);
					}

					return;
				}
				default: // IDIV
				{
					if ((value & mask) == 0)
					{
						throw new X86Exception("integer division by zero");
					}

					if (size == 8)
					{
						int dividend = (short)R[EAX];
						int divisor = (sbyte)value;
						int q = dividend / divisor;
						if (q != (sbyte)q) throw new X86Exception("integer division overflow");
						SetReg(EAX, 8, (uint)q);
						SetReg(4, 8, (uint)(dividend % divisor));
					}
					else if (size == 16)
					{
						int dividend = (int)((R[EAX] & 0xFFFF) | ((R[EDX] & 0xFFFF) << 16));
						int divisor = (short)value;
						int q = dividend / divisor;
						if (q != (short)q) throw new X86Exception("integer division overflow");
						SetReg(EAX, 16, (uint)q);
						SetReg(EDX, 16, (uint)(dividend % divisor));
					}
					else
					{
						long dividend = (long)(R[EAX] | ((ulong)R[EDX] << 32));
						int divisor = (int)value;
						if (dividend == long.MinValue && divisor == -1) throw new X86Exception("integer division overflow");
						long q = dividend / divisor;
						if (q != (int)q) throw new X86Exception("integer division overflow");
						R[EAX] = (uint)q;
						R[EDX] = (uint)(dividend % divisor);
					}

					return;
				}
			}
		}

		// --- group 5: INC, DEC, CALL, JMP, PUSH ---

		private void Group5(int osz)
		{
			DecodeModRm();
			switch (modReg)
			{
				case 0:
				{
					bool carry = CF;
					WriteRm(osz, Add(ReadRm(osz), 1, osz));
					CF = carry;
					return;
				}
				case 1:
				{
					bool carry = CF;
					WriteRm(osz, Sub(ReadRm(osz), 1, osz));
					CF = carry;
					return;
				}
				case 2:
				{
					uint target = ReadRm(32);
					Push(Eip);
					Eip = target;
					return;
				}
				case 4:
					Eip = ReadRm(32);
					return;
				case 6:
					Push(ReadRm(osz));
					return;
				default:
					throw Unsupported("FF /" + modReg + " (far call or jump)");
			}
		}

		// --- string instructions ---

		private delegate bool StringStep(int size);

		private void StringOp(StringStep step, int size)
		{
			if (!repz && !repnz)
			{
				step(size);
				return;
			}

			bool compares = step == Cmps || step == Scas;
			while (R[ECX] != 0)
			{
				step(size);
				R[ECX]--;
				if (compares && (repz ? !ZF : ZF))
				{
					break;
				}
			}
		}

		private uint Delta(int size)
		{
			uint n = (uint)(size / 8);
			return DF ? (uint)-(int)n : n;
		}

		private bool Movs(int size)
		{
			uint source = segment == 4 ? R[ESI] + FsBase : R[ESI];
			WriteMem(R[EDI], size, ReadMem(source, size));
			R[ESI] += Delta(size);
			R[EDI] += Delta(size);
			return true;
		}

		private bool Stos(int size)
		{
			WriteMem(R[EDI], size, R[EAX]);
			R[EDI] += Delta(size);
			return true;
		}

		private bool Lods(int size)
		{
			uint source = segment == 4 ? R[ESI] + FsBase : R[ESI];
			SetReg(EAX, size, ReadMem(source, size));
			R[ESI] += Delta(size);
			return true;
		}

		private bool Cmps(int size)
		{
			uint source = segment == 4 ? R[ESI] + FsBase : R[ESI];
			Sub(ReadMem(source, size), ReadMem(R[EDI], size), size);
			R[ESI] += Delta(size);
			R[EDI] += Delta(size);
			return true;
		}

		private bool Scas(int size)
		{
			Sub(GetReg(EAX, size), ReadMem(R[EDI], size), size);
			R[EDI] += Delta(size);
			return true;
		}

		// --- two-byte opcodes ---

		private ulong tscCounter;

		private void StepTwoByte(int osz)
		{
			byte op = Fetch8();
			switch (op)
			{
				case 0x0B:
					throw new X86Exception("UD2 (the plugin signalled an invalid state)");
				case 0x1F: // NOP r/m (multi-byte NOP)
				case 0x18: // PREFETCH
				case 0x0D:
					DecodeModRm();
					return;
				case 0x31: // RDTSC
					tscCounter += 1000 + (ulong)InstructionCount;
					R[EAX] = (uint)tscCounter;
					R[EDX] = (uint)(tscCounter >> 32);
					return;

				// CMOVcc
				case 0x40: case 0x41: case 0x42: case 0x43: case 0x44: case 0x45: case 0x46: case 0x47:
				case 0x48: case 0x49: case 0x4A: case 0x4B: case 0x4C: case 0x4D: case 0x4E: case 0x4F:
				{
					DecodeModRm();
					uint value = ReadRm(osz);
					if (Condition(op & 0xF))
					{
						SetReg(modReg, osz, value);
					}

					return;
				}

				// Jcc rel32
				case 0x80: case 0x81: case 0x82: case 0x83: case 0x84: case 0x85: case 0x86: case 0x87:
				case 0x88: case 0x89: case 0x8A: case 0x8B: case 0x8C: case 0x8D: case 0x8E: case 0x8F:
				{
					uint rel = Fetch32();
					if (Condition(op & 0xF))
					{
						Eip += rel;
					}

					return;
				}

				// SETcc
				case 0x90: case 0x91: case 0x92: case 0x93: case 0x94: case 0x95: case 0x96: case 0x97:
				case 0x98: case 0x99: case 0x9A: case 0x9B: case 0x9C: case 0x9D: case 0x9E: case 0x9F:
					DecodeModRm();
					WriteRm(8, Condition(op & 0xF) ? 1u : 0u);
					return;

				case 0xA0: // PUSH FS
					Push(0x3B);
					return;
				case 0xA1: // POP FS
					Pop();
					return;
				case 0xA2: // CPUID
					Cpuid();
					return;

				case 0xA3: BitOp(0, osz, false); return; // BT r/m, r
				case 0xAB: BitOp(1, osz, false); return; // BTS
				case 0xB3: BitOp(2, osz, false); return; // BTR
				case 0xBB: BitOp(3, osz, false); return; // BTC
				case 0xBA: // BT/BTS/BTR/BTC r/m, imm8
				{
					DecodeModRm();
					if (modReg < 4)
					{
						throw Unsupported("0F BA /" + modReg);
					}

					BitOpDecoded(modReg - 4, osz, Fetch8());
					return;
				}

				case 0xA4: // SHLD imm8
				case 0xA5: // SHLD CL
				case 0xAC: // SHRD imm8
				case 0xAD: // SHRD CL
				{
					DecodeModRm();
					uint count = (op & 1) == 0 ? Fetch8() : R[ECX];
					count &= 0x1F;
					uint dest = ReadRm(osz);
					uint source = GetReg(modReg, osz);
					if (count == 0)
					{
						return;
					}

					uint result;
					if (osz == 16)
					{
						throw Unsupported("16-bit SHLD/SHRD");
					}

					if (op <= 0xA5)
					{
						result = (dest << (int)count) | (source >> (32 - (int)count));
						CF = ((dest >> (32 - (int)count)) & 1) != 0;
					}
					else
					{
						result = (dest >> (int)count) | (source << (32 - (int)count));
						CF = ((dest >> ((int)count - 1)) & 1) != 0;
					}

					OF = ((result ^ dest) & 0x80000000) != 0;
					SetResultFlags(result, 32);
					WriteRm(32, result);
					return;
				}

				case 0xAF: // IMUL r, r/m
				{
					DecodeModRm();
					int a = (int)SignExtend(GetReg(modReg, osz), osz);
					int b = (int)SignExtend(ReadRm(osz), osz);
					SetReg(modReg, osz, ImulFlags(a, b, osz));
					return;
				}

				case 0xB0: // CMPXCHG
				case 0xB1:
				{
					int size = op == 0xB0 ? 8 : osz;
					DecodeModRm();
					uint current = ReadRm(size);
					Sub(GetReg(EAX, size), current, size);
					if (ZF)
					{
						WriteRm(size, GetReg(modReg, size));
					}
					else
					{
						SetReg(EAX, size, current);
					}

					return;
				}

				case 0xB6: // MOVZX r, r/m8
					DecodeModRm();
					SetReg(modReg, osz, ReadRm(8));
					return;
				case 0xB7: // MOVZX r, r/m16
					DecodeModRm();
					SetReg(modReg, osz, ReadRm(16));
					return;
				case 0xBE: // MOVSX r, r/m8
					DecodeModRm();
					SetReg(modReg, osz, (uint)(sbyte)ReadRm(8));
					return;
				case 0xBF: // MOVSX r, r/m16
					DecodeModRm();
					SetReg(modReg, osz, (uint)(short)ReadRm(16));
					return;

				case 0xBC: // BSF
				case 0xBD: // BSR
				{
					DecodeModRm();
					uint value = ReadRm(osz);
					if (value == 0)
					{
						ZF = true;
						return;
					}

					ZF = false;
					int bit = op == 0xBC ? System.Numerics.BitOperations.TrailingZeroCount(value) : 31 - System.Numerics.BitOperations.LeadingZeroCount(value);
					SetReg(modReg, osz, (uint)bit);
					return;
				}

				case 0xC0: // XADD
				case 0xC1:
				{
					int size = op == 0xC0 ? 8 : osz;
					DecodeModRm();
					uint dest = ReadRm(size);
					uint source = GetReg(modReg, size);
					uint sum = Add(dest, source, size);
					SetReg(modReg, size, dest);
					WriteRm(size, sum);
					return;
				}

				case 0xC8: case 0xC9: case 0xCA: case 0xCB: case 0xCC: case 0xCD: case 0xCE: case 0xCF: // BSWAP
				{
					uint v = R[op & 7];
					R[op & 7] = (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);
					return;
				}

				case 0xAE: // group 15: fences, LDMXCSR/STMXCSR, FXSAVE
				{
					DecodeModRm();
					if (ModIsRegister)
					{
						return; // LFENCE / MFENCE / SFENCE
					}

					switch (modReg)
					{
						case 2:
							mxcsr = Memory.Read32(modAddress);
							return;
						case 3:
							Memory.Write32(modAddress, mxcsr);
							return;
						default:
							throw Unsupported("0F AE /" + modReg);
					}
				}
			}

			if (StepSse(op, osz))
			{
				return;
			}

			throw Unsupported("opcode 0F " + op.ToString("X2") + (opSize16 ? " (66)" : string.Empty) + (repz ? " (F3)" : string.Empty) + (repnz ? " (F2)" : string.Empty));
		}

		private void BitOp(int kind, int osz, bool unused)
		{
			DecodeModRm();
			uint bit = GetReg(modReg, osz);
			if (ModIsRegister)
			{
				BitOpDecoded(kind, osz, bit);
				return;
			}

			// With a register bit offset, a memory operand is a bit string: the offset may reach beyond it.
			int offset = (int)bit;
			modAddress = (uint)(modAddress + (offset >> 5) * 4);
			BitOpDecoded(kind, 32, (uint)(offset & 31));
		}

		private void BitOpDecoded(int kind, int osz, uint bit)
		{
			bit &= (uint)(osz - 1);
			uint value = ReadRm(osz);
			CF = ((value >> (int)bit) & 1) != 0;
			switch (kind)
			{
				case 1:
					WriteRm(osz, value | (1u << (int)bit));
					break;
				case 2:
					WriteRm(osz, value & ~(1u << (int)bit));
					break;
				case 3:
					WriteRm(osz, value ^ (1u << (int)bit));
					break;
			}
		}

		/// <summary>
		/// CPUID: a Pentium 4-class processor with SSE and SSE2, and nothing newer, so compiler
		/// runtimes choose the code paths this interpreter implements.
		/// </summary>
		private void Cpuid()
		{
			switch (R[EAX])
			{
				case 0:
					R[EAX] = 1;
					R[EBX] = 0x756E6547; // "GenuineIntel"
					R[EDX] = 0x49656E69;
					R[ECX] = 0x6C65746E;
					break;
				case 1:
					R[EAX] = 0x00000F29; // family 15
					R[EBX] = 0;
					R[ECX] = 0;
					R[EDX] = (1u << 0) | (1u << 4) | (1u << 8) | (1u << 15) | (1u << 23) | (1u << 24) | (1u << 25) | (1u << 26); // FPU TSC CX8 CMOV MMX FXSR SSE SSE2
					break;
				default:
					R[EAX] = R[EBX] = R[ECX] = R[EDX] = 0;
					break;
			}
		}
	}
}
