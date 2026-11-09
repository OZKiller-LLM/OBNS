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
using Android.Content;
using Android.OS;

namespace OpenBve.Android
{
	/*
	 * Android's performance hints (ADPF, Android 12 and later), for Potato mode (2026-11-09). An app cannot set the CPU's
	 * or GPU's clocks - that takes root, and on Galaxy phones Samsung's Game Optimizing Service governs them. What it can do
	 * is tell the system how long each frame may take and how long it really took: when the render thread falls behind its
	 * 33.3 ms, the system raises its clocks and moves it to the big cores. Older Android, or a device without a hint
	 * service, gets none of this and the game runs as before.
	 */
	internal sealed class PerformanceHints : IDisposable
	{
		private readonly PerformanceHintManager.Session session;

		private PerformanceHints(PerformanceHintManager.Session session)
		{
			this.session = session;
		}

		/// <summary>A hint session for the calling thread, or null where Android has none.</summary>
		/// <param name="context">Any context of the app.</param>
		/// <param name="targetNanoseconds">How long a frame may take.</param>
		/// <param name="why">Why there is none, for the log.</param>
		public static PerformanceHints TryCreate(Context context, long targetNanoseconds, out string why)
		{
			why = null;
			if (Build.VERSION.SdkInt < BuildVersionCodes.S)
			{
				why = "Android " + Build.VERSION.Release + " has no performance hints (12 and later)";
				return null;
			}

			try
			{
				if (!(context?.GetSystemService(Context.PerformanceHintService) is PerformanceHintManager manager))
				{
					why = "no performance hint service";
					return null;
				}

				PerformanceHintManager.Session session = manager.CreateHintSession(new[] { global::Android.OS.Process.MyTid() }, targetNanoseconds);
				if (session == null)
				{
					why = "the device declined a hint session";
					return null;
				}

				return new PerformanceHints(session);
			}
			catch (Exception e)
			{
				why = e.GetType().Name + ": " + e.Message;
				return null;
			}
		}

		/// <summary>How long the last frame's work took.</summary>
		public void Report(long nanoseconds)
		{
			if (nanoseconds > 0)
			{
				try
				{
					session.ReportActualWorkDuration(nanoseconds);
				}
				catch (Exception)
				{
					// A hint lost is no matter.
				}
			}
		}

		public void Dispose()
		{
			try
			{
				session.Close();
			}
			catch (Exception)
			{
				// Gone already.
			}
		}
	}
}
