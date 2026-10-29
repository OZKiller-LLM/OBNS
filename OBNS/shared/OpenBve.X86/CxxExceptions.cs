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

using System.Collections.Generic;

namespace OpenBve.X86
{
	/// <summary>
	/// C++ exceptions as Visual C++ raises and catches them on x86 (the "FH3" scheme): what plugins that parse with
	/// std::stoi and catch its std::invalid_argument need.
	/// </summary>
	/// <remarks>
	/// A throw walks the frames registered at FS:[0]. A C++ function's record is { next, handler, state } at EBP - 12, its
	/// stack pointer saved below it; its handler stub loads the function's FuncInfo into EAX and jumps to
	/// __CxxFrameHandler3, so FuncInfo is read from the stub. The first try block around the frame's state whose catch takes
	/// the exception's type (by the type descriptors' names, or catch (...)) gets it: the exception object is copied or
	/// referred to as the catch declares, the catch block (a funclet using the frame's EBP) is run, and execution resumes
	/// at the address it returns, the stack as the frame keeps it. The destructors of the objects the throw leaves behind
	/// are not run (their memory is lost, as nothing else refers to it).
	/// </remarks>
	public sealed partial class Win32Process
	{
		private readonly Dictionary<string, uint> standardThrowInfo = new Dictionary<string, uint>();
		private uint standardExceptionVftable;

		/// <summary>Enters the catch block for a throw (Cpu.ThrowDispatcher): false if no frame of this call takes it.</summary>
		private bool DispatchThrow(GuestThrow thrown)
		{
			uint floor = Cpu.CallFloor;
			uint node = Memory.Read32(TebAddress);
			for (int depth = 0; depth < 256 && node != 0xFFFFFFFF && node != 0 && node < floor; depth++)
			{
				uint funcInfo = FrameFuncInfo(Memory.Read32(node + 4));
				if (funcInfo != 0 && TryCatch(node, funcInfo, thrown))
				{
					return true;
				}

				node = Memory.Read32(node);
			}

			return false;
		}

		/// <summary>The FuncInfo a frame handler stub loads (mov eax, FuncInfo; jmp __CxxFrameHandler3), or 0.</summary>
		private uint FrameFuncInfo(uint handler)
		{
			if (handler == 0 || !Memory.IsMapped(handler))
			{
				return 0;
			}

			for (uint at = handler; at < handler + 48; at++)
			{
				if (Memory.Read8(at) != 0xB8)
				{
					continue;
				}

				byte next = Memory.Read8(at + 5);
				if (next != 0xE9 && !(next == 0xFF && Memory.Read8(at + 6) == 0x25))
				{
					continue;
				}

				uint funcInfo = Memory.Read32(at + 1);
				uint magic = Memory.IsMapped(funcInfo) ? Memory.Read32(funcInfo) & 0x1FFFFFFF : 0;
				if (magic >= 0x19930520 && magic <= 0x19930522)
				{
					return funcInfo;
				}
			}

			return 0;
		}

		/// <summary>Enters a frame's catch for a throw, if one of its try blocks around the frame's state takes it.</summary>
		private bool TryCatch(uint node, uint funcInfo, GuestThrow thrown)
		{
			int state = (int)Memory.Read32(node + 8);
			uint tryCount = Memory.Read32(funcInfo + 12), tryMap = Memory.Read32(funcInfo + 16);
			for (uint i = 0; i < tryCount; i++)
			{
				uint entry = tryMap + 20 * i;
				int tryLow = (int)Memory.Read32(entry), tryHigh = (int)Memory.Read32(entry + 4);
				if (state < tryLow || state > tryHigh)
				{
					continue;
				}

				uint catchCount = Memory.Read32(entry + 12), handlers = Memory.Read32(entry + 16);
				for (uint j = 0; j < catchCount; j++)
				{
					// HandlerType: adjectives, the type caught (0: catch (...)), where the catch object is, the catch block.
					uint handler = handlers + 16 * j;
					uint adjectives = Memory.Read32(handler), type = Memory.Read32(handler + 4);
					uint catchObject = Memory.Read32(handler + 8), block = Memory.Read32(handler + 12);
					if (!CatchTakes(type, adjectives, thrown.ThrowInfo, out uint catchable))
					{
						continue;
					}

					uint frame = node + 12;
					if (catchObject != 0 && catchable != 0)
					{
						// CatchableType: properties, type, this displacement (mdisp, pdisp, vdisp), size, copy constructor.
						uint thrownObject = thrown.Object + Memory.Read32(catchable + 8);
						if ((adjectives & 8) != 0)
						{
							Memory.Write32(frame + catchObject, thrownObject); // by reference
						}
						else if (Memory.Read32(catchable + 24) != 0 && (Memory.Read32(catchable) & 1) == 0)
						{
							Cpu.R[Cpu.ECX] = frame + catchObject;
							Cpu.Call(Memory.Read32(catchable + 24), thrownObject);
						}
						else
						{
							CopyMemory(frame + catchObject, thrownObject, Memory.Read32(catchable + 20));
						}
					}

					Log("C++ exception (" + thrown.Message + ") caught by the catch at 0x" + block.ToString("X8"));
					Memory.Write32(TebAddress, node); // the frames thrown past unregistered
					Memory.Write32(node + 8, (uint)(tryHigh + 1));
					Cpu.R[Cpu.EBP] = frame;
					uint continuation = (uint)Cpu.Call(block);
					Cpu.R[Cpu.ESP] = Memory.Read32(node - 4);
					Cpu.R[Cpu.EBP] = frame;
					Cpu.Eip = continuation;
					return true;
				}
			}

			return false;
		}

		/// <summary>Whether a catch's type takes a thrown type: catch (...), or a catchable type of the same name.</summary>
		private bool CatchTakes(uint type, uint adjectives, uint throwInfo, out uint catchable)
		{
			catchable = 0;
			if (type == 0 || (adjectives & 0x40) != 0)
			{
				return true;
			}

			string name = Memory.ReadCString(type + 8);
			if (string.IsNullOrEmpty(name))
			{
				return true;
			}

			if (throwInfo == 0)
			{
				return false;
			}

			uint types = Memory.Read32(throwInfo + 12);
			uint count = Memory.Read32(types);
			for (uint k = 0; k < count && k < 32; k++)
			{
				uint candidate = Memory.Read32(types + 4 + 4 * k);
				if (Memory.ReadCString(Memory.Read32(candidate + 4) + 8) == name)
				{
					catchable = candidate;
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Throws one of the standard library's exceptions (std::invalid_argument and the like) as msvcp140 does: an object
		/// laid out as std::exception (vptr, what, whether to free it), with the type and its bases catchable.
		/// </summary>
		private GuestThrow StandardThrow(string type, string what, params string[] bases)
		{
			if (standardExceptionVftable == 0)
			{
				standardExceptionVftable = AllocateStatic(8);
				Memory.Write32(standardExceptionVftable, Cpu.AddHostFunction("std::exception destructor", c => c.R[Cpu.ECX], 4));
				Memory.Write32(standardExceptionVftable + 4, Cpu.AddHostFunction("std::exception::what", c => Memory.Read32(c.R[Cpu.ECX] + 4), 0));
			}

			if (!standardThrowInfo.TryGetValue(type, out uint throwInfo))
			{
				List<string> names = new List<string> { type };
				names.AddRange(bases);
				uint array = AllocateStatic(4 + 4 * (uint)names.Count);
				Memory.Write32(array, (uint)names.Count);
				for (int i = 0; i < names.Count; i++)
				{
					string mangled = ".?AV" + names[i] + "@std@@";
					uint descriptor = AllocateStatic(8 + (uint)mangled.Length + 1);
					Memory.Write32(descriptor, standardExceptionVftable);
					Memory.WriteCString(descriptor + 8, mangled);
					uint catchable = AllocateStatic(28);
					Memory.Write32(catchable + 4, descriptor);
					Memory.Write32(catchable + 12, 0xFFFFFFFF); // pdisp: no virtual base
					Memory.Write32(catchable + 20, 12);
					Memory.Write32(array + 4 + 4 * (uint)i, catchable);
				}

				throwInfo = AllocateStatic(16);
				Memory.Write32(throwInfo + 12, array);
				standardThrowInfo[type] = throwInfo;
			}

			uint thrownObject = HeapAllocate(12);
			Memory.Write32(thrownObject, standardExceptionVftable);
			Memory.Write32(thrownObject + 4, StaticCString(what));
			return new GuestThrow(thrownObject, throwInfo, "std::" + type + ": " + what);
		}
	}
}
