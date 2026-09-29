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
using System.IO;
using System.Linq;
using System.Text;
using Android.Util;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Routes;
using OpenBveApi.Runtime;
using OpenBveApi.Trains;
using RouteManager2;
using RouteManager2.SignalManager.PreTrain;
using RouteManager2.Stations;
using TrainManager;
using TrainManager.Car;
using TrainManager.Handles;
using TrainManager.Trains;
using Path = System.IO.Path;

namespace OpenBve.Android
{
	/// <summary>
	/// The player train: loading it, placing it at the first station, and advancing the
	/// simulation each frame with the camera in the driver's cab.
	/// </summary>
	/// <remarks>
	/// A port of the relevant parts of upstream's Loading (train creation and plugin dispatch)
	/// and GameWindow (initial placement, signalling, the fast-forward to the start time) sequences,
	/// for the player train and the route's preceding trains. Not yet ported: the score.
	/// </remarks>
	public class AndroidTrainSession
	{
		private const string Tag = "OpenBVE";

		private readonly AndroidHost host;
		private readonly AndroidTrainManager trainManager;
		private readonly AndroidRenderer renderer;
		private readonly CurrentRoute route;
		private double sectionUpdateTimer;

		/// <summary>The player train.</summary>
		public TrainBase Train { get; }

		/// <summary>The controls the train's panels added for their touch areas, which name them by index.</summary>
		public Control[] TouchControls { get; private set; } = new Control[0];

		/// <summary>The camera and its view modes.</summary>
		public AndroidCamera Camera { get; }

		/// <summary>
		/// The stations the player can jump to from the pause menu, as upstream's menu lists them:
		/// those where the player train stops. Route data, fixed once loaded, so safe to read from
		/// the UI thread.
		/// </summary>
		public IReadOnlyList<(int Index, string Name)> JumpStations { get; }

		private AndroidTrainSession(AndroidHost host, AndroidTrainManager trainManager, AndroidRenderer renderer, CurrentRoute route, TrainBase train)
		{
			this.host = host;
			this.trainManager = trainManager;
			this.renderer = renderer;
			this.route = route;
			Train = train;
			Camera = new AndroidCamera(renderer, route, trainManager);
			List<(int, string)> stations = new List<(int, string)>();
			for (int i = 0; i < route.Stations.Length; i++)
			{
				if (route.Stations[i].PlayerStops() && route.Stations[i].Stops.Length > 0)
				{
					stations.Add((i, route.Stations[i].Name));
				}
			}

			JumpStations = stations;
		}

		/// <summary>Loads a train and places it on the route.</summary>
		/// <param name="trainFolder">The train chosen in the menu, or null for the first train under the train folder.</param>
		/// <param name="encoding">The train's text encoding, or null for UTF-8.</param>
		/// <param name="session">Receives the session, or null if no train loaded.</param>
		/// <returns>A one line summary for the status display.</returns>
		public static string Load(AndroidHost host, AndroidTrainManager trainManager, AndroidRenderer renderer,
			CurrentRoute route, OpenBveApi.FileSystem.FileSystem fileSystem, string trainFolder, Encoding encoding, out AndroidTrainSession session)
		{
			session = null;
			encoding = encoding ?? Encoding.UTF8;
			trainFolder = string.IsNullOrEmpty(trainFolder) ? FindTrain(fileSystem.InitialTrainFolder, host) : trainFolder;
			if (trainFolder == null)
			{
				return "no trains found - copy a train folder into " + fileSystem.InitialTrainFolder;
			}

			ContentLoadingPlugin plugin = host.Plugins.FirstOrDefault(p => p.Train != null && p.Train.CanLoadTrain(trainFolder));
			if (plugin == null)
			{
				return "no train plugin accepted " + Path.GetFileName(trainFolder);
			}

			TrainBase train = new TrainBase(TrainState.Pending, TrainType.LocalPlayerTrain);
			trainManager.Trains = new List<TrainBase> { train };
			TrainManagerBase.PlayerTrain = train;
			host.TrainList = trainManager.Trains;
			host.TrackFollowingObjects = trainManager.TFOs;

			AbstractTrain abstractTrain = train;
			Control[] controls = new Control[0];
			LoadProgress.Train = plugin.Train;
			if (!plugin.Train.LoadTrain(encoding, trainFolder, ref abstractTrain, ref controls))
			{
				return "train " + Path.GetFileName(trainFolder) + " failed to load - see logcat";
			}


			/*
			 * Upstream always gives the player train a safety-system plugin: the train's own (ats.cfg)
			 * if it can load, otherwise OpenBVE's built-in default (OpenBveAts). It must, because the
			 * power and brake handles only follow the driver through the plugin's safety state -
			 * with no plugin at all, the actual power notch stays at zero whatever the driver does.
			 * On Android a native Win32 plugin is refused as Windows-only, so trains that ship one
			 * fall back to the default here.
			 */
			string pluginSummary;
			InitializationModes startMode = (InitializationModes)host.Options.TrainStart;
			if (NativePlugins.NativePluginBridge.TryLoad(train, trainFolder, encoding, startMode, out string translated))
			{
				// A Win32 plugin, identified and run as a managed translation.
				pluginSummary = translated;
			}
			else if (train.LoadCustomPlugin(trainFolder, encoding))
			{
				pluginSummary = "the train's own plugin";
			}
			else
			{
				train.LoadDefaultPlugin(trainFolder);
				pluginSummary = "OpenBVE default (ATS-Sx)";
			}

			Log.Info(Tag, "safety plugin: " + (train.Plugin == null ? "none" : pluginSummary));

			string others = LoadPrecedingTrains(host, trainManager, route, plugin, trainFolder, encoding);
			session = new AndroidTrainSession(host, trainManager, renderer, route, train)
			{
				// The panel's touch areas name their commands by index into these (CabTouch).
				TouchControls = controls
			};
			int touchAreas = train.Cars[train.DriverCar].CarSections.TryGetValue(LibRender2.Trains.CarSectionType.Interior, out LibRender2.Trains.CarSection cab)
				? cab.Groups.Sum(g => g.TouchElements?.Length ?? 0)
				: 0;
			Log.Info(Tag, "cab touch areas: " + touchAreas + " in " + (cab?.Groups.Length ?? 0) + " panel group(s), " + controls.Length + " touch control(s)");
			session.PlaceAtFirstStation();
			return "train " + Path.GetFileName(trainFolder) + ": " + train.Cars.Length + " car(s), starting at " +
			       train.Cars[0].TrackPosition.ToString("0") + " m" + others;
		}

		/*
		 * The route's other timetabled traffic, as upstream's Loading sets it up:
		 *  - Train.RunInterval: preceding trains, each a copy of the player's train running the
		 *    same timetable the given number of seconds earlier, driven by SimpleHumanDriverAI;
		 *  - RunInterval / PreTrain track following objects: the same, but a train of the file's
		 *    choosing (parsed with the route; moved here from the scripted trains, as upstream does,
		 *    so that they occupy signalling sections);
		 *  - .PreTrain: an invisible train that only occupies sections, moved through the route's
		 *    timed positions by BogusPretrainAI.
		 * Upstream loads one invisible train per .PreTrain instruction, and all of them follow the
		 * same instructions to the same places. One behaves identically and loads once.
		 */
		private static string LoadPrecedingTrains(AndroidHost host, AndroidTrainManager trainManager, CurrentRoute route, ContentLoadingPlugin plugin,
			string trainFolder, Encoding encoding)
		{
			NormalizeBogusInstructions(route);
			int preceding = 0;
			for (int i = 0; i < route.PrecedingTrainTimeDeltas.Length; i++)
			{
				TrainBase other = LoadCopy(plugin, trainFolder, encoding, TrainState.Pending);
				if (other == null)
				{
					continue;
				}

				other.AI = new Game.SimpleHumanDriverAI(other, host.Options.PrecedingTrainSpeedLimit);
				other.TimetableDelta = route.PrecedingTrainTimeDeltas[i];
				other.Specs.DoorOpenMode = DoorMode.Manual;
				other.Specs.DoorCloseMode = DoorMode.Manual;
				trainManager.Trains.Add(other);
				preceding++;
			}

			bool bogus = false;
			if (route.BogusPreTrainInstructions.Length != 0)
			{
				TrainBase other = LoadCopy(plugin, trainFolder, encoding, TrainState.Bogus);
				if (other != null)
				{
					trainManager.Trains.Add(other);
					bogus = true;
				}
			}

			int scripted = 0;
			for (int i = trainManager.TFOs.Count - 1; i >= 0; i--)
			{
				if (trainManager.TFOs[i].Type == TrainType.PreTrain && trainManager.TFOs[i] is TrainBase other)
				{
					trainManager.Trains.Add(other);
					route.PrecedingTrainTimeDeltas = route.PrecedingTrainTimeDeltas.Append(other.TimetableDelta).ToArray();
					trainManager.TFOs.RemoveAt(i);
					scripted++;
				}
			}

			Log.Info(Tag, "preceding trains: " + preceding + " by run interval, " + scripted + " from track following objects" +
			              (bogus ? ", and an invisible .PreTrain train over " + route.BogusPreTrainInstructions.Length + " timed positions" : string.Empty));
			int total = preceding + scripted;
			return total == 0 && !bogus ? string.Empty
				: "\npreceding trains: " + total + (bogus ? " + invisible pre-train" : string.Empty);
		}

		/// <summary>Loads another copy of the player's train, for a preceding or invisible train.</summary>
		private static TrainBase LoadCopy(ContentLoadingPlugin plugin, string trainFolder, Encoding encoding, TrainState state)
		{
			TrainBase other = new TrainBase(state, TrainType.PreTrain);
			AbstractTrain abstractTrain = other;
			Control[] controls = new Control[0];
			using (LoadProfile.Enter("preceding trains"))
			{
				if (!plugin.Train.LoadTrain(encoding, trainFolder, ref abstractTrain, ref controls) || other.Cars.Length == 0)
				{
					Log.Warn(Tag, "a preceding train failed to load");
					return null;
				}
			}

			return other;
		}

		/// <summary>
		/// As upstream's Loading: the .PreTrain times and positions must both increase, so any that
		/// do not are pushed on by a second or a metre past the one before.
		/// </summary>
		private static void NormalizeBogusInstructions(CurrentRoute route)
		{
			BogusPreTrainInstruction[] instructions = route.BogusPreTrainInstructions;
			if (instructions.Length == 0)
			{
				return;
			}

			double t = instructions[0].Time;
			double p = instructions[0].TrackPosition;
			for (int i = 1; i < instructions.Length; i++)
			{
				if (instructions[i].Time > t)
				{
					t = instructions[i].Time;
				}
				else
				{
					t += 1.0;
					instructions[i].Time = t;
				}

				if (instructions[i].TrackPosition > p)
				{
					p = instructions[i].TrackPosition;
				}
				else
				{
					p += 1.0;
					instructions[i].TrackPosition = p;
				}
			}
		}

		/// <summary>
		/// Mirrors upstream's GameWindow setup: find the player's first station stop and the other
		/// trains' first station, initialise every train, enter the first signalling section, move
		/// the trains into position, put the camera in the driver's car, and run the simulation
		/// forward to the start time so that preceding trains are where their timetable has them.
		/// </summary>
		private void PlaceAtFirstStation()
		{
			if (route.Sections.Length > 0)
			{
				route.UpdateAllSections();
			}

			renderer.CameraTrackFollower.TriggerType = EventTriggerType.Camera;

			int firstStation = route.PlayerFirstStationIndex;
			double startPosition = 0.0;
			double startupTime = 0.0;
			if (firstStation >= 0 && firstStation < route.Stations.Length)
			{
				RouteStation station = route.Stations[firstStation];
				int stop = station.GetStopIndex(Train);
				startPosition = stop >= 0 ? station.Stops[stop].TrackPosition : station.DefaultTrackPosition;

				// The route's own start time, else the first station's arrival, else its departure less the stop time.
				if (route.InitialStationTime != -1)
				{
					startupTime = route.InitialStationTime;
				}
				else if (station.ArrivalTime >= 0.0)
				{
					startupTime = station.ArrivalTime;
				}
				else if (station.DepartureTime >= 0.0)
				{
					startupTime = station.DepartureTime - station.StopTime;
				}

				route.SecondsSinceMidnight = startupTime;
			}

			// The other trains start from the first station where every train stops.
			int otherFirstStation = -1;
			double otherFirstPosition = 0.0;
			double otherFirstTime = 0.0;
			for (int i = 0; i < route.Stations.Length; i++)
			{
				RouteStation station = route.Stations[i];
				if (station.StopMode == StationStopMode.AllStop | station.StopMode == StationStopMode.PlayerPass & station.Stops.Length != 0)
				{
					otherFirstStation = i;
					int stop = station.GetStopIndex(Train);
					otherFirstPosition = stop >= 0 ? station.Stops[stop].TrackPosition : station.DefaultTrackPosition;
					otherFirstTime = station.ArrivalTime >= 0.0 ? station.ArrivalTime
						: station.DepartureTime >= 0.0 ? station.DepartureTime - station.StopTime : 0.0;
					break;
				}
			}

			// With preceding trains, the clock starts early enough for the earliest of them to set off.
			if (route.PrecedingTrainTimeDeltas.Length != 0)
			{
				otherFirstTime -= route.PrecedingTrainTimeDeltas[route.PrecedingTrainTimeDeltas.Length - 1];
				if (otherFirstTime < route.SecondsSinceMidnight)
				{
					route.SecondsSinceMidnight = otherFirstTime;
				}
			}

			/*
			 * The view must be chosen before the train initialises, because each car picks which of
			 * its sections to show (cab interior or exterior) from the camera mode as it does so.
			 * The train plugin has already set the restriction mode from the cab type it loaded.
			 */
			if (Train.Cars[Train.DriverCar].CarSections.ContainsKey(LibRender2.Trains.CarSectionType.Interior))
			{
				renderer.Camera.CurrentMode = CameraViewMode.Interior;
			}
			else
			{
				renderer.Camera.CurrentMode = CameraViewMode.Exterior;
				renderer.Camera.CurrentRestriction = OpenBveApi.Graphics.CameraRestrictionMode.Off;
			}

			foreach (TrainBase train in trainManager.Trains)
			{
				train.Initialize();
				int s = train.IsPlayerTrain ? firstStation : otherFirstStation;
				if (s >= 0 && s < route.Stations.Length)
				{
					foreach (CarBase car in train.Cars)
					{
						car.Doors[0].AnticipatedOpen |= route.Stations[s].OpenLeftDoors;
						car.Doors[1].AnticipatedOpen |= route.Stations[s].OpenRightDoors;
					}
				}

				if (route.Sections.Length != 0)
				{
					route.Sections[0].Enter(train);
				}

				// Upstream nudges each car back and forth once so that its followers settle.
				double carLength = train.Cars[0].Length;
				foreach (CarBase car in train.Cars)
				{
					car.Move(-carLength);
					car.Move(carLength);
				}
			}

			// Track following objects, loaded with the route, initialise the same way.
			foreach (AbstractTrain tfo in trainManager.TFOs)
			{
				if (!(tfo is ScriptedTrain scripted) || scripted.Cars.Length == 0)
				{
					continue;
				}

				scripted.Initialize();
				double length = scripted.Cars[0].Length;
				foreach (CarBase car in scripted.Cars)
				{
					car.Move(-length);
					car.Move(length);
				}
			}

			Log.Info(Tag, trainManager.TFOs.Count + " track following object(s)");

			if (route.Sections.Length > 0)
			{
				route.UpdateAllSections();
			}

			foreach (TrainBase train in trainManager.Trains)
			{
				double position;
				if (train.IsPlayerTrain)
				{
					position = startPosition;
				}
				else if (train.State == TrainState.Bogus)
				{
					position = route.BogusPreTrainInstructions[0].TrackPosition;
					train.AI = new Game.BogusPretrainAI(train);
				}
				else
				{
					position = otherFirstPosition;
				}

				foreach (CarBase car in train.Cars)
				{
					car.Move(position);
				}
			}

			foreach (TrainBase train in trainManager.Trains)
			{
				if (route.Sections.Length > train.CurrentSectionIndex)
				{
					route.Sections[train.CurrentSectionIndex].Enter(train);
				}
			}

			if (route.Sections.Length > 0)
			{
				route.UpdateAllSections();
			}

			FastForward(startupTime);

			Camera.Initialize(startPosition);
			/*
			 * Upstream's GameWindow preloads every texture of the player train here
			 * (TrainBase.PreloadTextures). Not on Android: for the MTR R-Train that is 911 textures,
			 * ~2.8 GB once uploaded with mipmaps (most of them cab display frames, only one of which
			 * shows at a time), and on a phone GPU memory is system RAM - the low-memory killer
			 * ended the game. Textures load as they are first drawn instead, and unused ones are
			 * unloaded again (AndroidOptions.UnloadUnusedTextures).
			 */
			// Positions every car's objects, including the cab, which is attached to the camera.
			trainManager.UpdateTrainObjects(0.0, true);
			renderer.UpdateVisibility(true);
			host.UpdateAnimatedWorldObjects(renderer, 0.0, true);
			Log.Info(Tag, "player train placed at " + startPosition.ToString("0.0") + " m, driver car " + Train.DriverCar +
			              ", view " + renderer.Camera.CurrentMode + ", camera restriction " + renderer.Camera.CurrentRestriction);
		}

		/*
		 * Upstream's fast-forward to the start time: with preceding trains the clock starts before
		 * the player's, and the simulation runs on in quarter-second steps, with no collisions and
		 * with trains waiting for their timetabled times, until the player's start time. The
		 * preceding trains are then already on their way, and the signals show them.
		 */
		private void FastForward(double startupTime)
		{
			double remaining = startupTime - route.SecondsSinceMidnight;
			if (remaining <= 0.0)
			{
				return;
			}

			System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
			const double step = 0.25;
			double sinceSectionUpdate = 0.0;
			long trainTicks = 0;
			host.MinimalisticSimulation = true;
			try
			{
				while (remaining > 0.0)
				{
					double v = Math.Min(step, remaining);
					remaining -= v;
					route.SecondsSinceMidnight += v;
					long before = timer.ElapsedTicks;
					trainManager.UpdateTrains(v);
					trainTicks += timer.ElapsedTicks - before;
					sinceSectionUpdate += v;
					if (sinceSectionUpdate >= 1.0 && route.Sections.Length > 0)
					{
						route.UpdateAllSections();
						sinceSectionUpdate = 0.0;
					}
				}
			}
			finally
			{
				host.MinimalisticSimulation = false;
			}

			Log.Info(Tag, "fast-forwarded to the start time in " + timer.ElapsedMilliseconds + " ms (trains " +
			              trainTicks * 1000 / System.Diagnostics.Stopwatch.Frequency + " ms); " +
			              string.Join(", ", trainManager.Trains.Where(t => !t.IsPlayerTrain).Select(t =>
				              t.State + " train at " + t.Cars[0].TrackPosition.ToString("0") + " m")));
		}

		/// <summary>
		/// Sets up what upstream's in-game overlays show, as its GameWindow does once the train is
		/// placed: the score's first arrival station and maximum, the auto-generated timetable, and
		/// the route's own timetable for the first station (shown by default where it exists).
		/// </summary>
		public void PrepareOverlays()
		{
			// The route map and gradient profile for ROUTE_INFORMATION, drawn in the background as upstream's Loading does.
			new System.Threading.Thread(() =>
			{
				try
				{
					route.Information.LoadInformation();
				}
				catch (Exception ex)
				{
					Log.Warn(Tag, "route map: " + ex.Message);
				}
			}) { IsBackground = true, Name = "Illustrations" }.Start();

			int first = route.PlayerFirstStationIndex;
			Game.CurrentScore.ArrivalStation = first + 1;
			Game.CurrentScore.DepartureStation = first;
			Game.CurrentScore.Maximum = 0;
			for (int i = 0; i < route.Stations.Length; i++)
			{
				if (i != first & route.Stations[i].PlayerStops())
				{
					if (i == 0 || route.Stations[i - 1].Type != StationType.ChangeEnds && route.Stations[i - 1].Type != StationType.Jump)
					{
						Game.CurrentScore.Maximum += Game.ScoreValueStationArrival;
					}
				}
			}

			if (Game.CurrentScore.Maximum <= 0)
			{
				Game.CurrentScore.Maximum = Game.ScoreValueStationArrival;
			}

			try
			{
				lock (LibRender2.BaseRenderer.GdiPlusLock)
				{
					Timetable.CreateTimetable();
				}

				if (Train.Station >= 0)
				{
					RouteStation station = route.Stations[Train.Station];
					Timetable.UpdateCustomTimetable(station.TimetableDaytimeTexture, station.TimetableNighttimeTexture);
					// Upstream opens the route's own timetable at the start; the touch interface has its card instead.
				if (!TouchInterface && Timetable.CustomObjectsUsed != 0 & Timetable.CustomTimetableAvailable &&
					    Interface.CurrentOptions.TimeTableStyle != LibRender2.Overlays.TimeTableMode.AutoGenerated &&
					    Interface.CurrentOptions.TimeTableStyle != LibRender2.Overlays.TimeTableMode.None)
					{
						renderer.CurrentTimetable = LibRender2.Overlays.DisplayedTimetable.Custom;
					}
				}
			}
			catch (Exception ex)
			{
				// A timetable that cannot be drawn should not stop the game.
				Log.Warn(Tag, "timetable: " + ex.Message);
			}
		}

		/// <summary>Advances the simulation by one frame.</summary>
		public void Update(double timeElapsed)
		{
			route.SecondsSinceMidnight += timeElapsed;
			trainManager.UpdateTrains(timeElapsed);
			renderer.Profile.Mark("trains");

			// Upstream re-evaluates every signalling section once a second, not every frame.
			sectionUpdateTimer += timeElapsed;
			if (sectionUpdateTimer >= 1.0 && route.Sections.Length > 0)
			{
				sectionUpdateTimer = 0.0;
				route.UpdateAllSections();
			}

			host.UpdateAnimatedWorldObjects(renderer, timeElapsed, false);
			renderer.Profile.Mark("world objects");
			UpdateView(timeElapsed, timeElapsed);
		}

		/// <summary>
		/// Places the camera and the train objects that hang off it. Also the whole of a paused
		/// frame, so the view can still be looked around while the simulation stands still.
		/// </summary>
		/// <param name="cameraTime">Real time since the last frame, for the camera's own motion.</param>
		/// <param name="simulationTime">Simulated time since the last frame: zero while paused.</param>
		public void UpdateView(double cameraTime, double simulationTime)
		{
			Camera.Update(cameraTime);

			// After the camera, as upstream does: the cab objects are placed relative to it.
			trainManager.UpdateTrainObjects(simulationTime, false);
			Camera.EndFrame();
			renderer.Profile.Mark("train objects");
			renderer.UpdateVisibility(false);
		}

		/// <summary>
		/// Moves the player train to a station, as upstream's pause menu does: the train jumps,
		/// the scripted trains are re-placed for the new time, and the world catches up.
		/// </summary>
		public void JumpToStation(int stationIndex)
		{
			Train.Jump(stationIndex, 0);
			trainManager.JumpTFO();
			sectionUpdateTimer = 0.0;
			if (route.Sections.Length > 0)
			{
				route.UpdateAllSections();
			}

			renderer.UpdateVisibility(true);
			Log.Info(Tag, "jumped to station " + stationIndex + " (" + route.Stations[stationIndex].Name + ") at " +
			              Train.Cars[0].TrackPosition.ToString("0") + " m");
		}

		/// <summary>
		/// Whether the touch interface is in use. Its information line then carries the current
		/// in-game messages (station arrivals, signals, doors...) that the desktop HUD, left off,
		/// would otherwise show, and its own timetable card stands in for upstream's.
		/// </summary>
		public static bool TouchInterface { get; set; }

		/// <summary>The train's speed in km/h.</summary>
		public double SpeedKmh => Train.CurrentSpeed * 3.6;

		/// <summary>The line of driving information shown over the cab: speed, limit, handles.</summary>
		/// <summary>The driving information as one text (logs and diagnostics); the touch interface shows <see cref="Status"/>.</summary>
		public string DriverInfo()
		{
			DriverStatus s = Status(string.Empty, false);
			string limitText = s.Limit == null ? string.Empty : s.Limit <= 0.0 ? "   signal at danger" : "   limit " + s.Limit.Value.ToString("0");
			string next = s.NextName == null
				? string.Empty
				: "\nnext stop: " + s.NextName + "   " + s.NextDistance + (s.NextArrival != null ? "   arr " + s.NextArrival : string.Empty) +
				  (s.NextDeparture != null ? "   dep " + s.NextDeparture : string.Empty);
			return s.Speed.ToString("0") + " km/h" + limitText + (s.Warning != null ? "   ⚠ " + s.Warning : string.Empty) + "   |   " + s.Handles +
			       "   |   REV " + s.Reverser + "   |   " + s.Clock + next + string.Concat(s.Messages.Select(m => "\n" + m.Text));
		}

		/// <summary>
		/// What the touch interface's information bar shows, read on the render thread: speed and
		/// limit, handles and reverser, the clock, the next stop, a safety system intervention, and
		/// the in-game messages.
		/// </summary>
		public DriverStatus Status(string cameraMode, bool paused)
		{
			CabHandles h = Train.Handles;
			string reverser = h.Reverser.Driver == ReverserPosition.Forwards ? "F" : h.Reverser.Driver == ReverserPosition.Reverse ? "R" : "N";
			string handles;
			HandleState state;
			if (h.EmergencyBrake.Driver)
			{
				handles = "EMG";
				state = HandleState.Emergency;
			}
			else if (h.HandleType == HandleType.SingleHandle)
			{
				handles = h.Brake.Driver != 0 ? h.Brake.GetNotchDescription(out _) : h.Power.GetNotchDescription(out _);
				state = h.Brake.Driver != 0 ? HandleState.Brake : h.Power.Driver != 0 ? HandleState.Power : HandleState.Neutral;
			}
			else
			{
				handles = h.Power.GetNotchDescription(out _) + " " + h.Brake.GetNotchDescription(out _);
				state = h.Brake.Driver != 0 ? HandleState.Brake : h.Power.Driver != 0 ? HandleState.Power : HandleState.Neutral;
			}

			double limit = Math.Min(Train.CurrentRouteLimit, Train.CurrentSectionLimit);
			// No limit is stored as double.MaxValue or infinity, depending on where it came from.
			double? limitKmh = double.IsNaN(limit) || limit > 1000.0 ? null : Math.Max(0.0, limit * 3.6);

			string warning = SafetyState();
			warning = warning.Length == 0 ? null : warning.Replace("⚠", string.Empty).Trim();

			/*
			 * The next station the player's train stops at and how far off its stop point is,
			 * chosen as upstream's HUD chooses it (Overlays.HUD, DistNextStation2): the station
			 * being served until its stop is completed, otherwise the next one after the last
			 * served, skipping stations the train passes. Negative once the front of the train is
			 * past the stop point.
			 */
			string nextName = null, nextDistance = null, nextArrival = null, nextDeparture = null;
			int index = Train.Station >= 0 && Train.StationState != TrainStopState.Completed ? Train.LastStation : Train.LastStation + 1;
			index = Math.Max(0, Math.Min(index, route.Stations.Length - 1));
			for (int i = index; i < route.Stations.Length; i++)
			{
				RouteStation station = route.Stations[i];
				if (!station.PlayerStops())
				{
					continue;
				}

				int stop = station.GetStopIndex(Train);
				double position = station.Stops.Length > 0 && stop >= 0 ? station.Stops[stop].TrackPosition : station.DefaultTrackPosition;
				double distance = position - Train.FrontCarTrackPosition;
				nextName = station.Name;
				nextDistance = Math.Abs(distance) < 1000.0 ? distance.ToString("0") + " m" : (distance / 1000.0).ToString("0.00") + " km";
				// The timetabled arrival and departure (none at a terminus, or where the route gives none).
				nextArrival = station.ArrivalTime >= 0.0 ? ClockText(station.ArrivalTime) : null;
				nextDeparture = station.DepartureTime >= 0.0 && station.Type != StationType.Terminal ? ClockText(station.DepartureTime) : null;
				break;
			}

			return new DriverStatus(SpeedKmh, limitKmh, handles, state, reverser, ClockText(route.SecondsSinceMidnight), nextName, nextDistance,
				nextArrival, nextDeparture, warning, CurrentMessages(), cameraMode, paused);
		}

		/// <summary>
		/// The timetable as the touch interface shows it: every named station in route order, as
		/// upstream's generated timetable lists them, with where the train is now. Station data is
		/// fixed once loaded and the positions are single reads, so the UI thread may call this.
		/// </summary>
		public TimetableSnapshot GetTimetable()
		{
			int index = Train.Station >= 0 && Train.StationState != TrainStopState.Completed ? Train.LastStation : Train.LastStation + 1;
			int next = -1;
			for (int i = Math.Max(0, index); i < route.Stations.Length; i++)
			{
				if (route.Stations[i].PlayerStops())
				{
					next = i;
					break;
				}
			}

			double front = Train.FrontCarTrackPosition;
			List<TimetableRow> rows = new List<TimetableRow>();
			for (int i = 0; i < route.Stations.Length; i++)
			{
				RouteStation station = route.Stations[i];
				if (station.Name.Length == 0 || station.Dummy)
				{
					continue;
				}

				bool stops = station.PlayerStops();
				int stop = station.GetStopIndex(Train);
				double position = station.Stops.Length > 0 && stop >= 0 ? station.Stops[stop].TrackPosition : station.DefaultTrackPosition;
				TimetableRowState state;
				if (i == next)
				{
					state = Train.Station == i && Train.StationState != TrainStopState.Completed ? TimetableRowState.AtStation : TimetableRowState.Next;
				}
				else if (next >= 0 ? i < next && (stops || position < front) : position < front)
				{
					state = TimetableRowState.Done;
				}
				else
				{
					state = TimetableRowState.Ahead;
				}

				rows.Add(new TimetableRow(station.Name, station.ArrivalTime >= 0.0 ? ClockText(station.ArrivalTime) : null,
					station.DepartureTime >= 0.0 && station.Type != StationType.Terminal ? ClockText(station.DepartureTime) : null,
					!stops, station.Type != StationType.Normal, position - front, state,
					stops && station.ArrivalTime >= 0.0 ? station.ArrivalTime - route.SecondsSinceMidnight : double.NaN,
					stops && station.DepartureTime >= 0.0 ? station.DepartureTime - route.SecondsSinceMidnight : double.NaN));
			}

			// The route's own timetable picture for the current section, when it has one on disk.
			string image = (OpenBve.Timetable.CurrentCustomTimetableDaytimeTexture?.Origin as OpenBveApi.Textures.PathOrigin)?.Path;
			return new TimetableSnapshot(rows, ClockText(route.SecondsSinceMidnight), image != null && File.Exists(image) ? image : null);
		}

		private RouteGeometry geometry;

		/// <summary>
		/// The line as the touch interface's map and gradient chart draw it: the track's plan and
		/// elevation over the part the stations use (upstream's illustrations' range), the named
		/// stations, and the speed limits. Built once; the track does not change after loading.
		/// </summary>
		public RouteGeometry GetRouteGeometry()
		{
			if (geometry != null)
			{
				return geometry;
			}

			TrackElement[] elements = route.Tracks[0].Elements;
			int first = elements.Length - 1, last = 0;
			for (int i = 0; i < elements.Length; i++)
			{
				if (elements[i].Events.Any(e => e is RouteManager2.Events.StationStartEvent))
				{
					first = Math.Min(first, i);
					last = Math.Max(last, i);
				}
			}

			// As upstream's TotalRouteRange: a little before the first station and after the last.
			first = Math.Max(0, first - 4);
			last = Math.Min(elements.Length - 1, last + 8);
			if (last <= first)
			{
				first = 0;
				last = elements.Length - 1;
			}

			int count = last - first + 1;
			double[] position = new double[count], x = new double[count], y = new double[count], z = new double[count], pitch = new double[count];
			List<RouteLimit> limits = new List<RouteLimit>();
			for (int i = 0; i < count; i++)
			{
				TrackElement element = elements[first + i];
				position[i] = element.StartingTrackPosition;
				x[i] = element.WorldPosition.X;
				y[i] = element.WorldPosition.Y;
				z[i] = element.WorldPosition.Z;
				pitch[i] = element.Pitch;
			}

			// Every limit on the line: the one in force at the first station is often set further back.
			foreach (TrackElement element in elements)
			{
				foreach (GeneralEvent e in element.Events)
				{
					if (e is RouteManager2.Events.LimitChangeEvent limit)
					{
						limits.Add(new RouteLimit(element.StartingTrackPosition + limit.TrackPositionDelta,
							double.IsPositiveInfinity(limit.NextSpeedLimit) ? double.PositiveInfinity : limit.NextSpeedLimit * 3.6));
					}
				}
			}

			List<RouteStationMark> stations = new List<RouteStationMark>();
			for (int i = 0; i < route.Stations.Length; i++)
			{
				RouteStation station = route.Stations[i];
				if (station.Name.Length == 0 || station.Dummy)
				{
					continue;
				}

				int stop = station.GetStopIndex(Train);
				double at = station.Stops.Length > 0 && stop >= 0 ? station.Stops[stop].TrackPosition : station.DefaultTrackPosition;
				int k = Array.BinarySearch(position, at);
				k = Math.Max(0, Math.Min(count - 1, k < 0 ? ~k - 1 : k));
				stations.Add(new RouteStationMark(i, station.Name, at, x[k], y[k], z[k], station.PlayerStops()));
			}

			Log.Info(Tag, "route geometry: " + count + " elements, " + stations.Count + " stations, " + limits.Count + " speed limits");
			return geometry = new RouteGeometry(position, x, y, z, pitch, stations, limits);
		}

		/// <summary>Where the trains are now, for the touch map and gradient chart.</summary>
		public RouteLive GetRouteLive()
		{
			List<(double X, double Z, bool Player)> trains = new List<(double, double, bool)>();
			foreach (TrainBase train in trainManager.Trains)
			{
				if (train.State == TrainState.Available)
				{
					OpenBveApi.Math.Vector3 p = train.Cars[0].FrontAxle.Follower.WorldPosition;
					trains.Add((p.X, p.Z, train.IsPlayerTrain));
				}
			}

			int index = Train.Station >= 0 && Train.StationState != TrainStopState.Completed ? Train.LastStation : Train.LastStation + 1;
			int next = -1;
			for (int i = Math.Max(0, index); i < route.Stations.Length; i++)
			{
				if (route.Stations[i].PlayerStops())
				{
					next = i;
					break;
				}
			}

			OpenBveApi.Math.Vector3 front = Train.Cars[0].FrontAxle.Follower.WorldPosition;
			return new RouteLive(Train.FrontCarTrackPosition, front.X, front.Y, front.Z, trains, next, Train.CurrentSpeed * 3.6);
		}

		private static string ClockText(double secondsSinceMidnight)
		{
			return TimeSpan.FromSeconds(Math.Max(0.0, secondsSinceMidnight) % 86400.0).ToString(@"hh\:mm\:ss");
		}

		/// <summary>
		/// The in-game messages being shown now (as upstream's HUD would draw them), one per line,
		/// when the desktop HUD is off.
		/// </summary>
		private static IReadOnlyList<(string Text, OpenBveApi.Colors.MessageColor Color)> CurrentMessages()
		{
			List<(string, OpenBveApi.Colors.MessageColor)> lines = new List<(string, OpenBveApi.Colors.MessageColor)>();
			if (!TouchInterface)
			{
				return lines;
			}

			foreach (RouteManager2.MessageManager.AbstractMessage message in MessageManager.TextualMessages)
			{
				if (message.MessageToDisplay is string text && text.Length != 0)
				{
					lines.Add((text, message.Color));
					if (lines.Count == 3)
					{
						break;
					}
				}
			}

			return lines;
		}

		/// <summary>
		/// What the safety system is doing to the handles, when that is not what the driver asked
		/// for. A plugin holding the emergency brake on, or cutting power because a door is open,
		/// otherwise reads as the train stopping by itself: on a desktop the cab panel shows it,
		/// but few trains' panels are readable on a phone, and none are in the exterior views.
		/// </summary>
		private string SafetyState()
		{
			CabHandles h = Train.Handles;
			if (h.EmergencyBrake.Safety && !h.EmergencyBrake.Driver)
			{
				// The reset sequence is ATS-Sx's; a train's own system has its own (see its manual).
				bool defaultAts = Train.Plugin?.PluginTitle?.StartsWith("OpenBveAts", StringComparison.OrdinalIgnoreCase) == true;
				return defaultAts ? "   ⚠ ATS EMERGENCY BRAKE (S, then B1 to reset)" : "   ⚠ SAFETY SYSTEM EMERGENCY BRAKE";
			}

			// Open doors are the commonest reason, and the driver's to fix: say so rather than blame the ATS.
			bool doorsOpen = (Train.GetDoorsState(true, true) & TrainDoorState.Opened) != 0;
			if (h.Brake.Safety > h.Brake.Driver && h.Brake.Safety > 0)
			{
				return doorsOpen ? "   ⚠ BRAKE HELD: DOORS OPEN" : "   ⚠ ATS BRAKING";
			}

			if (h.Power.Driver > 0 && h.Power.Safety == 0)
			{
				return doorsOpen ? "   ⚠ POWER CUT: DOORS OPEN" : "   ⚠ ATS POWER CUT";
			}

			return string.Empty;
		}

		/// <summary>A diagnostic summary of the cab and handle state.</summary>
		public string Describe()
		{
			CarBase driverCar = Train.Cars[Train.DriverCar];
			CabHandles h = Train.Handles;
			string doors = driverCar.Doors.Length >= 2
				? "doors L " + driverCar.Doors[0].State.ToString("0.0") + " R " + driverCar.Doors[1].State.ToString("0.0")
				: "no doors";
			int running = trainManager.TFOs.Count(t => t.State == TrainState.Available);
			string others = string.Concat(trainManager.Trains.Where(t => !t.IsPlayerTrain).Select(t =>
				(t.State == TrainState.Bogus ? "invisible pre-train" : "preceding train " + t.State) + " at " +
				t.Cars[0].TrackPosition.ToString("0") + " m " + (t.CurrentSpeed * 3.6).ToString("0") + " km/h, "));
			return running + "/" + trainManager.TFOs.Count + " scripted trains running, " + others +
			       "state " + Train.State + ", view " + renderer.Camera.CurrentMode + " car " + Train.CameraCar + "/" + Train.DriverCar + ", " +
			       "cab section " + driverCar.CurrentCarSection +
			       " (has " + string.Join("/", driverCar.CarSections.Keys) + ")" +
			       ", reverser " + h.Reverser.Driver + "/" + h.Reverser.Actual +
			       ", power " + h.Power.Driver + "/" + h.Power.Actual +
			       ", brake " + h.Brake.Driver + "/" + h.Brake.Actual +
			       ", emergency " + h.EmergencyBrake.Driver + "/" + h.EmergencyBrake.Actual +
			       ", " + doors;
		}

		/// <summary>The first folder under the train folder that a train plugin can load, or null.</summary>
		internal static string FindTrain(string trainFolder, AndroidHost host)
		{
			if (!Directory.Exists(trainFolder))
			{
				return null;
			}

			foreach (string folder in Directory.EnumerateDirectories(trainFolder))
			{
				if (host.Plugins.Any(p => p.Train != null && p.Train.CanLoadTrain(folder)))
				{
					return folder;
				}
			}

			return null;
		}
	}

	/// <summary>What the driver's handles are doing, for the information bar's colouring.</summary>
	public enum HandleState
	{
		Neutral,
		Power,
		Brake,
		Emergency
	}

	/// <summary>The touch interface's driving information (see AndroidTrainSession.Status).</summary>
	/// <param name="Speed">km/h.</param>
	/// <param name="Limit">The speed limit in km/h, 0 for a signal at danger, or null for none.</param>
	/// <param name="Handles">The notches, as the cab shows them ("N B5", "P3").</param>
	/// <param name="Warning">What the safety system is doing to the handles, or null.</param>
	/// <param name="Messages">The in-game messages shown now, with their colours (at most three).</param>
	public record DriverStatus(
		double Speed,
		double? Limit,
		string Handles,
		HandleState HandleState,
		string Reverser,
		string Clock,
		string NextName,
		string NextDistance,
		string NextArrival,
		string NextDeparture,
		string Warning,
		IReadOnlyList<(string Text, OpenBveApi.Colors.MessageColor Color)> Messages,
		string CameraMode,
		bool Paused);

	/// <summary>Where a timetable row is relative to the train.</summary>
	public enum TimetableRowState
	{
		/// <summary>Stopped at or passed.</summary>
		Done,

		/// <summary>The next stop.</summary>
		Next,

		/// <summary>The next stop, where the train is now.</summary>
		AtStation,

		/// <summary>Still to come.</summary>
		Ahead
	}

	/// <summary>One station in the touch timetable.</summary>
	/// <param name="Arrival">The timetabled arrival (hh:mm:ss), or null if the route gives none.</param>
	/// <param name="Departure">The timetabled departure, or null (and always at a terminus).</param>
	/// <param name="Distance">Metres from the train's front to the stop point.</param>
	/// <param name="ToArrival">Seconds until the timetabled arrival at a stop, or NaN.</param>
	/// <param name="ToDeparture">Seconds until the timetabled departure from a stop, or NaN.</param>
	public sealed record TimetableRow(string Name, string Arrival, string Departure, bool Pass, bool Terminal, double Distance,
		TimetableRowState State, double ToArrival, double ToDeparture);

	/// <summary>A named station on the touch map and gradient chart.</summary>
	/// <param name="Index">The station's index in the route.</param>
	/// <param name="Position">The track position of the player's stop point.</param>
	public sealed record RouteStationMark(int Index, string Name, double Position, double X, double Y, double Z, bool Stops);

	/// <summary>A speed limit from a track position on, in km/h (infinity for none).</summary>
	public sealed record RouteLimit(double Position, double Kmh);

	/// <summary>The track's plan and elevation, one entry per track element, with the stations and speed limits.</summary>
	public sealed record RouteGeometry(double[] Position, double[] X, double[] Y, double[] Z, double[] Pitch,
		IReadOnlyList<RouteStationMark> Stations, IReadOnlyList<RouteLimit> Limits);

	/// <summary>The player's train and the others, now.</summary>
	/// <param name="NextStation">The route index of the next stop, or -1.</param>
	public sealed record RouteLive(double Position, double X, double Y, double Z, IReadOnlyList<(double X, double Z, bool Player)> Trains,
		int NextStation, double Kmh);

	/// <summary>The touch timetable's contents at one moment.</summary>
	/// <param name="Clock">The in-game time (hh:mm:ss).</param>
	/// <param name="CustomImage">The route's own timetable picture for this section, or null.</param>
	public sealed record TimetableSnapshot(IReadOnlyList<TimetableRow> Rows, string Clock, string CustomImage);
}
