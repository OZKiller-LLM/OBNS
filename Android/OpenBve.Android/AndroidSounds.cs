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
	/// sources to match, then plays the route's microphone sounds (<see cref="RecAndPlay"/>).
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
					/*
					 * A sound paused before it had an OpenAL source (still waiting to start - the
					 * pause menu, or the app leaving the screen, pauses every sound) has nothing to
					 * pause or resume: it stays paused without one, and on resuming waits to be
					 * given one like any sound about to start. Sent to OpenAL, source 0 is an error
					 * on every call; a beta tester's log had 75,000 of them, most while the game was
					 * in the background, and one five-second stall in the sound update.
					 */
					case SoundSourceState.PausePending:
						if (Sources[i].OpenAlSourceName != 0)
						{
							AL.SourcePause(Sources[i].OpenAlSourceName);
						}

						Sources[i].State = SoundSourceState.Paused;
						break;
					case SoundSourceState.ResumePending:
						if (Sources[i].OpenAlSourceName != 0)
						{
							AL.SourcePlay(Sources[i].OpenAlSourceName);
							Sources[i].State = SoundSourceState.Playing;
						}
						else
						{
							Sources[i].State = SoundSourceState.PlayPending;
						}

						break;
					case SoundSourceState.Paused when Sources[i].OpenAlSourceName == 0:
						// Paused before it started: nothing to update until it is resumed.
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

						if (Sources[i].State == SoundSourceState.Playing && Sources[i].OpenAlSourceName == 0)
						{
							// Marked playing without a source (it could not be made): ask for a new one.
							Sources[i].State = SoundSourceState.PlayPending;
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
				bool active = source.OpenAlSourceName != 0 &&
				              (source.State == SoundSourceState.Playing || source.State == SoundSourceState.Paused || source.State == SoundSourceState.PausePending);
				if (!active && (source.State == SoundSourceState.Paused || source.State == SoundSourceState.PausePending))
				{
					// Paused before it had a source: it must not start playing now.
					continue;
				}
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
							if (source.OpenAlSourceName == 0)
							{
								// OpenAL is out of sources: try again next frame rather than drive source 0.
								continue;
							}

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

			RecAndPlay(listenerPosition, clampFactor);
		}

		/// <summary>
		/// The route's microphone sounds (Track.MicSound), as upstream's Sounds.RecAndPlay with the
		/// inverse distance model: while PLAY_MIC_SOUNDS is on, what the phone's microphone hears is
		/// played at each microphone position the listener is within range of. The microphone opens
		/// only then, once the app has the RECORD_AUDIO permission (OpenTK.Audio.AudioCapture).
		/// </summary>
		private void RecAndPlay(Vector3 listenerPosition, double clampFactor)
		{
			if (OpenAlMic == null)
			{
				return;
			}

			if (!IsPlayingMicSounds)
			{
				if (OpenAlMic.IsRunning)
				{
					OpenAlMic.Stop();
				}

				return;
			}

			if (!OpenAlMic.IsRunning)
			{
				OpenAlMic.Start();
				if (!OpenAlMic.IsRunning)
				{
					// No microphone yet (no permission, or none on the device)
					return;
				}
			}

			int[] states = new int[MicSources.Count];
			for (int i = 0; i < MicSources.Count; i++)
			{
				AL.GetSource(MicSources[i].OpenAlSourceName, ALGetSourcei.BuffersProcessed, out states[i]);
			}

			int sample = OpenAlMic.AvailableSamples;
			for (int i = 0; i < MicSources.Count; i++)
			{
				MicSource mic = MicSources[i];
				if (listenerPosition.Z < mic.Position.Z - mic.BackwardTolerance || listenerPosition.Z > mic.Position.Z + mic.ForwardTolerance)
				{
					continue;
				}

				// When the last buffer has played and there is something recorded
				if (sample <= 0 || states[i] != 1)
				{
					continue;
				}

				OpenAlMic.ReadSamples(MicStore, sample);
				int buffer = AL.GenBuffer();
				AL.BufferData(buffer, OpenAlMic.SampleFormat, MicStore, MicStore.Length, OpenAlMic.SampleFrequency);
				AL.SourceQueueBuffer(mic.OpenAlSourceName, buffer);
				UnloadMicBuffers(mic.OpenAlSourceName, states[i]);

				Vector3 positionDifference = mic.Position - listenerPosition;
				double distance = positionDifference.Norm();
				double innerRadius = 15.0;
				double gain = 0.0;
				if (!GlobalMute)
				{
					CameraViewMode mode = renderer.Camera.CurrentMode;
					if (mode == CameraViewMode.Interior | mode == CameraViewMode.InteriorLookAhead)
					{
						innerRadius *= 0.5;
					}

					gain = distance < 2.0 * innerRadius
						? 1.0 - distance * distance * (4.0 * innerRadius - distance) / (16.0 * innerRadius * innerRadius * innerRadius)
						: innerRadius / distance;
					gain = Math.Max(0.0, gain);
					gain = Math.Max(0.0, gain - clampFactor * distance * distance);
				}

				AL.Source(mic.OpenAlSourceName, ALSource3f.Position, (float)positionDifference.X, (float)positionDifference.Y, (float)positionDifference.Z);
				AL.Source(mic.OpenAlSourceName, ALSourcef.Gain, (float)gain);
				if (AL.GetSourceState(mic.OpenAlSourceName) != ALSourceState.Playing)
				{
					AL.SourcePlay(mic.OpenAlSourceName);
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
