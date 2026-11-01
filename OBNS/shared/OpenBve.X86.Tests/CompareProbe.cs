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
using System.Runtime.InteropServices;
using System.Text;

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// Runs one drive through two copies of a plugin and compares every frame's handles, panel and sounds. A copy is a DLL in
	/// the emulator (x86 or x64), or "native:" and a 64-bit DLL, loaded for real into this 64-bit process on Windows: the
	/// reference for what the x64 layer should do. Use native only with a plugin you would run on this PC anyway.
	/// </summary>
	/// <remarks>Usage: OpenBve.X86.Tests &lt;root&gt; &lt;dll&gt; compare &lt;other dll, or native:dll&gt; [frames] [beacon list file]</remarks>
	internal static class CompareProbe
	{
		private interface IRunner
		{
			string Name { get; }
			void Start();
			void Spec(int brake, int power, int ats, int b67, int cars);
			void Init(int mode);
			void Key(int key, bool down);
			void Lever(int kind, int value);
			void Door(bool open);
			void Horn(int type);
			void Signal(int aspect);
			void Beacon(int type, int signal, float distance, int optional);
			AtsHandles Elapse(AtsVehicleState state, int[] panel, int[] sound);
		}

		private sealed class Emulated : IRunner
		{
			private readonly AtsPlugin plugin;
			public string Name { get; }

			internal Emulated(string root, string dll, Encoding encoding)
			{
				Name = (AtsPlugin.IsX64(dll) ? "x64 layer " : "x86 layer ") + System.IO.Path.GetFileName(dll);
				plugin = AtsPlugin.Open(root, dll, encoding, message =>
				{
					if (message.Contains("caught") || message.Contains("not provided") || message.Contains("plugin:"))
					{
						Console.WriteLine("  [" + Name + "] " + message);
					}
				});
			}

			public void Start() { plugin.Load(); Console.WriteLine("  [" + Name + "] version 0x" + plugin.GetPluginVersion().ToString("X")); }
			public void Spec(int brake, int power, int ats, int b67, int cars) => plugin.SetVehicleSpec(brake, power, ats, b67, cars);
			public void Init(int mode) => plugin.Initialize(mode);
			public void Key(int key, bool down) { if (down) plugin.KeyDown(key); else plugin.KeyUp(key); }
			public void Lever(int kind, int value) { if (kind == 0) plugin.SetPower(value); else if (kind == 1) plugin.SetBrake(value); else plugin.SetReverser(value); }
			public void Door(bool open) { if (open) plugin.DoorOpen(); else plugin.DoorClose(); }
			public void Horn(int type) => plugin.HornBlow(type);
			public void Signal(int aspect) => plugin.SetSignal(aspect);
			public void Beacon(int type, int signal, float distance, int optional) => plugin.SetBeaconData(type, signal, distance, optional);
			public AtsHandles Elapse(AtsVehicleState state, int[] panel, int[] sound) => plugin.Elapse(state, panel, sound);
		}

		[StructLayout(LayoutKind.Sequential, Size = 20)] private struct Spec64 { public int A, B, C, D, E; }
		[StructLayout(LayoutKind.Sequential, Size = 16)] private struct Beacon64 { public int Type, Signal; public float Distance; public int Optional; }
		[StructLayout(LayoutKind.Sequential, Size = 40)] private struct State64 { public double Location; public float Speed; public int Time; public float Bc, Mr, Er, Bp, Sap, Current; }
		[StructLayout(LayoutKind.Sequential, Size = 16)] private struct Handles64 { public int Brake, Power, Reverser, ConstantSpeed; }

		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VoidCall();
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VersionCall();
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void IntCall(int value);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SpecCall(Spec64 spec);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeaconCall(Beacon64 beacon);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate Handles64 ElapseCall(State64 state, IntPtr panel, IntPtr sound);

		private sealed class Native : IRunner
		{
			[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
			[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

			private readonly IntPtr module;
			private readonly int[] nativePanel = new int[256], nativeSound = new int[256];
			private GCHandle panelHandle, soundHandle;
			public string Name { get; }

			internal Native(string dll)
			{
				Name = "native " + System.IO.Path.GetFileName(dll);
				module = LoadLibraryEx(dll, IntPtr.Zero, 8);
				if (module == IntPtr.Zero)
				{
					throw new InvalidOperationException("LoadLibraryEx failed: " + Marshal.GetLastWin32Error());
				}

				panelHandle = GCHandle.Alloc(nativePanel, GCHandleType.Pinned);
				soundHandle = GCHandle.Alloc(nativeSound, GCHandleType.Pinned);
			}

			private T Get<T>(string name) where T : class
			{
				IntPtr address = GetProcAddress(module, name);
				return address == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
			}

			public void Start() { Get<VoidCall>("Load")?.Invoke(); Console.WriteLine("  [" + Name + "] version 0x" + (Get<VersionCall>("GetPluginVersion")?.Invoke() ?? 0).ToString("X")); }
			public void Spec(int brake, int power, int ats, int b67, int cars) => Get<SpecCall>("SetVehicleSpec")?.Invoke(new Spec64 { A = brake, B = power, C = ats, D = b67, E = cars });
			public void Init(int mode) => Get<IntCall>("Initialize")?.Invoke(mode);
			public void Key(int key, bool down) => Get<IntCall>(down ? "KeyDown" : "KeyUp")?.Invoke(key);
			public void Lever(int kind, int value) => Get<IntCall>(kind == 0 ? "SetPower" : kind == 1 ? "SetBrake" : "SetReverser")?.Invoke(value);
			public void Door(bool open) => Get<VoidCall>(open ? "DoorOpen" : "DoorClose")?.Invoke();
			public void Horn(int type) => Get<IntCall>("HornBlow")?.Invoke(type);
			public void Signal(int aspect) => Get<IntCall>("SetSignal")?.Invoke(aspect);
			public void Beacon(int type, int signal, float distance, int optional) => Get<BeaconCall>("SetBeaconData")?.Invoke(new Beacon64 { Type = type, Signal = signal, Distance = distance, Optional = optional });

			public AtsHandles Elapse(AtsVehicleState s, int[] panel, int[] sound)
			{
				// As the game does: the same arrays each frame. Values the host changed go in, and the plugin's come out.
				Array.Copy(panel, nativePanel, 256);
				Array.Copy(sound, nativeSound, 256);
				Handles64 h = Get<ElapseCall>("Elapse").Invoke(new State64 { Location = s.Location, Speed = s.Speed, Time = s.Time, Bc = s.BcPressure, Mr = s.MrPressure, Er = s.ErPressure, Bp = s.BpPressure, Sap = s.SapPressure, Current = s.Current }, panelHandle.AddrOfPinnedObject(), soundHandle.AddrOfPinnedObject());
				Array.Copy(nativePanel, panel, 256);
				Array.Copy(nativeSound, sound, 256);
				return new AtsHandles { Brake = h.Brake, Power = h.Power, Reverser = h.Reverser, ConstantSpeed = h.ConstantSpeed };
			}
		}

		private static IRunner Open(string root, string spec, Encoding encoding)
		{
			return spec.StartsWith("native:", StringComparison.Ordinal) ? new Native(spec.Substring(7)) : new Emulated(root, spec, encoding);
		}

		internal static int Run(string root, string first, string second, int frames, Encoding encoding)
		{
			IRunner a = Open(root, first, encoding), b = Open(root, second, encoding);
			IRunner[] both = { a, b };
			foreach (IRunner r in both)
			{
				r.Start();
				r.Spec(8, 4, 1, 6, 10);
				r.Init(0);
				r.Lever(2, 1);
				r.Lever(0, 0);
				r.Lever(1, 8);
			}

			int[][] panels = { new int[256], new int[256] }, sounds = { new int[256], new int[256] };
			double location = 0, speed = 0;
			int power = 0, brake = 8, mismatches = 0, panelChanges = 0, soundChanges = 0;
			int[] lastPanel = new int[256];
			HashSet<int> keysPressed = new HashSet<int>();
			Random random = new Random(217);
			for (int frame = 0; frame < frames; frame++)
			{
				const double step = 1.0 / 30.0;
				int second1 = frame / 30;
				// The same scripted drive for both: levers, keys, doors, horns, signals and beacons at fixed frames.
				List<Action<IRunner>> events = new List<Action<IRunner>>();
				if (frame == 30) { brake = 0; events.Add(r => r.Lever(1, 0)); }
				if (frame % 300 == 60) { power = 4; events.Add(r => r.Lever(0, 4)); }
				if (frame % 300 == 200) { power = 0; events.Add(r => r.Lever(0, 0)); }
				if (frame % 300 == 230) { brake = 4; events.Add(r => r.Lever(1, 4)); }
				if (frame % 300 == 290) { brake = 0; events.Add(r => r.Lever(1, 0)); }
				if (frame % 900 == 299) { events.Add(r => r.Door(true)); }
				if (frame % 900 == 359) { events.Add(r => r.Door(false)); }
				if (frame % 450 == 100) { int horn = random.Next(3); events.Add(r => r.Horn(horn)); }
				if (frame % 180 == 40) { int aspect = random.Next(6); events.Add(r => r.Signal(aspect)); }
				if (frame == 10) { events.Add(r => r.Beacon(1000, 0, 10f, 379304)); } // the IC card, on the TIMS trains: Uchibo's 3793F
				if (frame % 240 == 20)
				{
					int type = BeaconTypes[random.Next(BeaconTypes.Length)], signal = random.Next(6), optional = random.Next(120);
					float distance = random.Next(1000);
					events.Add(r => r.Beacon(type, signal, distance, optional));
				}

				if (frame % 60 == 15)
				{
					int key = random.Next(16);
					keysPressed.Add(key);
					events.Add(r => r.Key(key, true));
				}

				if (frame % 60 == 25 && keysPressed.Count > 0)
				{
					List<int> held = new List<int>(keysPressed);
					keysPressed.Clear();
					events.Add(r => { foreach (int key in held) r.Key(key, false); });
				}

				AtsVehicleState state = new AtsVehicleState
				{
					Location = location,
					Speed = (float)speed,
					Time = (int)(36000 * 1000 + frame * 1000.0 / 30.0),
					BcPressure = brake * 50f,
					MrPressure = 800f,
					ErPressure = 490f,
					BpPressure = 490f - brake * 10f,
					SapPressure = 780f,
					Current = power * 100f
				};
				AtsHandles[] handles = new AtsHandles[2];
				for (int i = 0; i < 2; i++)
				{
					foreach (Action<IRunner> e in events)
					{
						e(both[i]);
					}

					handles[i] = both[i].Elapse(state, panels[i], sounds[i]);
				}

				string difference = null;
				if (handles[0].Brake != handles[1].Brake || handles[0].Power != handles[1].Power || handles[0].Reverser != handles[1].Reverser || handles[0].ConstantSpeed != handles[1].ConstantSpeed)
				{
					difference = "handles B" + handles[0].Brake + " P" + handles[0].Power + " R" + handles[0].Reverser + " C" + handles[0].ConstantSpeed + " against B" + handles[1].Brake + " P" + handles[1].Power + " R" + handles[1].Reverser + " C" + handles[1].ConstantSpeed;
				}

				for (int i = 0; i < 256 && difference == null; i++)
				{
					if (panels[0][i] != panels[1][i]) difference = "panel[" + i + "] " + panels[0][i] + " against " + panels[1][i];
					else if (sounds[0][i] != sounds[1][i]) difference = "sound[" + i + "] " + sounds[0][i] + " against " + sounds[1][i];
				}

				if (difference != null && mismatches++ < 8)
				{
					Console.WriteLine("  frame " + frame + " (" + second1 + " s): " + difference);
				}

				for (int i = 0; i < 256; i++)
				{
					if (panels[0][i] != lastPanel[i])
					{
						panelChanges++;
						lastPanel[i] = panels[0][i];
					}

					// A sound instruction is acted on and cleared by the host each frame, in both copies alike.
					if (sounds[0][i] != 0) soundChanges++;
					sounds[0][i] = sounds[1][i] = 0;
				}

				speed = Math.Max(0, speed + (power * 0.7 - brake * 0.9 - 0.02) * step);
				location += speed / 3.6 * step;
			}

			Console.WriteLine("  " + a.Name + " against " + b.Name + ": " + frames + " frames, " + panelChanges + " panel changes, " + soundChanges + " sound instructions, " + (mismatches == 0 ? "NO differences" : mismatches + " frames differ"));
			return mismatches == 0 ? 0 : 1;
		}

		// ATS-Sx/P, the JR ATC group and the like, and types a plugin would ignore.
		private static readonly int[] BeaconTypes = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 25, 30, 44, 100, 1001, 1002 };
	}
}
