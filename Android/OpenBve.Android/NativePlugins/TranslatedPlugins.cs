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
using System.IO;
using Android.Util;
using OpenBveApi.Runtime;

namespace OpenBve.Android.NativePlugins
{
	/// <summary>
	/// The catalogue of Win32 train plugins this port can run, and the managed re-implementations
	/// it runs them as.
	/// </summary>
	/// <remarks>
	/// A Win32 plugin is 32-bit x86 machine code calling the Windows API, so on an ARM phone it
	/// cannot be loaded, and machine code cannot be turned back into equivalent portable code
	/// automatically - a decompiler produces something that compiles, not something that is known
	/// to signal identically, which is exactly the property a safety system needs.
	///
	/// What is reproducible is a plugin whose behaviour is defined by data the train ships: a
	/// loader that dispatches to other plugins listed in a text file, or a configurable system
	/// whose indicators, keys and thresholds all come from its .cfg. Those are re-implemented
	/// here against the same configuration files, so the panel and sound indices the cab expects
	/// keep their meanings. Anything else is run as a pass-through module, which leaves the train
	/// drivable and its indices untouched rather than guessing at signalling behaviour.
	/// </remarks>
	public static class TranslatedPlugins
	{
		private const string Tag = "OpenBVE";

		/// <summary>Returns a managed module for a plugin file, or a pass-through module if there is no translation.</summary>
		public static IRuntime Translate(NativePluginImage image, out string description)
		{
			string name = Path.GetFileNameWithoutExtension(image.FileName).ToLowerInvariant();
			switch (name)
			{
				case "detailmanager":
					description = image.FileName + " → managed module loader";
					return new DetailManagerRuntime(image);
				case "os_ats1":
				case "os_ats2":
				case "os_ats":
					description = image.FileName + " → managed OS_ATS (configuration driven)";
					return new OsAtsRuntime(image);
				default:
					description = image.FileName + " → pass-through (no translation available)";
					return new PassThroughRuntime(image);
			}
		}

		/// <summary>Logs what a plugin file is, whether or not it can be translated.</summary>
		public static void Report(NativePluginImage image, string description)
		{
			Log.Info(Tag, "train plugin: " + image + ": " + description);
		}
	}

	/// <summary>
	/// A module that does nothing at all, for a Win32 plugin with no translation.
	/// </summary>
	/// <remarks>
	/// Deliberately inert: it neither touches the handles nor writes to the panel. The train
	/// drives, the route's own signalling and speed limits still apply, and every panel index the
	/// missing plugin would have driven stays at zero - a dark indicator rather than a wrong one.
	/// </remarks>
	public class PassThroughRuntime : IRuntime
	{
		private readonly NativePluginImage image;

		public PassThroughRuntime(NativePluginImage image)
		{
			this.image = image;
		}

		/// <summary>The plugin this stands in for.</summary>
		public string Name => image.FileName;

		public bool Load(LoadProperties properties)
		{
			properties.AISupport = AISupport.None;
			return true;
		}

		public void Unload()
		{
		}

		public void SetVehicleSpecs(VehicleSpecs specs)
		{
		}

		public void Initialize(InitializationModes mode)
		{
		}

		public void Elapse(ElapseData data)
		{
		}

		public void SetReverser(int reverser)
		{
		}

		public void SetPower(int powerNotch)
		{
		}

		public void SetBrake(int brakeNotch)
		{
		}

		public void KeyDown(VirtualKeys key)
		{
		}

		public void KeyUp(VirtualKeys key)
		{
		}

		public void HornBlow(HornTypes type)
		{
		}

		public void DoorChange(DoorStates oldState, DoorStates newState)
		{
		}

		public void SetSignal(SignalData[] signal)
		{
		}

		public void SetBeacon(BeaconData beacon)
		{
		}

		public void PerformAI(AIData data)
		{
		}
	}

	/// <summary>
	/// DetailManager, re-implemented: it loads the plugins named in <c>detailmodules.txt</c> beside
	/// it and passes every call on to each of them, which is all the original does.
	/// </summary>
	/// <remarks>
	/// The original merges each module's panel and sound arrays through shared Windows atoms; here
	/// the modules write into their own arrays and this merges the indices each module has claimed
	/// (any index it has ever written) back into the train's panel, so two modules driving
	/// different parts of the cab do not overwrite each other.
	/// </remarks>
	public class DetailManagerRuntime : IRuntime
	{
		private const string Tag = "OpenBVE";

		private readonly NativePluginImage image;
		private readonly List<Module> modules = new List<Module>();
		private int[] panel = Array.Empty<int>();

		public DetailManagerRuntime(NativePluginImage image)
		{
			this.image = image;
		}

		private sealed class Module
		{
			internal IRuntime Runtime;
			internal int[] Panel = Array.Empty<int>();
			internal readonly HashSet<int> Claimed = new HashSet<int>();
			internal string Name;
		}

		public bool Load(LoadProperties properties)
		{
			properties.AISupport = AISupport.None;
			// The original reserves 256 panel and sound values for its modules.
			panel = new int[512];
			properties.Panel = panel;

			string folder = Path.GetDirectoryName(image.Path) ?? properties.PluginFolder;
			string list = Path.Combine(folder, "detailmodules.txt");
			if (!File.Exists(list))
			{
				properties.FailureReason = "detailmodules.txt is missing beside " + image.FileName;
				return false;
			}

			foreach (string line in File.ReadAllLines(list))
			{
				string entry = line.Trim();
				if (entry.Length == 0 || entry.StartsWith(";", StringComparison.Ordinal))
				{
					continue;
				}

				string file = Path.Combine(folder, entry);
				if (!File.Exists(file))
				{
					Log.Warn(Tag, "detailmodules.txt lists " + entry + ", which is not there");
					continue;
				}

				NativePluginImage moduleImage = NativePluginImage.Read(file);
				IRuntime runtime = TranslatedPlugins.Translate(moduleImage, out string description);
				TranslatedPlugins.Report(moduleImage, description);

				LoadProperties moduleProperties = new LoadProperties(folder, properties.TrainFolder, properties.PlaySound,
					properties.PlayCarSound, properties.PlayMultipleCarSound, properties.AddMessage, properties.AddScore,
					properties.OpenDoors, properties.CloseDoors);
				if (!runtime.Load(moduleProperties))
				{
					Log.Warn(Tag, "module " + entry + " did not load: " + (moduleProperties.FailureReason ?? "no reason given"));
					continue;
				}

				modules.Add(new Module
				{
					Runtime = runtime,
					Panel = moduleProperties.Panel ?? Array.Empty<int>(),
					Name = entry
				});
			}

			if (modules.Count == 0)
			{
				properties.FailureReason = "none of the modules in detailmodules.txt could be used";
				return false;
			}

			Log.Info(Tag, "DetailManager: " + modules.Count + " module(s) running");
			return true;
		}

		public void Unload()
		{
			foreach (Module module in modules)
			{
				module.Runtime.Unload();
			}
		}

		public void SetVehicleSpecs(VehicleSpecs specs)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetVehicleSpecs(specs);
			}
		}

		public void Initialize(InitializationModes mode)
		{
			foreach (Module module in modules)
			{
				module.Runtime.Initialize(mode);
			}
		}

		public void Elapse(ElapseData data)
		{
			/*
			 * Each module sees the handles as the previous module left them, which is how the
			 * original chains them: a module applying the brakes is not undone by the next.
			 */
			foreach (Module module in modules)
			{
				module.Runtime.Elapse(data);
				Merge(module);
			}
		}

		/// <summary>Copies the indices a module drives into the train's panel.</summary>
		private void Merge(Module module)
		{
			int[] source = module.Panel;
			for (int i = 0; i < source.Length && i < panel.Length; i++)
			{
				if (source[i] != 0)
				{
					module.Claimed.Add(i);
				}

				if (module.Claimed.Contains(i))
				{
					panel[i] = source[i];
				}
			}
		}

		public void SetReverser(int reverser)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetReverser(reverser);
			}
		}

		public void SetPower(int powerNotch)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetPower(powerNotch);
			}
		}

		public void SetBrake(int brakeNotch)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetBrake(brakeNotch);
			}
		}

		public void KeyDown(VirtualKeys key)
		{
			foreach (Module module in modules)
			{
				module.Runtime.KeyDown(key);
			}
		}

		public void KeyUp(VirtualKeys key)
		{
			foreach (Module module in modules)
			{
				module.Runtime.KeyUp(key);
			}
		}

		public void HornBlow(HornTypes type)
		{
			foreach (Module module in modules)
			{
				module.Runtime.HornBlow(type);
			}
		}

		public void DoorChange(DoorStates oldState, DoorStates newState)
		{
			foreach (Module module in modules)
			{
				module.Runtime.DoorChange(oldState, newState);
			}
		}

		public void SetSignal(SignalData[] signal)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetSignal(signal);
			}
		}

		public void SetBeacon(BeaconData beacon)
		{
			foreach (Module module in modules)
			{
				module.Runtime.SetBeacon(beacon);
			}
		}

		public void PerformAI(AIData data)
		{
		}
	}
}
