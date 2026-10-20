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
using OpenBveApi.Interface;
using SoundManager;
using TrainManager.Motor;
using TrainManager.Trains;

namespace Train.OpenBve
{
	/// <summary>
	/// A BVE5 train's motor noise: the motor noise file ([Power] and [Brake], each a Volume and a
	/// Frequency table) and the sound file's [Motor] sounds, through upstream's BVE5 motor sound
	/// (Bve5MotorSoundTableParser and BVE5MotorSound, as train.xml uses them).
	/// </summary>
	internal static class Bve5MotorNoise
	{
		internal static void Apply(TrainBase train, Bve5Vehicle vehicle)
		{
			if (string.IsNullOrEmpty(vehicle.MotorNoise) || !System.IO.File.Exists(vehicle.MotorNoise))
			{
				return;
			}

			Dictionary<string, Dictionary<string, string>> noise = Bve5Text.Read(vehicle.MotorNoise).Sections();
			string Table(string section, string key) =>
				noise.TryGetValue(section, out Dictionary<string, string> keys) && keys.TryGetValue(key, out string value) ? Bve5Text.ResolvePath(vehicle.MotorNoise, value) : null;
			string powerFrequency = Table("Power", "Frequency"), powerVolume = Table("Power", "Volume");
			string brakeFrequency = Table("Brake", "Frequency"), brakeVolume = Table("Brake", "Volume");

			// The motor sounds, by index, from the sound file's [Motor].
			SoundBuffer[] buffers = new SoundBuffer[0];
			if (!string.IsNullOrEmpty(vehicle.Sound) && System.IO.File.Exists(vehicle.Sound) &&
			    Bve5Text.Read(vehicle.Sound).Sections().TryGetValue("Motor", out Dictionary<string, string> motor))
			{
				foreach (KeyValuePair<string, string> entry in motor)
				{
					if (!int.TryParse(entry.Key, out int index) || index < 0 || index > 255)
					{
						continue;
					}

					string file = Bve5Text.ResolvePath(vehicle.Sound, entry.Value);
					if (file == null || !System.IO.File.Exists(file))
					{
						Plugin.CurrentHost.AddMessage(MessageType.Warning, true, "Motor sound " + entry.Value + " was not found in " + vehicle.Sound);
						continue;
					}

					if (index >= buffers.Length)
					{
						Array.Resize(ref buffers, index + 1);
					}

					Plugin.CurrentHost.RegisterSound(file, SoundCfgParser.mediumRadius, out var handle);
					buffers[index] = handle as SoundBuffer;
				}
			}

			for (int i = 0; i < train.Cars.Length; i++)
			{
				if (!train.Cars[i].TractionModel.ProvidesPower)
				{
					continue;
				}

				try
				{
					BVE5MotorSound sound = Bve5MotorSoundTableParser.Parse(train.Cars[i], powerFrequency, powerVolume, brakeFrequency, brakeVolume);
					// Upstream's sound reads the tables' first entries unchecked: without tables, the car keeps no motor sound.
					if (sound.MotorSoundTable.Length == 0 || sound.BrakeSoundTable.Length == 0)
					{
						continue;
					}

					sound.SoundBuffers = buffers;
					train.Cars[i].TractionModel.MotorSounds = sound;
				}
				catch (Exception ex)
				{
					Plugin.CurrentHost.AddMessage(MessageType.Error, false, "The motor noise of " + vehicle.FileName + " could not be read: " + ex.Message);
					return;
				}
			}
		}
	}
}
