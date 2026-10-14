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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using OpenBveApi.Objects;

namespace OpenBve.Android
{
	/*
	 * Multi-core object loading. Upstream's route parsers load each object file the moment they meet
	 * it, one after another on the loading thread: on the Tuen Ma Line 15,933 files, ~60 s. The object
	 * parsers themselves are independent (the CSV/B3D plugin keeps no per-parse state; the host's
	 * texture registration, the one callback they make, is locked), so the files can be parsed ahead,
	 * on the other cores:
	 *  - the route file and its $Include files are scanned for the object files they name (any
	 *    comma/semicolon/equals-separated argument ending in .csv/.b3d that exists in the Object
	 *    folder - a scan, not a parse: anything missed simply loads as before); .animated files
	 *    named are scanned the same way for their state and include objects (relative to their own
	 *    folder), which are queued right after them - the animated files themselves still load on
	 *    the loading thread, as their parser uses the host's object cache;
	 *  - worker threads parse those files in order, at most WindowAhead past the last one the route
	 *    parser has asked for (so the lead, and the memory it holds, stays bounded);
	 *  - the workers optimise each object too, as LoadObject would (upstream's OptimizeObject with
	 *    preserveVertices off and vertex culling on - the flags the route parser's structures use);
	 *  - when the route parser asks for a file (AndroidHost.LoadObject / LoadStaticObject), the host
	 *    takes the result - waiting for a worker, or parsing it itself if none has started - and
	 *    carries on exactly as before (caching; OptimizeObject returns at once for an object already
	 *    optimised). A result is only used if it was read with the same text encoding the parser
	 *    asks for (the same fallback encoding gives the same answer for the same file, so the
	 *    whole-file detection is only repeated when the fallbacks differ) and optimised the same way: LoadStaticObject culls no vertices unless the hidden
	 *    vertexCulling option is on, so those few files (FormCL/CR, RoofCL/CR, Crack) are left for
	 *    the loading thread, as before.
	 * --ez prefetch_objects false turns it off for comparisons.
	 */
	internal static class ObjectPrefetch
	{
		public static bool Enabled = true;

		private const int WindowAhead = 512;

		private sealed class Entry
		{
			public int Index;
			public Encoding Encoding;
			public Encoding Fallback;
			public Lazy<UnifiedObject> Result;
		}

		/// <summary>Whether OptimizeObject(preserveVertices, threshold, vertexCulling) culls vertices, the one way its flags change the result.</summary>
		public static bool Culls(bool preserveVertices, bool vertexCulling) => !preserveVertices && vertexCulling;

		private static ConcurrentDictionary<string, Entry> entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		private static CancellationTokenSource cancel;
		private static int highestTaken = -1;
		private static int candidates, parsed, taken, mismatched, otherCulling;

		private static readonly Regex IncludePattern = new Regex(@"\$include\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		private static readonly char[] Separators = { ',', ';', '=' };

		/// <summary>For the load log.</summary>
		public static string Describe() => !Enabled ? "object prefetch off" :
			"object prefetch: " + candidates + " named, " + parsed + " parsed ahead, " + taken + " used" + (mismatched > 0 ? ", " + mismatched + " encoding mismatches" : string.Empty) +
			(otherCulling > 0 ? ", " + otherCulling + " left for other optimisation flags" : string.Empty);

		/// <summary>Starts scanning and parsing ahead for a CSV/RW route. Returns at once.</summary>
		public static void Start(AndroidHost host, string routeFile, Encoding encoding, string objectFolder)
		{
			Stop();
			candidates = parsed = taken = mismatched = otherCulling = 0;
			highestTaken = -1;
			string extension = Path.GetExtension(routeFile).ToLowerInvariant();
			if (!Enabled || (extension != ".csv" && extension != ".rw") || string.IsNullOrEmpty(objectFolder))
			{
				return;
			}

			CancellationTokenSource source = new CancellationTokenSource();
			cancel = source;
			ConcurrentDictionary<string, Entry> map = entries;
			Task.Run(() =>
			{
				try
				{
					List<string> files = Scan(routeFile, encoding, objectFolder, source.Token);
					candidates = files.Count;
					for (int i = 0; i < files.Count; i++)
					{
						string file = files[i];
						Encoding fileEncoding = OpenBveApi.TextEncoding.GetSystemEncodingFromFile(file, encoding);
						map.TryAdd(file, new Entry
						{
							Index = i,
							Encoding = fileEncoding,
							Fallback = encoding,
							Result = new Lazy<UnifiedObject>(() => host.ParseObjectForPrefetch(file, fileEncoding), LazyThreadSafetyMode.ExecutionAndPublication)
						});
					}

					ParallelOptions options = new ParallelOptions
					{
						MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
						CancellationToken = source.Token
					};
					Parallel.ForEach(files, options, file =>
					{
						if (!map.TryGetValue(file, out Entry entry))
						{
							return;
						}

						while (entry.Index > Volatile.Read(ref highestTaken) + WindowAhead && !source.IsCancellationRequested)
						{
							Thread.Sleep(5);
						}

						if (!source.IsCancellationRequested && map.ContainsKey(file) && !entry.Result.IsValueCreated)
						{
							_ = entry.Result.Value;
							Interlocked.Increment(ref parsed);
						}
					});
				}
				catch (OperationCanceledException)
				{
				}
				catch (Exception ex)
				{
					Log.Warn("OpenBVE", "object prefetch stopped: " + ex.Message);
				}
			});
		}

		/// <summary>
		/// The parsed and optimised object for a file the route parser asks for, or null to load it as
		/// usual. <paramref name="culls"/>: whether the caller's optimisation would cull vertices (<see cref="Culls"/>).
		/// </summary>
		public static UnifiedObject Take(string path, Encoding encoding, bool culls)
		{
			if (cancel == null || string.IsNullOrEmpty(path) || !entries.TryGetValue(path, out Entry entry))
			{
				return null;
			}

			if (!culls)
			{
				// Optimised the LoadObject way; left in place for a LoadObject of the same file later.
				Interlocked.Increment(ref otherCulling);
				return null;
			}

			if (!entries.TryRemove(path, out entry))
			{
				return null;
			}

			int index = entry.Index;
			int seen;
			while (index > (seen = Volatile.Read(ref highestTaken)) && Interlocked.CompareExchange(ref highestTaken, index, seen) != seen)
			{
			}

			if (!Equals(encoding, entry.Fallback) && !Equals(OpenBveApi.TextEncoding.GetSystemEncodingFromFile(path, encoding), entry.Encoding))
			{
				Interlocked.Increment(ref mismatched);
				return null;
			}

			UnifiedObject result;
			if (entry.Result.IsValueCreated)
			{
				result = entry.Result.Value;
			}
			else
			{
				using (LoadProfile.Enter("object load: prefetch wait or own parse"))
				{
					result = entry.Result.Value;
				}
			}

			if (result != null)
			{
				Interlocked.Increment(ref taken);
			}

			return result;
		}

		/// <summary>Stops the workers and drops whatever was not taken (the route has loaded).</summary>
		public static void Stop()
		{
			cancel?.Cancel();
			cancel = null;
			entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		}

		/// <summary>The object files a CSV/RW route names, in the order it names them.</summary>
		private static List<string> Scan(string routeFile, Encoding encoding, string objectFolder, CancellationToken token)
		{
			List<string> found = new List<string>();
			HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			Queue<string> routeFiles = new Queue<string>();
			routeFiles.Enqueue(routeFile);
			while (routeFiles.Count > 0 && visited.Count < 500 && !token.IsCancellationRequested)
			{
				string file = routeFiles.Dequeue();
				if (!visited.Add(file) || !File.Exists(file))
				{
					continue;
				}

				string folder = Path.GetDirectoryName(file) ?? string.Empty;
				foreach (string line in File.ReadLines(file, OpenBveApi.TextEncoding.GetSystemEncodingFromFile(file, encoding)))
				{
					if (line.IndexOf("$include", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						foreach (Match include in IncludePattern.Matches(line))
						{
							foreach (string argument in include.Groups[1].Value.Split(';', ','))
							{
								string name = argument.Split(':')[0].Trim().Trim('"');
								string included = Resolve(folder, name);
								if (included != null)
								{
									routeFiles.Enqueue(included);
								}
							}
						}

						continue;
					}

					foreach (string piece in line.Split(Separators))
					{
						string candidate = piece;
						int close = candidate.LastIndexOf(')');
						if (close >= 0)
						{
							candidate = candidate.Substring(close + 1);
						}

						AddObject(objectFolder, candidate, encoding, found, seen, 0);
					}
				}
			}

			return found;
		}

		/// <summary>
		/// Queues an object file named in a route or animated file: a CSV/B3D file itself, or an
		/// animated file's own object files (scanned, up to a few levels of animated files within).
		/// </summary>
		private static void AddObject(string folder, string candidate, Encoding encoding, List<string> found, HashSet<string> seen, int depth)
		{
			candidate = candidate.Trim().Trim('"');
			if (candidate.Length < 5 || candidate.Length > 260)
			{
				return;
			}

			bool animated = candidate.EndsWith(".animated", StringComparison.OrdinalIgnoreCase);
			if (!animated && !candidate.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".b3d", StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			string file = Resolve(folder, candidate);
			if (file == null || !seen.Add(file))
			{
				return;
			}

			if (!animated)
			{
				found.Add(file);
				return;
			}

			if (depth >= 4)
			{
				return;
			}

			string animatedFolder = Path.GetDirectoryName(file) ?? string.Empty;
			try
			{
				foreach (string line in File.ReadLines(file, OpenBveApi.TextEncoding.GetSystemEncodingFromFile(file, encoding)))
				{
					foreach (string piece in line.Split(Separators))
					{
						AddObject(animatedFolder, piece, encoding, found, seen, depth + 1);
					}
				}
			}
			catch (Exception)
			{
				// Unreadable here: it loads as usual.
			}
		}

		private static string Resolve(string folder, string relative)
		{
			if (string.IsNullOrEmpty(relative))
			{
				return null;
			}

			try
			{
				string path = OpenBveApi.Path.CombineFile(folder, relative);
				return OpenBveApi.Path.FileExists(path) ? path : null;
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
