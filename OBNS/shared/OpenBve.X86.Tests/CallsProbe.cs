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
using System.Text;
using OpenBve.X86;

namespace OpenBve.X86.Tests
{
	/// <summary>Prints every Windows function a plugin calls while loading and reporting its version.</summary>
	internal static class CallsProbe
	{
		internal static void Run(string root, string dll, Encoding encoding)
		{
			Win32Process process = null;
			string Describe(uint value)
			{
				if (value < 0x10000 || process == null || !process.Memory.IsMapped(value)) return "0x" + value.ToString("X");
				string text = process.Memory.ReadCString(value, encoding, 80);
				bool printable = !string.IsNullOrEmpty(text) && text.Length > 1 && text.IndexOfAny(new[] { '\u0001', '\u0002', '\u0003', '\u0004', '\u0005', '\u0006', '\u0007', '\u0008', '\u000E', '\u000F', '\u0010', '\u007F', '\uFFFD' }) < 0;
				return printable ? "0x" + value.ToString("X") + "\"" + text + "\"" : "0x" + value.ToString("X");
			}

			AtsPlugin plugin = AtsPlugin.Open(root, dll, encoding, Console.WriteLine, p =>
			{
				process = p;
				p.Cpu.HostCallTrace = (name, args, result) =>
					Console.WriteLine($"  {name}({string.Join(", ", Array.ConvertAll(args, Describe))}) = 0x{result:X}");
			});
			plugin.Load();
			Console.WriteLine("version: 0x" + plugin.GetPluginVersion().ToString("X"));
			Console.WriteLine("--- SetVehicleSpec, Initialize");
			plugin.SetVehicleSpec(8, 5, 2, 6, 10);
			plugin.Initialize(0);
			Console.WriteLine("--- Elapse");
			int[] panel = new int[256], sound = new int[256];
			plugin.Elapse(new AtsVehicleState { Location = 1000, Time = 36000000, MrPressure = 800000 }, panel, sound);
			Console.WriteLine("ok");
		}
	}
}
