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
using Formats.OpenBve;
using Formats.OpenBve.XML;
using OpenBveApi;
using OpenBveApi.Interface;
using OpenBveApi.Math;
using OpenBveApi.Trains;
using TrainManager;
using TrainManager.Car;
using TrainManager.Trains;
using Path = OpenBveApi.Path;

namespace OpenBve.Android
{
	/// <summary>
	/// Reads a track following object (a scripted train that runs a fixed timetable along the
	/// route, such as a train on the opposite line) from its XML file and loads its train.
	/// </summary>
	/// <remarks>
	/// A port of upstream's OpenBVE/Parsers/Script/TrackFollowingObjectParser, with Program.CurrentHost,
	/// Program.CurrentRoute, Program.FileSystem and Loading.CurrentTrainFolder taken from the host.
	/// Both kinds are read: a <c>RunInterval</c> / <c>PreTrain</c> file gives a preceding train
	/// driven by upstream's SimpleHumanDriverAI (moved into the train list with the other
	/// preceding trains by <see cref="AndroidTrainSession"/>), and a <c>Definition</c> block a
	/// scripted train on its own TrackFollowingObjectAI. One deliberate difference: upstream keeps
	/// the train being built in a static field that is not reset between files, so a file with
	/// neither kind reuses the previous file's train. Here it starts empty each time and such a
	/// file is reported as an error.
	/// </remarks>
	internal static class TrackFollowingObjectParser
	{
		/// <summary>Parses a track following object.</summary>
		/// <param name="host">The host, for plugins, route, file system and messages.</param>
		/// <param name="objectPath">Absolute path to the route's object folder.</param>
		/// <param name="fileName">The XML file to parse.</param>
		/// <returns>The train, or null if it could not be loaded.</returns>
		internal static TrainBase Parse(AndroidHost host, string objectPath, string fileName)
		{
			TrainBase train = null;
			LoadProfile.Scope xmlScope = LoadProfile.Enter("scripted trains: xml");
			XMLFile<TrackFollowingObjectSection, TrackFollowingObjectKey> xmlFile =
				new XMLFile<TrackFollowingObjectSection, TrackFollowingObjectKey>(fileName, "/openBVE/TrackFollowingObject", host);
			if (xmlFile.GetValue(TrackFollowingObjectKey.RunInterval, out double interval) || xmlFile.GetValue(TrackFollowingObjectKey.PreTrain, out interval))
			{
				train = new TrainBase(TrainState.Pending, TrainType.PreTrain) { TimetableDelta = interval };
			}

			xmlFile.ReadBlock(TrackFollowingObjectSection.Train, out Block<TrackFollowingObjectSection, TrackFollowingObjectKey> trainBlock);
			string trainDirectory = string.Empty;
			bool consistReversed = false;
			List<TravelData> travelData = new List<TravelData>();
			while (xmlFile.RemainingSubBlocks > 0)
			{
				Block<TrackFollowingObjectSection, TrackFollowingObjectKey> subBlock = xmlFile.ReadNextBlock();
				switch (subBlock.Key)
				{
					case TrackFollowingObjectSection.Definition:
						ScriptedTrain scripted = new ScriptedTrain(TrainState.Pending);
						ParseDefinitionBlock(subBlock, scripted);
						train = scripted;
						break;
					case TrackFollowingObjectSection.Points:
					case TrackFollowingObjectSection.Stops:
						ParseTravelDataBlock(host, subBlock, travelData);
						break;
				}
			}

			xmlScope.Dispose();
			if (train == null)
			{
				host.AddMessage(MessageType.Error, false, $"Neither a RunInterval nor a Definition block was found in {fileName}");
				return null;
			}

			if (trainBlock == null)
			{
				throw new InvalidDataException("No train node specified for scripted train");
			}

			ParseTrainBlock(host, objectPath, fileName, trainBlock, ref trainDirectory, ref consistReversed);

			if (train is ScriptedTrain)
			{
				if (travelData.Count < 2)
				{
					host.AddMessage(MessageType.Error, false, $"There must be at least two points to go through in {fileName}");
					return null;
				}

				if (!(travelData.First() is TravelStopData) || !(travelData.Last() is TravelStopData))
				{
					host.AddMessage(MessageType.Error, false, $"The first and the last point to go through must be the \"Stop\" node in {fileName}");
					return null;
				}
			}

			if (string.IsNullOrEmpty(trainDirectory))
			{
				host.AddMessage(MessageType.Error, false, $"No train has been specified in {fileName}");
				return null;
			}

			string trainData;
			if (!trainDirectory.EndsWith(".con", StringComparison.InvariantCultureIgnoreCase) || !File.Exists(trainDirectory))
			{
				// train.ai first: functionally identical, but keeps AI-only trains out of the train list.
				trainData = Path.CombineFile(trainDirectory, "train.ai");
				if (!File.Exists(trainData))
				{
					trainData = Path.CombineFile(trainDirectory, "train.dat");
				}

				string exteriorFile = Path.CombineFile(trainDirectory, "extensions.cfg");
				if (!File.Exists(trainData) || !File.Exists(exteriorFile))
				{
					host.AddMessage(MessageType.Error, true, $"The supplied train folder in TrackFollowingObject {fileName} does not contain an exterior model.");
				}
			}
			else
			{
				// MSTS consist
				trainData = trainDirectory;
			}

			AbstractTrain currentTrain = train;
			Control[] controls = new Control[0];
			foreach (ContentLoadingPlugin plugin in host.Plugins)
			{
				if (plugin.Train != null && plugin.Train.CanLoadTrain(trainData))
				{
					using (LoadProfile.Enter("scripted trains: train plugin"))
					{
						plugin.Train.LoadTrain(Encoding.UTF8, trainDirectory, ref currentTrain, ref controls);
					}
				}
			}

			if (!train.Cars.Any())
			{
				host.AddMessage(MessageType.Error, false, $"Failed to load the specified train in {fileName}");
				return null;
			}

			if (consistReversed)
			{
				train.Reverse();
			}

			if (train is ScriptedTrain st)
			{
				train.AI = new TrackFollowingObjectAI(st, travelData.ToArray());
				foreach (CarBase car in train.Cars)
				{
					car.FrontAxle.Follower.TrackIndex = travelData[0].RailIndex;
					car.RearAxle.Follower.TrackIndex = travelData[0].RailIndex;
					car.FrontBogie.FrontAxle.Follower.TrackIndex = travelData[0].RailIndex;
					car.FrontBogie.RearAxle.Follower.TrackIndex = travelData[0].RailIndex;
					car.RearBogie.FrontAxle.Follower.TrackIndex = travelData[0].RailIndex;
					car.RearBogie.RearAxle.Follower.TrackIndex = travelData[0].RailIndex;
					train.PlaceCars(travelData[0].Position);
				}
			}
			else
			{
				// A preceding train: driven by upstream's simple human driver, doors worked by hand.
				train.AI = new Game.SimpleHumanDriverAI(train, host.Options.PrecedingTrainSpeedLimit);
				train.Specs.DoorOpenMode = DoorMode.Manual;
				train.Specs.DoorCloseMode = DoorMode.Manual;
			}

			return train;
		}

		private static void ParseDefinitionBlock(Block<TrackFollowingObjectSection, TrackFollowingObjectKey> definitionBlock, ScriptedTrain scriptedTrain)
		{
			definitionBlock.TryGetTime(TrackFollowingObjectKey.AppearanceTime, ref scriptedTrain.AppearanceTime);
			definitionBlock.TryGetValue(TrackFollowingObjectKey.AppearanceStartPosition, ref scriptedTrain.AppearanceStartPosition);
			definitionBlock.TryGetValue(TrackFollowingObjectKey.AppearanceEndPosition, ref scriptedTrain.AppearanceEndPosition);
			definitionBlock.TryGetTime(TrackFollowingObjectKey.LeaveTime, ref scriptedTrain.LeaveTime);
			definitionBlock.ReportErrors();
		}

		/// <summary>Finds the train folder a Train block names, trying the same places upstream does, in order.</summary>
		private static void ParseTrainBlock(AndroidHost host, string objectPath, string fileName, Block<TrackFollowingObjectSection, TrackFollowingObjectKey> definitionBlock,
			ref string trainDirectory, ref bool consistReversed)
		{
			if (definitionBlock.GetValue(TrackFollowingObjectKey.Directory, out string value))
			{
				string tmpPath = string.Empty;
				try
				{
					tmpPath = Path.CombineDirectory(System.IO.Path.GetDirectoryName(fileName), value);

					if (!Directory.Exists(tmpPath) && value.EndsWith(".con", StringComparison.InvariantCultureIgnoreCase) && host.FileSystem != null)
					{
						// potential MSTS consist
						string consistDirectory = Path.CombineDirectory(host.FileSystem.MSTSDirectory, "TRAINS\\Consists");
						string consistFile = Path.CombineFile(consistDirectory, value);
						if (File.Exists(consistFile))
						{
							trainDirectory = consistFile;
						}
					}

					if (string.IsNullOrEmpty(trainDirectory) && !Directory.Exists(tmpPath) && host.FileSystem != null)
					{
						tmpPath = Path.CombineFile(host.FileSystem.InitialTrainFolder, value);
					}

					if (string.IsNullOrEmpty(trainDirectory) && !Directory.Exists(tmpPath) && host.FileSystem != null)
					{
						tmpPath = Path.CombineFile(host.FileSystem.TrainInstallationDirectory, value);
					}

					if (string.IsNullOrEmpty(trainDirectory) && !Directory.Exists(tmpPath))
					{
						tmpPath = Path.CombineFile(objectPath, value);
					}

					if (string.IsNullOrEmpty(trainDirectory) && !Directory.Exists(tmpPath) && !string.IsNullOrEmpty(host.CurrentTrainFolder))
					{
						// very fuzzy match attempt- step backwards one level from the current train folder
						tmpPath = Path.CombineDirectory(host.CurrentTrainFolder, "..");
						tmpPath = Path.CombineFile(tmpPath, value);
					}
				}
				catch
				{
					host.AddMessage(MessageType.Error, false, $"Directory was invalid in in {definitionBlock.Key} in {fileName}");
				}

				if (string.IsNullOrEmpty(trainDirectory))
				{
					if (!Directory.Exists(tmpPath))
					{
						host.AddMessage(MessageType.Error, false, $"Directory was not found in in  {definitionBlock.Key} in {fileName}");
					}
					else
					{
						trainDirectory = tmpPath;
					}
				}
			}

			definitionBlock.TryGetValue(TrackFollowingObjectKey.Reversed, ref consistReversed);
		}

		private static void ParseTravelDataBlock(AndroidHost host, Block<TrackFollowingObjectSection, TrackFollowingObjectKey> travelDataBlock, ICollection<TravelData> travelData)
		{
			while (travelDataBlock.RemainingSubBlocks > 0)
			{
				Block<TrackFollowingObjectSection, TrackFollowingObjectKey> subBlock = travelDataBlock.ReadNextBlock();
				switch (subBlock.Key)
				{
					case TrackFollowingObjectSection.Stop:
						travelData.Add(ParseTravelStopBlock(host, subBlock));
						break;
					case TrackFollowingObjectSection.Point:
						travelData.Add(ParseTravelPointBlock(host, subBlock));
						break;
				}
			}

			travelDataBlock.ReportErrors();
		}

		private static void ParseTravelDataBlock(AndroidHost host, Block<TrackFollowingObjectSection, TrackFollowingObjectKey> travelDataBlock, TravelData travelData)
		{
			double decelerate = 0.0;
			double accelerate = 0.0;
			double targetSpeed = 0.0;

			travelDataBlock.TryGetValue(TrackFollowingObjectKey.Decelerate, ref decelerate, NumberRange.NonNegative);
			travelDataBlock.TryGetValue(TrackFollowingObjectKey.Position, ref travelData.Position);
			travelDataBlock.TryGetValue(TrackFollowingObjectKey.StopPosition, ref travelData.Position);
			travelDataBlock.TryGetValue(TrackFollowingObjectKey.Accelerate, ref accelerate, NumberRange.NonNegative);
			bool targetSpeedSet = travelDataBlock.TryGetValue(TrackFollowingObjectKey.TargetSpeed, ref targetSpeed, NumberRange.NonNegative);
			travelDataBlock.TryGetValue(TrackFollowingObjectKey.Rail, ref travelData.RailIndex, NumberRange.NonNegative);
			if (host.Route == null || !host.Route.Tracks.ContainsKey(travelData.RailIndex) || host.Route.Tracks[travelData.RailIndex].Elements.Length == 0)
			{
				host.AddMessage(MessageType.Error, false, $"RailIndex {travelData.RailIndex} is invalid in {travelDataBlock.Key} in {travelDataBlock.FileName}");
				travelData.RailIndex = 0;
			}

			if (!targetSpeedSet)
			{
				host.AddMessage(MessageType.Warning, false, $"A TargetSpeed was not set in {travelDataBlock.Key}. This may cause unexpected results.");
			}

			travelData.Decelerate = -decelerate / 3.6;
			travelData.Accelerate = accelerate / 3.6;
			travelData.TargetSpeed = targetSpeed / 3.6;
		}

		private static TravelStopData ParseTravelStopBlock(AndroidHost host, Block<TrackFollowingObjectSection, TrackFollowingObjectKey> sectionElement)
		{
			TravelStopData travelStopData = new TravelStopData();

			ParseTravelDataBlock(host, sectionElement, travelStopData);
			sectionElement.TryGetTime(TrackFollowingObjectKey.StopTime, ref travelStopData.StopTime);
			if (sectionElement.GetValue(TrackFollowingObjectKey.Doors, out string doorDirection))
			{
				int doorSide = 0;
				bool doorBoth = false;

				switch (doorDirection.ToLowerInvariant())
				{
					case "l":
					case "left":
						doorSide = -1;
						break;
					case "r":
					case "right":
						doorSide = 1;
						break;
					case "n":
					case "none":
					case "neither":
						doorSide = 0;
						break;
					case "b":
					case "both":
						doorBoth = true;
						break;
					default:
						if (!NumberFormats.TryParseIntVb6(doorDirection, out doorSide))
						{
							host.AddMessage(MessageType.Error, false, $"Door direction is invalid in in {sectionElement.Key} in {sectionElement.FileName}");
						}

						break;
				}

				travelStopData.OpenLeftDoors = doorSide < 0.0 | doorBoth;
				travelStopData.OpenRightDoors = doorSide > 0.0 | doorBoth;
			}

			if (sectionElement.GetValue(TrackFollowingObjectKey.Direction, out string travelDirection))
			{
				int d;
				switch (travelDirection.ToLowerInvariant())
				{
					case "f":
						d = 1;
						break;
					case "r":
						d = -1;
						break;
					default:
						if (!NumberFormats.TryParseIntVb6(travelDirection, out d) || !Enum.IsDefined(typeof(TravelDirection), d))
						{
							host.AddMessage(MessageType.Error, false, $"TravelDirection is invalid in {sectionElement.Key} in {sectionElement.FileName}");
							d = 1;
						}

						break;
				}

				travelStopData.Direction = (TravelDirection)d;
			}

			return travelStopData;
		}

		private static TravelPointData ParseTravelPointBlock(AndroidHost host, Block<TrackFollowingObjectSection, TrackFollowingObjectKey> sectionElement)
		{
			TravelPointData travelPointData = new TravelPointData();

			ParseTravelDataBlock(host, sectionElement, travelPointData);

			double passingSpeed = 0.0;
			sectionElement.TryGetValue(TrackFollowingObjectKey.PassingSpeed, ref passingSpeed);
			travelPointData.PassingSpeed = passingSpeed / 3.6;

			return travelPointData;
		}
	}
}
