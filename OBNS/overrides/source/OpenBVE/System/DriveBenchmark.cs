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

// OBNS: not an upstream file. The desktop's copy of Android's drive benchmark (Android/OpenBve.Android/DriveBenchmark.cs;
// the user, 2026-11-11: "do the driving run via ATO, similar to the Android tests; driving instructions identical"), so a
// desktop is driven from Tuen Mun to Long Ping exactly as the phones are. With OBNS_DRIVE_BENCHMARK set ("1", or the
// Android extra's settings: "selector_presses=5;last_station=LOP;dwell=20"), the train's own SelTrac plugin drives - never
// OpenBVE's AI driver. At the start the reverser to F and the controller to B8, then Delete (the mode selector) a set
// number of times for SelTrac's mode; at each station the doors are opened on the platform's side, closed again when the
// train is due (again if they jam open), and Space (ATO start) departs. The keys are pressed as a driver's would be:
// controls of the benchmark's own, with no key bound, set pressed for a frame and released the next, which the game
// handles as it does any key. Frame times are kept per section and standing or moving; the summary goes to the log and to
// drive-benchmark-<time>.txt in the settings folder, and the game ends.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenBveApi.Interface;
using OpenBveApi.Runtime;
using OpenBveApi.Trains;
using RouteManager2;
using RouteManager2.Stations;
using TrainManager.Car;
using TrainManager.Handles;
using TrainBase = TrainManager.Trains.TrainBase;

namespace OpenBve
{
	internal sealed class DriveBenchmark
	{
		/// <summary>The drive asked for with OBNS_DRIVE_BENCHMARK, or null.</summary>
		internal static readonly DriveBenchmark Current = FromEnvironment();

		/// <summary>The drive is over (finished or given up): the game ends.</summary>
		internal bool Finished { get; private set; }

		private int selectorPresses = 5;
		private string lastStation = "LOP";
		private double dwell = 20.0;

		private enum Step { Settle, Setup, Selector, CloseDoors, WaitClosed, Depart, Running, Arrived, Dwelling, Done }

		private Step step = Step.Settle;
		private double stepStarted, now, lastTap;
		private int taps, closeAttempts, departAttempts, station = -1, firstStation = -1;
		private bool doorsLeft, doorsRight;
		private double stoppedSince = -1;
		private double aiOff = -10, viewChecked;
		private int viewFixes;

		private readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();

		// The frames: each one's time, by section (between stations) and by standing or moving.
		private readonly List<double> all = new List<double>(65536);
		private readonly List<(string Name, List<double> Moving, List<double> Standing)> sections = new List<(string, List<double>, List<double>)>();
		private List<double> moving, standing;
		private readonly StringBuilder events = new StringBuilder();

		/// <summary>The benchmark's own controls (index in Interface.CurrentControls), one a command, added on first use.</summary>
		private readonly Dictionary<Translations.Command, int> controls = new Dictionary<Translations.Command, int>();
		private readonly List<int> pressed = new List<int>();

		private static DriveBenchmark FromEnvironment()
		{
			string settings = Environment.GetEnvironmentVariable("OBNS_DRIVE_BENCHMARK");
			if (string.IsNullOrEmpty(settings) || settings == "0")
			{
				return null;
			}

			DriveBenchmark benchmark = new DriveBenchmark();
			foreach (string part in settings.Split(';'))
			{
				benchmark.Apply(part);
			}

			return benchmark;
		}

		private void Apply(string raw)
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

		private string Describe() => "drive benchmark: " + Path.GetFileName(Program.CurrentRoute?.Information?.RouteFile ?? string.Empty) + " with " +
		                             Path.GetFileName((Program.CurrentRoute?.Information?.TrainFolder ?? string.Empty).TrimEnd('/', '\\')) + ", to " + lastStation +
		                             ", " + selectorPresses + " Delete presses for SelTrac's mode, doors open at least " + dwell.ToString("0", CultureInfo.InvariantCulture) + " s";

		private static void Log(string line) => Program.FileSystem.AppendToLogFile(line);

		private void Event(string what)
		{
			string line = "benchmark: " + now.ToString("0.0", CultureInfo.InvariantCulture) + " s  " + what;
			events.AppendLine(line);
			Log(line);
		}

		private void Goto(Step next)
		{
			step = next;
			stepStarted = now;
			taps = 0;
		}

		/// <summary>A key pressed this frame and released the next, as a driver's tap.</summary>
		private void Tap(Translations.Command command)
		{
			if (!controls.TryGetValue(command, out int index))
			{
				index = Interface.CurrentControls.Length;
				Array.Resize(ref Interface.CurrentControls, index + 1);
				Interface.CurrentControls[index] = new Control
				{
					Command = command,
					InheritedType = Translations.CommandType.Digital,
					Method = ControlMethod.Invalid,
					DigitalState = DigitalControlState.ReleasedAcknowledged
				};
				controls[command] = index;
			}

			Interface.CurrentControls[index].DigitalState = DigitalControlState.Pressed;
			pressed.Add(index);
			lastTap = now;
			taps++;
		}

		private static CurrentRoute Route => Program.CurrentRoute;

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

		private static string StationName(int index) => Route != null && index >= 0 && index < Route.Stations.Length ? Route.Stations[index].Name : "station " + index;

		private void StartSection(string name)
		{
			moving = new List<double>(16384);
			standing = new List<double>(4096);
			sections.Add((name, moving, standing));
		}

		/// <summary>One frame, before the controls are processed: the drive's next step, and the frame's time kept.</summary>
		/// <param name="frameSeconds">The real time since the frame before.</param>
		internal void Update(double frameSeconds)
		{
			// Last frame's taps released: the game sees each key up a frame after its key down.
			foreach (int index in pressed)
			{
				if (index < Interface.CurrentControls.Length && Interface.CurrentControls[index].DigitalState != DigitalControlState.Pressed)
				{
					Interface.CurrentControls[index].DigitalState = DigitalControlState.Released;
				}
			}

			pressed.Clear();
			TrainBase train = TrainManager.PlayerTrain;
			if (Finished || train == null || Route == null)
			{
				return;
			}

			if (!clock.IsRunning)
			{
				clock.Start();
				Log(Describe());
			}

			now = clock.Elapsed.TotalSeconds;
			if (train.AI != null && now - aiOff >= 2.0)
			{
				aiOff = now;
				// Never OpenBVE's AI driver: the train's own plugin drives.
				Event("OpenBVE's AI driver was on; turned off");
				Tap(Translations.Command.MiscAI);
			}

			// The exterior view throughout: checked each second once set, put back if anything changed it (counted in the
			// summary - frames in another view would not be the exterior's).
			if (step != Step.Settle && now - viewChecked >= 1.0)
			{
				viewChecked = now;
				if (Program.Renderer.Camera.CurrentMode != CameraViewMode.Exterior)
				{
					viewFixes++;
					Event("the view was " + Program.Renderer.Camera.CurrentMode + "; back to the exterior");
					Tap(Translations.Command.CameraExterior);
				}
			}

			double speed = Math.Abs(train.CurrentSpeed);
			if (step != Step.Settle && frameSeconds > 0 && frameSeconds < 10)
			{
				all.Add(frameSeconds);
				(speed > 0.3 ? moving : standing)?.Add(frameSeconds);
			}

			double inStep = now - stepStarted;
			switch (step)
			{
				case Step.Settle:
					// A few seconds for the first frames' uploads, then the exterior view.
					if (inStep >= 5.0)
					{
						Tap(Translations.Command.CameraExterior);
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
						Tap(Translations.Command.ReverserForward);
					}
					else if (h.HandleType != HandleType.SingleHandle && h.Power.Driver > 0 && taps < 40)
					{
						Tap(Translations.Command.PowerDecrease);
					}
					else if (h.EmergencyBrake.Driver && taps < 40)
					{
						// The game starts the train in emergency, its brake notch already the highest: B8, not EMG (the user,
						// 2026-11-07). One step towards power releases the emergency brake and leaves the notch where it is.
						Tap(h.HandleType == HandleType.SingleHandle ? Translations.Command.SinglePower : Translations.Command.BrakeDecrease);
					}
					else if (h.Brake.Driver < targetBrake && taps < 40)
					{
						Tap(h.HandleType == HandleType.SingleHandle ? Translations.Command.SingleBrake : Translations.Command.BrakeIncrease);
					}
					else if (h.Brake.Driver > targetBrake && taps < 40)
					{
						Tap(h.HandleType == HandleType.SingleHandle ? Translations.Command.SinglePower : Translations.Command.BrakeDecrease);
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
							Tap(Translations.Command.SecurityA2);
						}
						else if (inStep >= 1.5)
						{
							Event("Delete pressed " + selectorPresses + " times; waiting for the timetable's departure");
							// As at every station: SelTrac counts down to the timetable's departure and ignores ATO start before it.
							station = firstStation;
							Goto(Step.Dwelling);
						}
					}

					break;
				case Step.CloseDoors:
					closeAttempts = 0;
					if (AnyOpen(train))
					{
						CloseDoors(train);
					}

					Goto(Step.WaitClosed);
					break;
				case Step.WaitClosed:
					if (!AnyOpen(train))
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
						CloseDoors(train);
						stepStarted = now;
					}

					break;
				case Step.Depart:
					if (taps == 0)
					{
						departAttempts++;
						Tap(Translations.Command.SecurityS);
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
					if (station < Route.Stations.Length)
					{
						RouteStation s = Route.Stations[station];
						doorsLeft = s.OpenLeftDoors;
						doorsRight = s.OpenRightDoors;
						if (doorsLeft && !SideOpen(train, 0))
						{
							Tap(Translations.Command.DoorsLeft);
						}

						if (doorsRight && !SideOpen(train, 1))
						{
							Tap(Translations.Command.DoorsRight);
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
					double departure = station >= 0 && station < Route.Stations.Length ? Route.Stations[station].DepartureTime : -1;
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

			if (Finished)
			{
				MainLoop.Quit = QuitMode.QuitProgram;
			}
		}

		private void CloseDoors(TrainBase train)
		{
			closeAttempts++;
			if (SideOpen(train, 0))
			{
				Tap(Translations.Command.DoorsLeft);
			}

			if (SideOpen(train, 1))
			{
				Tap(Translations.Command.DoorsRight);
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
			text.AppendLine("Potato mode " + (Potato.Active ? "on" : "off") + ", renderer " + Program.Renderer.GetType().Name + ", window " + Program.Renderer.Screen.Width + "x" + Program.Renderer.Screen.Height);
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
					Log("benchmark summary: " + line.TrimEnd());
				}
			}

			try
			{
				string file = Path.Combine(Program.FileSystem.SettingsFolder, "drive-benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
				File.WriteAllText(file, text.ToString());
				Log("benchmark: summary written to " + file);
			}
			catch (Exception e)
			{
				Log("benchmark: summary not written (" + e.Message + ")");
			}
		}
	}
}
