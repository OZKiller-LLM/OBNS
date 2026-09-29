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

namespace OpenBve.X86
{
	/// <summary>The handle positions a plugin's Elapse returns.</summary>
	public struct AtsHandles
	{
		public int Brake;
		public int Power;
		public int Reverser;
		public int ConstantSpeed;
	}

	/// <summary>The vehicle state passed to a plugin's Elapse (ATS_VEHICLESTATE).</summary>
	public struct AtsVehicleState
	{
		public double Location;
		public float Speed;
		public int Time;
		public float BcPressure;
		public float MrPressure;
		public float ErPressure;
		public float BpPressure;
		public float SapPressure;
		public float Current;
	}

	/// <summary>
	/// A 32-bit Windows BVE ATS plugin, running in the emulator: its exports called with exactly
	/// the arguments OpenBVE's Windows proxy (AtsPluginProxy) passes.
	/// </summary>
	/// <remarks>
	/// Every export is __stdcall. SetVehicleSpec, SetBeaconData and Elapse take their structures
	/// by value on the stack; Elapse returns its 16-byte ATS_HANDLES through a hidden pointer
	/// passed first. The panel and sound arrays live in emulated memory and are copied across
	/// each frame.
	/// </remarks>
	public sealed class AtsPlugin
	{
		private const int ArraySize = 256;

		public Win32Process Process { get; }
		public LoadedModule Module { get; }

		private readonly uint load, dispose, getPluginVersion, setVehicleSpec, initialize, elapse;
		private readonly uint setPower, setBrake, setReverser, keyDown, keyUp, hornBlow, doorOpen, doorClose, setSignal, setBeaconData;
		private readonly uint panelAddress, soundAddress, handlesAddress;

		/// <summary>
		/// When set, every call into the plugin and everything it returns is written here, for
		/// <see cref="AtsTrace.Replay"/> to run again elsewhere and compare.
		/// </summary>
		public AtsTrace.Writer Trace { get; set; }

		private AtsPlugin(Win32Process process, LoadedModule module)
		{
			Process = process;
			Module = module;
			load = module.Export("Load");
			dispose = module.Export("Dispose");
			getPluginVersion = module.Export("GetPluginVersion");
			setVehicleSpec = module.Export("SetVehicleSpec");
			initialize = module.Export("Initialize");
			elapse = module.Export("Elapse");
			setPower = module.Export("SetPower");
			setBrake = module.Export("SetBrake");
			setReverser = module.Export("SetReverser");
			keyDown = module.Export("KeyDown");
			keyUp = module.Export("KeyUp");
			hornBlow = module.Export("HornBlow");
			doorOpen = module.Export("DoorOpen");
			doorClose = module.Export("DoorClose");
			setSignal = module.Export("SetSignal");
			setBeaconData = module.Export("SetBeaconData");
			panelAddress = process.HeapAllocate(4 * ArraySize);
			soundAddress = process.HeapAllocate(4 * ArraySize);
			handlesAddress = process.HeapAllocate(16);
		}

		/// <summary>
		/// Loads a plugin DLL into a new emulated process whose C: drive is <paramref name="rootFolder"/>.
		/// The DLL's startup code runs here; anything it cannot do is raised as <see cref="X86Exception"/>.
		/// </summary>
		public static AtsPlugin Open(string rootFolder, string dllPath, Encoding ansiEncoding, Action<string> log, Action<Win32Process> beforeLoad = null)
		{
			Win32Process process = new Win32Process(rootFolder)
			{
				Log = log ?? (_ => { }),
				AnsiEncoding = ansiEncoding ?? Encoding.Latin1
			};
			beforeLoad?.Invoke(process);
			string windows = process.ToWindowsPath(dllPath);
			int slash = windows.LastIndexOf('\\');
			process.CurrentDirectoryRelative = windows.Substring(3, Math.Max(0, slash - 3));
			LoadedModule module = process.LoadLibrary(dllPath);
			return new AtsPlugin(process, module);
		}

		private Cpu Cpu => Process.Cpu;

		public void Load()
		{
			Trace?.Op(AtsTrace.OpLoad);
			if (load != 0) Cpu.Call(load);
		}

		public void Dispose()
		{
			if (dispose != 0) Cpu.Call(dispose);
		}

		public int GetPluginVersion()
		{
			return getPluginVersion != 0 ? (int)(uint)Cpu.Call(getPluginVersion) : 0;
		}

		public void SetVehicleSpec(int brakeNotches, int powerNotches, int atsNotch, int b67Notch, int cars)
		{
			Trace?.Op(AtsTrace.OpSetVehicleSpec, brakeNotches, powerNotches, atsNotch, b67Notch, cars);
			if (setVehicleSpec == 0) return;
			// struct ATS_VEHICLESPEC { int BrakeNotches, PowerNotches, AtsNotch, B67Notch, Cars; } by value.
			Cpu.Call(setVehicleSpec, (uint)brakeNotches, (uint)powerNotches, (uint)atsNotch, (uint)b67Notch, (uint)cars);
		}

		public void Initialize(int brake)
		{
			Trace?.Op(AtsTrace.OpInitialize, brake);
			if (initialize != 0) Cpu.Call(initialize, (uint)brake);
		}

		/// <summary>Runs one frame of the plugin. The panel and sound arrays are read and written in place.</summary>
		public AtsHandles Elapse(AtsVehicleState state, int[] panel, int[] sound)
		{
			Trace?.ElapseInput(state, panel, sound);
			Memory memory = Process.Memory;
			for (int i = 0; i < ArraySize; i++)
			{
				memory.Write32(panelAddress + (uint)(4 * i), (uint)panel[i]);
				memory.Write32(soundAddress + (uint)(4 * i), (uint)sound[i]);
			}

			AtsHandles result = default;
			if (elapse != 0)
			{
				// Arguments right to left: sound, panel, the 40-byte state by value, then the hidden return pointer.
				Cpu.Push(soundAddress);
				Cpu.Push(panelAddress);
				Cpu.R[Cpu.ESP] -= 40;
				uint s = Cpu.R[Cpu.ESP];
				memory.WriteDouble(s, state.Location);
				memory.WriteSingle(s + 8, state.Speed);
				memory.Write32(s + 12, (uint)state.Time);
				memory.WriteSingle(s + 16, state.BcPressure);
				memory.WriteSingle(s + 20, state.MrPressure);
				memory.WriteSingle(s + 24, state.ErPressure);
				memory.WriteSingle(s + 28, state.BpPressure);
				memory.WriteSingle(s + 32, state.SapPressure);
				memory.WriteSingle(s + 36, state.Current);
				Cpu.Push(handlesAddress);
				uint stackBefore = Cpu.R[Cpu.ESP] + 4 + 40 + 8;
				Cpu.CallPrepared(elapse);
				// Whatever the callee popped, the caller's frame ends where it began.
				Cpu.R[Cpu.ESP] = stackBefore;
				result.Brake = (int)memory.Read32(handlesAddress);
				result.Power = (int)memory.Read32(handlesAddress + 4);
				result.Reverser = (int)memory.Read32(handlesAddress + 8);
				result.ConstantSpeed = (int)memory.Read32(handlesAddress + 12);
			}

			for (int i = 0; i < ArraySize; i++)
			{
				panel[i] = (int)memory.Read32(panelAddress + (uint)(4 * i));
				sound[i] = (int)memory.Read32(soundAddress + (uint)(4 * i));
			}

			Trace?.ElapseOutput(result, panel, sound);
			return result;
		}

		private void CallInt(uint function, int value)
		{
			if (function != 0)
			{
				uint esp = Cpu.R[Cpu.ESP];
				Cpu.Call(function, (uint)value);
				Cpu.R[Cpu.ESP] = esp;
			}
		}

		private void CallVoid(uint function)
		{
			if (function != 0)
			{
				uint esp = Cpu.R[Cpu.ESP];
				Cpu.Call(function);
				Cpu.R[Cpu.ESP] = esp;
			}
		}

		public void SetPower(int notch) { Trace?.Op(AtsTrace.OpSetPower, notch); CallInt(setPower, notch); }
		public void SetBrake(int notch) { Trace?.Op(AtsTrace.OpSetBrake, notch); CallInt(setBrake, notch); }
		public void SetReverser(int position) { Trace?.Op(AtsTrace.OpSetReverser, position); CallInt(setReverser, position); }
		public void KeyDown(int key) { Trace?.Op(AtsTrace.OpKeyDown, key); CallInt(keyDown, key); }
		public void KeyUp(int key) { Trace?.Op(AtsTrace.OpKeyUp, key); CallInt(keyUp, key); }
		public void HornBlow(int type) { Trace?.Op(AtsTrace.OpHornBlow, type); CallInt(hornBlow, type); }
		public void DoorOpen() { Trace?.Op(AtsTrace.OpDoorOpen); CallVoid(doorOpen); }
		public void DoorClose() { Trace?.Op(AtsTrace.OpDoorClose); CallVoid(doorClose); }
		public void SetSignal(int aspect) { Trace?.Op(AtsTrace.OpSetSignal, aspect); CallInt(setSignal, aspect); }

		public void SetBeaconData(int type, int signal, float distance, int optional)
		{
			Trace?.Op(AtsTrace.OpSetBeaconData, type, signal, BitConverter.SingleToInt32Bits(distance), optional);
			if (setBeaconData == 0) return;
			uint esp = Cpu.R[Cpu.ESP];
			// struct ATS_BEACONDATA { int Type; int Signal; float Distance; int Optional; } by value.
			Cpu.Call(setBeaconData, (uint)type, (uint)signal, (uint)BitConverter.SingleToInt32Bits(distance), (uint)optional);
			Cpu.R[Cpu.ESP] = esp;
		}
	}
}
