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
using System.IO;
using System.Linq;
using System.Text;
using OpenBve.X86;

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// Loads a BVE ATS plugin in the emulator and runs it through a short simulated drive,
	/// printing what it does: handles, panel indices that change, and sounds.
	/// </summary>
	/// <remarks>Usage: OpenBve.X86.Tests &lt;root folder&gt; &lt;plugin dll&gt; [frames]</remarks>
	internal static class Program
	{
		private static int Main(string[] args)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			if (args[0] == "survey")
			{
				SurveyProbe.Run(args[1]);
				return 0;
			}

			string root = args[0];
			string dll = args[1];
			if (args.Length > 2 && args[2] == "ato")
			{
				AtoProbe.Run(root, dll);
				return 0;
			}

			if (args.Length > 2 && args[2] == "calls")
			{
				CallsProbe.Run(root, dll, args.Length > 3 ? Encoding.GetEncoding(int.Parse(args[3])) : Encoding.UTF8);
				return 0;
			}

			if (args.Length > 3 && args[2] == "replay")
			{
				ReplayProbe.Run(root, dll, args[3], args.Length > 4 ? int.Parse(args[4]) : int.MaxValue, args.Length > 5 ? args[5].Split(',') : null);
				return 0;
			}

			if (args.Length > 2 && args[2] == "tpws")
			{
				// tpws [types] [signal] [optional] [kmh] [reset key] [override key]
				TpwsProbe.Run(root, dll, args.Length > 3 ? args[3] : "44000-44010", args.Length > 4 ? int.Parse(args[4]) : 0,
					args.Length > 5 ? int.Parse(args[5]) : 0, args.Length > 6 ? double.Parse(args[6]) : 60.0,
					args.Length > 7 ? int.Parse(args[7]) : 0, args.Length > 8 ? int.Parse(args[8]) : 3);
				return 0;
			}

			if (args.Length > 2 && args[2] == "seltrac")
			{
				SelTracProbe.Run(root, dll);
				return 0;
			}

			if (args.Length > 2 && args[2] == "horn")
			{
				HornProbe.Run(root, dll);
				return 0;
			}

			if (args.Length > 2 && args[2] == "probe")
			{
				Probe.Run(root, dll);
				return 0;
			}

			int frames = args.Length > 2 ? int.Parse(args[2]) : 600;

			AtsPlugin plugin;
			try
			{
				plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(950), Console.WriteLine);
			}
			catch (X86Exception ex)
			{
				Console.WriteLine("LOAD FAILED: " + ex.Message);
				return 1;
			}

			Console.WriteLine("modules: " + string.Join(", ", plugin.Process.Modules.Select(m => m.Name + "@" + m.Base.ToString("X8"))));
			try
			{
				plugin.Load();
				Console.WriteLine("version: 0x" + plugin.GetPluginVersion().ToString("X"));
				plugin.SetVehicleSpec(8, 4, 1, 6, 12);
				plugin.Initialize(0); // InitializationModes.OnService
				plugin.SetReverser(1);
				plugin.SetPower(0);
				plugin.SetBrake(8);

				int[] panel = new int[256];
				int[] sound = new int[256];
				int[] lastPanel = new int[256];
				double location = 0;
				double speed = 0;
				int power = 0, brake = 8;
				long startInstructions = plugin.Process.Cpu.InstructionCount;
				System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
				for (int frame = 0; frame < frames; frame++)
				{
					double dt = 1.0 / 60.0;
					// A simple drive: release the brake after 1 s, notch up, run, and pass a few beacons.
					if (frame == 60) { brake = 0; plugin.SetBrake(0); }
					if (frame == 90) { power = 4; plugin.SetPower(4); }
					if (frame % 240 == 120)
					{
						plugin.SetBeaconData(0, 2, 300.0f, 0);
						plugin.SetSignal(2);
					}

					AtsVehicleState state = new AtsVehicleState
					{
						Location = location,
						Speed = (float)(speed * 3.6),
						Time = (int)(36000000 + frame * 1000 / 60),
						BcPressure = brake * 50000,
						MrPressure = 800000,
						ErPressure = 490000,
						BpPressure = 490000,
						SapPressure = 800000
					};
					AtsHandles handles = plugin.Elapse(state, panel, sound);
					double acceleration = handles.Brake > 0 ? -1.0 * handles.Brake / 8 : 0.8 * handles.Power / 4;
					speed = Math.Max(0, speed + acceleration * dt);
					location += speed * dt;

					if (frame % 60 == 0 || frame == frames - 1)
					{
						string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != lastPanel[i]).Select(i => i + "=" + panel[i]));
						string sounds = string.Join(" ", Enumerable.Range(0, 256).Where(i => sound[i] != 0 && sound[i] != 2 && sound[i] != -10000).Select(i => i + ":" + sound[i]));
						Console.WriteLine($"t={frame / 60.0:0.0}s v={speed * 3.6:0.0}km/h handles B{handles.Brake} P{handles.Power} R{handles.Reverser} C{handles.ConstantSpeed} | panel {changed} | sound {sounds}");
						Array.Copy(panel, lastPanel, 256);
					}
				}

				long executed = plugin.Process.Cpu.InstructionCount - startInstructions;
				Console.WriteLine($"{frames} frames, {executed:N0} instructions, {executed / Math.Max(1, frames):N0} per frame, {timer.Elapsed.TotalMilliseconds / frames:0.000} ms per frame");
				Console.WriteLine("non-zero panel at end: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != 0).Select(i => i + "=" + panel[i])));
				return 0;
			}
			catch (X86Exception ex)
			{
				Console.WriteLine("FAILED: " + ex.Message);
				Console.WriteLine("recent: " + string.Join(" ", plugin.Process.Cpu.RecentInstructions().Select(a => a.ToString("X8"))));
				return 2;
			}
		}
	}
}
