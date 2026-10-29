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
	/// <summary>Horns, doors and keys between frames, as the cab sends them; reports any failure.</summary>
	internal static class HornProbe
	{
		internal static void Run(string root, string dll)
		{
			AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(950), _ => { });
			plugin.Load();
			plugin.SetVehicleSpec(8, 4, 1, 6, 12);
			plugin.Initialize(0);
			plugin.SetReverser(1);
			plugin.SetBrake(0);
			plugin.SetPower(0);
			int[] panel = new int[256], sound = new int[256];
			string[] steps = { "horn 0", "horn 1", "horn 2", "horn 0", "key S", "key A1", "key B1", "key D", "key E", "doors", "horn 0", "horn 1" };
			int frame = 0;
			foreach (string step in steps)
			{
				try
				{
					string[] parts = step.Split(' ');
					switch (parts[0])
					{
						case "horn":
							plugin.HornBlow(int.Parse(parts[1]));
							break;
						case "key":
							int key = Array.IndexOf(new[] { "S", "A1", "A2", "B1", "B2", "C1", "C2", "D", "E", "F", "G", "H", "I", "J", "K", "L" }, parts[1]);
							plugin.KeyDown(key);
							Frames(plugin, panel, sound, ref frame, 3);
							plugin.KeyUp(key);
							break;
						case "doors":
							plugin.DoorOpen();
							Frames(plugin, panel, sound, ref frame, 3);
							plugin.DoorClose();
							break;
					}

					Frames(plugin, panel, sound, ref frame, 10);
					Console.WriteLine($"{step,-8} ok, ESP {plugin.Process.Cpu.R[Cpu.ESP]:X8}");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"{step,-8} FAILED: {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		private static void Frames(AtsPlugin plugin, int[] panel, int[] sound, ref int frame, int count)
		{
			for (int i = 0; i < count; i++, frame++)
			{
				plugin.Elapse(new AtsVehicleState { Time = 36000000 + frame * 16, MrPressure = 800000 }, panel, sound);
			}
		}
	}
}
