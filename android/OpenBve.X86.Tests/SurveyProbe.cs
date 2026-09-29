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
using System.Threading;
using OpenBve.X86;

namespace OpenBve.X86.Tests
{
	/// <summary>
	/// Runs every train plugin under a folder through the compatibility layer: finds each ats.cfg,
	/// classifies the DLL it names, and exercises the 32-bit native ones for a simulated minute
	/// (handles, keys, doors, horn, signals, beacons), reporting what failed and why.
	/// </summary>
	internal static class SurveyProbe
	{
		private const int StepTimeoutMilliseconds = 20000;
		private static readonly Dictionary<string, int> MissingImports = new Dictionary<string, int>();

		internal static void Run(string folder)
		{
			List<string> configs = Directory.EnumerateFiles(folder, "ats.cfg", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true }).ToList();
			Console.WriteLine(configs.Count + " trains with ats.cfg under " + folder);
			Dictionary<string, int> tally = new Dictionary<string, int>();
			Dictionary<string, Dictionary<string, int>> byCountry = new Dictionary<string, Dictionary<string, int>>();
			HashSet<string> seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string config in configs)
			{
				string train = Path.GetDirectoryName(config);
				string dll = Resolve(train, config);
				string name = Path.GetFileName(train);
				string outcome;
				if (dll == null)
				{
					outcome = "no plugin file";
				}
				else
				{
					// Identical plugin files (the same DLL copied into many trains) are run once.
					string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll)));
					if (!seenDlls.Add(hash))
					{
						continue;
					}

					outcome = Classify(dll);
					if (outcome == "x86")
					{
						outcome = Exercise(train, dll);
					}
				}

				string category = outcome.Split(':')[0].Split('(')[0].Trim();
				tally[category] = tally.TryGetValue(category, out int n) ? n + 1 : 1;
				string country = Country(train);
				if (!byCountry.TryGetValue(country, out Dictionary<string, int> perCountry)) byCountry[country] = perCountry = new Dictionary<string, int>();
				perCountry[category] = perCountry.TryGetValue(category, out int m) ? m + 1 : 1;
				Console.WriteLine($"{outcome,-60} | {country} | {name} ({(dll == null ? "?" : Path.GetFileName(dll))})");
			}

			Console.WriteLine();
			Console.WriteLine("By country (distinct plugin files):");
			foreach (KeyValuePair<string, Dictionary<string, int>> entry in byCountry.OrderByDescending(e => e.Value.Values.Sum()))
			{
				Console.WriteLine($"  {entry.Key,-12} " + string.Join(", ", entry.Value.OrderByDescending(e => e.Value).Select(e => e.Value + " " + e.Key)));
			}

			Console.WriteLine();
			Console.WriteLine("Imports not provided (plugins importing each):");
			foreach (KeyValuePair<string, int> entry in MissingImports.OrderByDescending(e => e.Value).ThenBy(e => e.Key))
			{
				Console.WriteLine($"{entry.Value,4}  {entry.Key}");
			}

			Console.WriteLine();
			foreach (KeyValuePair<string, int> entry in tally.OrderByDescending(e => e.Value))
			{
				Console.WriteLine($"{entry.Value,4}  {entry.Key}");
			}
		}

		/// <summary>
		/// The Windows code page the train's plugin was written for, approximated from its country
		/// folder (the app itself detects it from the train's text files).
		/// </summary>
		private static Encoding CodePageFor(string train)
		{
			string path = train.Replace('\\', '/');
			if (path.Contains("/日本/")) return Encoding.GetEncoding(932);
			if (path.Contains("/香港/") || path.Contains("/台灣/") || path.Contains("/KCR/") || path.Contains("/HKHOS/") || path.Contains("/MTR/")) return Encoding.GetEncoding(950);
			if (path.Contains("/韓國/")) return Encoding.GetEncoding(949);
			return Encoding.UTF8;
		}

		/// <summary>The folder under "Train" (or "Object") that the train sits in: by convention its country or operator.</summary>
		private static string Country(string train)
		{
			string[] parts = train.Replace('\\', '/').Split('/');
			for (int i = 0; i < parts.Length - 1; i++)
			{
				if (string.Equals(parts[i], "Train", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[i], "Object", StringComparison.OrdinalIgnoreCase))
				{
					return i + 2 < parts.Length ? parts[i + 1] : "(top level)";
				}
			}

			return "(elsewhere)";
		}

		private static string Resolve(string train, string config)
		{
			foreach (string raw in File.ReadAllLines(config, Encoding.Latin1))
			{
				string line = raw.Trim().TrimStart('﻿');
				int comment = line.IndexOf(';');
				if (comment >= 0) line = line.Substring(0, comment).Trim();
				if (line.Length == 0) continue;
				string path = Path.Combine(train, line.Replace('\\', Path.DirectorySeparatorChar));
				if (File.Exists(path)) return path;
				// Case-insensitive, as Windows.
				string current = train;
				foreach (string part in line.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
				{
					string match = Directory.Exists(current) ? Directory.EnumerateFileSystemEntries(current).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase)) : null;
					if (match == null) return null;
					current = match;
				}

				return File.Exists(current) ? current : null;
			}

			return null;
		}

		private static string Classify(string dll)
		{
			byte[] data = File.ReadAllBytes(dll);
			if (data.Length < 0x40 || data[0] != 'M' || data[1] != 'Z') return "not a PE file";
			int pe = BitConverter.ToInt32(data, 0x3C);
			if (pe <= 0 || pe + 0x18 > data.Length) return "not a PE file";
			ushort machine = BitConverter.ToUInt16(data, pe + 4);
			ushort magic = BitConverter.ToUInt16(data, pe + 0x18);
			int dataDirectories = pe + 0x18 + (magic == 0x20B ? 112 : 96);
			bool clr = dataDirectories + 15 * 8 + 8 <= data.Length && BitConverter.ToUInt32(data, dataDirectories + 14 * 8) != 0;
			if (clr) return "managed (.NET): upstream loader";
			if (machine == 0x8664) return "x64 native: not supported";
			if (machine == 0x14C) return "x86";
			return "other machine 0x" + machine.ToString("X");
		}

		private static string Exercise(string train, string dll)
		{
			string result = null;
			List<string> missing = new List<string>();
			Thread worker = new Thread(() =>
			{
				string stage = "load";
				try
				{
					string root = Directory.GetParent(train)?.Parent?.FullName ?? train;
					AtsPlugin plugin = AtsPlugin.Open(root, dll, CodePageFor(train), message =>
					{
						if (message.StartsWith("import not provided", StringComparison.Ordinal)) missing.Add(message.Substring(21).Split(' ')[0]);
					});
					stage = "Load";
					plugin.Load();
					int version = plugin.GetPluginVersion();
					if (version != 0x20000)
					{
						result = "wrong version: 0x" + version.ToString("X");
						return;
					}

					stage = "Initialize";
					plugin.SetVehicleSpec(8, 5, 2, 6, 10);
					plugin.Initialize(0);
					plugin.SetReverser(1);
					plugin.SetBrake(8);
					plugin.SetPower(0);
					int[] panel = new int[256], sound = new int[256];
					double location = 1000, speed = 0;
					for (int frame = 0; frame < 3600; frame++)
					{
						stage = "frame " + frame;
						switch (frame)
						{
							case 30: plugin.SetSignal(4); plugin.SetBeaconData(0, 4, 300f, 0); break;
							case 60: plugin.KeyDown(0); break;
							case 66: plugin.KeyUp(0); break;
							case 90: plugin.DoorOpen(); break;
							case 300: plugin.DoorClose(); break;
							case 330: plugin.HornBlow(0); break;
							case 360: plugin.SetBrake(0); break;
							case 400: plugin.SetPower(3); break;
							case 900: plugin.SetBeaconData(1, 2, 500f, 30); plugin.SetSignal(2); break;
							case 1500: plugin.KeyDown(1); break;
							case 1506: plugin.KeyUp(1); break;
							case 2400: plugin.SetPower(0); plugin.SetBrake(4); break;
						}

						AtsHandles h = plugin.Elapse(new AtsVehicleState
						{
							Location = location, Speed = (float)(speed * 3.6), Time = 36000000 + frame * 1000 / 60,
							BcPressure = 200000, MrPressure = 800000, ErPressure = 490000, BpPressure = 490000, SapPressure = 800000
						}, panel, sound);
						double a = h.Brake > 0 ? -0.8 * h.Brake / 8 : 0.6 * h.Power / 5;
						speed = Math.Max(0, speed + a / 60);
						location += speed / 60;
					}

					result = "OK";
				}
				catch (X86Exception ex)
				{
					result = "failed at " + stage + ": " + ex.Message;
				}
				catch (Exception ex)
				{
					result = "host error at " + stage + ": " + ex.GetType().Name + " " + ex.Message;
				}
			}, 64 * 1024 * 1024) { IsBackground = true };
			worker.Start();
			if (!worker.Join(StepTimeoutMilliseconds))
			{
				return "timed out (still running after 20 s)";
			}

			lock (MissingImports)
			{
				foreach (string import in missing.Distinct())
				{
					MissingImports[import] = MissingImports.TryGetValue(import, out int count) ? count + 1 : 1;
				}
			}

			if (result == "OK" && missing.Count > 0)
			{
				return "OK (unused imports: " + missing.Count + ")";
			}

			return result;
		}
	}
}
