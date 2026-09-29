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
		}

		/// <summary>Charges time to a category until the returned scope is disposed.</summary>
		public static Scope Enter(string category)
		{
			if (Environment.CurrentManagedThreadId != thread)
			{
				return default;
			}

			Charge();
			Categories.Push(category);
			Calls[category] = Calls.TryGetValue(category, out int n) ? n + 1 : 1;
			return new Scope(true);
		}

		private static void Charge()
		{
			long now = Clock.ElapsedTicks;
			if (Categories.Count != 0)
			{
				string current = Categories.Peek();
				Ticks[current] = (Ticks.TryGetValue(current, out long t) ? t : 0) + now - last;
			}

			last = now;
		}

		/// <summary>Stops measuring and describes where the time went, largest first.</summary>
		public static string End()
		{
			if (thread == -1)
			{
				return string.Empty;
			}

			Charge();
			thread = -1;
			double total = Ticks.Values.Sum() / (double)Stopwatch.Frequency;
			return total.ToString("0.0") + " s: " + string.Join(", ", Ticks.OrderByDescending(p => p.Value).Select(p =>
				p.Key + " " + (p.Value / (double)Stopwatch.Frequency).ToString("0.0") + " s" +
				(Calls.TryGetValue(p.Key, out int n) ? " (" + n + ")" : string.Empty)));
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

				Charge();
				Categories.Pop();
			}
		}
	}
}
