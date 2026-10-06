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
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace OpenBve.Android
{
	/// <summary>
	/// Where loading time goes: exclusive time per host callback (objects, textures, sounds,
	/// scripted trains, object creation), with everything else charged to the caller - during a
	/// route load, the route parser itself.
	/// </summary>
	/// <remarks>
	/// The content plugins call back into the host for every file they need, so timing those
	/// callbacks splits a load into parsing and each kind of content without touching upstream.
	/// Only the loading thread is measured; callbacks from other threads are ignored.
	/// </remarks>
	public static class LoadProfile
	{
		private static readonly Dictionary<string, long> Ticks = new Dictionary<string, long>();
		private static readonly Dictionary<string, int> Calls = new Dictionary<string, int>();
		private static readonly Stack<string> Categories = new Stack<string>();
		private static readonly Stopwatch Clock = new Stopwatch();
		private static int thread = -1;
		private static long last;

		/*
		 * Gaps: time a watched category (a plugin's own code, e.g. the train plugin loading a
		 * scripted train) spends between two host callbacks. The largest, with the callbacks on
		 * either side, say which of its steps is slow - without instrumenting the plugin itself.
		 */
		private const string Watched = "scripted trains: train plugin";
		private const int GapsKept = 10;
		private static readonly List<(long Ticks, string Before, string After, int FullCollections)> Gaps = new List<(long, string, string, int)>();
		private static int lastFullCollections;
		private static int[] collectionsAtBegin = new int[3];
		private static TimeSpan pauseAtBegin;
		private static string lastEvent = "start";
		private static long watchedGapTicks;
		private static int watchedGapCount;

		/// <summary>Starts measuring on the calling thread, charging time to <paramref name="category"/>.</summary>
		public static void Begin(string category)
		{
			Ticks.Clear();
			Calls.Clear();
			Categories.Clear();
			thread = Environment.CurrentManagedThreadId;
			Categories.Push(category);
			Clock.Restart();
			last = 0;
			Gaps.Clear();
			lastEvent = "start";
			lastFullCollections = GC.CollectionCount(2);
			collectionsAtBegin = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
			pauseAtBegin = GC.GetTotalPauseDuration();
			watchedGapTicks = 0;
			watchedGapCount = 0;
		}

		/// <summary>Charges time to a category until the returned scope is disposed.</summary>
		/// <param name="category">What the time is charged to.</param>
		/// <param name="detail">The file concerned, if any, named in the gap report.</param>
		public static Scope Enter(string category, string detail = null)
		{
			if (Environment.CurrentManagedThreadId != thread)
			{
				return default;
			}

			string eventName = "start " + category + (detail != null ? " " + System.IO.Path.GetFileName(detail) : string.Empty);
			Charge(eventName);
			Categories.Push(category);
			Calls[category] = Calls.TryGetValue(category, out int n) ? n + 1 : 1;
			return new Scope(true);
		}

		private static void Charge(string eventName = null)
		{
			long now = Clock.ElapsedTicks;
			if (Categories.Count != 0)
			{
				string current = Categories.Peek();
				Ticks[current] = (Ticks.TryGetValue(current, out long t) ? t : 0) + now - last;
				if (current == Watched && eventName != null)
				{
					long gap = now - last;
					watchedGapTicks += gap;
					watchedGapCount++;
					int fullCollections = GC.CollectionCount(2);
					int during = fullCollections - lastFullCollections;
					lastFullCollections = fullCollections;
					if (Gaps.Count < GapsKept || gap > Gaps[Gaps.Count - 1].Ticks)
					{
						Gaps.Add((gap, lastEvent, eventName, during));
						Gaps.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
						if (Gaps.Count > GapsKept)
						{
							Gaps.RemoveAt(Gaps.Count - 1);
						}
					}
				}
			}

			if (eventName != null)
			{
				lastEvent = eventName;
			}

			last = now;
		}

		/// <summary>The largest gaps inside the watched plugin, for the log; empty if none.</summary>
		public static string GapReport()
		{
			if (Gaps.Count == 0)
			{
				return string.Empty;
			}

			return "train plugin's own time: " + (watchedGapTicks / (double)Stopwatch.Frequency).ToString("0.0") + " s in " + watchedGapCount +
			       " stretches; largest: " + string.Join("; ", Gaps.Select(g =>
				       (g.Ticks / (double)Stopwatch.Frequency).ToString("0.00") + " s after '" + g.Before + "' before '" + g.After + "'" +
				       (g.FullCollections > 0 ? " (" + g.FullCollections + " full GC)" : string.Empty)));
		}

		/// <summary>Stops measuring and describes where the time went, largest first.</summary>
		public static string End()
		{
			if (thread == -1)
			{
				return string.Empty;
			}

			Charge("end of load");
			thread = -1;
			double total = Ticks.Values.Sum() / (double)Stopwatch.Frequency;
			// Garbage collections during the stage, and the time they paused the program.
			string collections = "; GC: " + (GC.CollectionCount(0) - collectionsAtBegin[0]) + " gen0, " + (GC.CollectionCount(1) - collectionsAtBegin[1]) + " gen1, " +
			                     (GC.CollectionCount(2) - collectionsAtBegin[2]) + " full, paused " + (GC.GetTotalPauseDuration() - pauseAtBegin).TotalSeconds.ToString("0.0") +
			                     " s, " + (GC.GetTotalMemory(false) >> 20) + " MB managed";
			return total.ToString("0.0") + " s: " + string.Join(", ", Ticks.OrderByDescending(p => p.Value).Select(p =>
				p.Key + " " + (p.Value / (double)Stopwatch.Frequency).ToString("0.0") + " s" +
				(Calls.TryGetValue(p.Key, out int n) ? " (" + n + ")" : string.Empty))) + collections;
		}

		/// <summary>Ends a category when disposed.</summary>
		public readonly struct Scope : IDisposable
		{
			private readonly bool active;

			internal Scope(bool active)
			{
				this.active = active;
			}

			public void Dispose()
			{
				if (!active || Environment.CurrentManagedThreadId != thread)
				{
					return;
				}

				Charge("end " + Categories.Peek());
				Categories.Pop();
			}
		}
	}
}
