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
	/// <summary>The TBL ATO start: reverser F, brake N, power N, then S; with S held for several frames or for none.</summary>
	internal static class AtoProbe
	{
		internal static void Run(string root, string dll)
		{
			foreach ((string name, int holdFrames, bool doorCycle) in new[] { ("S held 6 frames", 6, false), ("S same-frame tap", 0, false), ("doors, then S held", 6, true) })
			{
				AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(950), _ => { });
				plugin.Load();
				plugin.SetVehicleSpec(8, 4, 1, 6, 12);
				plugin.Initialize(0);
				plugin.SetReverser(1);
				plugin.SetBrake(0);
				plugin.SetPower(0);
				int[] panel = new int[256], sound = new int[256];
				double speed = 0, location = 0;
				string trace = string.Empty;
				for (int frame = 0; frame < 60 * 40; frame++)
				{
					if (doorCycle && frame == 10) plugin.DoorOpen();
					if (doorCycle && frame == 20) plugin.DoorClose();
					if (frame == 60)
					{
						plugin.KeyDown(0);
						if (holdFrames == 0) plugin.KeyUp(0);
					}

					if (holdFrames > 0 && frame == 60 + holdFrames) plugin.KeyUp(0);
					AtsHandles h = plugin.Elapse(new AtsVehicleState
					{
						Location = location,
						Speed = (float)(speed * 3.6),
						Time = 37488000 + frame * 1000 / 60,
						MrPressure = 800000,
						BcPressure = 0
					}, panel, sound);
					double a = h.Brake > 0 ? -h.Brake * 0.12 : h.Power * 0.2;
					speed = Math.Max(0, speed + a / 60);
					location += speed / 60;
					if (frame % 300 == 0)
					{
						trace += $" t{frame / 60}:B{h.Brake}P{h.Power}@{speed * 3.6:0}";
					}
				}

				Console.WriteLine($"{name,-20}{trace} | moved {location:0} m");
			}
		}
	}
}
