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
using System.Linq;
using System.Text;
using OpenBve.X86;

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// Finds what a plugin needs before it allows traction: tries door cycles, each ATS key, and
	/// signal/beacon inputs in turn, reporting the first that lets power through.
	/// </summary>
	internal static class Probe
	{
		internal static void Run(string root, string dll)
		{
			string[] steps =
			{
				"baseline", "doors open+close", "signal 4", "beacon 0 (aspect 4)",
				"key S", "key A1", "key A2", "key B1", "key B2", "key C1", "key C2", "key D", "key E", "key F", "key G", "key H", "key I", "key J", "key K", "key L"
			};

			foreach (string step in steps)
			{
				AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(950), _ => { });
				plugin.Load();
				plugin.SetVehicleSpec(8, 4, 1, 6, 12);
				plugin.Initialize(0);
				plugin.SetReverser(1);
				plugin.SetBrake(8);
				int[] panel = new int[256], sound = new int[256];
				AtsHandles h = default;
				for (int frame = 0; frame < 300; frame++)
				{
					if (frame == 30)
					{
						switch (step)
						{
							case "doors open+close":
								plugin.DoorOpen();
								break;
							case "signal 4":
								plugin.SetSignal(4);
								break;
							case "beacon 0 (aspect 4)":
								plugin.SetBeaconData(0, 4, 500f, 0);
								break;
						}

						if (step.StartsWith("key ", StringComparison.Ordinal))
						{
							int key = Array.IndexOf(new[] { "S", "A1", "A2", "B1", "B2", "C1", "C2", "D", "E", "F", "G", "H", "I", "J", "K", "L" }, step.Substring(4));
							plugin.KeyDown(key);
						}
					}

					if (frame == 40)
					{
						if (step == "doors open+close") plugin.DoorClose();
						if (step.StartsWith("key ", StringComparison.Ordinal))
						{
							int key = Array.IndexOf(new[] { "S", "A1", "A2", "B1", "B2", "C1", "C2", "D", "E", "F", "G", "H", "I", "J", "K", "L" }, step.Substring(4));
							plugin.KeyUp(key);
						}
					}

					if (frame == 60) plugin.SetBrake(0);
					if (frame == 90) plugin.SetPower(4);
					h = plugin.Elapse(new AtsVehicleState { Time = 36000000 + frame * 16, MrPressure = 800000 }, panel, sound);
				}

				Console.WriteLine($"{step,-22} -> B{h.Brake} P{h.Power} | lit: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != 0).Select(i => i + "=" + panel[i])));
			}
		}
	}
}
