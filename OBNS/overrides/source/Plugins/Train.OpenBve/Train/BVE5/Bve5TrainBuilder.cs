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
using LibRender2.Trains;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Routes;
using OpenBveApi.Trains;
using TrainManager.BrakeSystems;
using TrainManager.Car;
using TrainManager.Handles;
using TrainManager.Motor;
using TrainManager.Power;
using TrainManager.SafetySystems;
using TrainManager.Trains;

namespace Train.OpenBve
{
	/// <summary>
	/// Builds a train from a BVE5 vehicle: its cars, handles, brakes and traction, as upstream's
	/// TrainDatParser builds one from a train.dat (and with the same defaults for what BVE5 does not
	/// describe: car width and height, doors, couplers, friction and drag).
	/// </summary>
	internal static class Bve5TrainBuilder
	{
		private const double KmhToMs = 1.0 / 3.6;

		internal static void Build(TrainBase train, Bve5Vehicle vehicle, Bve5Parameters p, Bve5PerformanceSet power, Bve5PerformanceSet brake)
		{
			CultureInfo culture = CultureInfo.InvariantCulture;
			int motorCars = Math.Max(1, p.MotorcarCount);
			int trailerCars = Math.Max(0, p.TrailerCount);
			int cars = motorCars + trailerCars;
			train.Handles.HasLocoBrake = false;
			train.Specs.AveragesPressureDistribution = true;

			// --- handles ---
			int powerNotches = p.PowerNotchCount;
			int brakeNotches = p.BrakeNotchCount;
			double[] powerDelay = { p.LeverDelay };
			double[] brakeDelay = { p.BrakeLeverDelay };
			train.Handles.Power = new PowerHandle(powerNotches, powerNotches, powerDelay, powerDelay, train);
			if (p.BrakeSystem == Bve5BrakeSystem.Cl)
			{
				train.Handles.Brake = new AirBrakeHandle(train);
				train.Handles.HandleType = HandleType.TwinHandle;
			}
			else
			{
				// A holding-speed brake is BVE5's brake notch 1; OpenBVE counts it as its hold brake.
				train.Handles.HasHoldBrake = p.HoldingSpeedBrake;
				train.Handles.Brake = new BrakeHandle(brakeNotches, brakeNotches, train.Handles.EmergencyBrake, brakeDelay, brakeDelay, train);
				train.Handles.HandleType = p.OneLever ? HandleType.SingleHandle : HandleType.TwinHandle;
			}

			train.Handles.LocoBrake = new LocoBrakeHandle(0, train.Handles.EmergencyBrake, new double[] { }, new double[] { }, train);
			train.Handles.LocoBrakeType = LocoBrakeType.Combined;
			train.Handles.HoldBrake = new HoldBrakeHandle(train);

			// --- cars ---
			train.Cars = new CarBase[cars];
			for (int i = 0; i < cars; i++)
			{
				train.Cars[i] = new CarBase(train, i, 0.35, 0.0025, 1.1);
			}

			train.DriverCar = 0;
			bool[] motor = MotorCarPositions(cars, motorCars, trailerCars, p.FirstCarIsMotorCar);

			// The train's mass, and its effective mass with the rotating parts (BVE5's inertia factors).
			double mass = motorCars * p.MotorcarWeight + trailerCars * p.TrailerWeight;
			double effectiveMass = motorCars * p.MotorcarWeight * (1.0 + p.MotorcarInertiaFactor) + trailerCars * p.TrailerWeight * (1.0 + p.TrailerInertiaFactor);
			// Per newton of one motor car's force: the acceleration that car gives itself, so that the
			// train as a whole moves at (cars' total force) / (effective mass).
			double tractionScale = mass / (p.MotorcarWeight * effectiveMass);

			// --- traction ---
			AccelerationCurve[] curves = new AccelerationCurve[powerNotches];
			double maximumAcceleration = 0;
			for (int n = 0; n < powerNotches; n++)
			{
				curves[n] = new Bve5AccelerationCurve(power, n + 1, tractionScale);
				maximumAcceleration = Math.Max(maximumAcceleration, curves[n].MaximumAcceleration);
			}

			if (maximumAcceleration <= 0)
			{
				Plugin.CurrentHost.AddMessage(MessageType.Error, false, "No power was found in the performance tables of " + vehicle.FileName);
			}

			// --- brakes ---
			// Brake cylinder pressure at full service (BVE5: the command over SapBcOffset, times SapBcRatio).
			double serviceRate = p.PressureRates != null && p.PressureRates.Length >= brakeNotches + 1 ? p.PressureRates[brakeNotches] : 1.0;
			double emergencyRate = p.PressureRates != null && p.PressureRates.Length >= brakeNotches + 2 ? p.PressureRates[brakeNotches + 1] : serviceRate;
			double serviceCommand = p.MaximumPressure * serviceRate;
			double serviceBc = Math.Max(10000, p.SapBcRatio * (serviceCommand - p.SapBcOffset));
			double emergencyBc = Math.Max(serviceBc, p.SapBcRatio * (p.MaximumPressure * emergencyRate - p.SapBcOffset));
			// The deceleration the air brake gives at full service (m/s²): cylinder pressure on the
			// piston area, times the shoes' friction at a middling 40 km/h, over the mean car's effective mass.
			double carEffectiveMass = effectiveMass / cars;
			double brakeDeceleration = serviceBc * p.PistonArea * p.ShoeFrictionAt(40) / carEffectiveMass;
			// The electric brake's deceleration on a motor car (m/s²), at its strongest.
			double electricForce = brake.Force.Curves > 0 ? brake.Force.Max(0) : 0;
			double motorDeceleration = electricForce * tractionScale;
			EletropneumaticBrakeType blending = p.Blending == Bve5Blending.AirSupplement ? EletropneumaticBrakeType.DelayFillingControl
				: p.Blending == Bve5Blending.LockoutValve ? EletropneumaticBrakeType.ClosingElectromagneticValve
				: EletropneumaticBrakeType.None;
			if (electricForce <= 0)
			{
				blending = EletropneumaticBrakeType.None;
			}

			double brakeControlSpeed = (p.Blending != Bve5Blending.None ? p.BlendingRegenerationLimit : p.RegenerationLimit) * KmhToMs;
			// BVE5's charging and exhaust speeds are in Pa^1/2 per second: about speed x sqrt(pressure) Pa/s near full pressure.
			double bcUp = p.BcApplySpeed * Math.Sqrt(emergencyBc);
			double bcDown = p.BcReleaseSpeed * Math.Sqrt(emergencyBc);
			double operatingPressure = p.BrakeSystem == Bve5BrakeSystem.Ecb ? Math.Max(490000, p.BpInitialPressure) : p.BpInitialPressure;

			for (int i = 0; i < cars; i++)
			{
				AccelerationCurve[] decelerationCurves = { new BveDecelerationCurve(brakeDeceleration) };
				CarBase car = train.Cars[i];
				switch (p.BrakeSystem)
				{
					case Bve5BrakeSystem.Cl:
						car.CarBrake = new AutomaticAirBrake(blending, car, brakeControlSpeed, motorDeceleration, decelerationCurves);
						break;
					case Bve5BrakeSystem.Smee:
						car.CarBrake = new ElectromagneticStraightAirBrake(blending, car, brakeControlSpeed, motorDeceleration, 0, 0, decelerationCurves);
						break;
					default:
						car.CarBrake = new ElectricCommandBrake(blending, car, brakeControlSpeed, motorDeceleration, 0, 0, decelerationCurves);
						break;
				}

				car.TractionModel = motor[i] ? (TractionModel)new BVEMotorCar(car, curves) : new BVETrailerCar(car);
				car.CarBrake.MainReservoir = new MainReservoir(p.CompressorLowerPressure, p.CompressorUpperPressure, 0.01, (p.BrakeSystem == Bve5BrakeSystem.Cl ? 0.25 : 0.075) / cars);
				car.CarBrake.MainReservoir.Volume = 0.5;
				car.CarBrake.EqualizingReservoir = new EqualizingReservoir(50000.0, 250000.0, 200000.0);
				car.CarBrake.EqualizingReservoir.NormalPressure = 1.005 * operatingPressure;
				car.CarBrake.EqualizingReservoir.Volume = 0.015;
				car.CarBrake.BrakePipe = new BrakePipe(operatingPressure, 10000000.0, 1500000.0, 5000000.0, p.BrakeSystem == Bve5BrakeSystem.Ecb);
				car.CarBrake.BrakePipe.Volume = Math.Pow(0.0175 * Math.PI, 2) * (cars * 1.05);
				AirBrake airBrake = car.CarBrake as AirBrake;
				if (car.TractionModel.ProvidesPower || train.IsPlayerTrain && i == train.DriverCar || p.BrakeSystem == Bve5BrakeSystem.Ecb)
				{
					car.CarBrake.BrakeType = BrakeType.Main;
					airBrake.Compressor = new Compressor(p.CompressionSpeed, car.CarBrake.MainReservoir, car);
				}
				else
				{
					car.CarBrake.BrakeType = BrakeType.Auxiliary;
				}

				airBrake.StraightAirPipe = new StraightAirPipe(300000.0, 400000.0, 200000.0);
				double r = Math.Max(0.1, Math.Min(1.0, 200000.0 / emergencyBc - 1.0));
				car.CarBrake.AuxiliaryReservoir = new AuxiliaryReservoir(0.975 * operatingPressure, 200000.0, 0.5, r);
				car.CarBrake.AuxiliaryReservoir.Volume = 0.16;
				car.CarBrake.BrakeCylinder = new BrakeCylinder(serviceBc, emergencyBc, p.BrakeSystem == Bve5BrakeSystem.Cl ? bcUp : 0.3 * bcUp, bcUp, bcDown);
				car.CarBrake.BrakeCylinder.Volume = 0.14;
				car.CarBrake.JerkUp = 10.0;
				car.CarBrake.JerkDown = 10.0;
				car.CarBrake.Initialize(Plugin.CurrentOptions.TrainStart);
			}

			if (train.Handles.HasHoldBrake && train.Handles.Brake.MaximumNotch > 1)
			{
				train.Handles.Brake.MaximumNotch--;
			}

			train.Cars[train.DriverCar].SafetySystems.Add(SafetySystem.OverspeedMessage, new OverspeedMessage(train.Cars[train.DriverCar]));
			train.SafetySystems.PassAlarm = new PassAlarm(PassAlarmType.None, train.Cars[train.DriverCar]);
			train.SafetySystems.PilotLamp = new PilotLamp(train.Cars[train.DriverCar]);
			train.SafetySystems.StationAdjust = new StationAdjustAlarm(train);
			train.SafetySystems.Headlights = new LightSource(train, 1);
			StartingHandles(train, p);

			// --- the cars' other attributes, as TrainDatParser's defaults ---
			const double distanceBetweenCars = 0.3, carWidth = 2.6, carHeight = 3.6, centerOfGravityHeight = 1.6;
			double axleDistance = 0.4 * p.CarLength;
			for (int i = 0; i < cars; i++)
			{
				CarBase car = train.Cars[i];
				car.Coupler = new Coupler(0.9 * distanceBetweenCars, 1.1 * distanceBetweenCars, car, i < cars - 1 ? train.Cars[i + 1] : null);
				if (i == train.DriverCar)
				{
					car.Breaker = new Breaker(car);
				}

				car.ChangeCarSection(CarSectionType.NotVisible);
				car.FrontAxle.Follower.TriggerType = i == 0 ? EventTriggerType.FrontCarFrontAxle : EventTriggerType.OtherCarFrontAxle;
				car.RearAxle.Follower.TriggerType = i == cars - 1 ? EventTriggerType.RearCarRearAxle : EventTriggerType.OtherCarRearAxle;
				car.BeaconReceiver.TriggerType = i == 0 ? EventTriggerType.TrainFront : EventTriggerType.None;
				car.BeaconReceiverPosition = 0.5 * p.CarLength;
				car.FrontAxle.Position = axleDistance;
				car.RearAxle.Position = -axleDistance;
				car.Specs.JerkPowerUp = 10.0;
				car.Specs.JerkPowerDown = 10.0;
				car.Specs.ExposedFrontalArea = 0.65 * carWidth * carHeight;
				car.Specs.UnexposedFrontalArea = 0.2 * carWidth * carHeight;
				car.Doors[0] = new Door(-1, 1000.0, 0.0);
				car.Doors[1] = new Door(1, 1000.0, 0.0);
				car.Specs.DoorOpenFrequency = 0.0;
				car.Specs.DoorCloseFrequency = 0.0;
				car.Specs.CenterOfGravityHeight = centerOfGravityHeight;
				car.Width = carWidth;
				car.Height = carHeight;
				car.Length = p.CarLength;
				car.Specs.CriticalTopplingAngle = 0.5 * Math.PI - Math.Atan(2 * car.Specs.CenterOfGravityHeight / car.Width);
				car.SafetySystems.Add(SafetySystem.ConstantSpeedDevice, new CarConstSpeed(car));
				car.HoldBrake = new CarHoldBrake(car);
				car.ReAdhesionDevice = new BveReAdhesionDevice(car, ReadhesionDeviceType.TypeA);
				car.CargoMass = 0;
				if (car.TractionModel.ProvidesPower)
				{
					car.EmptyMass = p.MotorcarWeight;
					car.TractionModel.MaximumPossibleAcceleration = maximumAcceleration;
				}
				else
				{
					car.EmptyMass = p.TrailerWeight;
				}
			}

			train.Cars[cars - 1].BeaconReceiver.TriggerType = cars == 1 ? EventTriggerType.SingleCarTrain : EventTriggerType.TrainRear;

			// --- the driver's eye: BVE5 measures Z back from the car's front ---
			CarBase driverCar = train.Cars[train.DriverCar];
			driverCar.Driver.X = p.ViewPointX;
			driverCar.Driver.Y = p.ViewPointY;
			driverCar.Driver.Z = 0.5 * p.CarLength + p.ViewPointZ;
			if (train.IsPlayerTrain)
			{
				driverCar.HasInteriorView = true;
			}

			// The BVE4 sound parser builds BVE4 motor sounds from these when it finishes: empty tables, as
			// a train.dat without motor sound gives (Bve5MotorNoise then gives motor cars BVE5's own).
			BVEMotorSoundTable[] tables = new BVEMotorSoundTable[4];
			for (int i = 0; i < tables.Length; i++)
			{
				tables[i].Entries = new BVEMotorSoundTableEntry[16];
				for (int j = 0; j < 16; j++)
				{
					tables[i].Entries[j].SoundIndex = -1;
					tables[i].Entries[j].Pitch = 1.0f;
					tables[i].Entries[j].Gain = 1.0f;
				}
			}

			Plugin.MotorSoundTables = tables;
			Plugin.AccelerationCurves = new BveAccelerationCurve[0];
			Plugin.MaximumAcceleration = maximumAcceleration;
			Plugin.FileSystem.AppendToLogFile(string.Format(culture,
				"BVE5 train {0}: {1} motor and {2} trailer cars, {3} power and {4} brake notches, {5} brake; full power from rest {6:0.00} km/h/s, full service brake {7:0.00} km/h/s (electric {8:0.00})",
				vehicle.FileName, motorCars, trailerCars, powerNotches, brakeNotches, p.BrakeSystem, maximumAcceleration * motorCars * p.MotorcarWeight / mass * 3.6,
				brakeDeceleration * 3.6, motorDeceleration * motorCars * p.MotorcarWeight / mass * 3.6));
		}

		/// <summary>Which cars are motor cars: upstream's spread (TrainDatParser), from the front car or the second.</summary>
		private static bool[] MotorCarPositions(int cars, int motorCars, int trailerCars, bool frontCarIsMotorCar)
		{
			bool[] motor = new bool[cars];
			if (motorCars == 1)
			{
				motor[frontCarIsMotorCar || trailerCars == 0 ? 0 : cars - 1] = true;
			}
			else if (motorCars == 2)
			{
				if (frontCarIsMotorCar || trailerCars == 0)
				{
					motor[0] = true;
					motor[cars - 1] = true;
				}
				else if (trailerCars == 1)
				{
					motor[1] = true;
					motor[2] = true;
				}
				else
				{
					motor[(int)Math.Ceiling(0.25 * (cars - 1))] = true;
					motor[(int)Math.Floor(0.75 * (cars - 1))] = true;
				}
			}
			else if (motorCars > 0)
			{
				int first = frontCarIsMotorCar ? 0 : 1;
				motor[first] = true;
				double t = 1.0 + (frontCarIsMotorCar ? trailerCars : trailerCars - 1) / (double)(motorCars - 1);
				double r = 0.0, x = first;
				while (true)
				{
					double y = x + t - r;
					x = Math.Ceiling(y);
					r = x - y;
					int i = (int)x;
					if (i >= cars)
					{
						break;
					}

					motor[i] = true;
				}
			}

			return motor;
		}

		/// <summary>The handles at the start, by the game's start mode, as TrainDatParser sets them.</summary>
		private static void StartingHandles(TrainBase train, Bve5Parameters p)
		{
			bool airBrake = p.BrakeSystem == Bve5BrakeSystem.Cl;
			switch (Plugin.CurrentOptions.TrainStart)
			{
				case TrainStartMode.ServiceBrakesAts:
					if (airBrake)
					{
						train.Handles.Brake.Driver = (int)AirBrakeHandleState.Service;
						train.Handles.Brake.Actual = (int)AirBrakeHandleState.Service;
					}
					else
					{
						int notch = (int)Math.Round(0.7 * train.Handles.Brake.MaximumNotch);
						train.Handles.Brake.Driver = notch;
						train.Handles.Brake.Actual = notch;
					}

					train.Handles.EmergencyBrake.Driver = false;
					train.Handles.EmergencyBrake.Safety = false;
					train.Handles.EmergencyBrake.Actual = false;
					train.Handles.Reverser.Driver = ReverserPosition.Forwards;
					train.Handles.Reverser.Actual = ReverserPosition.Forwards;
					break;
				default:
					if (airBrake)
					{
						train.Handles.Brake.Driver = (int)AirBrakeHandleState.Service;
						train.Handles.Brake.Actual = (int)AirBrakeHandleState.Service;
					}
					else
					{
						train.Handles.Brake.Driver = train.Handles.Brake.MaximumNotch;
						train.Handles.Brake.Actual = train.Handles.Brake.MaximumNotch;
					}

					train.Handles.EmergencyBrake.Driver = true;
					train.Handles.EmergencyBrake.Safety = true;
					train.Handles.EmergencyBrake.Actual = true;
					break;
			}
		}
	}
}
