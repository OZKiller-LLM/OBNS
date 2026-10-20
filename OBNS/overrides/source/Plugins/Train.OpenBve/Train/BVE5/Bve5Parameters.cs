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

// OBNS: BVE Trainsim 5 trains (OBNS/BVE5.md), part of upstream's train plugin in the OBNS build.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Train.OpenBve
{
	/// <summary>The brake system a BVE5 train's parameters name.</summary>
	internal enum Bve5BrakeSystem
	{
		/// <summary>Electric command brake ([Ecb]).</summary>
		Ecb,
		/// <summary>Electromagnetic straight air brake ([Smee]).</summary>
		Smee,
		/// <summary>Automatic air brake ([Cl]).</summary>
		Cl
	}

	/// <summary>How a BVE5 train's air brake makes way for the electric brake.</summary>
	internal enum Bve5Blending
	{
		None,
		/// <summary>[AirSupplement]: the air brake makes up what the electric brake does not give (delay-filling control).</summary>
		AirSupplement,
		/// <summary>[LockoutValve]: the air brake is shut off while the electric brake works.</summary>
		LockoutValve
	}

	/// <summary>
	/// A BVE5 vehicle parameters file ("BveTs Vehicle Parameters 2.01", and the older 0.04, 1.00,
	/// 1.01 and 2.00), with BVE5's own defaults for what it leaves out. Units as BVE5's: pressures in
	/// Pa, speeds in km/h, masses in kg, lengths in m.
	/// </summary>
	internal sealed class Bve5Parameters
	{
		internal bool FirstCarIsMotorCar;
		internal bool LoadCompensating = true;

		// [Cab] or [OneLeverCab]
		internal bool OneLever;
		internal int BrakeNotchCount = 8;
		internal int PowerNotchCount = 5;
		internal int HoldingSpeedNotchCount;
		internal bool HoldingSpeedBrake;
		internal int AtsCancelNotch = 1;
		internal int MotorBrakeNotch = 1;

		// [MainCircuit]
		internal double RegenerationLimit = 5;
		internal double LeverDelay = 0.5;
		internal bool BrakePriority;

		// [Ecb], [Smee] or [Cl]
		internal Bve5BrakeSystem BrakeSystem = Bve5BrakeSystem.Ecb;
		internal double MaximumPressure = 440000;
		internal double[] PressureRates;
		internal double SapBcRatio = 0.94;
		internal double SapBcOffset = 30000;
		internal double BpInitialPressure = 490000;
		internal double BrakeLeverDelay = 0.2;

		// [AirSupplement] or [LockoutValve]
		internal Bve5Blending Blending = Bve5Blending.None;
		internal double BlendingMaximumPressure = 440000;
		internal double BlendingRegenerationLimit = 5;

		// [Bc] or [BcServo], [Brake]
		internal double BcApplySpeed = 500;
		internal double BcReleaseSpeed = 500;
		internal double PistonArea = 0.4;
		internal double[] ShoeFriction = { 0.26, 0.01, 0.025 };

		// [Compressor]
		internal double CompressorLowerPressure = 700000;
		internal double CompressorUpperPressure = 800000;
		internal double CompressionSpeed = 5000;

		// [Dynamics]
		internal double MotorcarWeight = 31500;
		internal double TrailerWeight = 31500;
		internal int MotorcarCount = 5;
		internal int TrailerCount = 5;
		internal double MotorcarInertiaFactor = 0.1;
		internal double TrailerInertiaFactor = 0.05;
		internal double CarLength = 20;

		// [Door]
		internal double DoorCloseTime = 5;

		// [ViewPoint]
		internal double ViewPointX = -1.0;
		internal double ViewPointY = 2.5;
		internal double ViewPointZ = -1.0;

		internal static Bve5Parameters Load(string fileName)
		{
			Bve5Parameters p = new Bve5Parameters();
			Dictionary<string, Dictionary<string, string>> s = Bve5Text.Read(fileName).Sections();

			string Value(string section, string key) =>
				s.TryGetValue(section, out Dictionary<string, string> keys) && keys.TryGetValue(key, out string value) ? value : null;
			double Number(string section, string key, double fallback) => Bve5Text.Number(Value(section, key), fallback);
			bool Flag(string section, string key, bool fallback)
			{
				string value = Value(section, key);
				return value == null ? fallback : value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
			}

			p.FirstCarIsMotorCar = string.Equals(Value(string.Empty, "FirstCar"), "M", StringComparison.OrdinalIgnoreCase);
			p.LoadCompensating = Flag(string.Empty, "LoadCompensating", true);

			string cab = s.ContainsKey("OneLeverCab") ? "OneLeverCab" : "Cab";
			p.OneLever = cab == "OneLeverCab";
			p.BrakeNotchCount = Math.Max(1, (int)Number(cab, "BrakeNotchCount", 8));
			p.PowerNotchCount = Math.Max(1, (int)Number(cab, "PowerNotchCount", 5));
			p.HoldingSpeedNotchCount = (int)Number(cab, "HoldingSpeedNotchCount", 0);
			p.HoldingSpeedBrake = Flag(cab, "HoldingSpeedBrake", false);
			p.AtsCancelNotch = (int)Number(cab, "AtsCancelNotch", 1);
			p.MotorBrakeNotch = (int)Number(cab, "MotorBrakeNotch", 1);

			p.RegenerationLimit = Number("MainCircuit", "RegenerationLimit", 5);
			// (0.04 and earlier: NotchDelay.)
			p.LeverDelay = Number("MainCircuit", "LeverDelay", Number("MainCircuit", "NotchDelay", 0.5));
			p.BrakePriority = Flag("MainCircuit", "BrakePriority", false);

			string brake = s.ContainsKey("Cl") ? "Cl" : s.ContainsKey("Smee") ? "Smee" : "Ecb";
			p.BrakeSystem = brake == "Cl" ? Bve5BrakeSystem.Cl : brake == "Smee" ? Bve5BrakeSystem.Smee : Bve5BrakeSystem.Ecb;
			p.MaximumPressure = Number(brake, "MaximumPressure", 440000);
			p.PressureRates = Numbers(Value(brake, "PressureRates"));
			p.SapBcRatio = Number(brake, "SapBcRatio", 0.94);
			p.SapBcOffset = Number(brake, "SapBcOffset", 30000);
			p.BpInitialPressure = Number(brake, "BpInitialPressure", 490000);
			p.BrakeLeverDelay = Number(brake, "LeverDelay", 0.2);

			string blending = s.ContainsKey("AirSupplement") ? "AirSupplement" : s.ContainsKey("LockoutValve") ? "LockoutValve" : null;
			if (blending != null)
			{
				p.Blending = blending == "AirSupplement" ? Bve5Blending.AirSupplement : Bve5Blending.LockoutValve;
				p.BlendingMaximumPressure = Number(blending, "MaximumPressure", 440000);
				p.BlendingRegenerationLimit = Number(blending, "RegenerationLimit", 5);
			}

			string bc = s.ContainsKey("BcServo") ? "BcServo" : "Bc";
			p.BcApplySpeed = Number(bc, "ApplySpeed", 500);
			p.BcReleaseSpeed = Number(bc, "ReleaseSpeed", 500);
			// (Older files give the piston area under [Bc].)
			p.PistonArea = Number("Brake", "PistonArea", Number(bc, "PistonArea", 0.4));
			double[] friction = Numbers(Value("Brake", "ShoeFriction"));
			if (friction != null && friction.Length >= 1)
			{
				p.ShoeFriction = new[] { friction[0], friction.Length > 1 ? friction[1] : 0.01, friction.Length > 2 ? friction[2] : 0.025 };
			}

			p.CompressorLowerPressure = Number("Compressor", "LowerPressure", 700000);
			p.CompressorUpperPressure = Number("Compressor", "UpperPressure", 800000);
			p.CompressionSpeed = Number("Compressor", "CompressionSpeed", 5000);

			p.MotorcarWeight = Number("Dynamics", "MotorcarWeight", 31500);
			p.TrailerWeight = Number("Dynamics", "TrailerWeight", 31500);
			p.MotorcarCount = Math.Max(0, (int)Number("Dynamics", "MotorcarCount", 5));
			p.TrailerCount = Math.Max(0, (int)Number("Dynamics", "TrailerCount", 5));
			p.MotorcarInertiaFactor = Number("Dynamics", "MotorcarInertiaFactor", 0.1);
			p.TrailerInertiaFactor = Number("Dynamics", "TrailerInertiaFactor", 0.05);
			p.CarLength = Number("Dynamics", "CarLength", 20);

			p.DoorCloseTime = Number("Door", "CloseTime", 5);

			p.ViewPointX = Number("ViewPoint", "X", -1.0);
			p.ViewPointY = Number("ViewPoint", "Y", 2.5);
			p.ViewPointZ = Number("ViewPoint", "Z", -1.0);
			return p;
		}

		private static double[] Numbers(string list)
		{
			if (string.IsNullOrWhiteSpace(list))
			{
				return null;
			}

			return list.Split(',').Select(v => Bve5Text.Number(v, 0)).ToArray();
		}

		/// <summary>
		/// The brake shoes' friction at a speed (km/h). BVE5 gives three coefficients a, b, c; taken
		/// here as a(1 + bv) / (1 + cv) - a friction falling with speed, from a at standstill.
		/// </summary>
		internal double ShoeFrictionAt(double kmh)
		{
			double a = ShoeFriction[0], b = ShoeFriction[1], c = ShoeFriction[2];
			return a * (1.0 + b * kmh) / (1.0 + c * kmh);
		}
	}
}
