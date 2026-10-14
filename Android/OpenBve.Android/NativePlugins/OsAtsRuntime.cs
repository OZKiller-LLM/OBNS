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
using System.Globalization;
using System.IO;
using Android.Util;
using OpenBveApi.Runtime;

namespace OpenBve.Android.NativePlugins
{
	/// <summary>
	/// OS_ATS, re-implemented from its configuration file.
	/// </summary>
	/// <remarks>
	/// OS_ATS is a Win32 plugin whose behaviour is described entirely by <c>OS_Ats1.cfg</c> beside
	/// it: which panel indices its indicators drive, which ATS keys work them, and the thresholds
	/// of its interlocks. That file is what makes this re-implementable: the cab's panel indices
	/// keep their meanings because they are read from the same file the cab was built against.
	///
	/// Modelled here: the traction and door interlocks, overspeed control, the power, door,
	/// reverser and wiper indicators, and the custom indicators (an ATS key toggling a panel
	/// index). Not modelled: TPWS/AWS trainstops, vigilance, fuel, heating and the power-gap
	/// behaviour, all of which need beacon meanings the configuration does not describe - their
	/// indicators are left dark rather than shown in a state that has not been established. What
	/// is and is not in use is logged at load, and <see cref="Summary"/> names it for the player.
	/// </remarks>
	public class OsAtsRuntime : IRuntime
	{
		private const string Tag = "OpenBVE";

		private readonly NativePluginImage image;
		private readonly Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private readonly List<string> ignored = new List<string>();
		private readonly Dictionary<VirtualKeys, int> customIndicators = new Dictionary<VirtualKeys, int>();
		private readonly HashSet<VirtualKeys> customState = new HashSet<VirtualKeys>();

		private int[] panel = Array.Empty<int>();
		private VehicleSpecs specs;
		private DoorStates doors = DoorStates.None;
		private int reverser;

		// --- configuration ---
		private int powerIndicator = -1;
		private int reverserIndex = -1;
		private int[] doorIndicator = Array.Empty<int>();
		private bool tractionInterlock;
		private bool doorPowerLock;
		private bool doorApplyBrake;
		private bool overspeedControl;
		private double overspeed = double.MaxValue;
		private double safeSpeed = double.MaxValue;
		private int overspeedIndicator = -1;
		private int overspeedAlarm = -1;

		// --- wipers ---
		private int wiperIndex = -1;
		private double wiperRate;
		private double wiperDelay;
		private int wiperHoldPosition;
		private VirtualKeys wiperOnKey = (VirtualKeys)(-1);
		private VirtualKeys wiperOffKey = (VirtualKeys)(-1);
		private bool wipersOn;
		private double wiperTimer;
		private int wiperPosition;

		private bool overspeedBraking;

		public OsAtsRuntime(NativePluginImage image)
		{
			this.image = image;
		}

		/// <summary>One line naming what this module is doing, for the startup summary.</summary>
		public string Summary { get; private set; } = string.Empty;

		public bool Load(LoadProperties properties)
		{
			properties.AISupport = AISupport.None;
			string folder = Path.GetDirectoryName(image.Path) ?? properties.PluginFolder;
			string name = Path.GetFileNameWithoutExtension(image.FileName) + ".cfg";
			string file = Path.Combine(folder, name);
			if (!File.Exists(file))
			{
				// Some trains keep the configuration beside the train, not beside the plugin.
				file = Path.Combine(properties.TrainFolder, name);
			}

			if (!File.Exists(file))
			{
				properties.FailureReason = name + " could not be found";
				return false;
			}

			foreach (string line in File.ReadAllLines(file))
			{
				string text = line.Trim();
				int comment = text.IndexOf(';');
				if (comment >= 0)
				{
					text = text.Substring(0, comment).Trim();
				}

				int equals = text.IndexOf('=');
				if (equals <= 0)
				{
					continue;
				}

				settings[text.Substring(0, equals).Trim()] = text.Substring(equals + 1).Trim();
			}

			Configure();
			panel = new int[Math.Max(256, LargestIndex() + 1)];
			properties.Panel = panel;
			Summary = "OS_ATS: " + settings.Count + " settings read, " + ignored.Count + " not modelled";
			Log.Info(Tag, Summary + "; not modelled: " + (ignored.Count == 0 ? "none" : string.Join(", ", ignored)));
			return true;
		}

		private void Configure()
		{
			powerIndicator = Index("powerindicator");
			reverserIndex = Index("reverserindex");
			doorIndicator = Indices("doorindicator");
			tractionInterlock = Number("tractioninterlock") != 0;
			doorPowerLock = Number("doorpowerlock") != 0;
			doorApplyBrake = Number("doorapplybrake") != 0;

			overspeedControl = Number("overspeedcontrol") != 0;
			overspeed = Speed("overspeed");
			safeSpeed = Speed("safespeed");
			overspeedIndicator = Index("overspeedindicator");
			overspeedAlarm = Index("overspeedalarm");

			wiperIndex = Index("wiperindex");
			wiperRate = Number("wiperrate") / 1000.0;
			wiperDelay = Number("wiperdelay") / 1000.0;
			wiperHoldPosition = Number("wiperholdposition");
			wiperOnKey = Key("wiperonkey");
			wiperOffKey = Key("wiperoffkey");

			// customindicators is a list of key/panel index pairs: pressing the key toggles the index.
			int[] custom = Indices("customindicators");
			for (int i = 0; i + 1 < custom.Length; i += 2)
			{
				if (custom[i] >= 0 && custom[i] < 20 && custom[i + 1] >= 0)
				{
					customIndicators[(VirtualKeys)custom[i]] = custom[i + 1];
				}
			}

			/*
			 * Everything the configuration asks for that this re-implementation does not do. Named
			 * rather than silently dropped: a cab whose TPWS lamp never lights should be explained.
			 */
			foreach (string key in new[]
			{
				"system", "awsindicator", "tpwsindicator", "tpwsindicator2", "tpwsindicator3", "tpwswarningsound",
				"tpwsresetkey", "tpwsoverridekey", "tpwsstopdelay", "tpwsoverridelifetime", "tpwsbrakecancel",
				"startuptest", "warningspeed", "powergapbehaviour", "powerpickuppoints", "reversercontrol",
				"effectivervrindex", "neutralrvrbrake", "reminderindicator", "reminderkey", "vigilance", "traction",
				"fuelconsumption", "heatingpart", "heatingrate", "overheatresult", "numberofdrops", "dropstartindex",
				"dropanimationmode"
			})
			{
				if (settings.ContainsKey(key) && Number(key) != -1 && Number(key) != 0)
				{
					ignored.Add(key);
				}
			}
		}

		private int LargestIndex()
		{
			int largest = Math.Max(powerIndicator, Math.Max(reverserIndex, Math.Max(wiperIndex, Math.Max(overspeedIndicator, overspeedAlarm))));
			foreach (int index in doorIndicator)
			{
				largest = Math.Max(largest, index);
			}

			foreach (int index in customIndicators.Values)
			{
				largest = Math.Max(largest, index);
			}

			// The wipers' rain drops occupy a run of indices from their start.
			largest = Math.Max(largest, Number("dropstartindex") + Math.Max(0, Number("numberofdrops")));
			return largest;
		}

		private int Number(string key)
		{
			return settings.TryGetValue(key, out string value) && int.TryParse(value.Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
				? number
				: -1;
		}

		/// <summary>A panel index, or -1 where the configuration disables it.</summary>
		private int Index(string key)
		{
			int value = Number(key);
			return value < 0 ? -1 : value;
		}

		private int[] Indices(string key)
		{
			if (!settings.TryGetValue(key, out string value))
			{
				return Array.Empty<int>();
			}

			string[] parts = value.Split(',');
			List<int> indices = new List<int>(parts.Length);
			foreach (string part in parts)
			{
				indices.Add(int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1);
			}

			return indices.ToArray();
		}

		/// <summary>A speed in km/h, where the configuration's "off" values are far above any train.</summary>
		private double Speed(string key)
		{
			int value = Number(key);
			return value <= 0 || value >= 999 ? double.MaxValue : value;
		}

		private VirtualKeys Key(string key)
		{
			int value = Number(key);
			return value >= 0 && value < 20 ? (VirtualKeys)value : (VirtualKeys)(-1);
		}

		public void Unload()
		{
		}

		public void SetVehicleSpecs(VehicleSpecs specs)
		{
			this.specs = specs;
		}

		public void Initialize(InitializationModes mode)
		{
			overspeedBraking = false;
			wipersOn = false;
			wiperTimer = 0.0;
			wiperPosition = wiperHoldPosition;
		}

		public void Elapse(ElapseData data)
		{
			double speed = Math.Abs(data.Vehicle.Speed.KilometersPerHour);
			bool doorsOpen = doors != DoorStates.None;

			// --- interlocks ---
			bool powerCut = false;
			if (doorsOpen && (doorPowerLock || tractionInterlock))
			{
				powerCut = true;
				data.Handles.PowerNotch = 0;
				if (doorApplyBrake && data.Handles.BrakeNotch == 0 && specs.BrakeNotches > 0)
				{
					data.Handles.BrakeNotch = 1;
				}
			}

			// --- overspeed ---
			if (overspeedControl && overspeed != double.MaxValue)
			{
				if (speed > overspeed)
				{
					overspeedBraking = true;
				}
				else if (overspeedBraking && speed <= Math.Min(safeSpeed, overspeed))
				{
					overspeedBraking = false;
				}

				if (overspeedBraking)
				{
					powerCut = true;
					data.Handles.PowerNotch = 0;
					data.Handles.BrakeNotch = specs.BrakeNotches;
				}
			}

			// --- wipers ---
			if (wiperIndex >= 0)
			{
				UpdateWipers(data.ElapsedTime.Seconds);
				Set(wiperIndex, wiperPosition);
			}

			// --- indicators ---
			Set(powerIndicator, powerCut ? 0 : 1);
			Set(reverserIndex, reverser + 1);
			if (doorIndicator.Length > 0)
			{
				Set(doorIndicator[0], (doors & DoorStates.Left) != 0 ? 1 : 0);
			}

			if (doorIndicator.Length > 1)
			{
				Set(doorIndicator[1], (doors & DoorStates.Right) != 0 ? 1 : 0);
			}

			Set(overspeedIndicator, overspeedBraking ? 1 : 0);
			Set(overspeedAlarm, overspeedBraking ? 1 : 0);
			foreach (KeyValuePair<VirtualKeys, int> indicator in customIndicators)
			{
				Set(indicator.Value, customState.Contains(indicator.Key) ? 1 : 0);
			}
		}

		/// <summary>Sweeps the wiper blade across and back while the wipers are on, then parks it.</summary>
		private void UpdateWipers(double timeElapsed)
		{
			if (!wipersOn && wiperPosition == wiperHoldPosition)
			{
				return;
			}

			wiperTimer += timeElapsed;
			double interval = wiperPosition == wiperHoldPosition && wipersOn ? Math.Max(0.05, wiperDelay) : Math.Max(0.05, wiperRate);
			if (wiperTimer < interval)
			{
				return;
			}

			wiperTimer = 0.0;
			// Two positions beyond the parked one: across, back, and park when switched off.
			wiperPosition = wiperPosition >= 2 ? (wipersOn ? 1 : wiperHoldPosition) : wiperPosition + 1;
		}

		private void Set(int index, int value)
		{
			if (index >= 0 && index < panel.Length)
			{
				panel[index] = value;
			}
		}

		public void SetReverser(int reverser)
		{
			this.reverser = reverser;
		}

		public void SetPower(int powerNotch)
		{
		}

		public void SetBrake(int brakeNotch)
		{
		}

		public void KeyDown(VirtualKeys key)
		{
			if (key == wiperOnKey)
			{
				wipersOn = true;
			}
			else if (key == wiperOffKey)
			{
				wipersOn = false;
			}

			if (customIndicators.ContainsKey(key) && !customState.Add(key))
			{
				customState.Remove(key);
			}
		}

		public void KeyUp(VirtualKeys key)
		{
		}

		public void HornBlow(HornTypes type)
		{
		}

		public void DoorChange(DoorStates oldState, DoorStates newState)
		{
			doors = newState;
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
}
