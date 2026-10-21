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
	/// BVE5 sound for a train.dat train: a train.dat train whose folder also holds a BVE5 motor noise file plays
	/// that motor noise in place of train.dat's motor sound, and a BVE5 vehicle sound file beside it, if there is
	/// one, in place of its sound.cfg. Its physics stay train.dat's.
	/// </summary>
	/// <remarks>
	/// A BVE5 motor noise file has no header line: it is recognised by its content, a [Power] section whose
	/// Volume and Frequency name BVE5 motor noise tables. A vehicle sound file is recognised by its header
	/// ("BveTs Vehicle Sound"). Both are looked for in the train's folder and two levels below it
	/// (MotorNoise\motornoise.txt and the like), the first by name where there are several.
	/// </remarks>
	internal static class Bve5SoundAddOn
	{
		/// <summary>How many folders below the train's folder the files are looked for.</summary>
		private const int Depth = 2;

		/// <summary>Finds a train.dat train's BVE5 motor noise file and vehicle sound file (null if none).</summary>
		/// <returns>Whether the train has a BVE5 motor noise file, which is what makes it one with BVE5 sound.</returns>
		internal static bool Find(string trainFolder, out string motorNoise, out string sound)
		{
			motorNoise = null;
			sound = null;
			if (string.IsNullOrEmpty(trainFolder) || !Directory.Exists(trainFolder))
			{
				return false;
			}

			foreach (string file in TextFiles(trainFolder, Depth))
			{
				string kind = Bve5Text.ReadKind(file, out _);
				if (sound == null && string.Equals(kind, "Vehicle Sound", StringComparison.OrdinalIgnoreCase))
				{
					sound = file;
				}
				else if (motorNoise == null && kind == null && IsMotorNoise(file))
				{
					motorNoise = file;
				}
			}

			return motorNoise != null;
		}

		/// <summary>The .txt files in a folder and the folders below it, to a depth, in name order.</summary>
		private static IEnumerable<string> TextFiles(string folder, int depth)
		{
			string[] files, folders;
			try
			{
				files = Directory.GetFiles(folder, "*.txt");
				folders = depth > 0 ? Directory.GetDirectories(folder) : new string[0];
			}
			catch (Exception)
			{
				yield break;
			}

			foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
			{
				yield return file;
			}

			foreach (string sub in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
			{
				foreach (string file in TextFiles(sub, depth - 1))
				{
					yield return file;
				}
			}
		}

		/// <summary>Whether a headerless file is a BVE5 motor noise file: its [Power] names two motor noise tables.</summary>
		private static bool IsMotorNoise(string file)
		{
			try
			{
				if (!Bve5Text.Read(file).Sections().TryGetValue("Power", out Dictionary<string, string> power))
				{
					return false;
				}

				return power.TryGetValue("Volume", out string volume) && IsNoiseTable(Bve5Text.ResolvePath(file, volume)) &&
				       power.TryGetValue("Frequency", out string frequency) && IsNoiseTable(Bve5Text.ResolvePath(file, frequency));
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static bool IsNoiseTable(string file)
		{
			return file != null && File.Exists(file) && string.Equals(Bve5Text.ReadKind(file, out _), "Motor Noise Table", StringComparison.OrdinalIgnoreCase);
		}
	}
}
