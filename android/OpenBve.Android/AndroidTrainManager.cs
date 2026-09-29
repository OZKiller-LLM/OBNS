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

using System.Linq;
using System.Threading.Tasks;
using LibRender2;
using OpenBveApi;
using OpenBveApi.Hosts;
using OpenBveApi.Trains;
using TrainManager;
using TrainManager.Car;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// The train manager for the Android front end.
	/// </summary>
	/// <remarks>
	/// As with the renderer, <see cref="TrainManagerBase"/> holds the implementation and upstream's
	/// desktop subclass (OpenBVE/Simulation/TrainManager) adds the per-frame update, which leans on
	/// Program statics. The route plugins need an instance before they can build a route, since a
	/// full load places scripted trains through it; the per-frame update arrives with train loading.
	/// </remarks>
	public class AndroidTrainManager : TrainManagerBase
	{
		/// <summary>Creates the train manager.</summary>
		public AndroidTrainManager(HostInterface host, BaseRenderer renderer, BaseOptions options, OpenBveApi.FileSystem.FileSystem fileSystem)
			: base(host, renderer, options, fileSystem)
		{
		}

		/// <summary>Advances every train by the given time.</summary>
		/// <remarks>
		/// A port of upstream's UpdateTrains, less developer mode (which does not exist here). The
		/// final world-coordinate and suspension pass is kept as upstream has it, since the camera
		/// anchor and the car objects are placed from those coordinates.
		/// </remarks>
		public void UpdateTrains(double timeElapsed)
		{
			foreach (TrainBase train in Trains)
			{
				/*
				 * A train starts Pending and introduces itself once the clock reaches its first
				 * station's time, which re-picks every car's visible section. It is worth knowing
				 * when that happens: it is a step where the cab can change and a train's worth of
				 * exterior textures can be loaded in one frame.
				 */
				TrainState before = train.State;
				train.Update(timeElapsed);
				if (train.State != before)
				{
					global::Android.Util.Log.Info("OpenBVE", "train state: " + before + " → " + train.State +
					                                        (train.IsPlayerTrain ? " (player, driver car section " + train.Cars[train.DriverCar].CurrentCarSection + ")" : string.Empty));
				}
			}

			foreach (AbstractTrain train in TFOs)
			{
				train.Update(timeElapsed);
			}

			// Collisions between trains and with buffer stops, as upstream (whose option defaults on).
			if (currentHost.SimulationState != SimulationState.MinimalisticSimulation)
			{
				for (int i = 0; i < Trains.Count; i++)
				{
					Trains[i].DetectTrainCollision(timeElapsed, i, Trains);
					Trains[i].DetectBufferCollision(timeElapsed, CurrentRoute.BufferTrackPositions);
				}
			}

			// A preceding train that has run its course is disposed, and gone a frame later.
			for (int i = Trains.Count - 1; i > 0; i--)
			{
				switch (Trains[i].State)
				{
					case TrainState.DisposePending:
						Trains[i].State = TrainState.Disposed;
						break;
					case TrainState.Disposed:
						Trains.RemoveAt(i);
						break;
				}
			}

			/*
			 * While fast-forwarding to the start time nothing is drawn, and the simulation itself
			 * works in track positions, so the world-coordinate pass is left to the first real
			 * frame. Upstream runs it on every step, which here was nearly all of the time spent
			 * (4 minutes of a route's traffic took 7 s).
			 */
			if (currentHost.SimulationState == SimulationState.MinimalisticSimulation)
			{
				return;
			}

			/*
			 * The same final pass, for the player train and the track following objects alike, one
			 * train per task as upstream does: each train only touches its own cars, and with a
			 * route's worth of scripted trains (16 on the MTR route, mostly not yet appeared but
			 * still positioned) this is the most expensive part of the simulation step.
			 */
			TrainBase[] all = Trains.Concat(TFOs.OfType<ScriptedTrain>()).ToArray();
			Parallel.For(0, all.Length, i =>
			{
				TrainBase train = all[i];
				if (train.State >= TrainState.DisposePending)
				{
					return;
				}

				foreach (CarBase car in train.Cars)
				{
					car.FrontAxle.Follower.UpdateWorldCoordinates(true);
					car.FrontBogie.FrontAxle.Follower.UpdateWorldCoordinates(true);
					car.FrontBogie.RearAxle.Follower.UpdateWorldCoordinates(true);
					car.RearAxle.Follower.UpdateWorldCoordinates(true);
					car.RearBogie.FrontAxle.Follower.UpdateWorldCoordinates(true);
					car.RearBogie.RearAxle.Follower.UpdateWorldCoordinates(true);

					// Upstream skips the suspension update for zero or excessive time steps.
					if (timeElapsed == 0.0 || timeElapsed > 0.5)
					{
						continue;
					}

					car.UpdateTopplingCantAndSpring(timeElapsed);
					car.FrontBogie.UpdateTopplingCantAndSpring();
					car.RearBogie.UpdateTopplingCantAndSpring();
				}
			});
		}
	}
}
