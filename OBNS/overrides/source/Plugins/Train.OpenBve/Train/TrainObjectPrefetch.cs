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

// OBNS: part of upstream's train plugin in the OBNS build.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBveApi;
using Path = OpenBveApi.Path;

namespace Train.OpenBve
{
	/// <summary>
	/// Loads the models of a train's cab and exterior on worker threads before the parsers ask for them. The parsers read one file
	/// after another on one thread, and the host's object cache hands them what is already loaded, so nothing about the
	/// train changes. A model this misses is loaded by the parser as before.
	/// </summary>
	internal static class TrainObjectPrefetch
	{
		/// <summary>How far .animated files that include others are followed.</summary>
		private const int Depth = 8;

		internal static void Run(Plugin plugin, string trainFolder, Encoding encoding, bool playerTrain)
		{
			int workers = Math.Min(8, Environment.ProcessorCount);
			if (workers < 2 || string.IsNullOrEmpty(trainFolder) || !Directory.Exists(trainFolder))
			{
				return;
			}

			try
			{
				HashSet<string> models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				if (playerTrain)
				{
					AddModel(Path.CombineFile(trainFolder, "panel.animated"), encoding, models, seen, Depth);
				}

				ReadExtensions(trainFolder, encoding, models, seen);
				string[] files = models.ToArray();
				int next = -1;
				Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
				{
					int i;
					while ((i = Interlocked.Increment(ref next)) < files.Length && !plugin.Cancel)
					{
						try
						{
							Plugin.CurrentHost.LoadObject(files[i], encoding, out _);
						}
						catch (Exception)
						{
							// The parser meets this file too, and reports it.
						}
					}
				});
			}
			catch (Exception ex)
			{
				Plugin.FileSystem.AppendToLogFile("Train models weren't loaded ahead: " + ex.Message);
			}
		}

		/// <summary>extensions.cfg's objects: Object in [CarN], [BogieN] and [CouplerN], and the numbered files in [Exterior].</summary>
		private static void ReadExtensions(string trainFolder, Encoding encoding, HashSet<string> models, HashSet<string> seen)
		{
			string file = Path.CombineFile(trainFolder, "extensions.cfg");
			if (!File.Exists(file))
			{
				return;
			}

			foreach (string line in ReadLines(file, encoding))
			{
				int equals = line.IndexOf('=');
				if (equals <= 0)
				{
					continue;
				}

				string key = line.Substring(0, equals).Trim();
				if (key.Equals("object", StringComparison.OrdinalIgnoreCase) || int.TryParse(key, out _))
				{
					AddPath(trainFolder, line.Substring(equals + 1).Trim(), encoding, models, seen, Depth);
				}
			}
		}

		private static void AddPath(string folder, string name, Encoding encoding, HashSet<string> models, HashSet<string> seen, int depth)
		{
			if (name.Length == 0)
			{
				return;
			}

			try
			{
				AddModel(Path.CombineFile(folder, name), encoding, models, seen, depth);
			}
			catch (Exception)
			{
				// A path that can't be made: the parser reports it.
			}
		}

		/// <summary>A model to load, or for an .animated file, the models it names: it's built quickly from them, on the parser's thread.</summary>
		private static void AddModel(string file, Encoding encoding, HashSet<string> models, HashSet<string> seen, int depth)
		{
			if (depth == 0 || !seen.Add(file) || !File.Exists(file))
			{
				return;
			}

			if (!file.EndsWith(".animated", StringComparison.OrdinalIgnoreCase))
			{
				models.Add(file);
				return;
			}

			string folder = System.IO.Path.GetDirectoryName(file);
			bool including = false;
			foreach (string line in ReadLines(file, encoding))
			{
				if (line.StartsWith("[", StringComparison.Ordinal))
				{
					including = line.StartsWith("[include]", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				int equals = line.IndexOf('=');
				if (equals > 0 && line.Substring(0, equals).Trim().Equals("states", StringComparison.OrdinalIgnoreCase))
				{
					foreach (string state in line.Substring(equals + 1).Split(','))
					{
						AddPath(folder, state.Trim(), encoding, models, seen, depth - 1);
					}
				}
				else if (including && equals < 0)
				{
					AddPath(folder, line, encoding, models, seen, depth - 1);
				}
			}
		}

		/// <summary>A file's lines, without comments or surrounding space, or none if it can't be read.</summary>
		private static IEnumerable<string> ReadLines(string file, Encoding encoding)
		{
			string[] lines;
			try
			{
				lines = File.ReadAllLines(file, TextEncoding.GetSystemEncodingFromFile(file, encoding));
			}
			catch (Exception)
			{
				yield break;
			}

			foreach (string raw in lines)
			{
				int comment = raw.IndexOf(';');
				string line = (comment >= 0 ? raw.Substring(0, comment) : raw).Trim();
				if (line.Length > 0)
				{
					yield return line;
				}
			}
		}
	}
}
