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
using OpenBveApi.Motor;
using TrainManager.Car;
using TrainManager.Motor;
using TrainManager.Power;

namespace Train.OpenBve
{
	/// <summary>
	/// A BVE5 train's motor car: upstream's, with its motor's current kept for panels (a panel's Subject = am, upstream's
	/// ampsindex): the power tables' current while it drives the car, the brake tables' as a negative current while it brakes.
	/// </summary>
	internal sealed class Bve5MotorCar : BVEMotorCar
	{
		private readonly CarBase car;
		private readonly Bve5PerformanceSet power, brake;
		private readonly TractionMotor motor;

		internal Bve5MotorCar(CarBase car, AccelerationCurve[] accelerationCurves, Bve5PerformanceSet power, Bve5PerformanceSet brake) : base(car, accelerationCurves)
		{
			this.car = car;
			this.power = power;
			this.brake = brake;
			motor = new TractionMotor(this, 0);
			Components.Add(EngineComponent.TractionMotor, motor);
		}

		public override void Update(double timeElapsed)
		{
			base.Update(timeElapsed);
			motor.CurrentAmps = Current();
		}

		private double Current()
		{
			double kmh = Math.Abs(car.CurrentSpeed) * 3.6;
			if (CurrentAcceleration > 0)
			{
				int notch = car.baseTrain.Handles.Power.Actual;
				int step = power.StepFor(notch, kmh);
				if (step < 0)
				{
					return 0;
				}

				// The current follows the force as it builds up and falls away.
				power.At(step, kmh, out _, out double current);
				double target = TargetAcceleration;
				return current * (target > CurrentAcceleration ? CurrentAcceleration / target : 1);
			}

			double strongest = car.CarBrake.motorDeceleration;
			if (CurrentAcceleration < 0 && strongest > 0)
			{
				return -Math.Abs(brake.Current.Get(0, kmh)) * Math.Min(1, -CurrentAcceleration / strongest);
			}

			return 0;
		}
	}
}
