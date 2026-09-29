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
using System.Linq;
using OpenBveApi.Hosts;
using OpenBveApi.Math;
using OpenBveApi.Objects;
using OpenBveApi.Runtime;
using OpenBveApi.Sounds;
using OpenBveApi.Trains;
using OpenTK.Audio.OpenAL;
using RouteManager2;
using SoundManager;
using TrainManager;
using TrainManager.Car;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// The sound manager: upstream's <see cref="SoundsBase"/> (buffers, sources, the loader thread,
	/// OpenAL device setup) plus a port of the desktop app's per-frame update.
	/// </summary>
	/// <remarks>
	/// <see cref="UpdateInverseModel"/> is upstream's OpenBve.Sounds.UpdateInverseModel with
	/// Program.Renderer, Program.CurrentRoute, TrainManager.PlayerTrain and Interface.CurrentOptions
	/// replaced by constructor arguments. Every frame it places the listener at the camera, works
	/// out each source's gain from its distance, keeps only the loudest
	/// <see cref="BaseOptions.SoundNumber"/> sources playing, and starts, moves and stops OpenAL
	/// sources to match. Microphone playback (the route's mic sound feature) is not ported: it
	/// would need the RECORD_AUDIO permission for a rarely used feature.
	/// </remarks>
	public class AndroidSounds : SoundsBase
	{
		private readonly AndroidRenderer renderer;
		private readonly AndroidOptions options;

		/// <summary>The route whose atmosphere sets the speed of sound.</summary>
		public CurrentRoute Route { get; set; }

		public AndroidSounds(HostInterface host, AndroidRenderer renderer, AndroidOptions options) : base(host)
		{
			this.renderer = renderer;
			this.options = options;
		}

		/// <summary>
		/// Pauses or resumes every sound, for the pause menu. The sources change state on the next
		/// <see cref="SoundsBase.Update"/>, as upstream's pause states are applied.
		/// </summary>
		public void SetPaused(bool paused)
		{
			for (int i = 0; i < SourceCount; i++)
			{
				if (paused)
				{
					Sources[i].Pause();
				}
				else if (Sources[i].State == SoundSourceState.Paused && Sources[i].OpenAlSourceName == 0)
				{
					// Paused before it ever started playing: there is no AL source to resume.
					Sources[i].State = SoundSourceState.PlayPending;
				}
				else
				{
					Sources[i].Resume();
				}
			}
		}

		/// <summary>Stops every sound attached to a train or any of its cars.</summary>
		public override void StopAllSounds(object train)
		{
			if (!(train is TrainBase t))
			{
				return;
			}

			for (int i = 0; i < SourceCount; i++)
			{
				if (t.Cars.Contains(Sources[i].Parent) || Sources[i].Parent == train)
				{
					if (Sources[i].State == SoundSourceState.Playing)
					{
						AL.DeleteSources(1, ref Sources[i].OpenAlSourceName);
						Sources[i].OpenAlSourceName = 0;
					}

					Sources[i].State = SoundSourceState.Stopped;
				}
			}
		}

		/// <inheritdoc />
		protected override void UpdateInverseModel(double timeElapsed)
		{
			TrainBase playerTrain = TrainManagerBase.PlayerTrain;
			CameraViewMode mode = renderer.Camera.CurrentMode;
			bool interior = mode == CameraViewMode.Interior | mode == CameraViewMode.InteriorLookAhead;

			// --- the listener ---
			Vector3 listenerPosition = renderer.Camera.AbsolutePosition;
			Orientation3 listenerOrientation = new Orientation3(renderer.Camera.AbsoluteSide, renderer.Camera.AbsoluteUp, renderer.Camera.AbsoluteDirection);
			Vector3 listenerVelocity = Vector3.Zero;
			if (playerTrain != null && (interior | mode == CameraViewMode.Exterior))
			{
				CarBase car = playerTrain.Cars[playerTrain.DriverCar];
				Vector3 diff = car.FrontAxle.Follower.WorldPosition - car.RearAxle.Follower.WorldPosition;
				listenerVelocity = diff.Norm() < 1e-10 ? car.CurrentSpeed * Vector3.Forward : car.CurrentSpeed * Vector3.Normalize(diff);
			}

			AL.Listener(ALListener3f.Position, 0.0f, 0.0f, 0.0f);
			AL.Listener(ALListener3f.Velocity, (float)listenerVelocity.X, (float)listenerVelocity.Y, (float)listenerVelocity.Z);
			float[] orientation =
			{
				(float)listenerOrientation.Z.X, (float)listenerOrientation.Z.Y, (float)listenerOrientation.Z.Z,
				-(float)listenerOrientation.Y.X, -(float)listenerOrientation.Y.Y, -(float)listenerOrientation.Y.Z
			};
			AL.Listener(ALListenerfv.Orientation, ref orientation);

			// --- the atmosphere ---
			if (Route != null)
			{
				double elevation = listenerPosition.Y + Route.Atmosphere.InitialElevation;
				double airTemperature = Route.Atmosphere.GetAirTemperature(elevation);
				double airPressure = Route.Atmosphere.GetAirPressure(elevation, airTemperature);
				double speedOfSound = Route.Atmosphere.GetSpeedOfSound(airPressure, airTemperature);
				try
				{
					AL.SpeedOfSound((float)speedOfSound);
				}
				catch
				{
					// negative or zero value will throw, but be ignored by AL
				}
			}

			// --- collect the sounds to be played, and stop the rest ---
			List<SoundSourceAttenuation> toBePlayed = new List<SoundSourceAttenuation>();
			for (int i = 0; i < SourceCount; i++)
			{
				switch (Sources[i].State)
				{
					case SoundSourceState.StopPending:
						AL.DeleteSources(1, ref Sources[i].OpenAlSourceName);
						Sources[i].State = SoundSourceState.Stopped;
						Sources[i].OpenAlSourceName = 0;
						Sources[i] = Sources[SourceCount - 1];
						SourceCount--;
						i--;
						break;
					case SoundSourceState.PausePending:
						AL.SourcePause(Sources[i].OpenAlSourceName);
						Sources[i].State = SoundSourceState.Paused;
						break;
					case SoundSourceState.ResumePending:
						AL.SourcePlay(Sources[i].OpenAlSourceName);
						Sources[i].State = SoundSourceState.Playing;
						break;
					case SoundSourceState.Stopped:
						Sources[i] = Sources[SourceCount - 1];
						SourceCount--;
						i--;
						break;
					default:
						if (GlobalMute)
						{
							if (Sources[i].State == SoundSourceState.Playing)
							{
								AL.DeleteSources(1, ref Sources[i].OpenAlSourceName);
								Sources[i].State = SoundSourceState.PlayPending;
								Sources[i].OpenAlSourceName = 0;
							}

							if (!Sources[i].Looped)
							{
								Sources[i].State = SoundSourceState.Stopped;
								Sources[i].OpenAlSourceName = 0;
								Sources[i] = Sources[SourceCount - 1];
								SourceCount--;
								i--;
							}

							break;
						}

						if (Sources[i].State == SoundSourceState.Playing)
						{
							AL.GetSource(Sources[i].OpenAlSourceName, ALGetSourcei.SourceState, out int state);
							if (state != (int)ALSourceState.Initial && state != (int)ALSourceState.Playing)
							{
								// Finished on its own.
								AL.DeleteSources(1, ref Sources[i].OpenAlSourceName);
								Sources[i].State = SoundSourceState.Stopped;
								Sources[i].OpenAlSourceName = 0;
								Sources[i] = Sources[SourceCount - 1];
								SourceCount--;
								i--;
								continue;
							}
						}

						Vector3 position = SourcePosition(Sources[i], out _);
						double distance = (position - listenerPosition).Norm();
						double radius = Sources[i].Radius;
						if (interior && playerTrain != null && Sources[i].Parent != playerTrain.Cars[playerTrain.DriverCar])
						{
							radius *= 0.5;
						}

						double gain = distance < 2.0 * radius
							? 1.0 - distance * distance * (4.0 * radius - distance) / (16.0 * radius * radius * radius)
							: radius / distance;
						gain *= Sources[i].Volume;
						if (gain <= 0.0)
						{
							if (Sources[i].State == SoundSourceState.Playing)
							{
								AL.DeleteSources(1, ref Sources[i].OpenAlSourceName);
								Sources[i].State = SoundSourceState.PlayPending;
								Sources[i].OpenAlSourceName = 0;
							}

							if (!Sources[i].Looped)
							{
								Sources[i].State = SoundSourceState.Stopped;
								Sources[i].OpenAlSourceName = 0;
								Sources[i] = Sources[SourceCount - 1];
								SourceCount--;
								i--;
							}
						}
						else
						{
							toBePlayed.Add(new SoundSourceAttenuation(Sources[i], gain, distance));
						}

						break;
				}
			}

			// --- sort by gain and adjust the clamp factor ---
			double clampFactor = Math.Exp(LogClampFactor);
			foreach (SoundSourceAttenuation s in toBePlayed)
			{
				s.Gain -= clampFactor * s.Distance * s.Distance;
			}

			toBePlayed.Sort();
			foreach (SoundSourceAttenuation s in toBePlayed)
			{
				s.Gain += clampFactor * s.Distance * s.Distance;
			}

			double desiredLogClampFactor;
			int index = Math.Min(SystemMaxSounds, options.SoundNumber);
			if (toBePlayed.Count <= index)
			{
				desiredLogClampFactor = MinLogClampFactor;
			}
			else
			{
				double cutoffDistance = toBePlayed[index].Distance;
				if (cutoffDistance <= 0.0)
				{
					desiredLogClampFactor = MaxLogClampFactor;
				}
				else
				{
					double cutoffGain = toBePlayed[index].Gain;
					desiredLogClampFactor = Math.Log(cutoffGain / (cutoffDistance * cutoffDistance));
					desiredLogClampFactor = Math.Max(MinLogClampFactor, Math.Min(MaxLogClampFactor, desiredLogClampFactor));
				}
			}

			const double rate = 3.0;
			if (LogClampFactor < desiredLogClampFactor)
			{
				LogClampFactor = Math.Min(desiredLogClampFactor, LogClampFactor + timeElapsed * rate);
			}
			else if (LogClampFactor > desiredLogClampFactor)
			{
				LogClampFactor = Math.Max(desiredLogClampFactor, LogClampFactor - timeElapsed * rate);
			}

			// --- play ---
			clampFactor = Math.Exp(LogClampFactor);
			for (int i = index; i < toBePlayed.Count; i++)
			{
				toBePlayed[i].Gain = 0.0;
			}

			int idx2 = Math.Min(index, toBePlayed.Count);
			for (int i = 0; i < toBePlayed.Count; i++)
			{
				SoundSource source = toBePlayed[i].Source;
				double gain = toBePlayed[i].Gain - clampFactor * toBePlayed[i].Distance * toBePlayed[i].Distance;
				bool active = source.State == SoundSourceState.Playing || source.State == SoundSourceState.Paused || source.State == SoundSourceState.PausePending;
				if (gain <= 0.0 || i > idx2)
				{
					if (source.State == SoundSourceState.Playing)
					{
						AL.DeleteSources(1, ref source.OpenAlSourceName);
						source.State = SoundSourceState.PlayPending;
						source.OpenAlSourceName = 0;
					}

					if (!source.Looped)
					{
						source.State = SoundSourceState.Stopped;
						source.OpenAlSourceName = 0;
					}

					continue;
				}

				if (!active)
				{
					LoadBuffer(source.Buffer);
					switch (source.Buffer.Loaded)
					{
						case SoundBufferState.Loaded:
							AL.GenSources(1, out source.OpenAlSourceName);
							AL.Source(source.OpenAlSourceName, ALSourcei.Buffer, source.Buffer.OpenAlBufferName);
							break;
						case SoundBufferState.Invalid:
							source.State = SoundSourceState.Stopped;
							continue;
						default:
							// Still loading on the background thread.
							continue;
					}
				}

				Vector3 sourcePosition = SourcePosition(source, out Vector3 velocity) - listenerPosition;
				AL.Source(source.OpenAlSourceName, ALSource3f.Position, (float)sourcePosition.X, (float)sourcePosition.Y, (float)sourcePosition.Z);
				AL.Source(source.OpenAlSourceName, ALSource3f.Velocity, (float)velocity.X, (float)velocity.Y, (float)velocity.Z);
				AL.Source(source.OpenAlSourceName, ALSourcef.Pitch, (float)source.Pitch);
				AL.Source(source.OpenAlSourceName, ALSourcef.Gain, (float)gain);
				if (!active)
				{
					AL.Source(source.OpenAlSourceName, ALSourceb.Looping, source.Looped);
					AL.SourcePlay(source.OpenAlSourceName);
					source.State = SoundSourceState.Playing;
				}
			}
		}

		/// <summary>A source's world position and velocity, following its car or animated object.</summary>
		private static Vector3 SourcePosition(SoundSource source, out Vector3 velocity)
		{
			switch (source.Type)
			{
				case SoundType.TrainCar:
					AbstractCar car = (AbstractCar)source.Parent;
					car.CreateWorldCoordinates(source.Position, out Vector3 position, out Vector3 direction);
					velocity = car.CurrentSpeed * direction;
					return position;
				case SoundType.AnimatedObject:
					WorldSound worldSound = (WorldSound)source.Parent;
					velocity = Vector3.Zero;
					return worldSound.Follower.WorldPosition + worldSound.Position;
				default:
					velocity = Vector3.Zero;
					return source.Position;
			}
		}
	}
}
