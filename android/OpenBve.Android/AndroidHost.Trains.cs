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
using LibRender2;
using OpenBveApi.Math;
using OpenBveApi.Trains;
using TrainManager.Trains;

namespace OpenBve.Android
{
	/// <summary>
	/// Train-related host services: finding the nearest train, and the per-frame update of
	/// animated world objects (signals, level crossings, animated scenery).
	/// </summary>
	public partial class AndroidHost
	{
		/// <summary>The trains in the simulation, set once the train manager exists.</summary>
		public List<TrainBase> TrainList { get; set; } = new List<TrainBase>();

		/*
		 * Upstream's simulation asks the host for the trains - the station departure signal
		 * held at red, for one, looks for the train due to leave. The base returns null, so
		 * this must override it (as upstream's Host does) rather than hide it.
		 */
		/// <inheritdoc />
		public override IEnumerable<AbstractTrain> Trains => TrainList;

		/// <summary>The scripted track-following objects, set once the train manager exists.</summary>
		public List<AbstractTrain> TrackFollowingObjects { get; set; } = new List<AbstractTrain>();

		/// <summary>
		/// Whether the simulation is fast-forwarding to the start time, as upstream's
		/// Game.MinimalisticSimulation: preceding trains then wait for their timetabled time
		/// rather than appearing at once, and collisions are not checked.
		/// </summary>
		public bool MinimalisticSimulation { get; set; }

		/// <inheritdoc />
		public override OpenBveApi.SimulationState SimulationState =>
			MinimalisticSimulation ? OpenBveApi.SimulationState.MinimalisticSimulation : OpenBveApi.SimulationState.Running;

		/// <inheritdoc />
		/// <remarks>
		/// A faithful port of the desktop host. Note that, as upstream does, the distance is
		/// measured in the X/Y plane; since Y is up and Z runs along the track in OpenBVE's world
		/// space, that ignores separation along the track. It is kept as upstream has it so that
		/// behaviour matches, and noted in PORTING.md as a probable upstream bug.
		/// </remarks>
		public override AbstractTrain ClosestTrain(Vector3 worldPosition)
		{
			AbstractTrain closestTrain = null;
			double bestDistance = double.MaxValue;

			void Consider(AbstractTrain train, Vector3 front, Vector3 rear)
			{
				double frontDistance = Math.Sqrt((worldPosition.X - front.X) * (worldPosition.X - front.X) + (worldPosition.Y - front.Y) * (worldPosition.Y - front.Y));
				double rearDistance = Math.Sqrt((worldPosition.X - rear.X) * (worldPosition.X - rear.X) + (worldPosition.Y - rear.Y) * (worldPosition.Y - rear.Y));
				double distance = Math.Min(frontDistance, rearDistance);
				if (distance < bestDistance)
				{
					closestTrain = train;
					bestDistance = distance;
				}
			}

			foreach (TrainBase train in TrainList)
			{
				if (train.State == TrainState.Available && train.Cars.Length > 0)
				{
					Consider(train, train.Cars[0].FrontAxle.Follower.WorldPosition, train.Cars[train.Cars.Length - 1].RearAxle.Follower.WorldPosition);
				}
			}

			foreach (AbstractTrain tfo in TrackFollowingObjects)
			{
				if (tfo is ScriptedTrain scripted && scripted.State == TrainState.Available && scripted.Cars.Length > 0)
				{
					Consider(scripted, scripted.Cars[0].FrontAxle.Follower.WorldPosition, scripted.Cars[scripted.Cars.Length - 1].RearAxle.Follower.WorldPosition);
				}
			}

			return closestTrain;
		}

		/// <inheritdoc />
		/// <remarks>
		/// Every animation function - door leaves, needles, cab displays, signal aspects, level
		/// crossings - is evaluated here: FunctionScript.ExecuteScript hands the work to the host,
		/// and the base host does nothing, so without this override every function stays at zero
		/// and nothing animated ever moves. The evaluator is upstream's own, compiled unchanged.
		/// </remarks>
		public override void ExecuteFunctionScript(OpenBveApi.FunctionScripting.FunctionScript functionScript, AbstractTrain train, int CarIndex,
			Vector3 Position, double TrackPosition, int SectionIndex, bool IsPartOfTrain, double TimeElapsed, int CurrentState)
		{
			FunctionScripts.ExecuteFunctionScript(functionScript, train as TrainBase, CarIndex, Position, TrackPosition, SectionIndex, IsPartOfTrain, TimeElapsed, CurrentState);
		}

		/// <inheritdoc />
		/// <remarks>
		/// A port of upstream's Host.ProcessJump and ObjectManager.ProcessJump, less the score:
		/// animated track followers are put back on their starting positions, every animated
		/// object catches up with the new time, and the player's cars are re-railed.
		/// </remarks>
		public override void ProcessJump(AbstractTrain train, int stationIndex, int trackIndex)
		{
			if (train.IsPlayerTrain)
			{
				for (int i = 0; i < AnimatedWorldObjectsUsed; i++)
				{
					if (AnimatedWorldObjects[i] is OpenBveApi.Objects.TrackFollowingObject follower)
					{
						// As upstream, including its assignment of both positions to the front follower.
						follower.FrontAxleFollower.TrackPosition = follower.TrackPosition + follower.FrontAxlePosition;
						follower.FrontAxleFollower.TrackPosition = follower.TrackPosition + follower.RearAxlePosition;
						follower.FrontAxleFollower.UpdateWorldCoordinates(false);
						follower.RearAxleFollower.UpdateWorldCoordinates(false);
					}
				}
			}

			if (Renderer != null && Route != null)
			{
				UpdateAnimatedWorldObjects(Renderer, 0.0, true);
			}

			if (train.IsPlayerTrain)
			{
				foreach (TrainBase t in TrainList)
				{
					global::TrainManager.TrainManagerBase.UnderailTrain(t);
				}
			}
		}

		/// <summary>
		/// Updates every animated world object once a frame: signal aspects, animated scenery,
		/// and anything driven by the nearest train. A port of upstream's
		/// ObjectManager.UpdateAnimatedWorldObjects.
		/// </summary>
		public void UpdateAnimatedWorldObjects(BaseRenderer renderer, double timeElapsed, bool forceUpdate)
		{
			double backgroundDistance = Route.CurrentBackground.BackgroundImageDistance;
			for (int i = 0; i < AnimatedWorldObjectsUsed; i++)
			{
				Vector3 cameraPosition = renderer.Camera.Alignment.Position;
				cameraPosition.Z += renderer.CameraTrackFollower.TrackPosition;
				bool visible = AnimatedWorldObjects[i].IsVisible(cameraPosition, backgroundDistance, renderer.Camera.ExtraViewingDistance);

				AbstractTrain train = visible || forceUpdate ? ClosestTrain(AnimatedWorldObjects[i].Position) : null;
				AnimatedWorldObjects[i].Update(train, timeElapsed, forceUpdate, visible);
			}
		}
	}
}
