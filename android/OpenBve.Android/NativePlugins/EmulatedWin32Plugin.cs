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
using System.Text;
using Android.Util;
using OpenBve.X86;
using OpenBveApi.Interface;
using OpenBveApi.Runtime;
using TrainManager.Trains;
using PluginBase = global::TrainManager.SafetySystems.Plugin;
using SoundInstructions = global::TrainManager.SafetySystems.SoundInstructions;

namespace OpenBve.Android.NativePlugins
{
	/// <summary>
	/// A Win32 ATS plugin running in the x86 emulator: upstream's Win32Plugin, with the calls
	/// into AtsPluginProxy.dll replaced by calls into the emulated DLL.
	/// </summary>
	/// <remarks>
	/// Everything between OpenBVE and the plugin - the version check, the vehicle spec, the
	/// handle and state structures, the panel array, and the plugin's sound instructions - follows
	/// upstream's Win32Plugin line for line (TrainManager/SafetySystems/Plugin/LegacyPlugin.cs),
	/// so the plugin sees exactly the calls it gets on Windows. Only the transport differs.
	/// </remarks>
	internal sealed class EmulatedWin32Plugin : PluginBase
	{
		private const string Tag = "OpenBVE";

		private readonly string pluginFile;
		private readonly string rootFolder;
		private readonly Encoding encoding;
		private readonly int[] lastSound = new int[256];
		private AtsPlugin plugin;
		private int beaconsLogged;
		private readonly int[] loggedPanel = new int[256];
		private double lastPanelLog;
		private int traceFrames;

		/// <summary>The emulated process, once loaded, for diagnostics.</summary>
		public AtsPlugin Emulated => plugin;

		internal EmulatedWin32Plugin(string pluginFile, string rootFolder, Encoding encoding, TrainBase train)
		{
			this.pluginFile = pluginFile;
			this.rootFolder = rootFolder;
			this.encoding = encoding;
			PluginTitle = Path.GetFileName(pluginFile);
			PluginValid = true;
			PluginMessage = null;
			Train = train;
			Panel = new int[256];
			Sound = new int[256];
			SupportsAI = AISupport.None;
			LastTime = 0.0;
			LastReverser = -2;
			LastPowerNotch = -1;
			LastBrakeNotch = -1;
			LastAspects = new int[] { };
			LastSection = -1;
			LastException = null;
		}

		public override bool Load(VehicleSpecs specs, InitializationModes mode)
		{
			try
			{
				plugin = AtsPlugin.Open(rootFolder, pluginFile, encoding, message => Log.Info(Tag, "x86 " + PluginTitle + ": " + message));
				if (NativePluginBridge.TraceFolder != null)
				{
					string traceFile = Path.Combine(NativePluginBridge.TraceFolder, "plugin-trace.bin");
					plugin.Trace = new AtsTrace.Writer(File.Create(traceFile), encoding.CodePage);
					Log.Info(Tag, "x86 " + PluginTitle + ": recording calls to " + traceFile);
				}

				plugin.Load();
				int version = plugin.GetPluginVersion();
				if (version == 0 && PluginTitle.ToLowerInvariant() != "ats2.dll" || version != 131072)
				{
					global::TrainManager.TrainManagerBase.currentHost.AddMessage(MessageType.Error, false, "The train plugin " + PluginTitle + " is of an unsupported version.");
					plugin.Dispose();
					plugin = null;
					return false;
				}

				plugin.SetVehicleSpec(specs.BrakeNotches, specs.PowerNotches, specs.AtsNotch, specs.B67Notch, specs.Cars);
				plugin.Initialize((int)mode);
			}
			catch (X86Exception ex)
			{
				LastException = ex;
				Log.Warn(Tag, "x86 " + PluginTitle + " could not start: " + ex.Message);
				plugin = null;
				return false;
			}

			UpdatePower();
			UpdateBrake();
			UpdateReverser();
			return true;
		}

		public override void Unload()
		{
			Guard(() => plugin?.Dispose());
			plugin?.Trace?.Dispose();
		}

		public override void BeginJump(InitializationModes mode)
		{
			Guard(() => plugin.Initialize((int)mode));
		}

		public override void EndJump()
		{
		}

		protected override void Elapse(ref ElapseData data)
		{
			if (plugin == null)
			{
				return;
			}

			double time = data.TotalTime.Milliseconds;
			AtsVehicleState state = new AtsVehicleState
			{
				Location = data.Vehicle.Location,
				Speed = (float)data.Vehicle.Speed.KilometersPerHour,
				Time = (int)Math.Floor(time - 2073600000.0 * Math.Floor(time / 2073600000.0)),
				BcPressure = (float)data.Vehicle.BcPressure,
				MrPressure = (float)data.Vehicle.MrPressure,
				ErPressure = (float)data.Vehicle.ErPressure,
				BpPressure = (float)data.Vehicle.BpPressure,
				SapPressure = (float)data.Vehicle.SapPressure,
				Current = 0.0f
			};

			AtsHandles handles;
			try
			{
				handles = plugin.Elapse(state, Panel, Sound);
			}
			catch (X86Exception ex)
			{
				Fail(ex);
				return;
			}

			LogPanelChanges();
			// The game's process is killed rather than unloaded, so a recording is flushed as it goes.
			if (plugin.Trace != null && ++traceFrames % 60 == 0)
			{
				plugin.Trace.Flush();
			}

			data.Handles.Reverser = handles.Reverser;
			data.Handles.PowerNotch = handles.Power;
			data.Handles.BrakeNotch = handles.Brake;
			if (handles.ConstantSpeed == 1)
			{
				data.Handles.ConstSpeed = true;
			}
			else if (handles.ConstantSpeed == 2)
			{
				data.Handles.ConstSpeed = false;
			}
			else if (handles.ConstantSpeed != 0)
			{
				PluginValid = false;
			}

			// The sound instructions, exactly as upstream's Win32Plugin processes them.
			var driverSounds = Train.Cars[Train.DriverCar].Sounds.Plugin;
			for (int i = 0; i < Sound.Length; i++)
			{
				if (Sound[i] != lastSound[i])
				{
					if (Sound[i] == SoundInstructions.Stop)
					{
						if (driverSounds.ContainsKey(i))
						{
							driverSounds[i].Stop();
						}
					}
					else if (Sound[i] > SoundInstructions.Stop & Sound[i] <= SoundInstructions.PlayLooping)
					{
						if (driverSounds.ContainsKey(i) && driverSounds[i].Buffer != null)
						{
							double volume = (Sound[i] - SoundInstructions.Stop) / (double)(SoundInstructions.PlayLooping - SoundInstructions.Stop);
							if (driverSounds[i].IsPlaying)
							{
								driverSounds[i].Source.Volume = volume;
							}
							else
							{
								driverSounds[i].Play(1.0, volume, Train.Cars[Train.DriverCar], true);
							}
						}
					}
					else if (Sound[i] == SoundInstructions.PlayOnce)
					{
						if (driverSounds.ContainsKey(i) && driverSounds[i].Buffer != null)
						{
							driverSounds[i].Play(Train.Cars[Train.DriverCar], false);
						}

						Sound[i] = SoundInstructions.Continue;
					}
					else if (Sound[i] != SoundInstructions.Continue)
					{
						PluginValid = false;
					}

					lastSound[i] = Sound[i];
				}
				else if ((Sound[i] < SoundInstructions.Stop | Sound[i] > SoundInstructions.PlayLooping) && Sound[i] != SoundInstructions.PlayOnce & Sound[i] != SoundInstructions.Continue)
				{
					PluginValid = false;
				}
			}
		}

		protected override void SetReverser(int reverser) => Guard(() => plugin.SetReverser(reverser));
		protected override void SetPower(int powerNotch) => Guard(() => plugin.SetPower(powerNotch));
		protected override void SetBrake(int brakeNotch) => Guard(() => plugin.SetBrake(brakeNotch));
		public override void KeyDown(VirtualKeys key)
		{
			Log.Info(Tag, "x86 " + PluginTitle + ": KeyDown " + key);
			Guard(() => plugin.KeyDown((int)key));
		}

		public override void KeyUp(VirtualKeys key)
		{
			Log.Info(Tag, "x86 " + PluginTitle + ": KeyUp " + key);
			Guard(() => plugin.KeyUp((int)key));
		}
		public override void HornBlow(HornTypes type) => Guard(() => plugin.HornBlow((int)type));

		public override void DoorChange(DoorStates oldState, DoorStates newState)
		{
			if (oldState == DoorStates.None & newState != DoorStates.None)
			{
				Guard(() => plugin.DoorOpen());
			}
			else if (oldState != DoorStates.None & newState == DoorStates.None)
			{
				Guard(() => plugin.DoorClose());
			}
		}

		protected override void SetSignal(SignalData[] signal)
		{
			if (LastAspects.Length == 0 || signal[0].Aspect != LastAspects[0])
			{
				Log.Info(Tag, "x86 " + PluginTitle + ": SetSignal " + signal[0].Aspect);
				Guard(() => plugin.SetSignal(signal[0].Aspect));
			}
		}

		protected override void SetBeacon(BeaconData beacon)
		{
			// The first beacons are logged: the route talks to signalling plugins through them.
			if (beaconsLogged < 200)
			{
				beaconsLogged++;
				Log.Info(Tag, "x86 " + PluginTitle + ": beacon type " + beacon.Type + " optional " + beacon.Optional + " signal " + beacon.Signal.Aspect + " at " + beacon.Signal.Distance.ToString("0"));
			}

			Guard(() => plugin.SetBeaconData(beacon.Type, beacon.Signal.Aspect, (float)beacon.Signal.Distance, beacon.Optional));
		}

		protected override void PerformAI(AIData data)
		{
		}

		/// <summary>
		/// Logs the panel indices the plugin changes, at most once a second, so a cab display that
		/// looks wrong can be told apart from a plugin that is not driving it.
		/// </summary>
		private void LogPanelChanges()
		{
			double now = Environment.TickCount64 / 1000.0;
			if (now - lastPanelLog < 1.0)
			{
				return;
			}

			lastPanelLog = now;
			StringBuilder changed = null;
			for (int i = 0; i < Panel.Length; i++)
			{
				if (Panel[i] != loggedPanel[i])
				{
					(changed ??= new StringBuilder()).Append(' ').Append(i).Append('=').Append(Panel[i]);
					loggedPanel[i] = Panel[i];
				}
			}

			if (changed != null)
			{
				Log.Info(Tag, "x86 " + PluginTitle + ": panel" + changed);
			}
		}

		/// <summary>Runs a call into the plugin; a fault stops the plugin rather than the game.</summary>
		private void Guard(Action call)
		{
			if (plugin == null)
			{
				return;
			}

			try
			{
				call();
			}
			catch (X86Exception ex)
			{
				Fail(ex);
			}
		}

		private void Fail(X86Exception ex)
		{
			LastException = ex;
			Log.Error(Tag, "x86 " + PluginTitle + " stopped: " + ex.Message);
			global::TrainManager.TrainManagerBase.currentHost.AddMessage(MessageType.Error, false, "The train plugin " + PluginTitle + " stopped: " + ex.Message);
			plugin = null;
		}
	}
}
