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
	/// Replays a recording made on the phone (plugin-trace.bin) against the same DLL here, reports
	/// any frame whose outputs differ, and prints a once-a-second summary of what the plugin did.
	/// </summary>
	internal static class ReplayProbe
	{
		internal static void Run(string root, string dll, string traceFile, int stopFrame = int.MaxValue, string[] script = null)
		{
			using FileStream stream = File.OpenRead(traceFile);
			int codePage = AtsTrace.ReadCodePage(stream);
			AtsPlugin plugin = AtsPlugin.Open(root, dll, Encoding.GetEncoding(codePage), _ => { });
			int[] last = new int[256];
			int lastBrake = -1, lastPower = -1;
			string events = string.Empty;
			AtsVehicleState lastState = default;
			int[] lastPanel = new int[256];
			bool quiet = script != null;
			AtsTrace.Replay(plugin, stream, Console.WriteLine, (frame, state, handles, panel, frameEvents) =>
			{
				lastState = state;
				Array.Copy(panel, lastPanel, 256);
				if (quiet) return;
				events += frameEvents;
				bool handlesChanged = handles.Brake != lastBrake || handles.Power != lastPower;
				if (frame % 60 != 0 && !handlesChanged && frameEvents.Length == 0)
				{
					return;
				}

				string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panel[i] != last[i]).Select(i => i + "=" + panel[i]));
				Array.Copy(panel, last, 256);
				TimeSpan clock = TimeSpan.FromMilliseconds(state.Time);
				Console.WriteLine($"{clock:hh\\:mm\\:ss} {state.Location:0}m {state.Speed:0}km/h -> B{handles.Brake} P{handles.Power}{events} | {changed}");
				events = string.Empty;
				lastBrake = handles.Brake;
				lastPower = handles.Power;
			}, stopFrame);

			if (script == null) return;
			// Carry on from the recorded state with scripted actions.
			int[] panelNow = lastPanel, sound = new int[256], shown = (int[])lastPanel.Clone();
			string[] keys = { "S", "A1", "A2", "B1", "B2", "C1", "C2", "D", "E", "F", "G", "H", "I", "J", "K", "L" };
			AtsVehicleState st = lastState;
			AtsHandles h = default;
			void Frames(int n)
			{
				for (int i = 0; i < n; i++)
				{
					st.Time += 16;
					h = plugin.Elapse(st, panelNow, sound);
				}
			}

			foreach (string step in script)
			{
				string[] p = step.Split(':');
				switch (p[0])
				{
					case "down": plugin.KeyDown(Array.IndexOf(keys, p[1])); break;
					case "up": plugin.KeyUp(Array.IndexOf(keys, p[1])); break;
					case "key": { int k = Array.IndexOf(keys, p[1]); plugin.KeyDown(k); Frames(int.Parse(p.Length > 2 ? p[2] : "5")); plugin.KeyUp(k); break; }
					case "power": plugin.SetPower(int.Parse(p[1])); break;
					case "brake": plugin.SetBrake(int.Parse(p[1])); break;
					case "rev": plugin.SetReverser(int.Parse(p[1])); break;
					case "open": plugin.DoorOpen(); break;
					case "close": plugin.DoorClose(); break;
					case "horn": plugin.HornBlow(int.Parse(p[1])); break;
					case "wait": Frames(int.Parse(p[1]) * 60); break;
				}

				Frames(30);
				string changed = string.Join(" ", Enumerable.Range(0, 256).Where(i => panelNow[i] != shown[i]).Select(i => i + "=" + panelNow[i]));
				Array.Copy(panelNow, shown, 256);
				Console.WriteLine($"{step,-12} -> B{h.Brake} P{h.Power} R{h.Reverser} | {changed}");
			}
		}
	}
}
