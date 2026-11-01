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

namespace OpenBve.X86
{
	/// <summary>
	/// C++ exceptions as Visual C++ raises and catches them on x64: table-driven, with the older FuncInfo (FH3) and the
	/// compressed FuncInfo4 (FH4).
	/// </summary>
	/// <remarks>
	/// A throw walks the stack by each function's unwind codes (the exception directory). At a function whose handler is one
	/// of the C++ frame handlers, the frame's state comes from the IP-to-state table, and the first try block around it whose
	/// catch takes the type (by the type descriptors' names, or catch (...)) gets the exception: the object is copied or
	/// referred to as the catch declares, the catch funclet is run with the frame's establisher pointer, and execution
	/// resumes where it says, with the registers as the unwinding left them for that frame. As on x86 here, the destructors
	/// of the objects the throw leaves behind are not run (their memory is lost, as nothing else refers to it).
	/// A frame guarded only by __try (__C_specific_handler) doesn't take a C++ exception and is walked past.
	/// </remarks>
	public sealed partial class Win32Process
	{
		/// <summary>The registers a walk tracks: the integer ones, and XMM6 to XMM15 where a prologue saved them.</summary>
		private sealed class UnwindContext
		{
			internal readonly ulong[] R = new ulong[16];
			internal readonly ulong[] X = new ulong[32];
			internal ulong Rip;
		}

		/// <summary>Enters the catch block for a throw in a 64-bit plugin (Cpu64.ThrowDispatcher): false if no frame of this call takes it.</summary>
		private bool DispatchThrow64(GuestThrow thrown)
		{
			Cpu64 cpu = Cpu.Long;
			UnwindContext context = new UnwindContext();
			System.Array.Copy(cpu.R, context.R, 16);
			System.Array.Copy(cpu.X, context.X, 32);
			// The host function was called: its return address is on the stack.
			context.Rip = Memory.Read64((uint)context.R[Cpu64.RSP]);
			context.R[Cpu64.RSP] += 8;
			ulong floor = cpu.CallFloor;
			for (int depth = 0; depth < 512 && context.R[Cpu64.RSP] <= floor; depth++)
			{
				LoadedModule module = ModuleAt((uint)context.Rip);
				if (module == null)
				{
					return false;
				}

				// A return address is just past its call, which may be the last instruction of a try block or function.
				uint controlPc = (uint)context.Rip - 1;
				uint entry = FindFunction(module, controlPc - module.Base);
				if (entry == 0)
				{
					// A leaf: the return address is on top of the stack.
					context.Rip = Memory.Read64((uint)context.R[Cpu64.RSP]);
					context.R[Cpu64.RSP] += 8;
					continue;
				}

				uint functionRva = Memory.Read32(entry);
				uint unwind = Memory.Read32(entry + 8);
				// The handler is in the first of a chain's unwind information.
				ulong establisher = EstablisherFrame(module, unwind, context);
				if (HandlerOf(module, unwind, out string handler, out uint data) && handler != null)
				{
					if (TryCatch64(module, functionRva, controlPc - module.Base, handler, data, establisher, context, thrown))
					{
						return true;
					}
				}

				VirtualUnwind(module, entry, (uint)context.Rip - module.Base, context);
			}

			return false;
		}

		/// <summary>The exception directory's entry for an RVA (it is sorted), following a chained entry to its root; 0 if none.</summary>
		private uint FindFunction(LoadedModule module, uint rva)
		{
			if (module.ExceptionTable == 0)
			{
				return 0;
			}

			int low = 0, high = (int)(module.ExceptionTableSize / 12) - 1;
			while (low <= high)
			{
				int middle = (low + high) / 2;
				uint entry = module.ExceptionTable + 12 * (uint)middle;
				if (rva < Memory.Read32(entry))
				{
					high = middle - 1;
				}
				else if (rva >= Memory.Read32(entry + 4))
				{
					low = middle + 1;
				}
				else
				{
					return entry;
				}
			}

			return 0;
		}

		/// <summary>The stack pointer the function's body works from: its frame register less its offset, or the stack pointer itself.</summary>
		private ulong EstablisherFrame(LoadedModule module, uint unwindRva, UnwindContext context)
		{
			uint info = module.Base + (unwindRva & ~1u);
			uint frame = Memory.Read8(info + 3);
			int register = (int)(frame & 15);
			return register != 0 ? context.R[register] - 16 * (frame >> 4) : context.R[Cpu64.RSP];
		}

		/// <summary>A function's language handler: which C++ frame handler it is (null for any other) and its data's address.</summary>
		private bool HandlerOf(LoadedModule module, uint unwindRva, out string handler, out uint data)
		{
			handler = null;
			data = 0;
			uint info = module.Base + (unwindRva & ~1u);
			byte header = Memory.Read8(info);
			if ((header >> 3 & 3) == 0)
			{
				return false;
			}

			uint codes = Memory.Read8(info + 2);
			uint after = info + 4 + 2 * ((codes + 1) & ~1u);
			uint address = module.Base + Memory.Read32(after);
			data = after + 4;
			// The handler's a thunk in the module (jmp [import]), or a stack cookie check (__GSHandlerCheck_EH4) that goes on to one.
			string name = ImportedFunction(module, address);
			for (uint at = address; name == null && at < address + 128; at++)
			{
				byte op = Memory.Read8(at);
				if (op == 0xE8 || op == 0xE9)
				{
					string target = ImportedFunction(module, at + 5 + Memory.Read32(at + 1));
					if (target != null && target.Contains("__CxxFrameHandler"))
					{
						name = target;
					}
				}
			}

			if (name != null && name.EndsWith("__CxxFrameHandler4", System.StringComparison.Ordinal))
			{
				handler = "4";
			}
			else if (name != null && name.EndsWith("__CxxFrameHandler3", System.StringComparison.Ordinal))
			{
				handler = "3";
			}
			else if (name == null)
			{
				// The C runtime is built into the plugin: FH3, if the data looks like its FuncInfo.
				uint funcInfo = module.Base + Memory.Read32(data);
				uint magic = Memory.IsMapped(funcInfo) ? Memory.Read32(funcInfo) & 0x1FFFFFFF : 0;
				if (magic >= 0x19930520 && magic <= 0x19930522)
				{
					handler = "3";
				}
			}

			return true;
		}

		/// <summary>The host function a thunk in a module jumps to (jmp [import]), or null.</summary>
		private string ImportedFunction(LoadedModule module, uint address)
		{
			if (address < module.Base || address + 6 > module.Base + module.Size || Memory.Read8(address) != 0xFF || Memory.Read8(address + 1) != 0x25)
			{
				return null;
			}

			uint slot = address + 6 + Memory.Read32(address + 2);
			return slot >= module.Base && slot + 8 <= module.Base + module.Size ? Cpu.HostFunctionName(Memory.Read32(slot)) : null;
		}

		/// <summary>Undoes a function's prologue on a context, as far as the instruction reached, and returns to its caller.</summary>
		private void VirtualUnwind(LoadedModule module, uint entry, uint rva, UnwindContext context)
		{
			uint functionRva = Memory.Read32(entry);
			uint unwindRva = Memory.Read32(entry + 8);
			while (true)
			{
				uint info = module.Base + (unwindRva & ~1u);
				byte header = Memory.Read8(info);
				uint count = Memory.Read8(info + 2);
				uint frame = Memory.Read8(info + 3);
				uint inFunction = rva - functionRva;
				for (uint i = 0; i < count; i++)
				{
					uint slot = info + 4 + 2 * i;
					uint codeOffset = Memory.Read8(slot);
					byte operation = Memory.Read8(slot + 1);
					int code = operation & 15, operand = operation >> 4;
					bool applies = codeOffset <= inFunction;
					switch (code)
					{
						case 0: // UWOP_PUSH_NONVOL
							if (applies)
							{
								context.R[operand] = Memory.Read64((uint)context.R[Cpu64.RSP]);
								context.R[Cpu64.RSP] += 8;
							}

							break;
						case 1: // UWOP_ALLOC_LARGE
						{
							uint size;
							if (operand == 0)
							{
								size = (uint)Memory.Read16(slot + 2) * 8;
								i += 1;
							}
							else
							{
								size = Memory.Read32(slot + 2);
								i += 2;
							}

							if (applies) context.R[Cpu64.RSP] += size;
							break;
						}
						case 2: // UWOP_ALLOC_SMALL
							if (applies) context.R[Cpu64.RSP] += (uint)operand * 8 + 8;
							break;
						case 3: // UWOP_SET_FPREG
							if (applies) context.R[Cpu64.RSP] = context.R[frame & 15] - 16 * (frame >> 4);
							break;
						case 4: // UWOP_SAVE_NONVOL
							if (applies) context.R[operand] = Memory.Read64((uint)context.R[Cpu64.RSP] + (uint)Memory.Read16(slot + 2) * 8);
							i += 1;
							break;
						case 5: // UWOP_SAVE_NONVOL_FAR
							if (applies) context.R[operand] = Memory.Read64((uint)context.R[Cpu64.RSP] + Memory.Read32(slot + 2));
							i += 2;
							break;
						case 8: // UWOP_SAVE_XMM128
						{
							uint at = (uint)context.R[Cpu64.RSP] + (uint)Memory.Read16(slot + 2) * 16;
							if (applies)
							{
								context.X[2 * operand] = Memory.Read64(at);
								context.X[2 * operand + 1] = Memory.Read64(at + 8);
							}

							i += 1;
							break;
						}
						case 9: // UWOP_SAVE_XMM128_FAR
						{
							uint at = (uint)context.R[Cpu64.RSP] + Memory.Read32(slot + 2);
							if (applies)
							{
								context.X[2 * operand] = Memory.Read64(at);
								context.X[2 * operand + 1] = Memory.Read64(at + 8);
							}

							i += 2;
							break;
						}
						default:
							throw new X86Exception("the plugin's unwind information has an operation this layer doesn't know (" + code + ")");
					}
				}

				if ((header >> 3 & 4) == 0)
				{
					break;
				}

				// UNW_FLAG_CHAININFO: the rest is in the parent's, which follows the codes.
				uint chain = info + 4 + 2 * ((count + 1) & ~1u);
				functionRva = Memory.Read32(chain);
				unwindRva = Memory.Read32(chain + 8);
			}

			context.Rip = Memory.Read64((uint)context.R[Cpu64.RSP]);
			context.R[Cpu64.RSP] += 8;
		}

		// --- the two kinds of table ---

		private struct CatchEntry
		{
			internal uint Adjectives, TypeRva, CatchObject, Handler;
			internal uint[] Continuations;
		}

		private bool TryCatch64(LoadedModule module, uint functionRva, uint pcRva, string handler, uint data, ulong establisher, UnwindContext context, GuestThrow thrown)
		{
			return handler == "4"
				? TryCatch4(module, functionRva, pcRva, data, establisher, context, thrown)
				: TryCatch3(module, pcRva, data, establisher, context, thrown);
		}

		/// <summary>FH3: FuncInfo { magic, maxState, unwind map, tryBlocks, try map, ipMapEntries, ip map, ... }.</summary>
		private bool TryCatch3(LoadedModule module, uint pcRva, uint data, ulong establisher, UnwindContext context, GuestThrow thrown)
		{
			uint funcInfo = module.Base + Memory.Read32(data);
			uint magic = Memory.Read32(funcInfo) & 0x1FFFFFFF;
			if (magic < 0x19930520 || magic > 0x19930522)
			{
				return false;
			}

			uint tryCount = Memory.Read32(funcInfo + 12), tryMap = module.Base + Memory.Read32(funcInfo + 16);
			uint ipCount = Memory.Read32(funcInfo + 20), ipMap = module.Base + Memory.Read32(funcInfo + 24);
			int state = -1;
			for (uint i = 0; i < ipCount; i++)
			{
				if (Memory.Read32(ipMap + 8 * i) > pcRva)
				{
					break;
				}

				state = (int)Memory.Read32(ipMap + 8 * i + 4);
			}

			for (uint i = 0; i < tryCount; i++)
			{
				uint entry = tryMap + 20 * i;
				int low = (int)Memory.Read32(entry), high = (int)Memory.Read32(entry + 4);
				if (state < low || state > high)
				{
					continue;
				}

				uint catches = Memory.Read32(entry + 12), handlers = module.Base + Memory.Read32(entry + 16);
				for (uint j = 0; j < catches; j++)
				{
					uint h = handlers + 20 * j;
					CatchEntry candidate = new CatchEntry
					{
						Adjectives = Memory.Read32(h),
						TypeRva = Memory.Read32(h + 4),
						CatchObject = Memory.Read32(h + 8),
						Handler = Memory.Read32(h + 12)
					};
					if (EnterCatch(module, candidate, 0, establisher, context, thrown))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>FH4's compressed integers: the low bits of the first byte say how many bytes there are.</summary>
		private uint ReadCompressed(ref uint at)
		{
			int kind = Memory.Read8(at) & 15;
			int[] lengths = { 1, 2, 1, 3, 1, 2, 1, 4, 1, 2, 1, 3, 1, 2, 1, 5 };
			int[] shifts = { 25, 18, 25, 11, 25, 18, 25, 4, 25, 18, 25, 11, 25, 18, 25, 0 };
			at += (uint)lengths[kind];
			return Memory.Read32(at - 4) >> shifts[kind];
		}

		/// <summary>FH4: FuncInfo4, a header byte saying which fields follow.</summary>
		private bool TryCatch4(LoadedModule module, uint functionRva, uint pcRva, uint data, ulong establisher, UnwindContext context, GuestThrow thrown)
		{
			uint at = module.Base + Memory.Read32(data);
			byte header = Memory.Read8(at++);
			bool isCatch = (header & 1) != 0, isSeparated = (header & 2) != 0;
			if ((header & 4) != 0) ReadCompressed(ref at); // BBT flags
			if ((header & 8) != 0) at += 4; // the unwind map (no destructors are run)
			if ((header & 16) == 0)
			{
				return false; // no try blocks
			}

			uint tryMapRva = Memory.Read32(at);
			at += 4;
			uint ipMapRva = Memory.Read32(at);
			at += 4;
			if (isCatch)
			{
				// A catch funclet's frame is its parent's, found through the funclet's own frame pointer slot.
				uint displacement = ReadCompressed(ref at);
				establisher = Memory.Read64((uint)establisher + displacement);
			}

			uint ipAt = module.Base + ipMapRva;
			if (isSeparated)
			{
				// A separated function's map is found by the funclet's start.
				uint separated = ipAt;
				uint count = ReadCompressed(ref separated);
				uint chosen = 0;
				for (uint i = 0; i < count; i++)
				{
					uint start = Memory.Read32(separated), map = Memory.Read32(separated + 4);
					separated += 8;
					if (start == functionRva)
					{
						chosen = map;
						break;
					}
				}

				if (chosen == 0)
				{
					return false;
				}

				ipAt = module.Base + chosen;
			}

			uint entries = ReadCompressed(ref ipAt);
			uint ip = functionRva;
			int state = -1;
			for (uint i = 0; i < entries; i++)
			{
				ip += ReadCompressed(ref ipAt);
				uint encoded = ReadCompressed(ref ipAt);
				if (ip > pcRva)
				{
					break;
				}

				state = (int)encoded - 1; // kept as state + 1, so as not to encode a negative
			}


			uint tryAt = module.Base + tryMapRva;
			uint tryCount = ReadCompressed(ref tryAt);
			for (uint i = 0; i < tryCount; i++)
			{
				int low = (int)ReadCompressed(ref tryAt), high = (int)ReadCompressed(ref tryAt);
				ReadCompressed(ref tryAt); // the catch high state
				uint handlersRva = Memory.Read32(tryAt);
				tryAt += 4;
				if (state < low || state > high)
				{
					continue;
				}

				uint h = module.Base + handlersRva;
				uint count = ReadCompressed(ref h);
				for (uint j = 0; j < count; j++)
				{
					byte flags = Memory.Read8(h++);
					CatchEntry candidate = new CatchEntry();
					if ((flags & 1) != 0) candidate.Adjectives = ReadCompressed(ref h);
					if ((flags & 2) != 0)
					{
						candidate.TypeRva = Memory.Read32(h);
						h += 4;
					}

					if ((flags & 4) != 0) candidate.CatchObject = ReadCompressed(ref h);
					candidate.Handler = Memory.Read32(h);
					h += 4;
					int continuations = (flags >> 4) & 3;
					candidate.Continuations = new uint[continuations];
					for (int k = 0; k < continuations; k++)
					{
						if ((flags & 8) != 0)
						{
							candidate.Continuations[k] = Memory.Read32(h);
							h += 4;
						}
						else
						{
							candidate.Continuations[k] = functionRva + ReadCompressed(ref h);
						}
					}

					if (EnterCatch(module, candidate, functionRva, establisher, context, thrown))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>Runs a catch that takes the exception, and moves execution to where it says to go on.</summary>
		private bool EnterCatch(LoadedModule module, CatchEntry entry, uint functionRva, ulong establisher, UnwindContext context, GuestThrow thrown)
		{
			if (!CatchTakes64(module, entry, thrown, out uint catchable, out uint throwBase))
			{
				return false;
			}

			if (entry.CatchObject != 0 && catchable != 0)
			{
				// CatchableType: properties, type, mdisp, pdisp, vdisp, size, copy constructor.
				uint frame = (uint)establisher + entry.CatchObject;
				uint thrownObject = thrown.Object + Memory.Read32(catchable + 8);
				uint copy = Memory.Read32(catchable + 24);
				if ((entry.Adjectives & 8) != 0)
				{
					Memory.Write64(frame, thrownObject); // by reference
				}
				else if (copy != 0 && (Memory.Read32(catchable) & 1) == 0)
				{
					Cpu.Long.Call(throwBase + copy, frame, thrownObject);
				}
				else
				{
					CopyMemory(frame, thrownObject, Memory.Read32(catchable + 20));
				}
			}

			Log("C++ exception (" + thrown.Message + ") caught by the catch at 0x" + (module.Base + entry.Handler).ToString("X8"));
			Cpu64 cpu = Cpu.Long;
			// The funclet takes the exception record (unused) and the frame's establisher pointer.
			ulong continuation = cpu.Call(module.Base + entry.Handler, 0, establisher);
			if (entry.Continuations != null && entry.Continuations.Length > 0 && continuation < (ulong)entry.Continuations.Length)
			{
				continuation = module.Base + entry.Continuations[continuation];
			}

			System.Array.Copy(context.R, cpu.R, 16);
			System.Array.Copy(context.X, cpu.X, 32);
			cpu.Rip = continuation;
			return true;
		}

		/// <summary>Whether a catch takes a thrown type: catch (...), or a catchable type of the same name.</summary>
		private bool CatchTakes64(LoadedModule module, CatchEntry entry, GuestThrow thrown, out uint catchable, out uint throwBase)
		{
			catchable = 0;
			// A throw's tables count from its own module; the host's own throws (StandardThrow) from nothing.
			throwBase = ModuleAt(thrown.ThrowInfo)?.Base ?? 0;
			if (entry.TypeRva == 0 || (entry.Adjectives & 0x40) != 0)
			{
				return true;
			}

			string name = Memory.ReadCString(module.Base + entry.TypeRva + 16);
			if (string.IsNullOrEmpty(name))
			{
				return true;
			}

			if (thrown.ThrowInfo == 0)
			{
				return false;
			}

			uint types = throwBase + Memory.Read32(thrown.ThrowInfo + 12);
			uint count = Memory.Read32(types);
			for (uint k = 0; k < count && k < 32; k++)
			{
				uint candidate = throwBase + Memory.Read32(types + 4 + 4 * k);
				if (Memory.ReadCString(throwBase + Memory.Read32(candidate + 4) + 16) == name)
				{
					catchable = candidate;
					return true;
				}
			}

			return false;
		}
	}
}
