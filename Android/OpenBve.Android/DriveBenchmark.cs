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
using System.Globalization;
using System.IO;
using System.Text;
using OpenBveApi.Interface;
using OpenBveApi.Trains;
using RouteManager2;
using RouteManager2.Stations;
using TrainManager.Car;
using TrainManager.Handles;
using TrainBase = TrainManager.Trains.TrainBase;

namespace OpenBve.Android
{
	/*
	 * The drive benchmark (the user, 2026-11-07): UserData/nx-drive-benchmark.txt loads the desktop benchmark's route and
	 * train (OBNS/bench/bench.ps1: TMLDN 20144.csv, the SP1900) without the menu and drives from Tuen Mun to Long Ping in
	 * the exterior view. The train's own SelTrac plugin drives - never OpenBVE's AI driver, which would drive it as an
	 * ordinary train: the benchmark only presses what a driver would. At the start the reverser to F and the controller to
	 * B8, then Delete (the mode selector) a set number of times for SelTrac's mode; at each station the doors are opened on
	 * the platform's side, closed again when the train is due (closed again if they jam open), and Space (ATO start)
	 * departs. Frame times are kept per section and standing or moving; the summary goes to the log and to
	 * UserData/drive-benchmark-<time>.txt, and the game ends.
	 *
	 * The file's lines, all optional (key=value; # starts a comment):
	 *   route=<file>              default: LegacyContent/Railway/Route/TMLDN 20144.csv
	 *   train=<folder>            default: LegacyContent/Train/MTR TML SP1900 EMU ver2022
	 *   selector_presses=<n>      Delete presses for SelTrac's mode (to Auto), default 5
	 *   last_station=<name part>  where the drive ends (the route's station name, often a code), default LOP
	 *   dwell=<s>                 the least time the doors stay open, default 20
	 */
	public sealed class DriveBenchmark
	{
		public const string FileName = "nx-drive-benchmark.txt";

		public string RouteFile { get; private set; }
		public string TrainFolder { get; private set; }

		/// <summary>The drive is over (finished or given up): the game ends.</summary>
		public bool Finished { get; private set; }

		private int selectorPresses = 5;
		private string lastStation = "LOP";
		private double dwell = 20.0;

		private readonly Action<string> log;

		private enum Step { Settle, Setup, Selector, CloseDoors, WaitClosed, Depart, Running, Arrived, Dwelling, Done }

		private Step step = Step.Settle;

		/// <summary>The drive's step, for the watchdog.</summary>
		public string StepName => step.ToString();
		private double stepStarted, now, lastTap;
		private int taps, closeAttempts, departAttempts, station = -1, firstStation = -1;
		private bool doorsLeft, doorsRight;
		private double stoppedSince = -1;
		private double aiOff = -10, viewChecked;
		private int viewFixes;

		// The frames: each one's time, by section (between stations) and by standing or moving.
		private readonly List<double> all = new List<double>(65536);
		private readonly List<(string Name, List<double> Moving, List<double> Standing)> sections = new List<(string, List<double>, List<double>)>();
		private List<double> moving, standing;
		private readonly StringBuilder events = new StringBuilder();
		private int cpuMin = int.MaxValue, gpuMin = int.MaxValue;

		/// <summary>Where the summary file goes (the Switch: UserData; Android: the app's files folder); null: the log only.</summary>
		public string SummaryFolder { get; set; }

		private DriveBenchmark(Action<string> log)
		{
			this.log = log;
		}

		/// <summary>
		/// The benchmark from settings given in one line - Android's launch extra "drive_benchmark" (2026-11-08, the
		/// user: the Galaxy S24 Ultra driven to Long Ping as the Switch is): "selector_presses=5;last_station=LOP;dwell=20",
		/// the file's keys separated by ';'. The route and the train are the launch's own.
		/// </summary>
		public static DriveBenchmark FromSettings(string settings, Action<string> log)
		{
			DriveBenchmark benchmark = new DriveBenchmark(log);
			foreach (string part in (settings ?? string.Empty).Split(';'))
			{
				benchmark.Apply(part, null);
			}

			return benchmark;
		}

		private void Apply(string raw, string root)
		{
			string line = raw.Split('#')[0].Trim();
			int equals = line.IndexOf('=');
			if (equals <= 0)
			{
				return;
			}

			string key = line.Substring(0, equals).Trim().ToLowerInvariant(), value = line.Substring(equals + 1).Trim();
			switch (key)
			{
				case "route":
					RouteFile = value.StartsWith("/") || root == null ? value : root + "/LegacyContent/Railway/Route/" + value;
					break;
				case "train":
					TrainFolder = value.StartsWith("/") || root == null ? value : root + "/LegacyContent/Train/" + value;
					break;
				case "selector_presses":
					int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out selectorPresses);
					break;
				case "last_station":
					lastStation = value;
					break;
				case "dwell":
					double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out dwell);
					break;
			}
		}

		/// <summary>The benchmark the file asks for, or null without the file.</summary>
		public static DriveBenchmark FromFile(string root, Action<string> log)
		{
			string path = root + "/UserData/" + FileName;
			if (!File.Exists(path))
			{
				return null;
			}

			DriveBenchmark benchmark = new DriveBenchmark(log)
			{
				RouteFile = root + "/LegacyContent/Railway/Route/TMLDN 20144.csv",
				TrainFolder = root + "/LegacyContent/Train/MTR TML SP1900 EMU ver2022",
				SummaryFolder = root + "/UserData"
			};
			foreach (string raw in File.ReadAllLines(path))
			{
				benchmark.Apply(raw, root);
			}

			return benchmark;
		}

		public string Describe() => "drive benchmark: " + Path.GetFileName(RouteFile) + " with " + Path.GetFileName(TrainFolder) + ", to " + lastStation +
		                            ", " + selectorPresses + " Delete presses for SelTrac's mode, doors open at least " + dwell.ToString("0", CultureInfo.InvariantCulture) + " s";

		private void Event(string what)
		{
			string line = "benchmark: " + now.ToString("0.0", CultureInfo.InvariantCulture) + " s  " + what;
			events.AppendLine(line);
			log(line);
		}

		private void Goto(Step next)
		{
			step = next;
			stepStarted = now;
			taps = 0;
		}

		private void Tap(AndroidControls controls, Translations.Command command)
		{
			controls.Press(command);
			controls.Release(command);
			lastTap = now;
			taps++;
		}

		private static CurrentRoute Route => global::TrainManager.TrainManagerBase.CurrentRoute;

		private static bool AllClosed(TrainBase train)
		{
			foreach (CarBase car in train.Cars)
			{
				foreach (Door door in car.Doors)
				{
					if (door.State > 0.0 || door.AnticipatedOpen)
					{
						return false;
					}
				}
			}

			return true;
		}

		private static bool AnyOpen(TrainBase train)
		{
			foreach (CarBase car in train.Cars)
			{
				foreach (Door door in car.Doors)
				{
					if (door.State > 0.0 || door.AnticipatedOpen)
					{
						return true;
					}
				}
			}

			return false;
		}

		private static bool SideOpen(TrainBase train, int side)
		{
			foreach (CarBase car in train.Cars)
			{
				if (car.Doors.Length > side && (car.Doors[side].State > 0.0 || car.Doors[side].AnticipatedOpen))
				{
					return true;
				}
			}

			return false;
		}

		private static string TimeOfDay(double seconds)
		{
			TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));
			return t.Hours.ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
		}

		private string StationName(int index) => Route != null && index >= 0 && index < Route.Stations.Length ? Route.Stations[index].Name : "station " + index;

		private void StartSection(string name)
		{
			moving = new List<double>(16384);
			standing = new List<double>(4096);
			sections.Add((name, moving, standing));
		}

		/// <summary>One frame: the drive's next step, and the frame's time kept.</summary>
		/// <param name="frameSeconds">The time since the frame before.</param>
		public void Update(AndroidTrainSession session, AndroidControls controls, double time, double frameSeconds, int cpuMhz, int gpuMhz)
		{
			now = time;
			if (Finished || session == null)
			{
				return;
			}

			TrainBase train = session.Train;
			if (train.AI != null && now - aiOff >= 2.0)
			{
				aiOff = now;
				// Never OpenBVE's AI driver: the train's own plugin drives.
				Event("OpenBVE's AI driver was on; turned off");
				controls.Press(Translations.Command.MiscAI);
				controls.Release(Translations.Command.MiscAI);
			}

			// The exterior view throughout: checked each second once set, put back if anything changed it (counted in the
			// summary - frames in another view would not be the exterior's).
			if (step != Step.Settle && now - viewChecked >= 1.0)
			{
				viewChecked = now;
				if (!session.Camera.IsExterior)
				{
					viewFixes++;
					Event("the view was " + session.Camera.ModeName + "; back to the exterior");
					controls.Press(Translations.Command.CameraExterior);
					controls.Release(Translations.Command.CameraExterior);
				}
			}

			double speed = Math.Abs(train.CurrentSpeed);
			if (step != Step.Settle && frameSeconds > 0 && frameSeconds < 10)
			{
				all.Add(frameSeconds);
				(speed > 0.3 ? moving : standing)?.Add(frameSeconds);
				if (cpuMhz > 0) cpuMin = Math.Min(cpuMin, cpuMhz);
				if (gpuMhz > 0) gpuMin = Math.Min(gpuMin, gpuMhz);
			}

			double inStep = now - stepStarted;
			switch (step)
			{
				case Step.Settle:
					// A few seconds for the first frames' uploads, then the exterior view.
					if (inStep >= 5.0)
					{
						Tap(controls, Translations.Command.CameraExterior);
						firstStation = train.Station;
						Event("start at " + StationName(train.Station) + "; exterior view");
						StartSection("at " + StationName(train.Station));
						Goto(Step.Setup);
					}

					break;
				case Step.Setup:
					// The reverser to F, the controller to B8 (or the train's most), one press every 0.3 s.
					if (now - lastTap < 0.3)
					{
						break;
					}

					CabHandles h = train.Handles;
					int targetBrake = Math.Min(8, h.Brake.MaximumNotch);
					if (h.Reverser.Driver != ReverserPosition.Forwards && taps < 12)
					{
						Tap(controls, Translations.Command.ReverserForward);
					}
					else if (h.HandleType != HandleType.SingleHandle && h.Power.Driver > 0 && taps < 40)
					{
						Tap(controls, Translations.Command.PowerDecrease);
					}
					else if (h.EmergencyBrake.Driver && taps < 40)
					{
						// The game starts the train in emergency, its brake notch already the highest: B8, not EMG (the user,
						// 2026-11-07). One step towards power releases the emergency brake and leaves the notch where it is.
						Tap(controls, h.HandleType == HandleType.SingleHandle ? Translations.Command.SinglePower : Translations.Command.BrakeDecrease);
					}
					else if (h.Brake.Driver < targetBrake && taps < 40)
					{
						Tap(controls, h.HandleType == HandleType.SingleHandle ? Translations.Command.SingleBrake : Translations.Command.BrakeIncrease);
					}
					else if (h.Brake.Driver > targetBrake && taps < 40)
					{
						Tap(controls, h.HandleType == HandleType.SingleHandle ? Translations.Command.SinglePower : Translations.Command.BrakeDecrease);
					}
					else
					{
						Event("reverser " + h.Reverser.Driver + ", brake notch " + h.Brake.Driver + " (" + h.Brake.GetNotchDescription(out _) + "), power " + h.Power.Driver +
						      (h.EmergencyBrake.Driver ? ", the emergency brake still on" : ", the emergency brake off"));
						Goto(Step.Selector);
					}

					break;
				case Step.Selector:
					// Delete (the mode selector's A2) for SelTrac's mode.
					if (now - lastTap >= 0.6)
					{
						if (taps < selectorPresses)
						{
							Tap(controls, Translations.Command.SecurityA2);
						}
						else if (inStep >= 1.5)
						{
							Event("Delete pressed " + selectorPresses + " times; waiting for the timetable's departure");
							// As at every station: SelTrac counts down to the timetable's departure and ignores ATO start before it (07:43:00
							// at Tuen Mun; Space at 07:40 never moved the train, 2026-11-08).
							station = firstStation;
							Goto(Step.Dwelling);
						}
					}

					break;
				case Step.CloseDoors:
					closeAttempts = 0;
					if (AnyOpen(train))
					{
						CloseDoors(controls, train);
					}

					Goto(Step.WaitClosed);
					break;
				case Step.WaitClosed:
					if (AllClosed(train))
					{
						if (inStep >= 1.0)
						{
							Event("doors closed");
							Goto(Step.Depart);
						}
					}
					else if (inStep >= 8.0)
					{
						// Jammed or reopened: closed again.
						if (closeAttempts >= 6)
						{
							Fail("the doors would not close after " + closeAttempts + " tries");
							break;
						}

						Event("doors not closed after " + inStep.ToString("0") + " s (jammed?) - closing again");
						CloseDoors(controls, train);
						stepStarted = now;
					}

					break;
				case Step.Depart:
					if (taps == 0)
					{
						departAttempts++;
						Tap(controls, Translations.Command.SecurityS);
						Event("Space (ATO start)");
					}
					else if (speed > 0.3)
					{
						StartSection(StationName(Math.Max(station, firstStation)) + " onwards");
						Event("departed");
						stoppedSince = -1;
						Goto(Step.Running);
					}
					else if (inStep >= 15.0)
					{
						if (departAttempts >= 5)
						{
							Fail("the train didn't move after Space " + departAttempts + " times (SelTrac's mode? selector_presses=" + selectorPresses + ")");
							break;
						}

						Event("not moving 15 s after Space - pressing it again");
						taps = 0;
						stepStarted = now;
					}

					break;
				case Step.Running:
					if (speed < 0.05)
					{
						if (stoppedSince < 0)
						{
							stoppedSince = now;
						}

						if (now - stoppedSince >= 1.5 && train.Station >= 0 && train.Station != station && train.Station != firstStation &&
						    train.StationState != TrainStopState.Completed)
						{
							station = train.Station;
							Event("stopped at " + StationName(station));
							Goto(Step.Arrived);
						}
						else if (now - stoppedSince >= 180.0)
						{
							Fail("stopped away from a station for 3 minutes");
						}
					}
					else
					{
						stoppedSince = -1;
					}

					break;
				case Step.Arrived:
					departAttempts = 0;
					if (Route != null && station < Route.Stations.Length)
					{
						RouteStation s = Route.Stations[station];
						doorsLeft = s.OpenLeftDoors;
						doorsRight = s.OpenRightDoors;
						if (doorsLeft && !SideOpen(train, 0))
						{
							Tap(controls, Translations.Command.DoorsLeft);
						}

						if (doorsRight && !SideOpen(train, 1))
						{
							Tap(controls, Translations.Command.DoorsRight);
						}

						Event("doors opened" + (doorsLeft ? " left" : string.Empty) + (doorsRight ? " right" : string.Empty));
					}

					if (StationName(station).IndexOf(lastStation, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						Goto(Step.Done);
						break;
					}

					Goto(Step.Dwelling);
					break;
				case Step.Dwelling:
				{
					// At least the dwell; then until the timetable's departure (SelTrac won't start before it), at most 5 minutes.
					double departure = Route != null && station >= 0 && station < Route.Stations.Length ? Route.Stations[station].DepartureTime : -1;
					bool due = departure < 0 || Route.SecondsSinceMidnight >= departure;
					if (inStep >= dwell && (due || inStep >= 300.0))
					{
						Event("due at " + StationName(station) + (departure < 0 ? " (no departure time)" : " (departure " + TimeOfDay(departure) + ", now " + TimeOfDay(Route.SecondsSinceMidnight) + ")"));
						Goto(Step.CloseDoors);
					}

					break;
				}
				case Step.Done:
					if (inStep >= 5.0)
					{
						Event("arrived at " + StationName(station) + "; done");
						Summary();
						Finished = true;
					}

					break;
			}
		}

		private void CloseDoors(AndroidControls controls, TrainBase train)
		{
			closeAttempts++;
			if (SideOpen(train, 0))
			{
				Tap(controls, Translations.Command.DoorsLeft);
			}

			if (SideOpen(train, 1))
			{
				Tap(controls, Translations.Command.DoorsRight);
			}
		}

		private void Fail(string why)
		{
			Event("given up: " + why);
			Summary();
			Finished = true;
		}

		private static string Stats(List<double> frames)
		{
			if (frames == null || frames.Count == 0)
			{
				return "no frames";
			}

			double sum = 0;
			foreach (double f in frames)
			{
				sum += f;
			}

			double[] sorted = frames.ToArray();
			Array.Sort(sorted);
			double median = sorted[sorted.Length / 2];
			double low1 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))];
			double low01 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.999))];
			double under30 = 0;
			foreach (double f in sorted)
			{
				if (f > 1.0 / 30.0 + 0.0005)
				{
					under30 += f;
				}
			}

			return string.Format(CultureInfo.InvariantCulture,
				"{0} frames in {1:0} s: {2:0.0} fps average, {3:0.0} median, 1% low {4:0.0}, 0.1% low {5:0.0}, slowest {6:0} ms; {7:0}% of the time under 30 fps",
				frames.Count, sum, frames.Count / sum, 1.0 / median, 1.0 / low1, 1.0 / low01, sorted[sorted.Length - 1] * 1000, 100 * under30 / sum);
		}

		private void Summary()
		{
			StringBuilder text = new StringBuilder();
			text.AppendLine(Describe());
			text.AppendLine("lowest clocks seen: CPU " + (cpuMin == int.MaxValue ? "?" : cpuMin.ToString()) + " MHz, GPU " + (gpuMin == int.MaxValue ? "?" : gpuMin.ToString()) + " MHz");
			text.AppendLine("view: exterior throughout" + (viewFixes == 0 ? string.Empty : " (put back " + viewFixes + " time(s), see below)"));
			text.AppendLine("whole drive: " + Stats(all));
			foreach ((string name, List<double> movingFrames, List<double> standingFrames) in sections)
			{
				text.AppendLine(name + ", moving: " + Stats(movingFrames));
				text.AppendLine(name + ", standing: " + Stats(standingFrames));
			}

			text.AppendLine();
			text.Append(events);
			foreach (string line in text.ToString().Split('\n'))
			{
				if (line.Trim().Length != 0 && !line.StartsWith("benchmark:"))
				{
					log("benchmark summary: " + line.TrimEnd());
				}
			}

			try
			{
				if (SummaryFolder == null)
				{
					return;
				}

				string file = Path.Combine(SummaryFolder, "drive-benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
				File.WriteAllText(file, text.ToString());
				log("benchmark: summary written to " + file);
			}
			catch (Exception e)
			{
				log("benchmark: summary not written (" + e.Message + ")");
			}
		}
	}
}
