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
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OpenBve.X86
{
	/// <summary>The kernel32 functions BVE plugins and their C runtimes use.</summary>
	/// <remarks>
	/// The emulated process has one thread, so synchronisation objects always succeed at once, and
	/// thread-local storage is a single table. Files are the only real resource, and they are
	/// confined to the root folder.
	/// </remarks>
	public sealed partial class Win32Process
	{
		private const uint ErrorFileNotFound = 2;
		private const uint InvalidHandle = 0xFFFFFFFF;

		private uint lastError;
		private readonly uint[] tlsSlots = new uint[1088];
		private readonly bool[] tlsUsed = new bool[1088];
		private readonly Dictionary<ushort, string> atoms = new Dictionary<ushort, string>();
		private ushort nextAtom = 0xC000;
		private readonly Dictionary<uint, object> handles = new Dictionary<uint, object>();
		private uint nextHandle = 0x100;
		private readonly Stopwatch clock = Stopwatch.StartNew();
		private uint commandLineA, commandLineW, environmentA, environmentW;

		private uint NewHandle(object value)
		{
			uint handle = nextHandle;
			nextHandle += 4;
			handles[handle] = value;
			return handle;
		}

		private const uint ProcessHeapHandle = 0x00050000;
		private const uint StdInput = 0xFFFFFFF6, StdOutput = 0xFFFFFFF5, StdError = 0xFFFFFFF4;

		private sealed class FindState
		{
			internal string[] Entries;
			internal int Next;
		}

		private void RegisterKernel32()
		{
			const string k = "kernel32";

			// --- errors ---
			Export(k, "GetLastError", 0, c => lastError);
			Export(k, "SetLastError", 4, c => { lastError = c.Arg(0); return 0; });

			// --- modules ---
			Export(k, "GetModuleHandleA", 4, c => ModuleHandle(Memory.ReadCString(c.Arg(0), AnsiEncoding)));
			Export(k, "GetModuleHandleW", 4, c => ModuleHandle(Memory.ReadWString(c.Arg(0))));
			Export(k, "GetModuleHandleExW", 12, c =>
			{
				uint flags = c.Arg(0);
				uint handle = (flags & 4) != 0 ? ModuleAt(c.Arg(1))?.Base ?? 0 : ModuleHandle(Memory.ReadWString(c.Arg(1)));
				if (c.Arg(2) != 0) Memory.Write32(c.Arg(2), handle);
				return handle != 0 ? 1u : 0u;
			});
			Export(k, "GetModuleFileNameA", 12, c => ModuleFileName(c.Arg(0), c.Arg(1), c.Arg(2), false));
			Export(k, "GetModuleFileNameW", 12, c => ModuleFileName(c.Arg(0), c.Arg(1), c.Arg(2), true));
			Export(k, "LoadLibraryA", 4, c => LoadLibraryFromPlugin(Memory.ReadCString(c.Arg(0), AnsiEncoding)));
			Export(k, "LoadLibraryW", 4, c => LoadLibraryFromPlugin(Memory.ReadWString(c.Arg(0))));
			Export(k, "LoadLibraryExA", 12, c => LoadLibraryFromPlugin(Memory.ReadCString(c.Arg(0), AnsiEncoding)));
			Export(k, "LoadLibraryExW", 12, c => LoadLibraryFromPlugin(Memory.ReadWString(c.Arg(0))));
			Export(k, "FreeLibrary", 4, c => 1);
			Export(k, "GetProcAddress", 8, c => GetProcAddress(c.Arg(0), c.Arg(1)));

			// --- process and thread ---
			Export(k, "GetCurrentProcess", 0, c => 0xFFFFFFFF);
			Export(k, "GetCurrentThread", 0, c => 0xFFFFFFFE);
			Export(k, "GetCurrentProcessId", 0, c => 1);
			Export(k, "GetCurrentThreadId", 0, c => 1);
			Export(k, "ExitProcess", 4, c => throw new X86Exception("the plugin tried to end the process (ExitProcess " + c.Arg(0) + ")"));
			Export(k, "TerminateProcess", 8, c => throw new X86Exception("the plugin tried to end the process (TerminateProcess)"));
			Export(k, "IsDebuggerPresent", 0, c => 0);
			Export(k, "SetUnhandledExceptionFilter", 4, c => 0);
			Export(k, "UnhandledExceptionFilter", 4, c => 0);
			Export(k, "RaiseException", 16, c => throw new X86Exception("the plugin raised a Windows exception 0x" + c.Arg(0).ToString("X8")));
			Export(k, "RtlUnwind", 16, c => 0);
			Export(k, "IsProcessorFeaturePresent", 4, c => c.Arg(0) == 6 || c.Arg(0) == 10 ? 1u : 0u); // SSE, SSE2
			Export(k, "GetCommandLineA", 0, c => commandLineA != 0 ? commandLineA : commandLineA = StaticCString("\"C:\\OpenBve.exe\""));
			Export(k, "GetCommandLineW", 0, c => commandLineW != 0 ? commandLineW : commandLineW = StaticWString("\"C:\\OpenBve.exe\""));
			Export(k, "GetStartupInfoA", 4, c => { Memory.Fill(c.Arg(0), 0, 68); Memory.Write32(c.Arg(0), 68); return 0; });
			Export(k, "GetStartupInfoW", 4, c => { Memory.Fill(c.Arg(0), 0, 68); Memory.Write32(c.Arg(0), 68); return 0; });
			Export(k, "GetVersion", 0, c => 0x1DB10106); // 6.1 (Windows 7), build 7601
			Export(k, "GetVersionExA", 4, c => VersionInfo(c.Arg(0), false));
			Export(k, "GetVersionExW", 4, c => VersionInfo(c.Arg(0), true));
			Export(k, "Sleep", 4, c => 0);
			Export(k, "SleepEx", 8, c => 0);

			// --- environment ---
			Export(k, "GetEnvironmentStrings", 0, c => environmentA != 0 ? environmentA : environmentA = StaticCString("\0"));
			Export(k, "GetEnvironmentStringsA", 0, c => environmentA != 0 ? environmentA : environmentA = StaticCString("\0"));
			Export(k, "GetEnvironmentStringsW", 0, c => environmentW != 0 ? environmentW : environmentW = StaticWString("\0"));
			Export(k, "FreeEnvironmentStringsA", 4, c => 1);
			Export(k, "FreeEnvironmentStringsW", 4, c => 1);
			Export(k, "GetEnvironmentVariableA", 12, c => { lastError = 203; return 0; });
			Export(k, "GetEnvironmentVariableW", 12, c => { lastError = 203; return 0; });
			Export(k, "SetEnvironmentVariableA", 8, c => 1);
			Export(k, "SetEnvironmentVariableW", 8, c => 1);

			// --- time ---
			Export(k, "GetTickCount", 0, c => (uint)clock.ElapsedMilliseconds);
			Export(k, "GetTickCount64", 0, c => (ulong)clock.ElapsedMilliseconds);
			Export(k, "QueryPerformanceCounter", 4, c => { Memory.Write64(c.Arg(0), (ulong)clock.ElapsedTicks); return 1; });
			Export(k, "QueryPerformanceFrequency", 4, c => { Memory.Write64(c.Arg(0), (ulong)Stopwatch.Frequency); return 1; });
			Export(k, "GetSystemTimeAsFileTime", 4, c => { Memory.Write64(c.Arg(0), (ulong)DateTime.UtcNow.ToFileTimeUtc()); return 0; });
			Export(k, "GetSystemTime", 4, c => { WriteSystemTime(c.Arg(0), DateTime.UtcNow); return 0; });
			Export(k, "GetLocalTime", 4, c => { WriteSystemTime(c.Arg(0), DateTime.Now); return 0; });
			Export(k, "FileTimeToLocalFileTime", 8, c =>
			{
				long utc = (long)Memory.Read64(c.Arg(0));
				Memory.Write64(c.Arg(1), (ulong)DateTime.FromFileTimeUtc(utc).ToLocalTime().Ticks - (ulong)new DateTime(1601, 1, 1).Ticks);
				return 1;
			});
			Export(k, "FileTimeToSystemTime", 8, c =>
			{
				WriteSystemTime(c.Arg(1), new DateTime(1601, 1, 1).AddTicks((long)Memory.Read64(c.Arg(0))));
				return 1;
			});
			Export(k, "GetTimeZoneInformation", 4, c => { Memory.Fill(c.Arg(0), 0, 172); return 0; });

			// --- synchronisation: one thread, so everything succeeds immediately ---
			Export(k, "InitializeCriticalSection", 4, c => { Memory.Fill(c.Arg(0), 0, 24); return 0; });
			Export(k, "InitializeCriticalSectionAndSpinCount", 8, c => { Memory.Fill(c.Arg(0), 0, 24); return 1; });
			Export(k, "InitializeCriticalSectionEx", 12, c => { Memory.Fill(c.Arg(0), 0, 24); return 1; });
			Export(k, "EnterCriticalSection", 4, c => 0);
			Export(k, "LeaveCriticalSection", 4, c => 0);
			Export(k, "TryEnterCriticalSection", 4, c => 1);
			Export(k, "DeleteCriticalSection", 4, c => 0);
			Export(k, "CreateSemaphoreA", 16, c => NewHandle("semaphore"));
			Export(k, "CreateSemaphoreW", 16, c => NewHandle("semaphore"));
			Export(k, "ReleaseSemaphore", 12, c => 1);
			Export(k, "CreateMutexA", 12, c => NewHandle("mutex"));
			Export(k, "CreateMutexW", 12, c => NewHandle("mutex"));
			Export(k, "ReleaseMutex", 4, c => 1);
			Export(k, "CreateEventA", 16, c => NewHandle("event"));
			Export(k, "CreateEventW", 16, c => NewHandle("event"));
			Export(k, "SetEvent", 4, c => 1);
			Export(k, "ResetEvent", 4, c => 1);
			Export(k, "WaitForSingleObject", 8, c => 0);
			Export(k, "InterlockedIncrement", 4, c => { uint v = Memory.Read32(c.Arg(0)) + 1; Memory.Write32(c.Arg(0), v); return v; });
			Export(k, "InterlockedDecrement", 4, c => { uint v = Memory.Read32(c.Arg(0)) - 1; Memory.Write32(c.Arg(0), v); return v; });
			Export(k, "InterlockedExchange", 8, c => { uint v = Memory.Read32(c.Arg(0)); Memory.Write32(c.Arg(0), c.Arg(1)); return v; });
			Export(k, "InterlockedCompareExchange", 12, c =>
			{
				uint v = Memory.Read32(c.Arg(0));
				if (v == c.Arg(2)) Memory.Write32(c.Arg(0), c.Arg(1));
				return v;
			});
			Export(k, "InitializeSListHead", 4, c => { Memory.Write64(c.Arg(0), 0); return 0; });
			Export(k, "InterlockedFlushSList", 4, c => { uint v = Memory.Read32(c.Arg(0)); Memory.Write64(c.Arg(0), 0); return v; });
			Export(k, "EncodePointer", 4, c => c.Arg(0));
			Export(k, "DecodePointer", 4, c => c.Arg(0));

			// --- thread-local storage ---
			Export(k, "TlsAlloc", 0, c =>
			{
				for (uint i = 0; i < tlsUsed.Length; i++)
				{
					if (!tlsUsed[i])
					{
						tlsUsed[i] = true;
						tlsSlots[i] = 0;
						return i;
					}
				}

				return 0xFFFFFFFF;
			});
			Export(k, "TlsFree", 4, c => { if (c.Arg(0) < tlsUsed.Length) tlsUsed[c.Arg(0)] = false; return 1; });
			Export(k, "TlsGetValue", 4, c => { lastError = 0; return c.Arg(0) < tlsSlots.Length ? tlsSlots[c.Arg(0)] : 0u; });
			Export(k, "TlsSetValue", 8, c => { if (c.Arg(0) < tlsSlots.Length) tlsSlots[c.Arg(0)] = c.Arg(1); return 1; });
			Export(k, "FlsAlloc", 4, c => HostExportCall("kernel32", "TlsAlloc"));
			Export(k, "FlsGetValue", 4, c => c.Arg(0) < tlsSlots.Length ? tlsSlots[c.Arg(0)] : 0u);
			Export(k, "FlsSetValue", 8, c => { if (c.Arg(0) < tlsSlots.Length) tlsSlots[c.Arg(0)] = c.Arg(1); return 1; });
			Export(k, "FlsFree", 4, c => 1);

			// --- atoms: plugins sharing panel and sound memory with DetailManager use these ---
			Export(k, "AddAtomA", 4, c => AddAtom(AtomName(c.Arg(0), false)));
			Export(k, "AddAtomW", 4, c => AddAtom(AtomName(c.Arg(0), true)));
			Export(k, "GlobalAddAtomA", 4, c => AddAtom(AtomName(c.Arg(0), false)));
			Export(k, "FindAtomA", 4, c => FindAtom(AtomName(c.Arg(0), false)));
			Export(k, "FindAtomW", 4, c => FindAtom(AtomName(c.Arg(0), true)));
			Export(k, "GlobalFindAtomA", 4, c => FindAtom(AtomName(c.Arg(0), false)));
			Export(k, "GetAtomNameA", 12, c => GetAtomName((ushort)c.Arg(0), c.Arg(1), c.Arg(2), false));
			Export(k, "GetAtomNameW", 12, c => GetAtomName((ushort)c.Arg(0), c.Arg(1), c.Arg(2), true));
			Export(k, "GlobalGetAtomNameA", 12, c => GetAtomName((ushort)c.Arg(0), c.Arg(1), c.Arg(2), false));
			Export(k, "DeleteAtom", 4, c => 0);
			Export(k, "GlobalDeleteAtom", 4, c => 0);

			// --- memory ---
			Export(k, "GetProcessHeap", 0, c => ProcessHeapHandle);
			Export(k, "HeapCreate", 12, c => ProcessHeapHandle);
			Export(k, "HeapDestroy", 4, c => 1);
			Export(k, "HeapAlloc", 12, c => HeapAllocate(c.Arg(2), (c.Arg(1) & 8) != 0 || true));
			Export(k, "HeapFree", 12, c => { HeapFree(c.Arg(2)); return 1; });
			Export(k, "HeapReAlloc", 16, c => HeapReallocate(c.Arg(2), c.Arg(3)));
			Export(k, "HeapSize", 12, c => HeapBlockSize(c.Arg(2)));
			Export(k, "HeapValidate", 12, c => 1);
			Export(k, "HeapSetInformation", 16, c => 1);
			Export(k, "LocalAlloc", 8, c => HeapAllocate(c.Arg(1)));
			Export(k, "LocalFree", 4, c => { HeapFree(c.Arg(0)); return 0; });
			Export(k, "GlobalAlloc", 8, c => HeapAllocate(c.Arg(1)));
			Export(k, "GlobalFree", 4, c => { HeapFree(c.Arg(0)); return 0; });
			Export(k, "GlobalLock", 4, c => c.Arg(0));
			Export(k, "GlobalUnlock", 4, c => 1);
			Export(k, "VirtualAlloc", 16, c => VirtualAllocate(c.Arg(0), c.Arg(1)));
			Export(k, "VirtualFree", 12, c => 1);
			Export(k, "VirtualProtect", 16, c => { if (c.Arg(3) != 0) Memory.Write32(c.Arg(3), 4); return 1; });
			Export(k, "VirtualQuery", 12, c => 0);

			// --- code pages and strings ---
			Export(k, "GetACP", 0, c => (uint)AnsiEncoding.CodePage);
			Export(k, "GetOEMCP", 0, c => 437);
			Export(k, "GetConsoleCP", 0, c => 437);
			Export(k, "GetConsoleOutputCP", 0, c => 437);
			Export(k, "IsValidCodePage", 4, c => 1);
			Export(k, "GetCPInfo", 8, c =>
			{
				Memory.Fill(c.Arg(1), 0, 20);
				Memory.Write32(c.Arg(1), AnsiEncoding.IsSingleByte ? 1u : 2u);
				Memory.Write8(c.Arg(1) + 4, (byte)'?');
				return 1;
			});
			Export(k, "MultiByteToWideChar", 24, c => MultiByteToWideChar(c.Arg(0), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5)));
			Export(k, "WideCharToMultiByte", 32, c => WideCharToMultiByte(c.Arg(0), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5)));
			Export(k, "GetStringTypeA", 20, c => StringType(c.Arg(2), (int)c.Arg(3), c.Arg(4), false));
			Export(k, "GetStringTypeW", 16, c => StringType(c.Arg(1), (int)c.Arg(2), c.Arg(3), true));
			Export(k, "GetStringTypeExA", 20, c => StringType(c.Arg(2), (int)c.Arg(3), c.Arg(4), false));
			Export(k, "LCMapStringA", 24, c => MapString(c.Arg(1), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5), false));
			Export(k, "LCMapStringW", 24, c => MapString(c.Arg(1), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5), true));
			Export(k, "LCMapStringEx", 36, c => MapString(c.Arg(1), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5), true));
			Export(k, "CompareStringA", 24, c => CompareStrings(c.Arg(1), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5), false));
			Export(k, "CompareStringW", 24, c => CompareStrings(c.Arg(1), c.Arg(2), (int)c.Arg(3), c.Arg(4), (int)c.Arg(5), true));
			// GetLocaleInfoA/W and EnumSystemLocalesA/W: see Win32Extras (a small table of locales).
			Export(k, "GetUserDefaultLCID", 0, c => 0x0409);
			Export(k, "GetUserDefaultLangID", 0, c => 0x0409);
			Export(k, "IsValidLocale", 8, c => 1);
			Export(k, "lstrlenA", 4, c => (uint)(Memory.ReadCString(c.Arg(0), AnsiEncoding)?.Length ?? 0));
			Export(k, "lstrlenW", 4, c => (uint)(Memory.ReadWString(c.Arg(0))?.Length ?? 0));
			Export(k, "lstrcpyA", 8, c => { Memory.WriteCString(c.Arg(0), Memory.ReadCString(c.Arg(1), AnsiEncoding), AnsiEncoding); return c.Arg(0); });
			Export(k, "lstrcmpiA", 8, c => (uint)string.Compare(Memory.ReadCString(c.Arg(0), AnsiEncoding), Memory.ReadCString(c.Arg(1), AnsiEncoding), StringComparison.OrdinalIgnoreCase));

			// --- files ---
			Export(k, "CreateFileA", 28, c => CreateFile(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(1), c.Arg(4)));
			Export(k, "CreateFileW", 28, c => CreateFile(Memory.ReadWString(c.Arg(0)), c.Arg(1), c.Arg(4)));
			Export(k, "ReadFile", 20, c => ReadFile(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
			Export(k, "WriteFile", 20, c => WriteFile(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
			Export(k, "CloseHandle", 4, c =>
			{
				if (handles.TryGetValue(c.Arg(0), out object value))
				{
					(value as IDisposable)?.Dispose();
					handles.Remove(c.Arg(0));
				}

				return 1;
			});
			Export(k, "SetFilePointer", 16, c => SetFilePointer(c.Arg(0), (int)c.Arg(1), c.Arg(2), c.Arg(3)));
			Export(k, "SetFilePointerEx", 20, c =>
			{
				if (!(handles.TryGetValue(c.Arg(0), out object value) && value is FileStream stream)) return 0;
				long distance = (long)(c.Arg(1) | ((ulong)c.Arg(2) << 32));
				long position = stream.Seek(distance, (SeekOrigin)c.Arg(4));
				if (c.Arg(3) != 0) Memory.Write64(c.Arg(3), (ulong)position);
				return 1;
			});
			Export(k, "GetFileSize", 8, c =>
			{
				if (!(handles.TryGetValue(c.Arg(0), out object value) && value is FileStream stream)) return InvalidHandle;
				if (c.Arg(1) != 0) Memory.Write32(c.Arg(1), (uint)(stream.Length >> 32));
				return (uint)stream.Length;
			});
			Export(k, "GetFileSizeEx", 8, c =>
			{
				if (!(handles.TryGetValue(c.Arg(0), out object value) && value is FileStream stream)) return 0;
				Memory.Write64(c.Arg(1), (ulong)stream.Length);
				return 1;
			});
			Export(k, "SetEndOfFile", 4, c =>
			{
				if (handles.TryGetValue(c.Arg(0), out object value) && value is FileStream stream && stream.CanWrite) stream.SetLength(stream.Position);
				return 1;
			});
			Export(k, "FlushFileBuffers", 4, c => 1);
			Export(k, "GetFileType", 4, c => c.Arg(0) >= StdError ? 2u : handles.ContainsKey(c.Arg(0)) ? 1u : 0u);
			Export(k, "GetStdHandle", 4, c => c.Arg(0));
			Export(k, "SetStdHandle", 8, c => 1);
			Export(k, "SetHandleCount", 4, c => c.Arg(0));
			Export(k, "GetConsoleMode", 8, c => 0);
			Export(k, "WriteConsoleA", 20, c => ConsoleWrite(c.Arg(1), c.Arg(2), c.Arg(3), false));
			Export(k, "WriteConsoleW", 20, c => ConsoleWrite(c.Arg(1), c.Arg(2), c.Arg(3), true));
			Export(k, "GetFileAttributesA", 4, c => FileAttributes(Memory.ReadCString(c.Arg(0), AnsiEncoding)));
			Export(k, "GetFileAttributesW", 4, c => FileAttributes(Memory.ReadWString(c.Arg(0))));
			Export(k, "FindFirstFileA", 8, c => FindFirst(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(1), false));
			Export(k, "FindFirstFileW", 8, c => FindFirst(Memory.ReadWString(c.Arg(0)), c.Arg(1), true));
			Export(k, "FindFirstFileExA", 24, c => FindFirst(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(2), false));
			Export(k, "FindFirstFileExW", 24, c => FindFirst(Memory.ReadWString(c.Arg(0)), c.Arg(2), true));
			Export(k, "FindNextFileA", 8, c => FindNext(c.Arg(0), c.Arg(1), false));
			Export(k, "FindNextFileW", 8, c => FindNext(c.Arg(0), c.Arg(1), true));
			Export(k, "FindClose", 4, c => { handles.Remove(c.Arg(0)); return 1; });
			Export(k, "GetFullPathNameA", 16, c => FullPathName(Memory.ReadCString(c.Arg(0), AnsiEncoding), c.Arg(1), c.Arg(2), c.Arg(3), false));
			Export(k, "GetFullPathNameW", 16, c => FullPathName(Memory.ReadWString(c.Arg(0)), c.Arg(1), c.Arg(2), c.Arg(3), true));
			Export(k, "GetCurrentDirectoryA", 8, c => WriteString(c.Arg(1), c.Arg(0), "C:\\" + CurrentDirectoryRelative, false));
			Export(k, "GetCurrentDirectoryW", 8, c => WriteString(c.Arg(1), c.Arg(0), "C:\\" + CurrentDirectoryRelative, true));
			Export(k, "SetCurrentDirectoryA", 4, c => 1);
			Export(k, "GetDriveTypeA", 4, c => 3); // fixed disk
			Export(k, "GetDriveTypeW", 4, c => 3);
			Export(k, "GetPrivateProfileStringA", 24, c => PrivateProfileString(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
			Export(k, "GetPrivateProfileIntA", 16, c => PrivateProfileInt(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
			Export(k, "OutputDebugStringA", 4, c => { Log("plugin: " + Memory.ReadCString(c.Arg(0), AnsiEncoding)); return 0; });
			Export(k, "OutputDebugStringW", 4, c => { Log("plugin: " + Memory.ReadWString(c.Arg(0))); return 0; });

			// --- user32 odds and ends plugins call for diagnostics ---
			Export("user32", "MessageBoxA", 16, c =>
			{
				Log("plugin message box: " + Memory.ReadCString(c.Arg(2), AnsiEncoding) + ": " + Memory.ReadCString(c.Arg(1), AnsiEncoding));
				return 1;
			});
			Export("user32", "MessageBoxW", 16, c =>
			{
				Log("plugin message box: " + Memory.ReadWString(c.Arg(2)) + ": " + Memory.ReadWString(c.Arg(1)));
				return 1;
			});
		}

		private ulong HostExportCall(string dll, string name)
		{
			return hostExports[dll + "!" + name].Function(Cpu);
		}

		// --- module helpers ---

		private uint ModuleHandle(string name)
		{
			if (name == null)
			{
				// The executable: plugins only compare it or pass it back, so the first module stands in.
				return modules.Count > 0 ? modules[0].Base : 0x00400000;
			}

			LoadedModule module = FindModule(name);
			if (module != null)
			{
				return module.Base;
			}

			string dll = NormaliseDll(name);
			return dll == "kernel32" || dll == "msvcrt" || dll == "user32" || dll == "ntdll" ? FakeSystemModule(dll) : 0;
		}

		private readonly Dictionary<string, uint> fakeModules = new Dictionary<string, uint>();

		private uint FakeSystemModule(string dll)
		{
			if (!fakeModules.TryGetValue(dll, out uint handle))
			{
				handle = 0x7C000000 + (uint)fakeModules.Count * 0x100000;
				fakeModules[dll] = handle;
			}

			return handle;
		}

		private uint GetProcAddress(uint module, uint nameOrOrdinal)
		{
			string name = nameOrOrdinal < 0x10000 ? "#" + nameOrOrdinal : Memory.ReadCString(nameOrOrdinal);
			foreach (KeyValuePair<string, uint> fake in fakeModules)
			{
				if (fake.Value == module)
				{
					uint address = HostExport(fake.Key, name);
					if (address == 0)
					{
						lastError = 127;
					}

					return address;
				}
			}

			LoadedModule loaded = ModuleAt(module);
			if (loaded == null)
			{
				lastError = 126;
				return 0;
			}

			uint result = name.StartsWith("#", StringComparison.Ordinal)
				? (loaded.ExportsByOrdinal.TryGetValue(ushort.Parse(name.Substring(1)), out uint byOrdinal) ? byOrdinal : 0)
				: loaded.Export(name);
			if (result == 0)
			{
				lastError = 127;
			}

			return result;
		}

		private uint LoadLibraryFromPlugin(string name)
		{
			string dll = NormaliseDll(name);
			if (dll == "kernel32" || dll == "msvcrt" || dll == "user32" || dll == "ntdll")
			{
				return FakeSystemModule(dll);
			}

			LoadedModule existing = FindModule(name);
			if (existing != null)
			{
				return existing.Base;
			}

			string host = ToHostPath(name);
			if (host == null || !File.Exists(host))
			{
				// A bare name: look beside the modules already loaded.
				foreach (LoadedModule module in modules)
				{
					string candidate = ToHostPath(Path.GetDirectoryName(module.WindowsPath) + "\\" + Path.GetFileName(name.Replace('/', '\\')));
					if (candidate != null && File.Exists(candidate))
					{
						host = candidate;
						break;
					}
				}
			}

			if (host == null || !File.Exists(host))
			{
				Log("LoadLibrary: " + name + " not found");
				lastError = ErrorFileNotFound;
				return 0;
			}

			try
			{
				return LoadLibrary(host).Base;
			}
			catch (X86Exception ex)
			{
				Log("LoadLibrary " + name + " failed: " + ex.Message);
				lastError = 193;
				return 0;
			}
		}

		private uint ModuleFileName(uint module, uint buffer, uint size, bool wide)
		{
			LoadedModule loaded = module == 0 ? null : ModuleAt(module);
			string path = loaded?.WindowsPath ?? "C:\\OpenBve.exe";
			return WriteString(buffer, size, path, wide);
		}

		/// <summary>Writes a string into a caller's buffer as the Windows APIs do: truncated to fit, returning the length written.</summary>
		private uint WriteString(uint buffer, uint size, string text, bool wide)
		{
			if (size == 0)
			{
				return (uint)text.Length + 1;
			}

			if (wide)
			{
				string fitted = text.Length >= size ? text.Substring(0, (int)size - 1) : text;
				Memory.WriteWString(buffer, fitted);
				return (uint)fitted.Length;
			}

			byte[] bytes = AnsiEncoding.GetBytes(text);
			int length = Math.Min(bytes.Length, (int)size - 1);
			Memory.WriteBytes(buffer, bytes.AsSpan(0, length));
			Memory.Write8(buffer + (uint)length, 0);
			return (uint)length;
		}

		private uint VersionInfo(uint info, bool wide)
		{
			uint size = Memory.Read32(info);
			Memory.Write32(info + 4, 6);
			Memory.Write32(info + 8, 1);
			Memory.Write32(info + 12, 7601);
			Memory.Write32(info + 16, 2); // VER_PLATFORM_WIN32_NT
			if (size > 20)
			{
				Memory.Fill(info + 20, 0, wide ? 256u : 128u);
			}

			return 1;
		}

		private void WriteSystemTime(uint address, DateTime time)
		{
			Memory.Write16(address, (ushort)time.Year);
			Memory.Write16(address + 2, (ushort)time.Month);
			Memory.Write16(address + 4, (ushort)time.DayOfWeek);
			Memory.Write16(address + 6, (ushort)time.Day);
			Memory.Write16(address + 8, (ushort)time.Hour);
			Memory.Write16(address + 10, (ushort)time.Minute);
			Memory.Write16(address + 12, (ushort)time.Second);
			Memory.Write16(address + 14, (ushort)time.Millisecond);
		}

		private uint VirtualAllocate(uint address, uint size)
		{
			if (address != 0 && Memory.IsMapped(address))
			{
				return address;
			}

			uint rounded = (size + 0xFFF) & ~0xFFFu;
			heapNext = (heapNext + 0xFFF) & ~0xFFFu;
			uint result = heapNext;
			heapNext += rounded;
			Memory.Map(result, rounded);
			return result;
		}

		// --- atoms ---

		private string AtomName(uint pointer, bool wide)
		{
			if (pointer < 0x10000)
			{
				return "#" + pointer; // MAKEINTATOM
			}

			return wide ? Memory.ReadWString(pointer) : Memory.ReadCString(pointer, AnsiEncoding);
		}

		private uint AddAtom(string name)
		{
			foreach (KeyValuePair<ushort, string> atom in atoms)
			{
				if (string.Equals(atom.Value, name, StringComparison.OrdinalIgnoreCase))
				{
					return atom.Key;
				}
			}

			if (name.StartsWith("#", StringComparison.Ordinal) && ushort.TryParse(name.Substring(1), out ushort integer) && integer < 0xC000)
			{
				return integer;
			}

			ushort value = nextAtom++;
			atoms[value] = name;
			return value;
		}

		private uint FindAtom(string name)
		{
			foreach (KeyValuePair<ushort, string> atom in atoms)
			{
				if (string.Equals(atom.Value, name, StringComparison.OrdinalIgnoreCase))
				{
					return atom.Key;
				}
			}

			lastError = ErrorFileNotFound;
			return 0;
		}

		private uint GetAtomName(ushort atom, uint buffer, uint size, bool wide)
		{
			if (!atoms.TryGetValue(atom, out string name))
			{
				if (atom < 0xC000 && atom != 0)
				{
					name = "#" + atom;
				}
				else
				{
					lastError = 6;
					return 0;
				}
			}

			return WriteString(buffer, size, name, wide);
		}

		// --- strings ---

		private Encoding CodePage(uint codePage)
		{
			switch (codePage)
			{
				case 0: // CP_ACP
				case 3: // CP_THREAD_ACP
					return AnsiEncoding;
				case 1: // CP_OEMCP
					return Encoding.Latin1;
				case 65001:
					return Encoding.UTF8;
				default:
					try
					{
						return Encoding.GetEncoding((int)codePage);
					}
					catch (Exception)
					{
						return AnsiEncoding;
					}
			}
		}

		private uint MultiByteToWideChar(uint codePage, uint source, int sourceLength, uint destination, int destinationLength)
		{
			byte[] bytes;
			bool terminated = sourceLength < 0;
			if (terminated)
			{
				int n = 0;
				while (Memory.Read8(source + (uint)n) != 0) n++;
				bytes = new byte[n];
			}
			else
			{
				bytes = new byte[sourceLength];
			}

			Memory.ReadBytes(source, bytes);
			string text = CodePage(codePage).GetString(bytes) + (terminated ? "\0" : string.Empty);
			if (destinationLength == 0)
			{
				return (uint)text.Length;
			}

			int count = Math.Min(text.Length, destinationLength);
			for (int i = 0; i < count; i++)
			{
				Memory.Write16(destination + (uint)(2 * i), text[i]);
			}

			return (uint)count;
		}

		private uint WideCharToMultiByte(uint codePage, uint source, int sourceLength, uint destination, int destinationLength)
		{
			string text;
			if (sourceLength < 0)
			{
				text = Memory.ReadWString(source) + "\0";
			}
			else
			{
				char[] chars = new char[sourceLength];
				for (int i = 0; i < sourceLength; i++) chars[i] = (char)Memory.Read16(source + (uint)(2 * i));
				text = new string(chars);
			}

			byte[] bytes = CodePage(codePage).GetBytes(text);
			if (destinationLength == 0)
			{
				return (uint)bytes.Length;
			}

			int count = Math.Min(bytes.Length, destinationLength);
			Memory.WriteBytes(destination, bytes.AsSpan(0, count));
			return (uint)count;
		}

		private string ReadCountedString(uint source, int length, bool wide)
		{
			if (length < 0)
			{
				return wide ? Memory.ReadWString(source) : Memory.ReadCString(source, AnsiEncoding);
			}

			if (wide)
			{
				char[] chars = new char[length];
				for (int i = 0; i < length; i++) chars[i] = (char)Memory.Read16(source + (uint)(2 * i));
				return new string(chars);
			}

			byte[] bytes = new byte[length];
			Memory.ReadBytes(source, bytes);
			return AnsiEncoding.GetString(bytes);
		}

		private uint StringType(uint source, int length, uint types, bool wide)
		{
			string text = ReadCountedString(source, length, wide);
			for (int i = 0; i < text.Length; i++)
			{
				char ch = text[i];
				ushort t = 0;
				if (char.IsUpper(ch)) t |= 0x001;
				if (char.IsLower(ch)) t |= 0x002;
				if (char.IsDigit(ch)) t |= 0x004;
				if (char.IsWhiteSpace(ch)) t |= 0x008;
				if (char.IsPunctuation(ch) || char.IsSymbol(ch)) t |= 0x010;
				if (char.IsControl(ch)) t |= 0x020;
				if (ch == ' ') t |= 0x040;
				if (Uri.IsHexDigit(ch)) t |= 0x080;
				if (char.IsLetter(ch)) t |= 0x100;
				Memory.Write16(types + (uint)(2 * i), t);
			}

			return 1;
		}

		private uint MapString(uint flags, uint source, int sourceLength, uint destination, int destinationLength, bool wide)
		{
			string text = ReadCountedString(source, sourceLength, wide);
			if ((flags & 0x200) != 0) text = text.ToUpperInvariant(); // LCMAP_UPPERCASE
			if ((flags & 0x100) != 0) text = text.ToLowerInvariant(); // LCMAP_LOWERCASE
			if (sourceLength < 0) text += "\0";
			if (destinationLength == 0)
			{
				return (uint)(wide ? text.Length : AnsiEncoding.GetByteCount(text));
			}

			if (wide)
			{
				int count = Math.Min(text.Length, destinationLength);
				for (int i = 0; i < count; i++) Memory.Write16(destination + (uint)(2 * i), text[i]);
				return (uint)count;
			}

			byte[] bytes = AnsiEncoding.GetBytes(text);
			int n = Math.Min(bytes.Length, destinationLength);
			Memory.WriteBytes(destination, bytes.AsSpan(0, n));
			return (uint)n;
		}

		private uint CompareStrings(uint flags, uint a, int aLength, uint b, int bLength, bool wide)
		{
			string x = ReadCountedString(a, aLength, wide);
			string y = ReadCountedString(b, bLength, wide);
			int result = string.Compare(x, y, (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
			return result < 0 ? 1u : result == 0 ? 2u : 3u;
		}

		// --- files ---

		private uint CreateFile(string name, uint access, uint disposition)
		{
			string host = ToHostPath(name);
			if (host == null)
			{
				lastError = 5; // access denied: outside the folders the plugin may use
				return InvalidHandle;
			}

			bool write = (access & 0x40000000) != 0;
			try
			{
				FileMode mode;
				switch (disposition)
				{
					case 1: mode = FileMode.CreateNew; break;
					case 2: mode = FileMode.Create; break;
					case 4: mode = FileMode.OpenOrCreate; break;
					case 5: mode = FileMode.Truncate; break;
					default: mode = FileMode.Open; break;
				}

				if (!write && mode == FileMode.Open && !File.Exists(host))
				{
					lastError = ErrorFileNotFound;
					return InvalidHandle;
				}

				FileStream stream = new FileStream(host, mode, write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
				lastError = 0;
				return NewHandle(stream);
			}
			catch (Exception ex)
			{
				Log("CreateFile " + name + ": " + ex.Message);
				lastError = ErrorFileNotFound;
				return InvalidHandle;
			}
		}

		private uint ReadFile(uint handle, uint buffer, uint count, uint read)
		{
			if (!(handles.TryGetValue(handle, out object value) && value is FileStream stream))
			{
				lastError = 6;
				return 0;
			}

			byte[] bytes = new byte[count];
			int total = 0;
			while (total < count)
			{
				int n = stream.Read(bytes, total, (int)count - total);
				if (n <= 0) break;
				total += n;
			}

			Memory.WriteBytes(buffer, bytes.AsSpan(0, total));
			if (read != 0) Memory.Write32(read, (uint)total);
			return 1;
		}

		private uint WriteFile(uint handle, uint buffer, uint count, uint written)
		{
			byte[] bytes = new byte[count];
			Memory.ReadBytes(buffer, bytes);
			if (handle >= StdError)
			{
				Log("plugin: " + AnsiEncoding.GetString(bytes).TrimEnd());
			}
			else if (handles.TryGetValue(handle, out object value) && value is FileStream stream && stream.CanWrite)
			{
				stream.Write(bytes, 0, bytes.Length);
			}

			if (written != 0) Memory.Write32(written, count);
			return 1;
		}

		private uint ConsoleWrite(uint buffer, uint count, uint written, bool wide)
		{
			string text = ReadCountedString(buffer, (int)count, wide);
			Log("plugin: " + text.TrimEnd());
			if (written != 0) Memory.Write32(written, count);
			return 1;
		}

		private uint SetFilePointer(uint handle, int distance, uint high, uint method)
		{
			if (!(handles.TryGetValue(handle, out object value) && value is FileStream stream))
			{
				lastError = 6;
				return InvalidHandle;
			}

			long offset = distance;
			if (high != 0)
			{
				offset = (long)((ulong)(uint)distance | ((ulong)Memory.Read32(high) << 32));
			}

			long position = stream.Seek(offset, (SeekOrigin)method);
			if (high != 0) Memory.Write32(high, (uint)(position >> 32));
			return (uint)position;
		}

		private uint FileAttributes(string name)
		{
			string host = ToHostPath(name);
			if (host != null && Directory.Exists(host)) return 0x10;
			if (host != null && File.Exists(host)) return 0x80;
			lastError = ErrorFileNotFound;
			return InvalidHandle;
		}

		private uint FindFirst(string pattern, uint data, bool wide)
		{
			string windows = pattern.Replace('/', '\\');
			int slash = windows.LastIndexOf('\\');
			string folder = slash >= 0 ? windows.Substring(0, slash) : ".";
			string mask = slash >= 0 ? windows.Substring(slash + 1) : windows;
			string host = ToHostPath(folder);
			if (host == null || !Directory.Exists(host))
			{
				lastError = 3;
				return InvalidHandle;
			}

			List<string> matches = new List<string>();
			System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(
				"^" + System.Text.RegularExpressions.Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			foreach (string entry in Directory.EnumerateFileSystemEntries(host))
			{
				if (regex.IsMatch(Path.GetFileName(entry)))
				{
					matches.Add(entry);
				}
			}

			if (matches.Count == 0)
			{
				lastError = ErrorFileNotFound;
				return InvalidHandle;
			}

			FindState state = new FindState { Entries = matches.ToArray(), Next = 0 };
			uint handle = NewHandle(state);
			FindNext(handle, data, wide);
			return handle;
		}

		private uint FindNext(uint handle, uint data, bool wide)
		{
			if (!(handles.TryGetValue(handle, out object value) && value is FindState state) || state.Next >= state.Entries.Length)
			{
				lastError = 18; // ERROR_NO_MORE_FILES
				return 0;
			}

			string entry = state.Entries[state.Next++];
			bool directory = Directory.Exists(entry);
			Memory.Fill(data, 0, wide ? 592u : 320u);
			Memory.Write32(data, directory ? 0x10u : 0x80u);
			if (!directory)
			{
				long length = new FileInfo(entry).Length;
				Memory.Write32(data + 28, (uint)(length >> 32));
				Memory.Write32(data + 32, (uint)length);
			}

			if (wide) Memory.WriteWString(data + 44, Path.GetFileName(entry));
			else Memory.WriteCString(data + 44, Path.GetFileName(entry), AnsiEncoding);
			return 1;
		}

		private uint FullPathName(string name, uint size, uint buffer, uint filePart, bool wide)
		{
			string full = name.Replace('/', '\\');
			if (!(full.Length >= 2 && full[1] == ':'))
			{
				full = "C:\\" + (full.StartsWith("\\", StringComparison.Ordinal) ? full.TrimStart('\\') : Path.Combine(CurrentDirectoryRelative, full).Replace('/', '\\'));
			}

			uint length = WriteString(buffer, size, full, wide);
			if (filePart != 0)
			{
				int slash = full.LastIndexOf('\\');
				Memory.Write32(filePart, buffer + (uint)((slash + 1) * (wide ? 2 : 1)));
			}

			return length;
		}

		// --- INI files, which configurable plugins read their settings from ---

		private uint PrivateProfileString(uint section, uint key, uint defaultValue, uint buffer, uint size, uint file)
		{
			string fileName = Memory.ReadCString(file, AnsiEncoding);
			if (section == 0 || key == 0)
			{
				// No section: every section name; no key: every key in the section. Each is
				// NUL-terminated, and the list ends with a second NUL.
				List<string> names = IniNames(fileName, section == 0 ? null : Memory.ReadCString(section, AnsiEncoding));
				return WriteStringList(buffer, size, names);
			}

			string fallback = Memory.ReadCString(defaultValue, AnsiEncoding) ?? string.Empty;
			string value = ReadIni(fileName, Memory.ReadCString(section, AnsiEncoding), Memory.ReadCString(key, AnsiEncoding)) ?? fallback.Trim();
			return WriteString(buffer, size, value, false);
		}

		private uint WriteStringList(uint buffer, uint size, List<string> names)
		{
			if (size < 2)
			{
				return 0;
			}

			uint written = 0;
			foreach (string name in names)
			{
				byte[] bytes = AnsiEncoding.GetBytes(name);
				if (written + bytes.Length + 2 > size)
				{
					break;
				}

				Memory.WriteBytes(buffer + written, bytes);
				written += (uint)bytes.Length;
				Memory.Write8(buffer + written++, 0);
			}

			Memory.Write8(buffer + written, 0);
			return written;
		}

		private List<string> IniNames(string file, string section)
		{
			List<string> names = new List<string>();
			string host = ToHostPath(file);
			if (host == null || !File.Exists(host))
			{
				return names;
			}

			bool inSection = false;
			foreach (string raw in File.ReadAllLines(host, AnsiEncoding))
			{
				string line = raw.Trim();
				if (line.StartsWith("[", StringComparison.Ordinal))
				{
					string name = line.Trim('[', ']').Trim();
					if (section == null) names.Add(name);
					inSection = section != null && string.Equals(name, section.Trim(), StringComparison.OrdinalIgnoreCase);
					continue;
				}

				int equals = line.IndexOf('=');
				if (inSection && equals > 0 && !line.StartsWith(";", StringComparison.Ordinal))
				{
					names.Add(line.Substring(0, equals).Trim());
				}
			}

			return names;
		}

		private uint PrivateProfileInt(uint section, uint key, uint defaultValue, uint file)
		{
			string value = ReadIni(Memory.ReadCString(file, AnsiEncoding), Memory.ReadCString(section, AnsiEncoding), Memory.ReadCString(key, AnsiEncoding));
			return value != null && int.TryParse(value.Trim(), out int number) ? (uint)number : defaultValue;
		}

		private string ReadIni(string file, string section, string key)
		{
			// Windows ignores spaces around the section and key names asked for, as around those in the file.
			section = section?.Trim();
			key = key?.Trim();
			string host = ToHostPath(file);
			if (host == null || !File.Exists(host))
			{
				return null;
			}

			bool inSection = false;
			foreach (string raw in File.ReadAllLines(host, AnsiEncoding))
			{
				string line = raw.Trim();
				if (line.StartsWith("[", StringComparison.Ordinal))
				{
					inSection = string.Equals(line.Trim('[', ']').Trim(), section, StringComparison.OrdinalIgnoreCase);
					continue;
				}

				int equals = line.IndexOf('=');
				if (inSection && equals > 0 && string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase))
				{
					return line.Substring(equals + 1).Trim();
				}
			}

			return null;
		}
	}
}
