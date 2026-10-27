// OBNS: a copy of upstream's source/TrainManager/TrainManager.cs (OpenBVE 1.14.1.2), laid over the desktop build's
// mirror (OBNS/build-desktop.ps1): every train's car objects updated on several cores (UpdateTrainObjectsParallel).
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
			foreach (TrainBase train in trains)
			{
				for (int i = 0; i < train.Cars.Length; i++)
				{
					CarBase car = train.Cars[i];
					if (train.IsPlayerTrain && i == train.DriverCar && car.CarSections.TryGetValue(car.CurrentCarSection, out CarSection section) && section.Type == ObjectType.Overlay)
					{
						UpdateCarObjects(car, timeElapsed);
					}
					else
					{
						parallelCars.Add(car);
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
						UpdateCarObjects(parallelCars[i], timeElapsed);
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
