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
using System.Globalization;
using TrainManager;
using TrainManager.BrakeSystems;
using TrainManager.Car;
using TrainManager.Cargo;
using TrainManager.Trains;

namespace Train.OpenBve
{
	/// <summary>
	/// A BVE5 train's passenger load, as BVE5 has it (the rules from Rinkin's runs of BVE5, D:\dev\Rinkin\docs\sim.md):
	/// the load factor of the stop just served (a station list's passengers), factor x Capacity people of BodyWeight
	/// a car, from the first stop's at the start. With LoadCompensating, the current limits lie between
	/// CurrentLimitingValue_Empty and _Full by the load (at most 1), steps with jerk regulation raise their force by
	/// the loaded mass over the empty mass, and the air brake's cylinder pressure rises by it, so that a loaded
	/// train accelerates and brakes about as an empty one; without, the same force moves more mass.
	/// </summary>
	internal sealed class Bve5Load
	{
		private readonly TrainBase train;
		private readonly Bve5Parameters parameters;
		private readonly Bve5PerformanceSet power;
		private readonly double emptyMass;

		// The brake cylinders as made for the empty train (BrakeCylinder's pressures are fixed: a loaded one is made anew).
		private readonly double serviceBc, emergencyBc, serviceChargeRate, emergencyChargeRate, releaseRate;
		private readonly Bve5DecelerationCurve[] decelerationCurves;

		/// <summary>The load factor (-1 until the first stop's is taken).</summary>
		internal double Factor { get; private set; } = -1;

		/// <summary>The passengers' mass in each car (kg).</summary>
		internal double CarMass { get; private set; }

		internal Bve5Load(TrainBase train, Bve5Parameters parameters, Bve5PerformanceSet power, double emptyMass, double serviceBc, double emergencyBc, double serviceChargeRate, double emergencyChargeRate, double releaseRate, Bve5DecelerationCurve[] decelerationCurves)
		{
			this.train = train;
			this.parameters = parameters;
			this.power = power;
			this.emptyMass = emptyMass;
			this.serviceBc = serviceBc;
			this.emergencyBc = emergencyBc;
			this.serviceChargeRate = serviceChargeRate;
			this.emergencyChargeRate = emergencyChargeRate;
			this.releaseRate = releaseRate;
			this.decelerationCurves = decelerationCurves;
		}

		/// <summary>The load at the start: the first stop's.</summary>
		internal void Start()
		{
			double factor = 0;
			if (TrainManagerBase.CurrentRoute != null && TrainManagerBase.CurrentRoute.Stations.Length > 0)
			{
				int first = TrainManagerBase.CurrentRoute.PlayerFirstStationIndex;
				factor = TrainManagerBase.CurrentRoute.Stations[first >= 0 && first < TrainManagerBase.CurrentRoute.Stations.Length ? first : 0].PassengerRatio;
			}

			Set(factor);
		}

		/// <summary>Carries a load factor (a stop's passengers): the cars' masses, and load compensation.</summary>
		internal void Set(double factor)
		{
			factor = Math.Max(0, factor);
			if (factor == Factor)
			{
				return;
			}

			Factor = factor;
			CarMass = factor * parameters.PassengerCapacity * parameters.PassengerBodyWeight;
			double ratio = parameters.LoadCompensating && emptyMass > 0 ? (emptyMass + CarMass * train.Cars.Length) / emptyMass : 1;
			power.LoadShare = parameters.LoadCompensating ? Math.Min(factor, 1) : 0;
			power.MassRatio = ratio;
			for (int i = 0; i < train.Cars.Length; i++)
			{
				CarBase car = train.Cars[i];
				car.CargoMass = CarMass;
				decelerationCurves[i].MassRatio = ratio;
				BrakeCylinder old = car.CarBrake.BrakeCylinder;
				car.CarBrake.BrakeCylinder = new BrakeCylinder(serviceBc * ratio, emergencyBc * ratio, serviceChargeRate, emergencyChargeRate, releaseRate)
				{
					CurrentPressure = old.CurrentPressure,
					Volume = old.Volume
				};
			}

			if (train.IsPlayerTrain)
			{
				Plugin.FileSystem.AppendToLogFile(string.Format(CultureInfo.InvariantCulture,
					"BVE5 train load {0:0.00}: {1:0} kg of passengers a car{2}", factor, CarMass,
					parameters.LoadCompensating ? string.Format(CultureInfo.InvariantCulture, ", load compensation for {0:0.000} of the empty mass", ratio) : ", no load compensation"));
			}
		}
	}

	/// <summary>A BVE5 train's car's passengers: upstream's (their comfort, for the score) with BVE5's load (Bve5Load).</summary>
	internal sealed class Bve5Passengers : Passengers
	{
		private readonly Bve5Load load;

		internal Bve5Passengers(CarBase car, Bve5Load load) : base(car)
		{
			this.load = load;
		}

		public override double Mass => load.CarMass;

		/// <summary>A stop's load factor (upstream calls this on arriving; BVE5's load is the one on leaving, the same).</summary>
		public override void UpdateLoading(double ratio)
		{
			Ratio = ratio;
			load.Set(ratio);
		}

		public override void Update(double Acceleration, double TimeElapsed)
		{
			if (load.Factor < 0)
			{
				load.Start();
				Ratio = load.Factor;
			}

			base.Update(Acceleration, TimeElapsed);
		}
	}
}
