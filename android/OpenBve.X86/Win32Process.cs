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
using System.IO;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>A PE module loaded into the emulated process.</summary>
	public sealed class LoadedModule
	{
		public string Name;
		public string HostPath;
		public string WindowsPath;
		public uint Base;
		public uint Size;
		public uint EntryPoint;
		public readonly Dictionary<string, uint> Exports = new Dictionary<string, uint>(StringComparer.Ordinal);
		public readonly Dictionary<ushort, uint> ExportsByOrdinal = new Dictionary<ushort, uint>();

		/// <summary>The address of an export, or 0.</summary>
		public uint Export(string name)
		{
			return Exports.TryGetValue(name, out uint address) ? address : 0;
		}
	}

	/// <summary>
	/// An emulated 32-bit Windows process: the address space, a PE loader, the heap, a thread
	/// information block, and the Windows and C runtime functions plugins import.
	/// </summary>
	/// <remarks>
	/// The plugin's only view of the outside world is what the functions registered here give it.
	/// Files are confined to <see cref="RootFolder"/>, presented to the plugin as drive C:.
	/// </remarks>
	public sealed partial class Win32Process
	{
		// --- layout ---
		private const uint StackTop = 0x7FF00000;
		private const uint StackSize = 0x00200000;
		private const uint TebAddress = 0x7FFDE000;
		private const uint PebAddress = 0x7FFD8000;
		private const uint HeapBase = 0x20000000;
		private const uint HeapLimit = 0x60000000;
		private const uint MiscBase = 0x10000;

		public readonly Memory Memory = new Memory();
		public readonly Cpu Cpu;

		/// <summary>The host folder presented to the plugin as C:\.</summary>
		public string RootFolder { get; }

		/// <summary>Where diagnostic output (debug prints, unimplemented calls) goes.</summary>
		public Action<string> Log { get; set; } = _ => { };

		/// <summary>Text encoding for the "ANSI" API (file names, strings): the code page the plugin was written for.</summary>
		public Encoding AnsiEncoding { get; set; } = Encoding.Latin1;

		private readonly List<LoadedModule> modules = new List<LoadedModule>();

		public IReadOnlyList<LoadedModule> Modules => modules;

		public Win32Process(string rootFolder)
		{
			RootFolder = Path.GetFullPath(rootFolder);
			Cpu = new Cpu(Memory);

			Memory.Map(StackTop - StackSize, StackSize);
			Cpu.R[Cpu.ESP] = StackTop - 16;

			// Thread information block: the SEH chain head, the stack range, the self pointer and the PEB.
			Memory.Map(TebAddress, 0x2000);
			Memory.Write32(TebAddress + 0x00, 0xFFFFFFFF);
			Memory.Write32(TebAddress + 0x04, StackTop);
			Memory.Write32(TebAddress + 0x08, StackTop - StackSize);
			Memory.Write32(TebAddress + 0x18, TebAddress);
			Memory.Write32(TebAddress + 0x20, 1); // process id
			Memory.Write32(TebAddress + 0x24, 1); // thread id
			Memory.Write32(TebAddress + 0x2C, TebAddress + 0x1000); // TLS array
			Memory.Write32(TebAddress + 0x30, PebAddress);
			Cpu.FsBase = TebAddress;
			Memory.Map(PebAddress, 0x1000);

			Memory.Map(MiscBase, 0x10000);
			miscNext = MiscBase + 0x10;

			RegisterKernel32();
			RegisterMsvcrt();
			RegisterExtras();
		}

		// --- small fixed allocations (data imports, strings the process hands out) ---

		private uint miscNext;

		/// <summary>Allocates memory that is never freed, for process-lifetime data.</summary>
		public uint AllocateStatic(uint size, uint alignment = 16)
		{
			miscNext = (miscNext + alignment - 1) & ~(alignment - 1);
			uint address = miscNext;
			miscNext += size;
			Memory.Map(address, size);
			return address;
		}

		public uint StaticCString(string text)
		{
			byte[] bytes = AnsiEncoding.GetBytes(text);
			uint address = AllocateStatic((uint)bytes.Length + 1, 1);
			Memory.WriteBytes(address, bytes);
			Memory.Write8(address + (uint)bytes.Length, 0);
			return address;
		}

		public uint StaticWString(string text)
		{
			uint address = AllocateStatic((uint)(2 * text.Length + 2), 2);
			Memory.WriteWString(address, text);
			return address;
		}

		// --- heap ---

		/*
		 * A simple allocator: blocks carry an 8-byte header with their size, freed blocks are kept
		 * in lists by rounded size and reused first. Plugins allocate little, mostly at load.
		 */
		private uint heapNext = HeapBase;
		private readonly Dictionary<uint, Stack<uint>> freeBlocks = new Dictionary<uint, Stack<uint>>();

		private static uint RoundSize(uint size)
		{
			return (Math.Max(size, 1u) + 15) & ~15u;
		}

		public uint HeapAllocate(uint size, bool zero = true)
		{
			uint rounded = RoundSize(size);
			uint block;
			if (freeBlocks.TryGetValue(rounded, out Stack<uint> list) && list.Count > 0)
			{
				block = list.Pop();
			}
			else
			{
				block = heapNext;
				if ((ulong)block + rounded + 16 > HeapLimit)
				{
					return 0;
				}

				heapNext += rounded + 16;
				Memory.Map(block, rounded + 16);
			}

			Memory.Write32(block, rounded);
			Memory.Write32(block + 4, 0x48454150); // "HEAP", to catch frees of foreign pointers
			uint address = block + 16;
			if (zero)
			{
				Memory.Fill(address, 0, rounded);
			}

			return address;
		}

		public uint HeapBlockSize(uint address)
		{
			return address == 0 ? 0 : Memory.Read32(address - 16);
		}

		public void HeapFree(uint address)
		{
			if (address == 0)
			{
				return;
			}

			uint block = address - 16;
			if (!Memory.IsMapped(block) || Memory.Read32(block + 4) != 0x48454150)
			{
				Log("free of a pointer the heap did not allocate: 0x" + address.ToString("X8"));
				return;
			}

			uint size = Memory.Read32(block);
			Memory.Write32(block + 4, 0);
			if (!freeBlocks.TryGetValue(size, out Stack<uint> list))
			{
				list = new Stack<uint>();
				freeBlocks[size] = list;
			}

			list.Push(block);
		}

		public uint HeapReallocate(uint address, uint size)
		{
			if (address == 0)
			{
				return HeapAllocate(size);
			}

			uint old = HeapBlockSize(address);
			if (size <= old)
			{
				return address;
			}

			uint fresh = HeapAllocate(size);
			for (uint i = 0; i < old; i += 4)
			{
				Memory.Write32(fresh + i, Memory.Read32(address + i));
			}

			HeapFree(address);
			return fresh;
		}

		// --- paths ---

		/// <summary>The Windows path the plugin sees for a host file.</summary>
		public string ToWindowsPath(string hostPath)
		{
			string full = Path.GetFullPath(hostPath);
			string relative = Path.GetRelativePath(RootFolder, full);
			if (relative.StartsWith("..", StringComparison.Ordinal))
			{
				throw new X86Exception("the plugin is outside the folder it may use: " + hostPath);
			}

			return "C:\\" + relative.Replace('/', '\\');
		}

		/// <summary>
		/// The host path for a Windows path the plugin uses, resolving each component without
		/// regard to case, as Windows would. Returns null for paths outside the root.
		/// </summary>
		public string ToHostPath(string windowsPath)
		{
			if (string.IsNullOrEmpty(windowsPath))
			{
				return null;
			}

			string path = windowsPath.Replace('/', '\\');
			if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
			{
				path = path.Substring(4);
			}

			string relative;
			if (path.Length >= 2 && path[1] == ':')
			{
				if (char.ToUpperInvariant(path[0]) != 'C')
				{
					return null;
				}

				relative = path.Substring(2).TrimStart('\\');
			}
			else if (path.StartsWith("\\", StringComparison.Ordinal))
			{
				relative = path.TrimStart('\\');
			}
			else
			{
				relative = Path.Combine(CurrentDirectoryRelative, path);
			}

			string current = RootFolder;
			foreach (string part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
			{
				if (part == ".")
				{
					continue;
				}

				if (part == "..")
				{
					if (string.Equals(current, RootFolder, StringComparison.Ordinal))
					{
						return null;
					}

					current = Path.GetDirectoryName(current);
					continue;
				}

				string exact = Path.Combine(current, part);
				if (File.Exists(exact) || Directory.Exists(exact))
				{
					current = exact;
					continue;
				}

				string match = null;
				if (Directory.Exists(current))
				{
					foreach (string entry in Directory.EnumerateFileSystemEntries(current))
					{
						if (string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase))
						{
							match = entry;
							break;
						}
					}
				}

				current = match ?? exact;
			}

			return current;
		}

		/// <summary>The current directory relative to the root (the first module's folder, as a plugin host's would be irrelevant).</summary>
		public string CurrentDirectoryRelative { get; set; } = string.Empty;

		// --- modules ---

		/// <summary>Finds a loaded module by file name, ignoring case.</summary>
		public LoadedModule FindModule(string name)
		{
			string file = Path.GetFileName(name.Replace('\\', '/'));
			foreach (LoadedModule module in modules)
			{
				if (string.Equals(module.Name, file, StringComparison.OrdinalIgnoreCase))
				{
					return module;
				}
			}

			return null;
		}

		public LoadedModule ModuleAt(uint address)
		{
			foreach (LoadedModule module in modules)
			{
				if (address >= module.Base && address < module.Base + module.Size)
				{
					return module;
				}
			}

			return null;
		}

		/// <summary>
		/// Loads a DLL: maps it, relocates it if its preferred base is taken, binds its imports,
		/// and runs its entry point. Returns the module, or throws with the reason.
		/// </summary>
		public LoadedModule LoadLibrary(string hostPath)
		{
			LoadedModule existing = modules.Find(m => string.Equals(m.HostPath, hostPath, StringComparison.OrdinalIgnoreCase));
			if (existing != null)
			{
				return existing;
			}

			byte[] image = File.ReadAllBytes(hostPath);
			if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
			{
				throw new X86Exception(Path.GetFileName(hostPath) + " is not a Windows executable");
			}

			int pe = BitConverter.ToInt32(image, 0x3C);
			if (BitConverter.ToUInt32(image, pe) != 0x4550 || BitConverter.ToUInt16(image, pe + 4) != 0x14C)
			{
				throw new X86Exception(Path.GetFileName(hostPath) + " is not a 32-bit x86 DLL");
			}

			int sectionCount = BitConverter.ToUInt16(image, pe + 6);
			int optionalSize = BitConverter.ToUInt16(image, pe + 20);
			int optional = pe + 24;
			uint preferredBase = BitConverter.ToUInt32(image, optional + 28);
			uint imageSize = BitConverter.ToUInt32(image, optional + 56);
			uint headersSize = BitConverter.ToUInt32(image, optional + 60);
			uint entry = BitConverter.ToUInt32(image, optional + 16);
			int directories = optional + 96;

			uint Dir(int index, out uint size)
			{
				size = BitConverter.ToUInt32(image, directories + 8 * index + 4);
				return BitConverter.ToUInt32(image, directories + 8 * index);
			}

			uint baseAddress = ChooseBase(preferredBase, imageSize);
			Memory.Map(baseAddress, imageSize);
			Memory.WriteBytes(baseAddress, image.AsSpan(0, (int)Math.Min(headersSize, (uint)image.Length)));

			int sections = optional + optionalSize;
			for (int i = 0; i < sectionCount; i++)
			{
				int s = sections + 40 * i;
				uint virtualSize = BitConverter.ToUInt32(image, s + 8);
				uint virtualAddress = BitConverter.ToUInt32(image, s + 12);
				uint rawSize = BitConverter.ToUInt32(image, s + 16);
				uint rawPointer = BitConverter.ToUInt32(image, s + 20);
				uint copy = Math.Min(rawSize, virtualSize == 0 ? rawSize : virtualSize);
				if (rawPointer != 0 && copy > 0 && rawPointer + copy <= image.Length)
				{
					Memory.WriteBytes(baseAddress + virtualAddress, image.AsSpan((int)rawPointer, (int)copy));
				}
			}

			LoadedModule module = new LoadedModule
			{
				Name = Path.GetFileName(hostPath),
				HostPath = hostPath,
				WindowsPath = ToWindowsPath(hostPath),
				Base = baseAddress,
				Size = imageSize,
				EntryPoint = entry == 0 ? 0 : baseAddress + entry
			};

			// Relocations, if not at the preferred base.
			uint delta = baseAddress - preferredBase;
			uint relocations = Dir(5, out uint relocationSize);
			if (delta != 0)
			{
				if (relocations == 0)
				{
					throw new X86Exception(module.Name + " cannot be moved from its preferred address and that address is in use");
				}

				uint position = baseAddress + relocations;
				uint end = position + relocationSize;
				while (position < end)
				{
					uint page = Memory.Read32(position);
					uint blockSize = Memory.Read32(position + 4);
					if (blockSize < 8)
					{
						break;
					}

					for (uint o = 8; o < blockSize; o += 2)
					{
						ushort entryValue = Memory.Read16(position + o);
						int type = entryValue >> 12;
						uint target = baseAddress + page + (uint)(entryValue & 0xFFF);
						if (type == 3)
						{
							Memory.Write32(target, Memory.Read32(target) + delta);
						}
						else if (type != 0)
						{
							throw new X86Exception(module.Name + " uses relocation type " + type);
						}
					}

					position += blockSize;
				}
			}

			// Exports.
			uint exports = Dir(0, out _);
			if (exports != 0)
			{
				uint e = baseAddress + exports;
				uint ordinalBase = Memory.Read32(e + 16);
				uint functionCount = Memory.Read32(e + 20);
				uint nameCount = Memory.Read32(e + 24);
				uint functions = baseAddress + Memory.Read32(e + 28);
				uint names = baseAddress + Memory.Read32(e + 32);
				uint ordinals = baseAddress + Memory.Read32(e + 36);
				for (uint i = 0; i < functionCount; i++)
				{
					uint rva = Memory.Read32(functions + 4 * i);
					if (rva != 0)
					{
						module.ExportsByOrdinal[(ushort)(ordinalBase + i)] = baseAddress + rva;
					}
				}

				for (uint i = 0; i < nameCount; i++)
				{
					string name = Memory.ReadCString(baseAddress + Memory.Read32(names + 4 * i));
					ushort index = Memory.Read16(ordinals + 2 * i);
					module.Exports[name] = baseAddress + Memory.Read32(functions + 4u * index);
				}
			}

			modules.Add(module);

			// Imports.
			uint imports = Dir(1, out _);
			if (imports != 0)
			{
				for (uint d = baseAddress + imports; Memory.Read32(d + 12) != 0; d += 20)
				{
					string dll = Memory.ReadCString(baseAddress + Memory.Read32(d + 12));
					uint lookup = Memory.Read32(d);
					uint thunks = baseAddress + Memory.Read32(d + 16);
					uint names = lookup != 0 ? baseAddress + lookup : thunks;
					for (uint i = 0; ; i += 4)
					{
						uint value = Memory.Read32(names + i);
						if (value == 0)
						{
							break;
						}

						string function = (value & 0x80000000) != 0
							? "#" + (value & 0xFFFF)
							: Memory.ReadCString(baseAddress + value + 2);
						Memory.Write32(thunks + i, ResolveImport(dll, function, module));
					}
				}
			}

			Log("loaded " + module.Name + " at 0x" + baseAddress.ToString("X8") + (delta != 0 ? " (relocated)" : string.Empty));

			if (module.EntryPoint != 0)
			{
				uint ok = (uint)Cpu.Call(module.EntryPoint, module.Base, 1, 0); // DLL_PROCESS_ATTACH
				if (ok == 0)
				{
					throw new X86Exception(module.Name + " refused to initialise (DllMain returned FALSE)");
				}
			}

			return module;
		}

		private uint ChooseBase(uint preferred, uint size)
		{
			uint candidate = preferred;
			while (Overlaps(candidate, size) || candidate + size > HeapBase && candidate < HeapLimit || candidate == 0)
			{
				candidate = (candidate + 0x01000000) & 0xFFFF0000;
				if (candidate < 0x01000000 || candidate >= 0x78000000)
				{
					candidate = 0x01000000;
				}
			}

			return candidate;
		}

		private bool Overlaps(uint start, uint size)
		{
			foreach (LoadedModule module in modules)
			{
				if (start < module.Base + module.Size && module.Base < start + size)
				{
					return true;
				}
			}

			return false;
		}

		// --- imports ---

		private readonly Dictionary<string, (HostFunction Function, int ArgBytes)> hostExports =
			new Dictionary<string, (HostFunction, int)>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, uint> hostExportAddresses = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

		private readonly Dictionary<string, Func<uint>> dataImports = new Dictionary<string, Func<uint>>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Registers a host implementation of a DLL export.</summary>
		private void Export(string dll, string name, int argBytes, HostFunction function)
		{
			hostExports[dll + "!" + name] = (function, argBytes);
		}

		/// <summary>The address of a host export, creating its entry on first use; 0 if there is none.</summary>
		public uint HostExport(string dll, string name)
		{
			string key = NormaliseDll(dll) + "!" + name;
			if (hostExportAddresses.TryGetValue(key, out uint address))
			{
				return address;
			}

			if (!hostExports.TryGetValue(key, out (HostFunction Function, int ArgBytes) entry))
			{
				return 0;
			}

			address = Cpu.AddHostFunction(key, entry.Function, entry.ArgBytes);
			hostExportAddresses[key] = address;
			return address;
		}

		private static string NormaliseDll(string dll)
		{
			string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
			// The API-set and debug/versioned runtime names all resolve to the same functions.
			if (name.StartsWith("api-ms-win-crt", StringComparison.Ordinal) || name.StartsWith("msvcr", StringComparison.Ordinal) || name == "ucrtbase" || name.StartsWith("vcruntime", StringComparison.Ordinal))
			{
				return "msvcrt";
			}

			if (name.StartsWith("api-ms-win", StringComparison.Ordinal) || name == "kernelbase")
			{
				return "kernel32";
			}

			return name;
		}

		private uint ResolveImport(string dll, string function, LoadedModule importer)
		{
			string normalised = NormaliseDll(dll);
			if (dataImports.TryGetValue(normalised + "!" + function, out Func<uint> data))
			{
				return data();
			}

			uint address = HostExport(normalised, function);
			if (address != 0)
			{
				return address;
			}

			// Another plugin DLL in the same folder, loaded on demand.
			if (normalised != "kernel32" && normalised != "msvcrt" && normalised != "user32")
			{
				string sibling = ToHostPath(Path.GetDirectoryName(importer.WindowsPath) + "\\" + dll);
				if (sibling != null && File.Exists(sibling))
				{
					LoadedModule other = LoadLibrary(sibling);
					uint target = function.StartsWith("#", StringComparison.Ordinal)
						? (other.ExportsByOrdinal.TryGetValue(ushort.Parse(function.Substring(1)), out uint byOrdinal) ? byOrdinal : 0)
						: other.Export(function);
					if (target != 0)
					{
						return target;
					}
				}
			}

			// Unknown: bind to a stub that reports the call, so a plugin that never calls it still runs.
			string key = dll + "!" + function;
			Log("import not provided: " + key + " (needed by " + importer.Name + ")");
			return Cpu.AddHostFunction(key, cpu => throw new X86Exception("the plugin called " + key + ", which the Android compatibility layer does not provide"), 0);
		}
	}
}
