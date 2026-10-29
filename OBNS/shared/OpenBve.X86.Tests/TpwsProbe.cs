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

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// Exercises a TPWS/AWS plugin (OS_Ats1 and its kin): for each candidate beacon type, a fresh
	/// plugin is driven at speed and passes that beacon at a red signal, then (if it tripped) is
	/// brought to a stand, reset and overridden. Prints the panel, sound and handle changes.
	/// </summary>
	/// <remarks>
	/// Usage: OpenBve.X86.Tests &lt;root&gt; &lt;dll&gt; tpws [types] [signal] [optional] [kmh]
	/// where types is a comma list or a range like 44000-44010.
	/// </remarks>
	internal static class TpwsProbe
	{
		public static void Run(string root, string dll, string types, int signal, int optional, double kmh, int resetKey, int overrideKey)
		{
			if (types.Contains('+'))
			{
				// A pair: "a+b[@seconds]" passes a, then b that many seconds later (default 0.5).
				string[] at = types.Split('@');
				string[] pair = at[0].Split('+');
				Pair(root, dll, int.Parse(pair[0]), int.Parse(pair[1]), at.Length > 1 ? double.Parse(at[1]) : 0.5, signal, optional, kmh);
				return;
			}

			// "!type": the driver presses the override key a second before the beacon.
			bool overrideFirst = types.StartsWith("!");
			types = types.TrimStart('!');
			int[] candidates = types.Contains('-')
				? Enumerable.Range(int.Parse(types.Split('-')[0]), int.Parse(types.Split('-')[1]) - int.Parse(types.Split('-')[0]) + 1).ToArray()
				: types.Split(',').Select(int.Parse).ToArray();
			foreach (int type in candidates)
			{
				Console.WriteLine($"=== beacon {type}, signal {signal}, optional {optional}, {kmh} km/h ===");
				AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(1252), _ => { });
				try
				{
					plugin.Load();
					plugin.SetVehicleSpec(8, 4, 1, 6, 12);
					plugin.Initialize(0);
					plugin.SetReverser(1);
					plugin.SetPower(0);
					plugin.SetBrake(0);
					plugin.SetSignal(signal);
					int[] panel = new int[256], sound = new int[256], lastPanel = new int[256], lastSound = new int[256];
					double location = 0, speed = kmh / 3.6;
					int brake = 0;
					bool tripped = false;
					for (int frame = 0; frame < 60 * 40; frame++)
					{
						double dt = 1.0 / 60.0;
						double t = frame / 60.0;
						if (overrideFirst && frame == 60)
						{
							Console.WriteLine("  (override key " + overrideKey + " before the beacon)");
							plugin.KeyDown(overrideKey);
						}

						if (overrideFirst && frame == 70)
						{
							plugin.KeyUp(overrideKey);
						}

						if (frame == 120)
						{
							plugin.SetBeaconData(type, signal, 150.0f, optional);
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
						if (handles.Brake > 0 && frame > 120 && !tripped)
						{
							tripped = true;
							Console.WriteLine($"  t={t:0.00}s TRIP: brake B{handles.Brake} at {speed * 3.6:0.0} km/h");
						}

						brake = handles.Brake;
						double acceleration = handles.Brake > 0 ? -1.2 * Math.Min(handles.Brake, 9) / 8 : 0;
						speed = Math.Max(0, speed + acceleration * dt);
						location += speed * dt;

						// Once stood for 3 s: the driver resets, then overrides; the plugin should release.
						if (tripped && speed == 0 && frame % 60 == 0)
						{
							if (t >= 20 && t < 20.1)
							{
								Console.WriteLine("  (reset key " + resetKey + ")");
								plugin.KeyDown(resetKey);
							}
							else if (t >= 21 && t < 21.1)
							{
								plugin.KeyUp(resetKey);
							}
							else if (t >= 24 && t < 24.1)
							{
								Console.WriteLine("  (override key " + overrideKey + ")");
								plugin.KeyDown(overrideKey);
							}
							else if (t >= 25 && t < 25.1)
							{
								plugin.KeyUp(overrideKey);
							}
						}

						string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != lastPanel[i]).Select(i => i + "=" + panel[i]));
						string sounds = string.Join(" ", Enumerable.Range(0, 256).Where(i => sound[i] != lastSound[i]).Select(i => i + ":" + sound[i]));
						if (changed.Length + sounds.Length > 0 && frame > 0)
						{
							Console.WriteLine($"  t={t:0.00}s v={speed * 3.6:0.0} B{handles.Brake} P{handles.Power} | panel {changed} | sound {sounds}");
						}

						Array.Copy(panel, lastPanel, 256);
						Array.Copy(sound, lastSound, 256);
					}

					Console.WriteLine($"  end: v={speed * 3.6:0.0} km/h, brake B{brake}, tripped {tripped}");
				}
				finally
				{
					plugin.Dispose();
				}
			}
		}
		private static void Pair(string root, string dll, int first, int second, double gap, int signal, int optional, double kmh)
		{
			Console.WriteLine($"=== beacons {first} then {second} after {gap} s, signal {signal}, optional {optional}, {kmh} km/h ===");
			AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(1252), _ => { });
			try
			{
				plugin.Load();
				plugin.SetVehicleSpec(8, 4, 1, 6, 12);
				plugin.Initialize(0);
				plugin.SetReverser(1);
				plugin.SetPower(0);
				plugin.SetBrake(0);
				plugin.SetSignal(signal);
				int[] panel = new int[256], sound = new int[256], lastPanel = new int[256];
				int lastBrake = 0;
				double location = 0, speed = kmh / 3.6;
				int secondFrame = 120 + (int)(gap * 60);
				for (int frame = 0; frame < 60 * 10; frame++)
				{
					if (frame == 120)
					{
						plugin.SetBeaconData(first, signal, 150.0f, optional);
					}

					if (frame == secondFrame)
					{
						plugin.SetBeaconData(second, signal, 150.0f, optional);
					}

					AtsVehicleState state = new AtsVehicleState
					{
						Location = location, Speed = (float)(speed * 3.6), Time = (int)(36000000 + frame * 1000 / 60),
						MrPressure = 800000, ErPressure = 490000, BpPressure = 490000, SapPressure = 800000
					};
					AtsHandles handles = plugin.Elapse(state, panel, sound);
					if (handles.Brake > 0)
					{
						speed = Math.Max(0, speed - 1.2 / 60);
					}

					location += speed / 60;
					string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != lastPanel[i]).Select(i => i + "=" + panel[i]));
					if ((changed.Length > 0 || handles.Brake != lastBrake) && frame > 0)
					{
						Console.WriteLine($"  t={frame / 60.0:0.00}s v={speed * 3.6:0.0} B{handles.Brake} | panel {changed}");
					}

					lastBrake = handles.Brake;
					Array.Copy(panel, lastPanel, 256);
				}

				Console.WriteLine($"  end: v={speed * 3.6:0.0} km/h");
			}
			finally
			{
				plugin.Dispose();
			}
		}
	}
}
