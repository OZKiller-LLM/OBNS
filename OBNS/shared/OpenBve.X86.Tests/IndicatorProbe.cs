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
using System.Linq;
using System.Text;
using OpenBve.X86;

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// OS_Ats1's custom indicators (key -> panel index toggles): the small blocks it allocates while
	/// reading its configuration, read back, and the keys pressed - compared with a native run.
	/// </summary>
	internal static class IndicatorProbe
	{
		internal static void Run(string root, string dll)
		{
			Win32Process process = null;
			List<uint> small = new List<uint>();
			AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.UTF8, Console.WriteLine, p =>
			{
				process = p;
				p.Cpu.HostCallTrace = (name, args, result) =>
				{
					if (name.EndsWith("!malloc") && args[0] == 8)
					{
						small.Add((uint)result);
					}
				};
			});
			plugin.Load();
			System.IO.StreamWriter watch = null;
			string watchFor = Environment.GetEnvironmentVariable("WATCH_BYTES");
			if (!string.IsNullOrEmpty(watchFor))
			{
				// Instructions whose bytes contain WATCH_BYTES (an address), with the 40 before and 20 after each.
				watch = new System.IO.StreamWriter("watch-trace.txt");
				string[] ring = new string[40];
				long n = 0;
				int after = 0;
				process.Cpu.StepHook = cpu =>
				{
					byte[] bytes = new byte[10];
					for (int i = 0; i < 10; i++) bytes[i] = process.Memory.IsMapped(cpu.Eip + (uint)i) ? process.Memory.Read8(cpu.Eip + (uint)i) : (byte)0;
					string hex = BitConverter.ToString(bytes).Replace("-", " ");
					string line = $"{cpu.Eip:X8} {hex} | eax={cpu.R[0]:X8} ecx={cpu.R[1]:X8} edx={cpu.R[2]:X8} ebx={cpu.R[3]:X8} esp={cpu.R[4]:X8} ebp={cpu.R[5]:X8} esi={cpu.R[6]:X8} edi={cpu.R[7]:X8} {(cpu.CF ? "C" : "c")}{(cpu.ZF ? "Z" : "z")}{(cpu.SF ? "S" : "s")}{(cpu.OF ? "O" : "o")}";
					if (hex.Contains(watchFor))
					{
						watch.WriteLine("----- #" + n);
						for (int i = 0; i < 40; i++) { string r = ring[(n + i) % 40]; if (r != null) watch.WriteLine("  " + r); }
						watch.WriteLine(">>" + line);
						after = 20;
					}
					else if (after > 0)
					{
						watch.WriteLine("  " + line);
						after--;
					}

					ring[n % 40] = line;
					n++;
				};
			}

			plugin.SetVehicleSpec(8, 4, 1, 6, 12);
			plugin.Initialize(0);
			if (watch != null)
			{
				process.Cpu.StepHook = null;
				watch.Close();
				Console.WriteLine("global at watched address: " + process.Memory.Read32(0x620949A8));
				return;
			}
			Console.WriteLine(small.Count + " 8-byte blocks: " + string.Join(" ", small.Select(a => "(" + (int)process.Memory.Read32(a) + "," + (int)process.Memory.Read32(a + 4) + ")")));
			int[] panel = new int[256], sound = new int[256];
			AtsVehicleState state = new AtsVehicleState { Location = 1000, Time = 28800000 };
			void Frames(int n)
			{
				for (int i = 0; i < n; i++)
				{
					state.Time += 16;
					plugin.Elapse(state, panel, sound);
				}
			}

			if (Environment.GetEnvironmentVariable("WATCH_VALUE") == "1")
			{
				// Any instruction that changes the value at 0x620949A8, through the first frames.
				uint lastValue = process.Memory.Read32(0x620949A8);
				string previous = null;
				process.Cpu.StepHook = cpu =>
				{
					uint value = process.Memory.Read32(0x620949A8);
					if (value != lastValue)
					{
						Console.WriteLine("changed " + lastValue.ToString("X") + " -> " + value.ToString("X") + " by " + previous);
						lastValue = value;
					}

					byte[] bytes = new byte[10];
					for (int i = 0; i < 10; i++) bytes[i] = process.Memory.Read8(cpu.Eip + (uint)i);
					previous = $"{cpu.Eip:X8} {BitConverter.ToString(bytes).Replace("-", " ")} | eax={cpu.R[0]:X8} ecx={cpu.R[1]:X8} edx={cpu.R[2]:X8} ebx={cpu.R[3]:X8} esi={cpu.R[6]:X8} edi={cpu.R[7]:X8}";
				};
				Frames(60);
				process.Cpu.StepHook = null;
				Console.WriteLine("after 60 frames: " + process.Memory.Read32(0x620949A8).ToString("X"));
				return;
			}

			Frames(60);
			if (Environment.GetEnvironmentVariable("TRACE_KEYDOWN") == "1")
			{
				// Every instruction of KeyDown(8): address, bytes, then registers and flags before it.
				System.IO.StreamWriter trace = new System.IO.StreamWriter("keydown-trace.txt");
				process.Cpu.StepHook = cpu =>
				{
					byte[] bytes = new byte[8];
					for (int i = 0; i < 8; i++) bytes[i] = process.Memory.IsMapped(cpu.Eip + (uint)i) ? process.Memory.Read8(cpu.Eip + (uint)i) : (byte)0;
					trace.WriteLine($"{cpu.Eip:X8} {BitConverter.ToString(bytes).Replace("-", " ")} | eax={cpu.R[0]:X8} ecx={cpu.R[1]:X8} edx={cpu.R[2]:X8} ebx={cpu.R[3]:X8} esp={cpu.R[4]:X8} ebp={cpu.R[5]:X8} esi={cpu.R[6]:X8} edi={cpu.R[7]:X8} {(cpu.CF ? "C" : "c")}{(cpu.ZF ? "Z" : "z")}{(cpu.SF ? "S" : "s")}{(cpu.OF ? "O" : "o")}");
				};
				plugin.KeyDown(8);
				process.Cpu.StepHook = null;
				trace.Close();
				Console.WriteLine("state[108] after KeyDown: " + process.Memory.Read32(0x200000D0 + 108 * 4));
				// One Elapse, every instruction (to find where the states reach the panel array).
				System.IO.StreamWriter elapse = new System.IO.StreamWriter("elapse-trace.txt");
				process.Cpu.StepHook = cpu =>
				{
					byte[] bytes = new byte[8];
					for (int i = 0; i < 8; i++) bytes[i] = process.Memory.IsMapped(cpu.Eip + (uint)i) ? process.Memory.Read8(cpu.Eip + (uint)i) : (byte)0;
					elapse.WriteLine($"{cpu.Eip:X8} {BitConverter.ToString(bytes).Replace("-", " ")} | eax={cpu.R[0]:X8} ecx={cpu.R[1]:X8} edx={cpu.R[2]:X8} ebx={cpu.R[3]:X8} esi={cpu.R[6]:X8} edi={cpu.R[7]:X8} esp={cpu.R[4]:X8} {(cpu.CF ? "C" : "c")}{(cpu.ZF ? "Z" : "z")}{(cpu.SF ? "S" : "s")}{(cpu.OF ? "O" : "o")}");
				};
				Frames(1);
				process.Cpu.StepHook = null;
				elapse.Close();
				Console.WriteLine("state[108] after Elapse: " + process.Memory.Read32(0x200000D0 + 108 * 4) + ", panel[108]=" + panel[108]);
				Frames(10);
				Console.WriteLine("traced KeyDown(8): " + string.Join(" ", Enumerable.Range(100, 16).Select(i => i + "=" + panel[i])));
				return;
			}

			int[] before = (int[])panel.Clone();
			foreach (int key in new[] { 1, 5, 8, 12, 13 })
			{
				plugin.KeyDown(key);
				Frames(10);
				plugin.KeyUp(key);
				Frames(30);
				Console.WriteLine("key " + key + " -> " + string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != before[i]).Select(i => i + "=" + panel[i])));
				before = (int[])panel.Clone();
			}
		}
	}
}
