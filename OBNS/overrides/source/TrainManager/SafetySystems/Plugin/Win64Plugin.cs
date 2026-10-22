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

// OBNS: a 64-bit ATS plugin (BVE5's Ats64, BVE6's) loaded into the 64-bit desktop program, as BVE6 loads them.
// Upstream loads 32-bit plugins only, through its 32-bit proxy; a train with only a 64-bit plugin had none.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Runtime;
using TrainManager.Trains;

namespace TrainManager.SafetySystems
{
	/// <summary>
	/// A native 64-bit Win32 ATS plugin, called directly: the same interface (functions and structures) as the 32-bit
	/// plugins upstream reaches through AtsPluginProxy.dll, which passes the structures as pointers; on x64 they are
	/// passed by value as the plugin expects. The rest - handles, sounds, constant speed - follows upstream's
	/// Win32Plugin. The plugin runs in the program's process, so a plugin that crashes takes the program with it.
	/// </summary>
	internal class Win64Plugin : Plugin
	{
		// --- the plugin's functions ---

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);

		[DllImport("kernel32.dll")]
		private static extern bool FreeLibrary(IntPtr module);

		/// <summary>LoadLibraryEx: the plugin's own folder is searched for the libraries it needs.</summary>
		private const uint LoadWithAlteredSearchPath = 0x00000008;

		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VoidFunction();
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntResultFunction();
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void IntFunction(int value);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void SpecFunction(Win32VehicleSpec spec);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate Win32Handles ElapseFunction(Win32VehicleState state, IntPtr panel, IntPtr sound);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void BeaconFunction(Win32BeaconData beacon);

		private VoidFunction load, dispose, doorOpen, doorClose;
		private IntResultFunction getPluginVersion;
		private IntFunction initialize, setPower, setBrake, setReverser, keyDown, keyUp, hornBlow, setSignal;
		private SpecFunction setVehicleSpec;
		private ElapseFunction elapse;
		private BeaconFunction setBeaconData;

		[StructLayout(LayoutKind.Sequential, Size = 20)]
		private struct Win32VehicleSpec
		{
			internal int BrakeNotches;
			internal int PowerNotches;
			internal int AtsNotch;
			internal int B67Notch;
			internal int Cars;
		}

		[StructLayout(LayoutKind.Sequential, Size = 40)]
		private struct Win32VehicleState
		{
			internal double Location;
			internal float Speed;
			internal int Time;
			internal float BcPressure;
			internal float MrPressure;
			internal float ErPressure;
			internal float BpPressure;
			internal float SapPressure;
			internal float Current;
		}

		[StructLayout(LayoutKind.Sequential, Size = 16)]
		private struct Win32Handles
		{
			internal int Brake;
			internal int Power;
			internal int Reverser;
			internal int ConstantSpeed;
		}

		[StructLayout(LayoutKind.Sequential, Size = 16)]
		private struct Win32BeaconData
		{
			internal int Type;
			internal int Signal;
			internal float Distance;
			internal int Optional;
		}

		private static class ConstSpeedInstructions
		{
			internal const int Continue = 0;
			internal const int Enable = 1;
			internal const int Disable = 2;
		}

		// --- members ---
		private readonly string PluginFile;
		private readonly int[] LastSound;
		private GCHandle PanelHandle;
		private GCHandle SoundHandle;
		private IntPtr module;

		internal Win64Plugin(string pluginFile, TrainBase train)
		{
			PluginTitle = System.IO.Path.GetFileName(pluginFile);
			PluginValid = true;
			PluginMessage = null;
			Train = train;
			Panel = new int[256];
			SupportsAI = AISupport.None;
			LastTime = 0.0;
			LastReverser = -2;
			LastPowerNotch = -1;
			LastBrakeNotch = -1;
			LastAspects = new int[] { };
			LastSection = -1;
			LastException = null;
			PluginFile = pluginFile;
			Sound = new int[256];
			LastSound = new int[256];
			PanelHandle = new GCHandle();
			SoundHandle = new GCHandle();
		}

		/// <summary>Whether a file is a 64-bit (x64) Windows library.</summary>
		internal static bool CheckHeader(string file)
		{
			using (System.IO.FileStream stream = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read))
			{
				using (System.IO.BinaryReader reader = new System.IO.BinaryReader(stream))
				{
					if (reader.ReadUInt16() != 0x5A4D)
					{
						return false;
					}

					stream.Position = 0x3C;
					stream.Position = reader.ReadInt32();
					return reader.ReadUInt32() == 0x00004550 && reader.ReadUInt16() == 0x8664;
				}
			}
		}

		private T Function<T>(string name, bool required = true) where T : class
		{
			IntPtr address = GetProcAddress(module, name);
			if (address == IntPtr.Zero)
			{
				if (required)
				{
					throw new EntryPointNotFoundException(PluginTitle + " does not export " + name);
				}

				return null;
			}

			return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
		}

		public override bool Load(VehicleSpecs specs, InitializationModes mode)
		{
			module = LoadLibraryEx(PluginFile, IntPtr.Zero, LoadWithAlteredSearchPath);
			if (module == IntPtr.Zero)
			{
				int errorCode = Marshal.GetLastWin32Error();
				TrainManagerBase.currentHost.AddMessage(MessageType.Error, true, $"Error loading 64-bit plugin: {new Win32Exception(errorCode).Message} (0x{errorCode:x})");
				return false;
			}

			try
			{
				load = Function<VoidFunction>("Load");
				dispose = Function<VoidFunction>("Dispose");
				getPluginVersion = Function<IntResultFunction>("GetPluginVersion");
				setVehicleSpec = Function<SpecFunction>("SetVehicleSpec");
				initialize = Function<IntFunction>("Initialize");
				elapse = Function<ElapseFunction>("Elapse");
				setPower = Function<IntFunction>("SetPower");
				setBrake = Function<IntFunction>("SetBrake");
				setReverser = Function<IntFunction>("SetReverser");
				keyDown = Function<IntFunction>("KeyDown");
				keyUp = Function<IntFunction>("KeyUp");
				hornBlow = Function<IntFunction>("HornBlow");
				doorOpen = Function<VoidFunction>("DoorOpen");
				doorClose = Function<VoidFunction>("DoorClose");
				setSignal = Function<IntFunction>("SetSignal");
				setBeaconData = Function<BeaconFunction>("SetBeaconData");
			}
			catch (EntryPointNotFoundException ex)
			{
				TrainManagerBase.currentHost.AddMessage(MessageType.Error, false, "The train plugin " + PluginTitle + " is not an ATS plugin: " + ex.Message);
				FreeLibrary(module);
				module = IntPtr.Zero;
				return false;
			}

			try
			{
				load();
				int version = getPluginVersion();
				if (version != 131072)
				{
					TrainManagerBase.currentHost.AddMessage(MessageType.Error, false, "The train plugin " + PluginTitle + " is of an unsupported version.");
					dispose();
					FreeLibrary(module);
					module = IntPtr.Zero;
					return false;
				}

				Win32VehicleSpec win32Spec;
				win32Spec.BrakeNotches = specs.BrakeNotches;
				win32Spec.PowerNotches = specs.PowerNotches;
				win32Spec.AtsNotch = specs.AtsNotch;
				win32Spec.B67Notch = specs.B67Notch;
				win32Spec.Cars = specs.Cars;
				setVehicleSpec(win32Spec);
				initialize((int) mode);
			}
			catch (Exception ex)
			{
				LastException = ex;
				throw;
			}

			UpdatePower();
			UpdateBrake();
			UpdateReverser();
			if (PanelHandle.IsAllocated)
			{
				PanelHandle.Free();
			}

			if (SoundHandle.IsAllocated)
			{
				SoundHandle.Free();
			}

			PanelHandle = GCHandle.Alloc(Panel, GCHandleType.Pinned);
			SoundHandle = GCHandle.Alloc(Sound, GCHandleType.Pinned);
			return true;
		}

		public override void Unload()
		{
			if (PanelHandle.IsAllocated)
			{
				PanelHandle.Free();
			}

			if (SoundHandle.IsAllocated)
			{
				SoundHandle.Free();
			}

			if (module == IntPtr.Zero)
			{
				return;
			}

			try
			{
				dispose?.Invoke();
			}
			catch (Exception ex)
			{
				LastException = ex;
				throw;
			}
			finally
			{
				FreeLibrary(module);
				module = IntPtr.Zero;
			}
		}

		public override void BeginJump(InitializationModes mode)
		{
			Call(() => initialize((int) mode));
		}

		public override void EndJump()
		{
		}

		/// <summary>Calls the plugin, keeping what it throws as upstream's Win32Plugin does.</summary>
		private void Call(Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				LastException = ex;
				throw;
			}
		}

		protected override void Elapse(ref ElapseData data)
		{
			try
			{
				double time = data.TotalTime.Milliseconds;
				Win32VehicleState win32State;
				win32State.Location = data.Vehicle.Location;
				win32State.Speed = (float) data.Vehicle.Speed.KilometersPerHour;
				win32State.Time = (int) Math.Floor(time - 2073600000.0 * Math.Floor(time / 2073600000.0));
				win32State.BcPressure = (float) data.Vehicle.BcPressure;
				win32State.MrPressure = (float) data.Vehicle.MrPressure;
				win32State.ErPressure = (float) data.Vehicle.ErPressure;
				win32State.BpPressure = (float) data.Vehicle.BpPressure;
				win32State.SapPressure = (float) data.Vehicle.SapPressure;
				win32State.Current = 0.0f;
				Win32Handles win32Handles = elapse(win32State, PanelHandle.AddrOfPinnedObject(), SoundHandle.AddrOfPinnedObject());
				data.Handles.Reverser = win32Handles.Reverser;
				data.Handles.PowerNotch = win32Handles.Power;
				data.Handles.BrakeNotch = win32Handles.Brake;
				if (win32Handles.ConstantSpeed == ConstSpeedInstructions.Enable)
				{
					data.Handles.ConstSpeed = true;
				}
				else if (win32Handles.ConstantSpeed == ConstSpeedInstructions.Disable)
				{
					data.Handles.ConstSpeed = false;
				}
				else if (win32Handles.ConstantSpeed != ConstSpeedInstructions.Continue)
				{
					PluginValid = false;
				}

				// The sound instructions, as upstream's Win32Plugin reads them.
				for (int i = 0; i < Sound.Length; i++)
				{
					if (Sound[i] != LastSound[i])
					{
						if (Sound[i] == SoundInstructions.Stop)
						{
							if (Train.Cars[Train.DriverCar].Sounds.Plugin.ContainsKey(i))
							{
								Train.Cars[Train.DriverCar].Sounds.Plugin[i].Stop();
							}
						}
						else if (Sound[i] > SoundInstructions.Stop & Sound[i] <= SoundInstructions.PlayLooping)
						{
							if (Train.Cars[Train.DriverCar].Sounds.Plugin.ContainsKey(i) && Train.Cars[Train.DriverCar].Sounds.Plugin[i].Buffer != null)
							{
								double volume = (Sound[i] - SoundInstructions.Stop) / (double)(SoundInstructions.PlayLooping - SoundInstructions.Stop);
								if (Train.Cars[Train.DriverCar].Sounds.Plugin[i].IsPlaying)
								{
									Train.Cars[Train.DriverCar].Sounds.Plugin[i].Source.Volume = volume;
								}
								else
								{
									Train.Cars[Train.DriverCar].Sounds.Plugin[i].Play(1.0, volume, Train.Cars[Train.DriverCar], true);
								}
							}
						}
						else if (Sound[i] == SoundInstructions.PlayOnce)
						{
							if (Train.Cars[Train.DriverCar].Sounds.Plugin.ContainsKey(i) && Train.Cars[Train.DriverCar].Sounds.Plugin[i].Buffer != null)
							{
								Train.Cars[Train.DriverCar].Sounds.Plugin[i].Play(Train.Cars[Train.DriverCar], false);
							}

							Sound[i] = SoundInstructions.Continue;
						}
						else if (Sound[i] != SoundInstructions.Continue)
						{
							PluginValid = false;
						}

						LastSound[i] = Sound[i];
					}
					else if ((Sound[i] < SoundInstructions.Stop | Sound[i] > SoundInstructions.PlayLooping) && Sound[i] != SoundInstructions.PlayOnce & Sound[i] != SoundInstructions.Continue)
					{
						PluginValid = false;
					}
				}
			}
			catch (Exception ex)
			{
				LastException = ex;
				throw;
			}
		}

		protected override void SetReverser(int reverser)
		{
			Call(() => setReverser(reverser));
		}

		protected override void SetPower(int powerNotch)
		{
			Call(() => setPower(powerNotch));
		}

		protected override void SetBrake(int brakeNotch)
		{
			Call(() => setBrake(brakeNotch));
		}

		public override void KeyDown(VirtualKeys key)
		{
			Call(() => keyDown((int) key));
		}

		public override void KeyUp(VirtualKeys key)
		{
			Call(() => keyUp((int) key));
		}

		public override void HornBlow(HornTypes type)
		{
			Call(() => hornBlow((int) type));
		}

		public override void DoorChange(DoorStates oldState, DoorStates newState)
		{
			if (oldState == DoorStates.None & newState != DoorStates.None)
			{
				Call(() => doorOpen());
			}
			else if (oldState != DoorStates.None & newState == DoorStates.None)
			{
				Call(() => doorClose());
			}
		}

		protected override void SetSignal(SignalData[] signal)
		{
			if (LastAspects.Length == 0 || signal[0].Aspect != LastAspects[0])
			{
				Call(() => setSignal(signal[0].Aspect));
			}
		}

		protected override void SetBeacon(BeaconData beacon)
		{
			Win32BeaconData win32Beacon;
			win32Beacon.Type = beacon.Type;
			win32Beacon.Signal = beacon.Signal.Aspect;
			win32Beacon.Distance = (float) beacon.Signal.Distance;
			win32Beacon.Optional = beacon.Optional;
			Call(() => setBeaconData(win32Beacon));
		}

		protected override void PerformAI(AIData data)
		{
		}
	}
}
