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

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace OpenBve.Android
{
	/// <summary>
	/// Accumulates how long each phase of a frame takes. Each <see cref="Mark"/> charges the time
	/// since the previous mark to the named phase, so the phases add up to the whole frame.
	/// </summary>
	public class FrameProfile
	{
		private readonly Stopwatch clock = Stopwatch.StartNew();
		private readonly Dictionary<string, double> totals = new Dictionary<string, double>();
		private readonly List<string> order = new List<string>();
		private double lastMark;
		private int frames;

		/// <summary>
		/// The phase last completed, and when. Read from the UI thread by the watchdog, so that a
		/// frame that never finishes can still say where it stopped.
		/// </summary>
		public volatile string LastPhase = "starting";

		/// <summary>Ticks (<see cref="System.DateTime.UtcNow"/>) at the last mark.</summary>
		public long LastMarkTicks;

		/// <summary>Charges the time since the previous mark to <paramref name="phase"/>.</summary>
		public void Mark(string phase)
		{
			LastPhase = phase;
			System.Threading.Interlocked.Exchange(ref LastMarkTicks, System.DateTime.UtcNow.Ticks);
			double now = clock.Elapsed.TotalMilliseconds;
			if (!totals.ContainsKey(phase))
			{
				totals[phase] = 0.0;
				order.Add(phase);
			}

			totals[phase] += now - lastMark;
			lastMark = now;
		}

		/// <summary>Records the end of a frame.</summary>
		public void EndFrame()
		{
			frames++;
		}

		/// <summary>Returns the average time per frame of each phase, and resets.</summary>
		public string Report()
		{
			if (frames == 0)
			{
				return "no frames";
			}

			StringBuilder report = new StringBuilder();
			double sum = totals.Values.Sum();
			foreach (string phase in order)
			{
				report.Append(phase).Append(' ').Append((totals[phase] / frames).ToString("0.0")).Append(" ms, ");
			}

			report.Append("total ").Append((sum / frames).ToString("0.0")).Append(" ms/frame");

			foreach (string phase in order)
			{
				totals[phase] = 0.0;
			}

			frames = 0;
			return report.ToString();
		}
	}
}
