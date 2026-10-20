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

// OBNS: BVE Trainsim 5 trains (OBNS/BVE5.md), part of upstream's train plugin in the OBNS build.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Train.OpenBve
{
	/// <summary>
	/// A BVE5 vehicle file ("BveTs Vehicle 2.00", and the older 0.03 and 1.00): the files that make up
	/// the train, each relative to the vehicle file.
	/// </summary>
	internal sealed class Bve5Vehicle
	{
		internal string FileName;
		internal string PerformanceCurve;
		internal string Parameters;
		internal string Panel;
		internal string Sound;
		internal string MotorNoise;
		/// <summary>The safety plugin for a 32-bit process ("Ats32", or "Ats" in older files).</summary>
		internal string Ats32;
		/// <summary>The safety plugin for a 64-bit process ("Ats64").</summary>
		internal string Ats64;

		/// <summary>The folder the vehicle file is in, which OpenBVE treats as the train's folder.</summary>
		internal string Folder => Path.GetDirectoryName(FileName);

		/// <summary>Whether a file is a BVE5 vehicle file (by its header).</summary>
		internal static bool IsVehicleFile(string fileName)
		{
			return File.Exists(fileName) && string.Equals(Bve5Text.ReadKind(fileName, out _), "Vehicle", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>
		/// The vehicle file to load for a path: the path itself if it is one; in a folder, its
		/// vehicle.txt if that is a BVE5 vehicle file, else the folder's other BVE5 vehicle files, the
		/// first by name. Null if there is none.
		/// </summary>
		internal static string Find(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}

			if (File.Exists(path))
			{
				return IsVehicleFile(path) ? path : null;
			}

			if (!Directory.Exists(path))
			{
				return null;
			}

			try
			{
				string vehicleTxt = OpenBveApi.Path.CombineFile(path, "vehicle.txt");
				if (IsVehicleFile(vehicleTxt))
				{
					return vehicleTxt;
				}

				return Directory.GetFiles(path, "*.txt").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault(IsVehicleFile);
			}
			catch (Exception)
			{
				return null;
			}
		}

		internal static Bve5Vehicle Load(string fileName)
		{
			Bve5Text text = Bve5Text.Read(fileName);
			Dictionary<string, string> keys = text.Sections()[string.Empty];
			string Get(string key) => keys.TryGetValue(key, out string value) ? Bve5Text.ResolvePath(fileName, value) : null;
			return new Bve5Vehicle
			{
				FileName = fileName,
				PerformanceCurve = Get("PerformanceCurve"),
				Parameters = Get("Parameters"),
				Panel = Get("Panel"),
				Sound = Get("Sound"),
				MotorNoise = Get("MotorNoise"),
				Ats32 = Get("Ats32") ?? Get("Ats"),
				Ats64 = Get("Ats64")
			};
		}
	}
}
