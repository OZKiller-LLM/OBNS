// OBNS: a copy of upstream's source/TrainManager/Motor/BVE5/MotorSound.cs (OpenBVE 1.14.1.2), laid over the desktop
// build's mirror (OBNS/build-desktop.ps1) and compiled by Android (TrainManager.Android.csproj). Update is rewritten so
// that BVE5 motor noise sounds as the tables say; it uses nothing beyond upstream's own types, for upstream to take:
// - pitch and volume are interpolated from the row at or below the speed towards the next (upstream's power
//   interpolation ran backwards, giving the next row's values at a row's own speed);
// - rows at the same speed, the speeds below the first row and above the last take a row's values as they are
//   (upstream divided by zero between rows at one speed);
// - under the brake table the volume follows the electric brake, as BVE4's motor sound does (upstream scaled it by
//   the power output, which is nothing while braking, so the brake table was silent);
// - the sounds stop when the motor neither powers nor brakes, and when it changes between the two (upstream left the
//   last sound playing when coasting);
// - a row with fewer columns than another is silent in the columns it lacks (upstream read past its end).
// On an upstream update: copy the file again and reapply this Update, or drop this copy if upstream has fixed these.
using System;
using OpenBveApi.Sounds;
using SoundManager;
using TrainManager.Car;

namespace TrainManager.Motor
{
	public class BVE5MotorSound : AbstractMotorSound
	{
		/// <summary>Contains all sound buffers</summary>
		public SoundBuffer[] SoundBuffers;
		/// <summary>Contains all sound sources</summary>
		public SoundSource[] SoundSources;
		/// <summary>The motor sound table</summary>
		public BVE5MotorSoundTableEntry[] MotorSoundTable;
		/// <summary>The motor sound table</summary>
		public BVE5MotorSoundTableEntry[] BrakeSoundTable;

		/// <summary>OBNS: the table playing: 1 power, -1 brake, 0 none.</summary>
		private int currentDirection;

		public BVE5MotorSound(CarBase car) : base(car)
		{
			SoundBuffers = new SoundBuffer[0];
			SoundSources = new SoundSource[0];
		}

		public override void Update(double TimeElapsed)
		{
			if (!Car.TractionModel.ProvidesPower)
			{
				return;
			}

			int direction = Math.Sign(Car.TractionModel.CurrentAcceleration);
			if (direction != currentDirection)
			{
				// Power and brake each have their own table: what one was playing stops when the other takes over.
				StopAll();
				currentDirection = direction;
			}

			BVE5MotorSoundTableEntry[] table = direction > 0 ? MotorSoundTable : direction < 0 ? BrakeSoundTable : null;
			if (table == null || table.Length == 0)
			{
				return;
			}

			// The rows either side of the speed: the last at or below it and the next above it.
			double speed = Math.Abs(Car.Specs.PerceivedSpeed) * 3.6; // km/h
			int next = 0;
			while (next < table.Length && table[next].Speed <= speed)
			{
				next++;
			}

			int row = Math.Max(next - 1, 0);
			next = Math.Min(next, table.Length - 1);
			BVE5MotorSoundTableEntry entry = table[row];
			BVE5MotorSoundTableEntry nextEntry = table[next];
			double interpolate = nextEntry.Speed > entry.Speed ? (speed - entry.Speed) / (nextEntry.Speed - entry.Speed) : 0.0;
			if (interpolate < 0.0)
			{
				interpolate = 0.0;
			}

			/*
			 * The table gives the sound at full output; it is scaled by how much of that the motor is giving, as
			 * BVE4's motor sound is: power by the acceleration against the most possible, the electric brake by its
			 * deceleration against the service brake's at this notch.
			 */
			double output = 1.0;
			if (direction > 0)
			{
				if (Car.TractionModel.MaximumPossibleAcceleration != 0.0)
				{
					output = Math.Pow(Math.Max(Car.TractionModel.CurrentAcceleration, 0.0) / Car.TractionModel.MaximumPossibleAcceleration, 0.25);
				}
			}
			else
			{
				double max = Car.baseTrain != null ? Car.CarBrake.DecelerationAtServiceMaximumPressure(Car.baseTrain.Handles.Brake.Actual, Car.CurrentSpeed) : 0.0;
				if (max > 0.0)
				{
					output = Math.Pow(Math.Min(-Car.TractionModel.CurrentAcceleration / max, 1.0), 0.25);
				}
			}

			int sounds = Math.Max(Math.Max(entry.Sounds.Length, nextEntry.Sounds.Length), SoundSources.Length);
			for (int i = 0; i < sounds; i++)
			{
				// A row without this column is silent in it.
				SoundState from = i < entry.Sounds.Length ? entry.Sounds[i] : new SoundState();
				SoundState to = i < nextEntry.Sounds.Length ? nextEntry.Sounds[i] : new SoundState();
				double pitch = from.Pitch + (to.Pitch - from.Pitch) * interpolate;
				double gain = (from.Gain + (to.Gain - from.Gain) * interpolate) * output;
				if (i >= SoundBuffers.Length || SoundBuffers[i] == null || pitch <= 0.0 || gain <= 0.0)
				{
					Stop(i);
					continue;
				}

				if (i >= SoundSources.Length)
				{
					Array.Resize(ref SoundSources, i + 1);
				}

				if (SoundSources[i] != null && SoundSources[i].State != SoundSourceState.Stopped)
				{
					SoundSources[i].Pitch = pitch;
					SoundSources[i].Volume = gain;
				}
				else
				{
					SoundSources[i] = TrainManagerBase.currentHost.PlaySound(SoundBuffers[i], pitch, gain, Position, Car, true) as SoundSource;
				}
			}
		}

		/// <summary>OBNS: stops one sound, if it is playing.</summary>
		private void Stop(int index)
		{
			if (index < SoundSources.Length && SoundSources[index] != null)
			{
				TrainManagerBase.currentHost.StopSound(SoundSources[index]);
				SoundSources[index] = null;
			}
		}

		/// <summary>OBNS: stops every sound.</summary>
		private void StopAll()
		{
			for (int i = 0; i < SoundSources.Length; i++)
			{
				Stop(i);
			}
		}
	}
}
