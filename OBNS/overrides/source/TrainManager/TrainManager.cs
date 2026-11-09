// OBNS: a copy of upstream's source/TrainManager/TrainManager.cs (OpenBVE 1.14.1.2), laid over the desktop build's
// mirror (OBNS/build-desktop.ps1): every train's car objects updated on several cores (UpdateTrainObjectsParallel); in
// Potato mode, cars the camera hardly sees and scripted trains far along the line updated less often (CarUpdateLod).
// On an upstream update: copy the file again and reapply the parts marked OBNS.
using System;
using System.Collections.Generic;
using LibRender2;
using LibRender2.Trains;
using OpenBveApi;
using OpenBveApi.FileSystem;
using OpenBveApi.Hosts;
using OpenBveApi.Objects;
using OpenBveApi.Trains;
using RouteManager2;
using TrainManager.Car;
using TrainManager.Trains;
using ScriptedTrain = TrainManager.Trains.ScriptedTrain;

namespace TrainManager
{
	/// <summary>The base train manager class</summary>
	public abstract class TrainManagerBase
	{
		internal static HostInterface currentHost;
		internal static BaseRenderer Renderer;
		public static CurrentRoute CurrentRoute;
		internal static FileSystem FileSystem;
		public static bool Toppling;
		public static bool Derailments;

		/// <summary>The list of trains available in the simulation.</summary>
		public List<TrainBase> Trains = new List<TrainBase>();
		/// <summary>A reference to the train of the Trains element that corresponds to the player's train.</summary>
		public static TrainBase PlayerTrain = null;
		/// <summary>The list of TrackFollowingObject available on other tracks in the simulation.</summary>
		public List<AbstractTrain> TFOs = new List<AbstractTrain>();
		/// <summary>Stores a reference to the current options</summary>
		internal static BaseOptions CurrentOptions;
		/// <summary>Stores the plugin error message string, or a null reference if no error encountered</summary>
		public static string PluginError;

		protected TrainManagerBase(HostInterface host, BaseRenderer renderer, BaseOptions Options, FileSystem fileSystem)
		{
			currentHost = host;
			Renderer = renderer;
			CurrentOptions = Options;
			FileSystem = fileSystem;
		}

		/// <summary>Un-derails all trains within the simulation</summary>
		public void UnderailTrains()
		{
			System.Threading.Tasks.Parallel.For(0, Trains.Count, i =>
			{
				UnderailTrain(Trains[i]);
			});
		}

		
		/// <summary>Un-derails a train</summary>
		/// <param name="Train">The train</param>
		public static void UnderailTrain(TrainBase Train)
		{
			Train.Derailed = false;
			for (int i = 0; i < Train.Cars.Length; i++)
			{
				Train.Cars[i].Specs.RollDueToTopplingAngle = 0.0;
				Train.Cars[i].Derailed = false;
				if (Train.Cars[i].TractionModel.ProvidesPower && Train.Cars[i].Sounds.Loop != null)
				{
					Train.Cars[i].Sounds.Loop.Play(Train.Cars[i], true);
				}
			}
		}

		/// <summary>Updates the objects for all trains within the simulation world</summary>
		/// <param name="TimeElapsed">The time elapsed</param>
		/// <param name="ForceUpdate">Whether this is a forced update</param>
		public void UpdateTrainObjects(double TimeElapsed, bool ForceUpdate)
		{
			// OBNS: the cars of every train on several cores (see UpdateTrainObjectsParallel).
			if (OpenBve.ParallelUpdates.Enabled && !ForceUpdate && currentHost.SimulationState == SimulationState.Running)
			{
				UpdateTrainObjectsParallel(TimeElapsed);
				return;
			}

			for (int i = 0; i < Trains.Count; i++)
			{
				Trains[i].UpdateObjects(TimeElapsed, ForceUpdate);
			}

			// ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
			foreach (ScriptedTrain Train in TFOs) //Must not use var, as otherwise the wrong inferred type
			{
				Train.UpdateObjects(TimeElapsed, ForceUpdate);
			}
		}

		/// <summary>OBNS: the cars UpdateTrainObjectsParallel updates on several cores, kept between frames.</summary>
		private readonly List<CarBase> parallelCars = new List<CarBase>();

		/// <summary>OBNS: the time each of those cars is updated with (Potato mode: the frames it skipped included).</summary>
		private readonly List<double> parallelElapsed = new List<double>();

		/*
		 * OBNS: Potato mode (PotatoMode.cs in the program; Android's CarUpdateLod, the user's choice 2026-11-07 for the
		 * Switch, with the same thresholds). Cars the camera doesn't see well are updated less often: a car wholly behind the
		 * camera every BehindSeconds, one beyond FarDistance every other frame. The time skipped goes to the next update, so
		 * wheels, doors and the rest keep their pace; a car back in view is updated the same frame. And scripted trains
		 * beyond FarTrainDistance along the line skip the world-coordinate and suspension pass (the program's TrainManager,
		 * FarAlongTheLine); their track positions, which signals and timetables go by, are updated every frame as before.
		 */
		private const double BehindSeconds = 0.25, FarDistance = 150.0, FarTrainDistance = 1500.0;

		/// <summary>OBNS: whether cars and scripted trains the camera hardly sees are updated less often (Potato mode).</summary>
		public static bool CarUpdateLod;

		/// <summary>OBNS: scripted trains whose world pass was skipped last frame, and cars left for a later frame, for the log.</summary>
		public static int FarTrainsSkipped, CarsDeferred;

		private readonly Dictionary<CarBase, double> carPending = new Dictionary<CarBase, double>();
		private int carFrame;

		/// <summary>OBNS: whether a car's objects are updated this frame, and with how much time (the skipped frames' with it).</summary>
		private bool CarDue(CarBase car, int index, double timeElapsed, out double elapsed)
		{
			if (!CarUpdateLod)
			{
				elapsed = timeElapsed;
				return true;
			}

			carPending.TryGetValue(car, out double pending);
			elapsed = pending + timeElapsed;
			if (Renderer?.Camera != null)
			{
				OpenBveApi.Math.Vector3 middle = 0.5 * (car.FrontAxle.Follower.WorldPosition + car.RearAxle.Follower.WorldPosition);
				OpenBveApi.Math.Vector3 offset = middle - Renderer.Camera.AbsolutePosition;
				OpenBveApi.Math.Vector3 direction = Renderer.Camera.AbsoluteDirection;
				double ahead = offset.X * direction.X + offset.Y * direction.Y + offset.Z * direction.Z;
				bool behind = ahead < -0.5 * car.Length - 10.0;
				bool far = offset.NormSquared() > FarDistance * FarDistance;
				if ((behind && elapsed < BehindSeconds) || (!behind && far && ((carFrame + index) & 1) != 0 && pending == 0.0))
				{
					carPending[car] = elapsed;
					CarsDeferred++;
					return false;
				}
			}

			carPending[car] = 0.0;
			return true;
		}

		/// <summary>
		/// OBNS: whether a scripted train is far along the line from the player's (here, the driver car's track position):
		/// none of its cars within FarTrainDistance. Its world-coordinate pass then waits (Potato mode).
		/// </summary>
		public static bool FarAlongTheLine(TrainBase train, double here)
		{
			if (!CarUpdateLod || double.IsNaN(here) || train.Cars.Length == 0)
			{
				return false;
			}

			// Android's 1500 m is more than twice its longest viewing distance; a desktop's may be longer, and a train in view
			// must move.
			double distance = Math.Max(FarTrainDistance, 2.0 * (CurrentOptions?.ViewingDistance ?? 0));
			foreach (CarBase car in train.Cars)
			{
				if (Math.Abs(car.FrontAxle.Follower.TrackPosition - here) < distance || Math.Abs(car.RearAxle.Follower.TrackPosition - here) < distance)
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>OBNS: the player's driver car's track position, for FarAlongTheLine; NaN without a player's train.</summary>
		public static double PlayerTrackPosition => PlayerTrain != null && PlayerTrain.Cars.Length > 0 && PlayerTrain.DriverCar >= 0 && PlayerTrain.DriverCar < PlayerTrain.Cars.Length
			? PlayerTrain.Cars[PlayerTrain.DriverCar].FrontAxle.Follower.TrackPosition : double.NaN;

		/// <summary>
		/// OBNS: TrainBase.UpdateObjects for every train, with the cars of every train updated on several cores
		/// (OBNS/shared/ParallelUpdates.cs, as the Android port does): each car, with its bogies and the coupler
		/// behind it, only moves its own objects. On this thread: the player's driver car while it shows the cab
		/// (its LED elements rebuild GPU buffers), and the windscreens, after the others.
		/// </summary>
		private void UpdateTrainObjectsParallel(double timeElapsed)
		{
			List<TrainBase> trains = new List<TrainBase>(Trains.Count + TFOs.Count);
			trains.AddRange(Trains);
			foreach (AbstractTrain train in TFOs)
			{
				if (train is ScriptedTrain scripted)
				{
					trains.Add(scripted);
				}
			}

			parallelCars.Clear();
			parallelElapsed.Clear();
			carFrame++;
			int index = 0;
			foreach (TrainBase train in trains)
			{
				for (int i = 0; i < train.Cars.Length; i++, index++)
				{
					CarBase car = train.Cars[i];
					if (train.IsPlayerTrain && i == train.DriverCar && car.CarSections.TryGetValue(car.CurrentCarSection, out CarSection section) && section.Type == ObjectType.Overlay)
					{
						UpdateCarObjects(car, timeElapsed);
					}
					else if (CarDue(car, index, timeElapsed, out double elapsed))
					{
						parallelCars.Add(car);
						parallelElapsed.Add(elapsed);
					}
				}
			}

			OpenBve.ParallelUpdates.Active = true;
			try
			{
				OpenBve.FrameWorkers.Shared.Run(parallelCars.Count, (start, end) =>
				{
					for (int i = start; i < end; i++)
					{
						UpdateCarObjects(parallelCars[i], parallelElapsed[i]);
					}
				}, 4);
			}
			finally
			{
				OpenBve.ParallelUpdates.End(currentHost);
			}

			foreach (TrainBase train in trains)
			{
				if (train.DriverCar >= 0 && train.DriverCar < train.Cars.Length)
				{
					train.Cars[train.DriverCar].Windscreen?.Update(timeElapsed);
				}
			}
		}

		/// <summary>OBNS: one car's part of TrainBase.UpdateObjects: the car, its bogies and its coupler.</summary>
		private static void UpdateCarObjects(CarBase car, double timeElapsed)
		{
			car.UpdateObjects(timeElapsed, false, true);
			car.FrontBogie.UpdateObjects(timeElapsed, false);
			car.RearBogie.UpdateObjects(timeElapsed, false);
			car.Coupler.UpdateObjects(timeElapsed, false);
		}
		
		/// <summary>Performs the jump for all TFOs</summary>
		public void JumpTFO()
		{
			// ReSharper disable once PossibleInvalidCastExceptionInForeachLoop
			foreach (ScriptedTrain Train in TFOs) //Must not use var, as otherwise the wrong inferred type
			{
				Train.Jump(-1, 0);
			}
		}
	}
}
