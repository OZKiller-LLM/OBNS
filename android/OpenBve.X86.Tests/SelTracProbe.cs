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
	/// <summary>Scripted sequences against the MTR stack: what trips SelTrac's emergency brake, and what resets it.</summary>
	internal static class SelTracProbe
	{
		internal static void Run(string root, string dll)
		{
			AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.UTF8, _ => { });
			plugin.Load();
			plugin.SetVehicleSpec(7, 4, 1, 5, 9);
			plugin.Initialize(0);
			plugin.SetReverser(1);
			plugin.SetBrake(8);
			plugin.SetPower(0);
			int[] panel = new int[256], sound = new int[256], last = new int[256];
			int frame = 0;
			string[] script =
			{
				"beacons", "wait", "brake 8", "wait", "key A2", "key A2", "key A2", "key A2", "key A2", "wait",
				"doors", "wait", "wait", "wait", "wait", "wait", "wait", "wait", "wait", "wait", "wait", "wait", "wait",
				"brake 0", "wait", "wait", "wait", "wait", "key S", "wait", "wait", "hold S", "wait", "wait", "key S", "wait", "brake 1", "key S", "wait"
			};
			string[] keys = { "S", "A1", "A2", "B1", "B2", "C1", "C2", "D", "E", "F", "G", "H", "I", "J", "K", "L" };
			foreach (string step in script)
			{
				string[] p = step.Split(' ');
				switch (p[0])
				{
					case "brake": plugin.SetBrake(int.Parse(p[1])); break;
					case "power": plugin.SetPower(int.Parse(p[1])); break;
					case "key":
						int k = Array.IndexOf(keys, p[1]);
						plugin.KeyDown(k);
						Run(plugin, panel, sound, ref frame, 5);
						plugin.KeyUp(k);
						break;
					case "beacons":
						// As the phone logged them at the platform (MTR EAL2023, Lo Wu).
						foreach ((int type, int optional) in new[] { (12, 3037995), (13, 20), (14, 1820317), (50, 30), (51, 4525), (52, 12), (53, 11), (56, 2), (54, 18), (55, 4), (57, 1) })
						{
							plugin.SetBeaconData(type, 4, -4550f, optional);
						}

						plugin.SetSignal(4);
						break;
					case "hold":
						int hk = Array.IndexOf(keys, p[1]);
						plugin.KeyDown(hk);
						Run(plugin, panel, sound, ref frame, 120);
						plugin.KeyUp(hk);
						break;
					case "doors":
						plugin.DoorOpen();
						Run(plugin, panel, sound, ref frame, 60 * 35);
						plugin.DoorClose();
						break;
				}

				AtsHandles h = Run(plugin, panel, sound, ref frame, step == "wait" ? 180 : 30);
				string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != last[i]).Select(i => i + "=" + panel[i]));
				Array.Copy(panel, last, 256);
				Console.WriteLine($"{step,-10} -> B{h.Brake} P{h.Power} R{h.Reverser} | {changed}");
			}
		}

		private static AtsHandles Run(AtsPlugin plugin, int[] panel, int[] sound, ref int frame, int count)
		{
			AtsHandles h = default;
			for (int i = 0; i < count; i++, frame++)
			{
				h = plugin.Elapse(new AtsVehicleState { Location = 4548, Time = 37800000 + frame * 16, MrPressure = 800000, BcPressure = 0 }, panel, sound);
			}

			return h;
		}
	}
}
